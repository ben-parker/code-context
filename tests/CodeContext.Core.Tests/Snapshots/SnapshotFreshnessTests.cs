using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The stat check a one-shot query makes before answering from a snapshot: real files, real kernel times, a capture
/// time taken between building the tree and changing it (tolerance zero, so the test need not wait two seconds).
/// </summary>
public sealed class SnapshotFreshnessTests : IDisposable
{
    private static readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts" };
    private readonly string _root = Directory.CreateTempSubdirectory("cc-fresh-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PathOf(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Write(string relative, string content = "class A { }")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A small repository, then a capture time strictly after every time in it.</summary>
    private DateTime BuildAndCapture()
    {
        Write("src/a.cs");
        Write("src/b.cs");
        Write("web/app.ts");
        Write("README.md", "readme");
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs");
        Write("src/Lib.csproj", "<Project />");
        Write("src/obj/project.assets.json", "{}");
        Write("bin/out.cs");
        Write("node_modules/pkg/index.ts");
        Write(".git/HEAD", "ref");
        Write(".codecontext/cache/default/graph.0000000001.00000000.ccsnap", "x");
        Thread.Sleep(50);
        var captured = DateTime.UtcNow;
        Thread.Sleep(50);
        return captured;
    }

    private SnapshotFreshnessResult Check(DateTime captured, TimeSpan? tolerance = null)
        => SnapshotFreshness.Check(
            new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = _root })),
            _root,
            captured,
            Claimed.Contains,
            (path, included) => included && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
            ["project.assets.json"],
            tolerance ?? TimeSpan.Zero);

    [Fact]
    public void NothingTouched_IsUnchanged()
    {
        var captured = BuildAndCapture();

        var result = Check(captured);

        Assert.True(result.Unchanged, result.FirstMoved);
        Assert.Null(result.FirstMoved);
        // Root, src, src/obj is excluded, web; a.cs, b.cs, app.ts, .gitignore, Lib.csproj, project.assets.json.
        Assert.True(result.Checked >= 9, $"checked {result.Checked}");
    }

    [Fact]
    public void EditedClaimedFile_HasMoved()
    {
        var captured = BuildAndCapture();
        File.AppendAllText(PathOf("src/a.cs"), "// edit");

        var result = Check(captured);

        Assert.False(result.Unchanged);
        Assert.Equal("src/a.cs", result.FirstMoved);
    }

    [Fact]
    public void AddedFile_HasMoved()
    {
        var captured = BuildAndCapture();
        Write("src/c.cs");

        Assert.False(Check(captured).Unchanged);
    }

    [Fact]
    public void AddedFileOfAnUnclaimedKind_StillMovesItsFolder()
    {
        // Conservative: the folder's time cannot tell what kind of entry was added.
        var captured = BuildAndCapture();
        Write("src/notes.txt", "n");

        Assert.Equal("src", Check(captured).FirstMoved);
    }

    [Fact]
    public void DeletedFile_HasMovedItsFolder()
    {
        var captured = BuildAndCapture();
        File.Delete(PathOf("src/b.cs"));

        Assert.Equal("src", Check(captured).FirstMoved);
    }

    [Fact]
    public void RenamedFile_KeepingItsTimes_HasMovedItsFolder()
    {
        var captured = BuildAndCapture();
        File.Move(PathOf("web/app.ts"), PathOf("web/main.ts"));

        Assert.Equal("web", Check(captured).FirstMoved);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("ignored.cs")]
    [InlineData("bin/out.cs")]
    [InlineData("node_modules/pkg/index.ts")]
    [InlineData(".git/HEAD")]
    [InlineData(".codecontext/cache/default/graph.0000000001.00000000.ccsnap")]
    public void InPlaceEditOfAFileNoScanReads_DoesNotCount(string relative)
    {
        var captured = BuildAndCapture();
        File.AppendAllText(PathOf(relative), "changed");

        var result = Check(captured);

        Assert.True(result.Unchanged, result.FirstMoved);
    }

    [Fact]
    public void NewFilesUnderMandatoryExclusions_DoNotCount()
    {
        var captured = BuildAndCapture();
        Write("bin/Debug/new.cs");
        Write("node_modules/pkg/new.ts");
        Write(".git/objects/ab/cdef", "blob");
        Write(".codecontext/cache/default/graph.0000000002.00000000.ccsnap", "y");
        Write("src/obj/Debug/generated.cs");

        var result = Check(captured);

        Assert.True(result.Unchanged, result.FirstMoved);
    }

    [Fact]
    public void GitignoreEdit_HasMoved()
    {
        var captured = BuildAndCapture();
        File.AppendAllText(PathOf(".gitignore"), "web/\n");

        Assert.Equal(".gitignore", Check(captured).FirstMoved);
    }

    [Fact]
    public void ProjectFileEdit_HasMoved()
    {
        var captured = BuildAndCapture();
        File.WriteAllText(PathOf("src/Lib.csproj"), "<Project Sdk=\"x\" />");

        Assert.Equal("src/Lib.csproj", Check(captured).FirstMoved);
    }

    [Fact]
    public void RestoreBesideAProject_HasMoved_ThoughObjIsNotScanned()
    {
        var captured = BuildAndCapture();
        File.WriteAllText(PathOf("src/obj/project.assets.json"), "{\"restored\":true}");

        Assert.Equal("src/obj/project.assets.json", Check(captured).FirstMoved);
    }

    [Fact]
    public void ExplicitOldModificationTime_OnAnEditedFile_IsStillSeenThroughItsCreationTime_WhenTheSystemKeepsOne()
    {
        var captured = BuildAndCapture();
        // A file copied or unpacked with its original time: new on disk, old by modification time.
        var path = Write("web/copied.ts");
        File.SetLastWriteTimeUtc(path, captured - TimeSpan.FromDays(30));
        Directory.SetLastWriteTimeUtc(PathOf("web"), captured - TimeSpan.FromDays(30));

        var result = Check(captured);

        if (File.GetCreationTimeUtc(path) > captured) Assert.Equal("web/copied.ts", result.FirstMoved);
        else Assert.True(result.Unchanged, "no creation time on this file system: the documented blind spot");
    }

    [Fact]
    public void DefaultTolerance_CountsATimeJustBeforeTheCapture()
    {
        var captured = BuildAndCapture() + TimeSpan.FromSeconds(1);
        // Every time in the tree is now within a second before the capture: on a file system that keeps
        // whole seconds that is indistinguishable from just after it.
        Assert.False(Check(captured, SnapshotFreshness.Tolerance).Unchanged);
        Assert.True(Check(captured + SnapshotFreshness.Tolerance).Unchanged);
    }

    [Fact]
    public void FreshlyCreatedRoot_Moves()
    {
        var captured = BuildAndCapture();
        Write("top.cs");

        Assert.False(Check(captured).Unchanged);
    }
}
