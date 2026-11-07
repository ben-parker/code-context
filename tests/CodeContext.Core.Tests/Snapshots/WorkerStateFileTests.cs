using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The worker state file names (plan phase 5): the host makes them from checked parts, and a name read from a
/// snapshot becomes a path only through this pattern, joined to the cache directory.
/// </summary>
public class WorkerStateFileTests
{
    private const string StateId = "5d0c9a1e7b3f4c0e9a6d2f8b1c4e7a90";

    [Fact]
    public void FormatName_RoundTripsThroughTryParseName()
    {
        var name = WorkerStateFile.FormatName("csharp", 42, StateId);
        Assert.Equal("worker-csharp.0000000042.5d0c9a1e.state", name);
        Assert.True(WorkerStateFile.TryParseName(name, out var parser, out var sequence));
        Assert.Equal("csharp", parser);
        Assert.Equal(42UL, sequence);
        Assert.True(WorkerStateFile.TryParseTempName(WorkerStateFile.TempNameFor(name!), out var tempSequence));
        Assert.Equal(42UL, tempSequence);
    }

    [Theory]
    [InlineData("CSharp")]
    [InlineData("c.sharp")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("c:")]
    [InlineData("")]
    public void ParserIdsThatCouldFormAPath_TakeNoPart(string parserId)
    {
        Assert.False(WorkerStateFile.IsParserIdUsable(parserId));
        Assert.Null(WorkerStateFile.FormatName(parserId, 1, StateId));
    }

    [Theory]
    [InlineData("worker-csharp.0000000042.5d0c9a1e.state.tmp")]
    [InlineData("worker-csharp.42.5d0c9a1e.state")]
    [InlineData("worker-csharp.0000000042.5D0C9A1E.state")]
    [InlineData("worker-..0000000042.5d0c9a1e.state")]
    [InlineData("worker-../x.0000000042.5d0c9a1e.state")]
    [InlineData("worker-c:\\x.0000000042.5d0c9a1e.state")]
    [InlineData("..\\worker-csharp.0000000042.5d0c9a1e.state")]
    [InlineData("C:\\worker-csharp.0000000042.5d0c9a1e.state")]
    [InlineData("/worker-csharp.0000000042.5d0c9a1e.state")]
    [InlineData("graph.0000000042.5d0c9a1e.ccsnap")]
    [InlineData("worker-.0000000042.5d0c9a1e.state")]
    public void NamesThisCodeDoesNotMake_AreNotStateFiles(string name)
        => Assert.False(WorkerStateFile.TryParseName(name, out _, out _));

    [Fact]
    public void IdsAndHashes_HaveTheirForms()
    {
        Assert.True(WorkerStateFile.IsStateId(WorkerStateFile.NewStateId()));
        Assert.NotEqual(WorkerStateFile.NewStateId(), WorkerStateFile.NewStateId());
        Assert.False(WorkerStateFile.IsStateId(StateId.ToUpperInvariant()));
        Assert.False(WorkerStateFile.IsStateId(StateId[..31]));
        Assert.True(WorkerStateFile.IsSha256(new string('a', 64)));
        Assert.False(WorkerStateFile.IsSha256(new string('g', 64)));
        Assert.False(WorkerStateFile.IsSha256(null));
    }
}

/// <summary>
/// The two path guards of the cache directory for worker state files (plan phase 5): a name becomes a path only
/// when it is one this code makes, for the parser asking, and names a regular file in the cache directory; and a
/// state file is deleted only under such a name, never through a link or a directory.
/// </summary>
public sealed class CacheDirectoryWorkerStateTests : IDisposable
{
    private const string Name = "worker-csharp.0000000001.aaaaaaaa.state";
    private readonly string _root = Directory.CreateTempSubdirectory("cc-state-guard-").FullName;
    private readonly CacheDirectory _directory;

    public CacheDirectoryWorkerStateTests()
    {
        _directory = CacheDirectory.Open(_root, "default", out var refusal)!;
        Assert.Null(refusal);
    }

    public void Dispose()
    {
        // Links first, so the recursive delete never follows one.
        foreach (var entry in Directory.EnumerateFileSystemEntries(_directory.WorkspacePath))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0) continue;
            if (Directory.Exists(entry)) Directory.Delete(entry);
            else File.Delete(entry);
        }
        Directory.Delete(_root, recursive: true);
    }

    private string Plant(string name, string content = "state")
    {
        var path = _directory.PathOf(name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ExistingWorkerStateFile_IsTheRegularFileOfTheParsersOwnName()
    {
        var path = Plant(Name, "12345");
        var found = _directory.ExistingWorkerStateFile("csharp", Name);
        Assert.Equal((path, 5L), found);

        // Another parser's state file is never handed to this one.
        Assert.Null(_directory.ExistingWorkerStateFile("typescript", Name));
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", "worker-typescript.0000000001.aaaaaaaa.state"));
        // Nothing there.
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", "worker-csharp.0000000002.aaaaaaaa.state"));
    }

    [Theory]
    [InlineData("..\\" + Name)]
    [InlineData("../" + Name)]
    [InlineData("sub\\" + Name)]
    [InlineData("sub/" + Name)]
    [InlineData("worker-csharp.0000000001.AAAAAAAA.state")]
    [InlineData("worker-csharp.000000001.aaaaaaaa.state")]
    [InlineData("worker-csharp.00000000001.aaaaaaaa.state")]
    [InlineData("worker-csharp.0000000001.aaaaaaa.state")]
    [InlineData(Name + ":stream")]
    [InlineData("worker-c:sharp.0000000001.aaaaaaaa.state")]
    [InlineData("C:" + Name)]
    [InlineData("worker-csharp.0000000001.aaaaaaaa.state.tmp")]
    public void ExistingWorkerStateFile_RefusesEveryNameThisCodeDoesNotMake(string name)
    {
        // A valid file sits beside, inside and above, so a name that escaped the pattern would find one.
        Plant(Name);
        Directory.CreateDirectory(_directory.PathOf("sub"));
        Plant(Path.Combine("sub", Name));
        File.WriteAllText(Path.Combine(_directory.WorkspacePath, "..", Name), "outside");
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", name));
    }

    [Fact]
    public void ExistingWorkerStateFile_RefusesARootedPath()
    {
        var path = Plant(Name);
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", path));
    }

    [Fact]
    public void ExistingWorkerStateFile_RefusesADirectoryOfAValidName()
    {
        Directory.CreateDirectory(_directory.PathOf(Name));
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", Name));
    }

    [Fact]
    public void ExistingWorkerStateFile_RefusesADirectoryLinkOfAValidName()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        if (!SnapshotServiceTests.TryCreateDirectoryLink(_directory.PathOf(Name), target)) return;
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", Name));
    }

    [Fact]
    public void ExistingWorkerStateFile_RefusesAFileLinkOfAValidName()
    {
        var target = Path.Combine(_root, "target.state");
        File.WriteAllText(target, "elsewhere");
        try
        {
            File.CreateSymbolicLink(_directory.PathOf(Name), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file symbolic link needs a privilege on Windows (Developer Mode or elevation); without it this case
            // cannot be set up here, and the link check is covered on the platforms where it can.
            return;
        }
        Assert.Null(_directory.ExistingWorkerStateFile("csharp", Name));
    }

    [Fact]
    public void DeleteWorkerStateQuietly_DeletesTheStateFileAndItsTemp_OfAValidName()
    {
        var state = Plant(Name);
        var temp = Plant(WorkerStateFile.TempNameFor(Name));
        var other = Plant("worker-csharp.0000000002.bbbbbbbb.state");
        _directory.DeleteWorkerStateQuietly(Name);
        Assert.False(File.Exists(state));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData("graph.0000000001.aaaaaaaa.ccsnap")]
    [InlineData("worker-csharp.0000000001.AAAAAAAA.state")]
    [InlineData("..\\" + Name)]
    [InlineData("../" + Name)]
    [InlineData("sub\\" + Name)]
    public void DeleteWorkerStateQuietly_DeletesNothingUnderANameThisCodeDoesNotMake(string name)
    {
        Directory.CreateDirectory(_directory.PathOf("sub"));
        var planted = new[]
        {
            Plant("graph.0000000001.aaaaaaaa.ccsnap"),
            Plant("worker-csharp.0000000001.AAAAAAAA.state"),
            Plant(Path.Combine("sub", Name)),
        };
        var outside = Path.Combine(_directory.WorkspacePath, "..", Name);
        File.WriteAllText(outside, "outside");
        _directory.DeleteWorkerStateQuietly(name);
        Assert.All(planted, path => Assert.True(File.Exists(path), path));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteWorkerStateQuietly_LeavesADirectoryOrALinkOfAValidName()
    {
        // A directory of the name, with a file inside it.
        Directory.CreateDirectory(_directory.PathOf(Name));
        File.WriteAllText(Path.Combine(_directory.PathOf(Name), "inside"), "x");
        _directory.DeleteWorkerStateQuietly(Name);
        Assert.True(File.Exists(Path.Combine(_directory.PathOf(Name), "inside")));
        Directory.Delete(_directory.PathOf(Name), recursive: true);

        // A link of the name (a junction where a symbolic link needs a privilege), and what it points to.
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "kept"), "x");
        if (!SnapshotServiceTests.TryCreateDirectoryLink(_directory.PathOf(Name), target)) return;
        _directory.DeleteWorkerStateQuietly(Name);
        Assert.True(Directory.Exists(_directory.PathOf(Name)));
        Assert.True(File.Exists(Path.Combine(target, "kept")));
    }
}
