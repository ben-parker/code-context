using System.Reflection;
using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers.Projects;

/// <summary>
/// What may be read. The theory decides on spelling alone, so it can name hosts that do not
/// exist. The tests through <c>\\localhost\c$</c> open real files, because a check that only
/// compares strings would pass even if a path still reached the share.
/// </summary>
public sealed class DiskTests : IDisposable
{
    private const string OnLocalShare = @"\\localhost\c$\Windows\notepad.exe";

    /// <summary>These tests read their own temp files back through the share of drive C.</summary>
    private static bool ServesLocalShare
        => File.Exists(OnLocalShare) && Path.GetTempPath().StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase);

    private static readonly ToolchainLocations NoToolchain = new([], [], [], []);

    private readonly string _directory = Directory.CreateTempSubdirectory("cc-disk-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Disk On(string? root) => new(root, NoToolchain);

    /// <summary>
    /// The decision itself, which nothing in the worker can ask for. From outside, a refused
    /// path and a missing one are the same, so a host that does not exist needs this.
    /// </summary>
    private static bool Refused(Disk disk, string path)
        => (bool)typeof(Disk).GetMethod("Refused", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(disk, [path])!;

    private string RealFile(string text = "held")
    {
        var path = Path.Combine(_directory, "held.txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A file of this test as the machine's administrative share serves it.</summary>
    private static string ThroughLocalShare(string path, string host = "localhost")
    {
        Assert.StartsWith(@"C:\", path, StringComparison.OrdinalIgnoreCase);
        return $@"\\{host}\c$\{path[3..]}";
    }

    [WindowsTheory]
    // Network paths, in every spelling that reaches a share.
    [InlineData(@"\\host\share\x.dll", @"C:\repo", true)]
    [InlineData("//host/share/x.dll", @"C:\repo", true)]
    [InlineData(@"/\host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\/host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\\host\share\x.dll", null, true)]
    [InlineData(@"\??\UNC\host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\\?\UNC\host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\\.\UNC\host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\host\share\x.dll", @"C:\repo", true)]
    [InlineData(@"\\host", @"C:\repo", true)]
    [InlineData(@"\\", @"C:\repo", true)]
    // Local files spelled as devices are not on a drive letter, so they are refused too.
    [InlineData(@"\??\C:\Windows\notepad.exe", @"C:\repo", true)]
    [InlineData(@"\\?\C:\Windows\notepad.exe", @"C:\repo", true)]
    [InlineData(@"\\.\C:\Windows\notepad.exe", @"C:\repo", true)]
    [InlineData(@"\\.\pipe\x", @"C:\repo", true)]
    [InlineData(@"C:\repo\CON", @"C:\repo", true)]
    [InlineData("NUL", @"C:\repo", true)]
    // Nothing that cannot name a file.
    [InlineData("", @"C:\repo", true)]
    [InlineData("a\0b", @"C:\repo", true)]
    // A repository on a share reads that share and no other.
    [InlineData(@"\\other\share\x.dll", @"\\host\share\repo", true)]
    [InlineData(@"\\host\other\x.dll", @"\\host\share\repo", true)]
    [InlineData(@"\\?\UNC\host\share\x.dll", @"\\host\share\repo", true)]
    [InlineData(@"\??\UNC\host\share\x.dll", @"\\host\share\repo", true)]
    [InlineData(@"\\host\share\lib\x.dll", @"\\host\share\repo", false)]
    [InlineData(@"\\HOST\Share\lib\x.dll", @"\\host\share\repo", false)]
    [InlineData(@"//host/SHARE/lib/x.dll", @"\\host\share\repo", false)]
    // Parent segments do not climb out of a share.
    [InlineData(@"\\host\share\repo\..\..\..\other\x.dll", @"\\host\share\repo", false)]
    // A device root earns no share: every sibling would otherwise start with the same segments.
    [InlineData(@"\\?\UNC\other\share\x.dll", @"\\?\UNC\host\share\repo", true)]
    [InlineData(@"\\?\C:\other\x.dll", @"\\?\C:\repo", true)]
    // Drive letters.
    [InlineData(@"C:\lib\x.dll", @"C:\repo", false)]
    [InlineData(@"c:/lib/x.dll", @"C:\repo", false)]
    [InlineData(@"D:\lib\x.dll", @"\\host\share\repo", false)]
    public void OnlyADriveOrAnAllowedShareIsAllowed(string path, string? root, bool refused)
        => Assert.Equal(refused, Refused(On(root), path));

    [WindowsFact]
    public void AnOwnerConfiguredShareIsAllowed()
    {
        var disk = new Disk(@"C:\repo", new ToolchainLocations([@"\\tools\dotnet\sdk"], [@"\\nuget\cache"], [@"\\ref\roots"], [@"\\run\times"]));

        Assert.False(Refused(disk, @"\\TOOLS\Dotnet\packs\x.dll"));
        Assert.False(Refused(disk, @"\\nuget\cache\x\1.0.0\lib\x.dll"));
        Assert.False(Refused(disk, @"\\ref\roots\v4.8\mscorlib.dll"));
        Assert.False(Refused(disk, @"\\run\times\mscorlib.dll"));
        Assert.True(Refused(disk, @"\\nuget\other\x.dll"));
        Assert.True(Refused(disk, @"\??\UNC\nuget\cache\x.dll"));
    }

    [LocalShareTheory]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData(@"\??\C:\Windows\notepad.exe")]
    [InlineData(@"\\.\C:\Windows\notepad.exe")]
    [InlineData(OnLocalShare)]
    [InlineData(@"\??\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\?\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\.\UNC\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"/\localhost\c$\Windows\notepad.exe")]
    [InlineData(@"\/localhost\c$\Windows\notepad.exe")]
    public void ARefusedPathLooksMissingEvenWhenTheFileIsThere(string path)
    {
        Assert.True(File.Exists(path), "The row must name a file the runtime would really open.");
        var disk = On(@"C:\repo");
        var directory = Path.GetDirectoryName(path)!;

        Assert.False(disk.FileExists(path));
        Assert.False(disk.DirectoryExists(directory));
        Assert.Equal(File.GetLastWriteTimeUtc(@"C:\no\such\file"), disk.LastWriteUtc(path));
        Assert.Empty(disk.Files(directory, "*"));
        Assert.Empty(disk.Directories(directory));
        Assert.Empty(disk.RealDirectories(directory));
        Assert.Null(disk.LinkTarget(path));
        Assert.Throws<FileNotFoundException>(() => disk.OpenRead(path));
        Assert.Throws<FileNotFoundException>(() => disk.ReadAllText(path));
        Assert.Throws<FileNotFoundException>(() => disk.LoadXml(path));
        Assert.Throws<FileNotFoundException>(() => disk.Reference(path));
    }

    [LocalShareFact]
    public void ARepositoryOnAShareReadsThatShareAndNoOther()
    {
        var share = ThroughLocalShare(RealFile());
        var disk = On(ThroughLocalShare(_directory));

        Assert.Equal("held", disk.ReadAllText(share));
        Assert.Equal("held", disk.ReadAllText(share.ToUpperInvariant()));
        Assert.False(disk.FileExists(ThroughLocalShare(RealFile(), "127.0.0.1")));
    }

    [LocalShareFact]
    public void AnOwnerConfiguredShareIsReallyRead()
    {
        var share = ThroughLocalShare(RealFile());
        var disk = new Disk(@"C:\repo", new ToolchainLocations([], [ThroughLocalShare(_directory)], [], []));

        Assert.True(disk.FileExists(share));
    }

    [LocalShareFact]
    public void AShortNameIsExpandedOnAnAllowedShare()
        => Assert.Equal(
            Path.GetFullPath(@"\\localhost\c$\PROGRA~1"),
            On(@"\\localhost\c$\repo").FullPath(@"\\localhost\c$\PROGRA~1"));

    [LocalShareFact]
    public void NormalizingARefusedPathDoesNotTouchIt()
    {
        // The runtime asks the file system for the long form of anything holding a tilde, so
        // these would come back naming "Program Files" had they reached the share.
        var disk = On(@"C:\repo");

        Assert.Equal(@"\\localhost\c$\PROGRA~1", disk.FullPath(@"\\localhost\c$\PROGRA~1"));
        Assert.Equal(@"\\localhost\c$\PROGRA~1", disk.FullPath("//localhost/c$/x/../PROGRA~1", @"C:\repo"));
        Assert.Equal(@"\\localhost\c$\PROGRA~1\x", disk.FullPath("x", @"\\localhost\c$\PROGRA~1"));
        Assert.Equal(@"\\localhost\c$\PROGRA~1", disk.RelativePath(@"C:\repo", @"\\localhost\c$\PROGRA~1"));
    }

    [WindowsFact]
    public void AnUnreachableHostIsNotContactedToNormalizeAPath()
    {
        // A fresh address each run: Windows remembers a failed host for a while.
        var path = $@"\\192.0.2.{Random.Shared.Next(2, 250)}\share\a~1\x.props";
        var disk = On(@"C:\repo");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(path, disk.FullPath(path));
        Assert.False(disk.FileExists(path));
        Assert.Equal(path, disk.RelativePath(@"C:\repo", path));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
    }

    [WindowsFact]
    public void AShortNameIsExpandedOnADrive()
        => Assert.Equal(Path.GetFullPath(@"C:\PROGRA~1"), On(@"C:\repo").FullPath(@"C:\PROGRA~1"));

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    [InlineData("C:\\a\0b\\c.txt")]
    [InlineData("\0")]
    public void NoPathMakesItThrow(string path)
    {
        var disk = On(_directory);

        Assert.False(disk.FileExists(path));
        Assert.False(disk.DirectoryExists(path));
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(_directory, "missing")), disk.LastWriteUtc(path));
        Assert.Empty(disk.Files(path, "*"));
        Assert.Empty(disk.Directories(path));
        Assert.Empty(disk.RealDirectories(path));
        Assert.Null(disk.LinkTarget(path));
        Assert.Throws<FileNotFoundException>(() => disk.OpenRead(path));
        Assert.Throws<FileNotFoundException>(() => disk.ReadAllText(path));
        Assert.Throws<FileNotFoundException>(() => disk.LoadXml(path));
        Assert.Throws<FileNotFoundException>(() => disk.Reference(path));
        _ = disk.FullPath(path);
        _ = disk.FullPath(path, _directory);
        _ = disk.RelativePath(_directory, path);
        _ = disk.RelativePath(path, _directory);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("*")]
    [InlineData("a|b")]
    [InlineData("\"C:\\quoted\"")]
    public void AnOddPathIsAnsweredNotThrownOver(string path)
    {
        var disk = On(_directory);

        Assert.False(disk.FileExists(Path.Combine(_directory, path, "missing")));
        _ = disk.FullPath(path, _directory);
        _ = disk.FullPath(path);
        _ = disk.RelativePath(_directory, path);
        _ = disk.RelativePath(path, _directory);

        // Locations come straight from settings such as PATH, and a worker must still start.
        _ = new Disk([path]);
    }

    [Fact]
    public void AnAllowedFileReadsAsItAlwaysDid()
    {
        var path = RealFile();
        var disk = On(_directory);

        Assert.True(disk.FileExists(path));
        Assert.True(disk.DirectoryExists(_directory));
        Assert.Equal(File.GetLastWriteTimeUtc(path), disk.LastWriteUtc(path));
        Assert.Equal([path], disk.Files(_directory, "*.txt"));
        Assert.Equal("held", disk.ReadAllText(path));
        using var stream = disk.OpenRead(path);
        Assert.Equal(4, stream.Length);
    }

    [Fact]
    public void AFileThatIsNotThereIsNotFound()
    {
        var disk = On(_directory);
        var missing = Path.Combine(_directory, "missing.txt");

        Assert.False(disk.FileExists(missing));
        Assert.Equal(File.GetLastWriteTimeUtc(missing), disk.LastWriteUtc(missing));
        Assert.Throws<FileNotFoundException>(() => disk.ReadAllText(missing));
    }

    [Fact]
    public void ADirectoryLinkIsLeftOutOfRealDirectories()
    {
        var real = Directory.CreateDirectory(Path.Combine(_directory, "real")).FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_directory, "link"), real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Making a link can need a privilege this account lacks.
        }

        Assert.Equal([real], On(_directory).RealDirectories(_directory));
    }

    /// <summary>A disk over <c>C:\repo</c> whose links are these, so a link to a share needs no privilege to make.</summary>
    private static Disk WithLinks(Dictionary<string, string?> links, List<string>? listed = null)
        => new(
            @"C:\repo",
            NoToolchain,
            directory =>
            {
                listed?.Add(directory);
                return links.Keys
                    .Where(link => string.Equals(Path.GetDirectoryName(link), directory, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(link => Path.GetFileName(link), _ => true, StringComparer.OrdinalIgnoreCase);
            },
            (entry, _) => links.First(link => string.Equals(link.Key, entry, StringComparison.OrdinalIgnoreCase)).Value);

    [WindowsFact]
    public void ALinkInTheRepositoryThatLeadsToAShareIsRefusedWhereverItSits()
    {
        var listed = new List<string>();
        var disk = WithLinks(
            new()
            {
                [@"C:\repo\vendor"] = @"\\evil\share\vendor",
                [@"C:\repo\src\Leaf.cs"] = @"\\evil\share\Leaf.cs",
            },
            listed);

        Assert.True(Refused(disk, @"C:\repo\vendor\lib\X.cs"));
        Assert.True(Refused(disk, @"C:\repo\vendor"));
        Assert.True(Refused(disk, @"C:\repo\src\Leaf.cs"));
        Assert.True(Refused(disk, @"C:\repo\src\..\vendor\X.cs"));
        Assert.False(Refused(disk, @"C:\repo\src\Real.cs"));
        Assert.False(disk.FileExists(@"C:\repo\vendor\lib\X.cs"));
        Assert.Throws<FileNotFoundException>(() => disk.ReadAllText(@"C:\repo\src\Leaf.cs"));
        // Nothing on the share was looked at to decide.
        Assert.DoesNotContain(listed, directory => directory.StartsWith(@"\\", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void ALinkToADriveIsFollowed()
    {
        var disk = WithLinks(new()
        {
            [@"C:\repo\packages\Lib"] = @"C:\repo\.store\Lib.1.0",
            [@"C:\repo\shared"] = @"D:\elsewhere\shared",
            [@"C:\repo\relative"] = @"..\sibling",
            [@"C:\repo\cloud.cs"] = null,
        });

        Assert.False(Refused(disk, @"C:\repo\packages\Lib\Lib.dll"));
        Assert.False(Refused(disk, @"C:\repo\shared\X.cs"));
        Assert.False(Refused(disk, @"C:\repo\relative\X.cs"));
        // A reparse point that is not a link: a file kept in the cloud, say.
        Assert.False(Refused(disk, @"C:\repo\cloud.cs"));
    }

    [WindowsFact]
    public void ALinkThroughAnotherLinkToAShareIsRefusedAndALoopEnds()
    {
        var disk = WithLinks(new()
        {
            [@"C:\repo\a"] = @"C:\repo\b",
            [@"C:\repo\b"] = @"\\evil\share\b",
            [@"C:\repo\device"] = @"\\?\UNC\evil\share",
            [@"C:\repo\loop1"] = @"C:\repo\loop2",
            [@"C:\repo\loop2"] = @"C:\repo\loop1",
        });

        Assert.True(Refused(disk, @"C:\repo\a\X.cs"));
        Assert.True(Refused(disk, @"C:\repo\device\X.cs"));
        Assert.True(Refused(disk, @"C:\repo\loop1\X.cs"));
    }

    [WindowsFact]
    public void ALinkOutsideTheRepositoryIsTheOwnersAndIsNotLookedAt()
    {
        var listed = new List<string>();
        var disk = WithLinks(new() { [@"C:\tools\sdk"] = @"\\owner\share\sdk" }, listed);

        Assert.False(Refused(disk, @"C:\tools\sdk\ref\System.Runtime.dll"));
        Assert.False(Refused(disk, @"C:\repo"));
        Assert.Empty(listed);
    }

    [WindowsFact]
    public void LinksAreLookedAtOncePerDirectoryUntilForgotten()
    {
        var listed = new List<string>();
        var disk = WithLinks([], listed);

        Refused(disk, @"C:\repo\src\A.cs");
        Refused(disk, @"C:\repo\src\B.cs");
        Assert.Equal([@"C:\repo", @"C:\repo\src"], listed);

        disk.ForgetLinks();
        Refused(disk, @"C:\repo\src\C.cs");
        Assert.Equal([@"C:\repo", @"C:\repo\src", @"C:\repo", @"C:\repo\src"], listed);
    }

    [WindowsFact]
    public void AJunctionInTheRepositoryToADriveIsRead()
    {
        var real = Directory.CreateDirectory(Path.Combine(_directory, "real")).FullName;
        File.WriteAllText(Path.Combine(real, "held.txt"), "held");
        var link = Path.Combine(_directory, "link");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            Assert.Equal("held", On(_directory).ReadAllText(Path.Combine(link, "held.txt")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void ADocumentTypeIsNotFollowed()
    {
        var path = RealFile("""<!DOCTYPE x [ <!ENTITY e SYSTEM "file:///C:/Windows/win.ini"> ]><x>&e;</x>""");

        Assert.ThrowsAny<System.Xml.XmlException>(() => On(_directory).LoadXml(path));
    }

    private string WorkspaceCache()
        => Directory.CreateDirectory(Path.Combine(_directory, ".codecontext", "cache", "default")).FullName;

    private static byte Written(Stream stream)
    {
        stream.WriteByte(7);
        return 7;
    }

    [Fact]
    public void AStateFileIsReplacedWhereTheHostKeepsIt()
    {
        var path = Path.Combine(WorkspaceCache(), "worker-csharp.0000000001.aaaaaaaa.state");
        File.WriteAllText(path, "older");

        Assert.Equal(7, On(_directory).ReplaceStateFile(path, Written, CancellationToken.None));

        Assert.Equal([7], File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ARootWithATrailingSeparatorHasTheSameCache()
    {
        var path = Path.Combine(WorkspaceCache(), "worker-csharp.0000000001.aaaaaaaa.state");

        On(_directory + Path.DirectorySeparatorChar).ReplaceStateFile(path, Written, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [WindowsFact]
    public void ARootGivenByItsShortNameHasTheCacheTheHostNames()
    {
        // The host expands a root's short names, so the two spellings must meet. The directory is
        // not there, so passing the location check shows as failing to find it, and nothing is written.
        var host = Path.Combine(Path.GetFullPath(@"C:\PROGRA~1"), "cc-absent", ".codecontext", "cache", "default", "a.state");

        Assert.Throws<DirectoryNotFoundException>(
            () => On(@"C:\PROGRA~1\cc-absent").ReplaceStateFile(host, Written, CancellationToken.None));
    }

    [Theory]
    [InlineData("a.state")]
    [InlineData(".codecontext/a.state")]
    [InlineData(".codecontext/cache/a.state")]
    [InlineData(".codecontext/cache/default/deeper/a.state")]
    [InlineData(".codecontext/cache/default/graph.ccsnap")]
    [InlineData(".codecontext/cache/default/writer.lock")]
    [InlineData(".codecontext/cache/default/../../../a.state")]
    [InlineData(".codecontext/other/default/a.state")]
    [InlineData("src/.codecontext/cache/default/a.state")]
    [InlineData("../a.state")]
    public void NothingElseCanBeWritten(string relative)
    {
        var parent = Directory.CreateDirectory(Path.Combine(_directory, "parent")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(parent, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(root, ".codecontext", "cache", "default", "deeper"));
        Directory.CreateDirectory(Path.Combine(root, ".codecontext", "other", "default"));
        Directory.CreateDirectory(Path.Combine(root, "src", ".codecontext", "cache", "default"));
        var path = Path.Combine(root, relative);
        var before = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories);

        Assert.Throws<UnauthorizedAccessException>(() => On(root).ReplaceStateFile(path, Written, CancellationToken.None));

        Assert.Equal(before, Directory.GetFiles(_directory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void AnotherRepositorysCacheCannotBeWritten()
    {
        var path = Path.Combine(WorkspaceCache(), "a.state");
        var other = Directory.CreateDirectory(Path.Combine(_directory, "other")).FullName;

        Assert.Throws<UnauthorizedAccessException>(() => On(other).ReplaceStateFile(path, Written, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\0b.state")]
    public void AnUnusablePathIsNotWritten(string path)
        => Assert.Throws<UnauthorizedAccessException>(() => On(_directory).ReplaceStateFile(path, Written, CancellationToken.None));

    [Fact]
    public void WithoutARootNothingCanBeWritten()
    {
        var path = Path.Combine(WorkspaceCache(), "a.state");

        Assert.Throws<UnauthorizedAccessException>(() => new Disk([_directory]).ReplaceStateFile(path, Written, CancellationToken.None));
        Assert.Throws<UnauthorizedAccessException>(() => On(null).ReplaceStateFile(path, Written, CancellationToken.None));
    }

    [Fact]
    public void AFailedWriteLeavesNoTempFile()
    {
        var path = Path.Combine(WorkspaceCache(), "a.state");

        Assert.Throws<InvalidOperationException>(() => On(_directory).ReplaceStateFile<int>(
            path, _ => throw new InvalidOperationException(), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [LocalShareFact]
    public void AStateFileIsWrittenForARepositoryOnAShare()
    {
        var path = ThroughLocalShare(Path.Combine(WorkspaceCache(), "a.state"));

        On(ThroughLocalShare(_directory)).ReplaceStateFile(path, Written, CancellationToken.None);

        Assert.True(File.Exists(path));
    }

    [LocalShareTheory]
    [InlineData(@"\\?\UNC\localhost\c$\")]
    [InlineData(@"\??\UNC\localhost\c$\")]
    [InlineData(@"\\127.0.0.1\c$\")]
    public void AnotherSpellingOfTheShareIsNotWritten(string prefix)
    {
        var local = Path.Combine(WorkspaceCache(), "a.state");
        var disk = On(ThroughLocalShare(_directory));

        Assert.Throws<UnauthorizedAccessException>(() => disk.ReplaceStateFile(prefix + local[3..], Written, CancellationToken.None));

        Assert.False(File.Exists(local));
    }

    [Fact]
    public void AStatAndARawReadGoThroughTheGuard()
    {
        var path = RealFile();
        var disk = On(_directory);

        Assert.Equal((4L, File.GetLastWriteTimeUtc(path)), disk.Stat(path));
        Assert.Equal(File.ReadAllBytes(path), disk.ReadAllBytes(path));
        using var stream = disk.OpenForOneScan(path);
        Assert.Equal(4, stream.Length);
    }

    [LocalShareFact]
    public void TheNewReadsLookMissingWhenRefused()
    {
        var disk = On(@"C:\repo");

        Assert.Throws<FileNotFoundException>(() => disk.Stat(OnLocalShare));
        Assert.Throws<FileNotFoundException>(() => disk.ReadAllBytes(OnLocalShare));
        Assert.Throws<FileNotFoundException>(() => disk.OpenForOneScan(OnLocalShare));
    }

    [Fact]
    public void FullPathMatchesTheRuntimeForAnOrdinaryPath()
    {
        var disk = On(_directory);

        Assert.Equal(Path.GetFullPath("a/../b.txt", _directory), disk.FullPath("a/../b.txt", _directory));
        Assert.Equal(Path.GetFullPath(_directory), disk.FullPath(_directory));
        Assert.Equal(_directory, disk.FullPath(string.Empty, _directory));
        Assert.Equal(
            Path.GetRelativePath(_directory, Path.Combine(_directory, "a", "b")),
            disk.RelativePath(_directory, Path.Combine(_directory, "a", "b")));
    }

    [Fact]
    public void ATildeInAnOrdinaryNameSurvives()
    {
        var path = Path.Combine(_directory, "a~b", "c.txt");
        var disk = On(_directory);

        Assert.Equal(path, disk.FullPath(path));
        Assert.Equal(Path.Combine("a~b", "c.txt"), disk.RelativePath(_directory, path));
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "These spellings mean something only on Windows.";
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "These spellings mean something only on Windows.";
        }
    }

    private sealed class LocalShareTheoryAttribute : TheoryAttribute
    {
        public LocalShareTheoryAttribute()
        {
            if (!ServesLocalShare) Skip = "This machine does not serve its administrative share of the temp drive.";
        }
    }

    private sealed class LocalShareFactAttribute : FactAttribute
    {
        public LocalShareFactAttribute()
        {
            if (!ServesLocalShare) Skip = "This machine does not serve its administrative share of the temp drive.";
        }
    }
}
