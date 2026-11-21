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

    /// <summary>
    /// A file a node names is opened only under the root: not beside it, not above it by <c>..</c>, not through a link
    /// the repository holds, and not through the owner's own link to the root, which is another spelling of it.
    /// </summary>
    [Fact]
    public void AFileANodeNames_IsOpenedOnlyUnderTheRoot_ThroughNoLink()
    {
        var root = Path.Combine(_base, "repo");
        var outside = Path.Combine(_base, "outside");
        var beside = root + "-other";
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "");
        File.WriteAllText(Path.Combine(outside, "b.cs"), "");
        File.WriteAllText(Path.Combine(beside, "b.cs"), "");

        Assert.Equal(Path.Combine(root, "src", "a.cs"), ReportedPaths.FileInRoot(root, Path.Combine(root, "src", "a.cs")));
        Assert.Equal(Path.Combine(root, "src", "a.cs"), ReportedPaths.FileInRoot(root + Path.DirectorySeparatorChar, Path.Combine(root, "src", ".", "a.cs")));
        Assert.Equal(Path.Combine(root, "src", "a.cs"), ReportedPaths.FileInRoot(root, Path.Combine(root, "lib", "..", "src", "a.cs")));
        // Whether the file is there is not this rule's question.
        Assert.Equal(Path.Combine(root, "src", "missing.cs"), ReportedPaths.FileInRoot(root, Path.Combine(root, "src", "missing.cs")));

        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(outside, "b.cs")));
        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(root, "src", "..", "..", "outside", "b.cs")));
        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(beside, "b.cs")));
        Assert.Null(ReportedPaths.FileInRoot(root, root));
        Assert.Null(ReportedPaths.FileInRoot(root, _base));
        Assert.Null(ReportedPaths.FileInRoot(root, ""));
        Assert.Null(ReportedPaths.FileInRoot(root, null));
        Assert.Null(ReportedPaths.FileInRoot(root, "a\0.cs"));
        Assert.Null(ReportedPaths.FileInRoot(null, Path.Combine(root, "src", "a.cs")));
        Assert.Null(ReportedPaths.FileInRoot("", Path.Combine(root, "src", "a.cs")));

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "src", "leaf.cs"), Path.Combine(outside, "b.cs"));
            Directory.CreateSymbolicLink(Path.Combine(_base, "owners"), root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows without the privilege to make a link.
            return;
        }

        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(root, "link", "b.cs")));
        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(root, "src", "leaf.cs")));
        Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(_base, "owners", "src", "a.cs")));
        Assert.Equal(Path.Combine(root, "src", "a.cs"), ReportedPaths.FileInRoot(root, Path.Combine(root, "src", "a.cs")));
    }

    /// <summary>
    /// Where the file system holds <c>repo</c> and <c>REPO</c> as two directories, the second is not under the first.
    /// The guard alone compares the two without regard to case.
    /// </summary>
    [Fact]
    public void WhereCaseIsToldApart_TheRootsNameInAnotherCaseIsNotTheRoot()
    {
        var root = Path.Combine(_base, "repo");
        var otherCase = Path.Combine(_base, "REPO");
        Directory.CreateDirectory(root);
        var guard = new PathGuard(root, [], ReportedPaths.NoDisk, OperatingSystem.IsWindows());

        Assert.True(guard.InRepository(Path.Combine(otherCase, "a.cs")));
        Assert.Null(ReportedPaths.FileInRoot(guard, Path.Combine(otherCase, "a.cs"), tellsCaseApart: true));
        Assert.Equal(Path.Combine(otherCase, "a.cs"), ReportedPaths.FileInRoot(guard, Path.Combine(otherCase, "a.cs"), tellsCaseApart: false));
        Assert.Equal(Path.Combine(root, "a.cs"), ReportedPaths.FileInRoot(guard, Path.Combine(root, "a.cs"), tellsCaseApart: true));
        if (OperatingSystem.IsLinux()) Assert.Null(ReportedPaths.FileInRoot(root, Path.Combine(otherCase, "a.cs")));
    }

    /// <summary>
    /// On Windows a node's path on a share, a device or a stream is refused by its spelling, before the disk is asked
    /// anything about it: a short name in a share's path is not written out, which would reach the share.
    /// </summary>
    [Fact]
    public void OnWindows_AFileANodeNamesOnAShare_IsRefusedBeforeTheDiskIsAsked()
    {
        if (!OperatingSystem.IsWindows()) return;
        var disk = new RecordingDisk();
        var guard = new PathGuard(@"C:\repo", [], disk, windows: true);

        Assert.Equal(@"C:\repo\src\a.cs", ReportedPaths.FileInRoot(guard, @"C:\repo\src\a.cs", tellsCaseApart: false));
        Assert.Equal(@"c:\REPO\src\a.cs", ReportedPaths.FileInRoot(guard, @"c:\REPO\src\a.cs", tellsCaseApart: false));
        foreach (var refused in new[]
        {
            @"\\evil\share\a.cs",
            @"\\evil\share\RUNNER~1\a.cs",
            @"\\evil\share\repo\src\a.cs",
            @"\\?\UNC\evil\share\a.cs",
            @"\\?\C:\repo\src\a.cs",
            @"\\.\C:\repo\src\a.cs",
            @"C:\repo\src\a.cs:stream",
            @"C:\repo\src::$INDEX_ALLOCATION\a.cs",
            @"C:\repo\src\NUL",
            @"C:\repo\src\COM1.cs",
            @"C:\other\a.cs",
            @"C:\repo\..\other\a.cs",
            @"C:\repo-other\a.cs",
            @"D:\repo\src\a.cs",
        })
        {
            Assert.True(ReportedPaths.FileInRoot(guard, refused, tellsCaseApart: false) is null, refused);
        }

        Assert.DoesNotContain(disk.Asked, asked => !asked.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A disk with no links on it that remembers every path it was asked about.</summary>
    private sealed class RecordingDisk : ILinkProbe
    {
        public List<string> Asked { get; } = [];

        public Dictionary<string, bool>? ReparsePointsIn(string directory)
        {
            Asked.Add(directory);
            return [];
        }

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path)
        {
            Asked.Add(path);
            return (false, false);
        }

        public bool IsLink(string path, bool isDirectory)
        {
            Asked.Add(path);
            return false;
        }

        public string Expand(string path)
        {
            Asked.Add(path);
            return path;
        }
    }
}
