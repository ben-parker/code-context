using System;
using System.IO;
using CodeContext.Core.Paths;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Which paths a worker reported the host may look at (<see cref="ReportedPaths"/>). The rule is the C# worker's
/// guard, the same source file compiled into the host, which <c>DiskGuardVectorTests</c> runs against
/// <c>tests/Shared/disk-guard-vectors.json</c> on Windows. Here: the parts of it that read only a path's text, on
/// Windows spellings on every platform, and the link rule on this platform's disk.
/// </summary>
public sealed class ReportedPathsTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-reported-").FullName;

    public void Dispose() => Directory.Delete(_base, recursive: true);

    /// <summary>Normalized Windows paths, as the system writes them: on a drive, or on a share with its name.</summary>
    [Theory]
    [InlineData(@"C:\repo\src\Lib.csproj", true, null)]
    [InlineData(@"d:\packages\x\1.0.0\lib\x.dll", true, null)]
    [InlineData(@"\\evil-ext\share\obj\project.assets.json", false, @"evil-ext\share")]
    [InlineData(@"\\evil-ts-import\share\mod.ts", false, @"evil-ts-import\share")]
    [InlineData(@"\\owner\build\repo\src\a.cs", false, @"owner\build")]
    [InlineData(@"\\?\UNC\evil\share\a", false, null)]
    [InlineData(@"\\.\pipe\name", false, null)]
    [InlineData(@"\\?\C:\repo\a", false, null)]
    [InlineData(@"\\evil", false, null)]
    [InlineData(@"\\evil\", false, null)]
    [InlineData(@"C:", false, null)]
    [InlineData(@"\repo\a", false, null)]
    public void AWindowsPath_IsOnADriveOrNamesItsShare(string full, bool onDrive, string? share)
    {
        Assert.Equal(onDrive, PathGuard.IsOnDrive(full));
        Assert.Equal(share, PathGuard.ShareOf(full));
    }

    /// <summary>With no share given, only a drive path with no stream is allowed as it is spelled.</summary>
    [Theory]
    [InlineData(@"C:\repo\src\Lib.csproj", true)]
    [InlineData(@"C:\repo\link::$INDEX_ALLOCATION\a", false)]
    [InlineData(@"C:\repo\a.cs:stream", false)]
    [InlineData(@"\\evil-ext\share\obj\project.assets.json", false)]
    [InlineData(@"\\?\UNC\evil\share\a", false)]
    [InlineData(@"\\.\NUL", false)]
    public void WithNoShareGiven_OnlyADrivePathWithoutAStreamIsAllowed(string full, bool allowed)
        => Assert.Equal(allowed, new PathGuard(null, [], ReportedPaths.NoDisk, windows: true).AllowedAsSpelled(full));

    [Fact]
    public void OffWindows_NothingIsRefusedByItsSpelling_AndThereIsNoRuleToRun()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Null(ReportedPaths.RefusedAsSpelled(_base));
    }

    [Fact]
    public void OnWindows_AShareNobodyChoseIsRefusedByItsSpelling_AndTheRootsOwnShareIsNot()
    {
        if (!OperatingSystem.IsWindows()) return;
        var onDrive = ReportedPaths.RefusedAsSpelled(@"C:\repo")!;
        Assert.True(onDrive(@"\\evil-ext\share\obj\project.assets.json"));
        Assert.True(onDrive(@"//evil-ts-import/share/mod.ts"));
        Assert.True(onDrive(@"C:\repo\NUL"));
        Assert.False(onDrive(@"C:\repo\src\Lib.csproj"));
        Assert.False(onDrive(@"D:\packages\x.dll"));

        var onShare = ReportedPaths.RefusedAsSpelled(@"\\owner\build\repo")!;
        Assert.False(onShare(@"\\OWNER\Build\repo\src\Lib.csproj"));
        Assert.True(onShare(@"\\owner\other\x"));
    }

    /// <summary>
    /// The host follows no link the repository holds, when it walks the root and when it looks at a reported path:
    /// a path that is such a link, or passes through one, is refused on every platform. A link outside the repository
    /// is the machine owner's.
    /// </summary>
    [Fact]
    public void AReportedPathThroughALinkTheRepositoryHolds_IsRefused()
    {
        var root = Path.Combine(_base, "repo");
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "real", "a.props"), "");
        File.WriteAllText(Path.Combine(outside, "b.props"), "");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "real", "leaf.props"), Path.Combine(outside, "b.props"));
            Directory.CreateSymbolicLink(Path.Combine(_base, "owners"), root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows without the privilege to make a link.
            return;
        }

        var refused = ReportedPaths.RefusedOnDisk(root);

        Assert.True(refused(Path.Combine(root, "link", "b.props")));
        Assert.True(refused(Path.Combine(root, "link")));
        Assert.True(refused(Path.Combine(root, "link", "missing", "deeper.props")));
        Assert.True(refused(Path.Combine(root, "real", "leaf.props")));
        Assert.False(refused(Path.Combine(root, "real", "a.props")));
        Assert.False(refused(Path.Combine(root, "real", "missing.props")));
        Assert.False(refused(Path.Combine(root, "missing", "deeper.props")));
        Assert.False(refused(root));
        Assert.False(refused(Path.Combine(outside, "b.props")));
        Assert.False(refused(Path.Combine(_base, "owners", "real", "a.props")));
    }
}
