using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The stat check a one-shot query makes before answering from a snapshot: real files, real kernel times, a capture
/// time taken between building the tree and changing it (tolerance zero, so the test need not wait two seconds). What
/// is checked besides the walked tree is exactly what the workers reported (<see cref="WorkerInputs"/>), never a guess.
/// </summary>
public sealed class SnapshotFreshnessTests : IDisposable
{
    private static readonly HashSet<string> Claimed = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".ts" };
    private readonly string _base = Directory.CreateTempSubdirectory("cc-fresh-").FullName;
    private string Root => Path.Combine(_base, "repo");

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private string Write(string relative, string content = "class A { }")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A small repository, then a capture time strictly after every time in it.</summary>
    private DateTime BuildAndCapture(bool withCacheDirectory = true)
    {
        Build(withCacheDirectory);
        return Capture();
    }

    private static DateTime Capture()
    {
        Thread.Sleep(50);
        var captured = DateTime.UtcNow;
        Thread.Sleep(50);
        return captured;
    }

    private void Build(bool withCacheDirectory = true)
    {
        Write("src/a.cs");
        Write("src/b.cs");
        Write("web/app.ts");
        Write("web/tsconfig.json", "{}");
        Write("README.md", "readme");
        Write(".gitignore", "ignored.cs\n");
        Write("ignored.cs");
        Write("src/Lib.csproj", "<Project />");
        Write("src/obj/project.assets.json", "{}");
        Write("bin/out.cs");
        Write("node_modules/pkg/index.ts");
        Write(".git/HEAD", "ref");
        if (withCacheDirectory) Write(".codecontext/cache/default/graph.0000000001.00000000.ccsnap", "x");
        File.WriteAllText(Path.Combine(_base, "Directory.Build.props"), "<Project />");
    }

    /// <summary>What the two workers of this tree would have reported: project inputs, a config, a file outside the root.</summary>
    private WorkerInputs Reported() => new(
        [PathOf("src/Lib.csproj"), PathOf("src/obj/project.assets.json"), PathOf("web/tsconfig.json"), Path.Combine(_base, "Directory.Build.props")],
        [PathOf("src/Directory.Build.props"), PathOf("Directory.Packages.props"), Path.Combine(_base, "global.json")]);

    private SnapshotFreshnessResult Check(DateTime captured, TimeSpan? tolerance = null, WorkerInputs? inputs = null, long rootTicks = 0, DateTime? now = null)
        => SnapshotFreshness.Check(
            new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = Root })),
            Root,
            captured,
            Claimed.Contains,
            [inputs ?? Reported()],
            rootTicks,
            now,
            tolerance ?? TimeSpan.Zero);

    [Fact]
    public void NothingTouched_IsUnchanged()
    {
        var captured = BuildAndCapture();

        var result = Check(captured);

        Assert.True(result.Unchanged, result.FirstMoved);
        Assert.Null(result.FirstMoved);
        // Root, src, web; a.cs, b.cs, app.ts, .gitignore; four reported files and three absent paths.
        Assert.True(result.Checked >= 14, $"checked {result.Checked}");
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
    public void InPlaceEditOfAFileNothingReads_DoesNotCount(string relative)
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

    [Theory]
    [InlineData("src/Lib.csproj")]
    [InlineData("src/obj/project.assets.json")]
    [InlineData("web/tsconfig.json")]
    public void ReportedInputEditedInPlace_HasMoved(string relative)
    {
        var captured = BuildAndCapture();
        File.WriteAllText(PathOf(relative), "{\"changed\":true}");

        Assert.Equal(relative, Check(captured).FirstMoved);
    }

    [Fact]
    public void ReportedInputOutsideTheRoot_EditedInPlace_HasMoved()
    {
        var captured = BuildAndCapture();
        var props = Path.Combine(_base, "Directory.Build.props");
        File.WriteAllText(props, "<Project><PropertyGroup><DefineConstants>B</DefineConstants></PropertyGroup></Project>");

        var result = Check(captured);

        Assert.Equal(props, result.FirstMoved);
        Assert.Contains("changed after the saved index", result.Why);
    }

    [Fact]
    public void ReportedInputDeleted_HasMoved()
    {
        var captured = BuildAndCapture();
        File.Delete(Path.Combine(_base, "Directory.Build.props"));

        Assert.Contains("is gone", Check(captured).Why);
    }

    [Theory]
    [InlineData("src/Directory.Build.props")]
    [InlineData("Directory.Packages.props")]
    public void AbsentPathThatAppears_HasMoved(string relative)
    {
        var captured = BuildAndCapture();
        Write(relative, "<Project />");
        // A folder's own time would say so as well; here only the absent path can.
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(PathOf(relative))!, captured - TimeSpan.FromDays(1));

        var result = Check(captured, inputs: new WorkerInputs([], [PathOf(relative)]), now: DateTime.UtcNow);

        if (FileChangeTime.ReadsChangeTime) Assert.NotNull(result.FirstMoved);
        else Assert.Equal(relative, result.FirstMoved);
    }

    [Fact]
    public void AbsentPathOutsideTheRoot_ThatAppears_HasMoved()
    {
        var captured = BuildAndCapture();
        var global = Path.Combine(_base, "global.json");
        File.WriteAllText(global, "{}");

        var result = Check(captured);

        Assert.Equal(global, result.FirstMoved);
        Assert.Contains("exists now", result.Why);
    }

    [Fact]
    public void AFileNoWorkerReported_IsNotGuessedAt()
    {
        // project.assets.json in obj/ is checked only because the C# worker reports reading it.
        var captured = BuildAndCapture();
        File.WriteAllText(PathOf("src/obj/project.assets.json"), "{\"restored\":true}");

        var result = Check(captured, inputs: new WorkerInputs([], []));

        Assert.True(result.Unchanged, result.FirstMoved);
    }

    [Theory]
    [InlineData("touch-d")]
    [InlineData("copy-with-old-times")]
    public void ContentWithAnOlderTimeSetAfterwards_IsSeenThroughTheChangeTime_WhereThePlatformKeepsOne(string how)
    {
        var captured = BuildAndCapture();
        var old = captured - TimeSpan.FromDays(30);
        var path = PathOf("web/app.ts");
        if (how == "touch-d")
        {
            // Rewritten in place, then its time set back (touch -d, cp -p or rsync -t onto it).
            File.WriteAllText(path, "export const changed = 1;");
            File.SetLastWriteTimeUtc(path, old);
        }
        else
        {
            // Unpacked or copied with its original times (tar -x, unzip, rsync -a), its folder's time restored too.
            File.Delete(path);
            File.WriteAllText(path, "export const changed = 2;");
            File.SetLastWriteTimeUtc(path, old);
            File.SetCreationTimeUtc(path, old);
            Directory.SetLastWriteTimeUtc(PathOf("web"), old);
        }

        var result = Check(captured);

        if (FileChangeTime.ReadsChangeTime) Assert.False(result.Unchanged, "the status change time moves whatever the tool sets");
        else if (File.GetCreationTimeUtc(path) > captured) Assert.False(result.Unchanged);
        else Assert.True(result.Unchanged, "no change time on this system: the documented blind spot");
    }

    [Fact]
    public void AModificationTimeInTheFuture_IsIgnoredWhereTheChangeTimeIsRead_AndFlaggedElsewhere()
    {
        Build();
        File.SetLastWriteTimeUtc(PathOf("src/a.cs"), DateTime.UtcNow + TimeSpan.FromDays(365));
        var captured = Capture();

        var result = Check(captured, inputs: new WorkerInputs([], []));

        if (FileChangeTime.ReadsChangeTime)
        {
            Assert.True(result.Unchanged, result.FirstMoved);
        }
        else
        {
            Assert.Equal("src/a.cs", result.FirstMoved);
            Assert.True(result.InFuture);
        }
    }

    [Fact]
    public void DefaultTolerance_CountsATimeJustBeforeTheCapture()
    {
        var captured = BuildAndCapture() + TimeSpan.FromSeconds(1);
        // Every time in the tree is now within a second before the capture: on a file system that keeps
        // whole seconds that is indistinguishable from just after it.
        Assert.False(Check(captured, SnapshotFreshness.Tolerance).Unchanged);
        Assert.True(Check(captured + SnapshotFreshness.Tolerance, SnapshotFreshness.Tolerance, now: captured + TimeSpan.FromSeconds(3)).Unchanged);
    }

    [Fact]
    public void FileAddedAtTheRoot_Moves()
    {
        var captured = BuildAndCapture();
        Write("top.cs");

        Assert.False(Check(captured).Unchanged);
    }

    [Fact]
    public void TheRootTimeItsWriterRecorded_IsNotAChange_AndAnyOtherIs()
    {
        // A small repository's first snapshot: making .codecontext/ moves the root's time just before the capture.
        var captured = BuildAndCapture(withCacheDirectory: false);
        Directory.CreateDirectory(PathOf(".codecontext/cache/default"));
        Assert.True(FileChangeTime.TryRead(Root, out var recorded));

        Assert.Equal(".", Check(captured).FirstMoved);
        Assert.True(Check(captured, rootTicks: recorded.Ticks).Unchanged);
        Thread.Sleep(20);
        Write("later.txt", "x");
        Assert.Equal(".", Check(captured, rootTicks: recorded.Ticks).FirstMoved);
    }
}
