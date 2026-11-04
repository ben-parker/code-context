using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A type split across files has one node, stored under one file, while its declarations
/// contribute edges from every file. These tests hold the invariant that an incremental
/// update converges to the graph a fresh scan of the same files produces, through the real
/// worker, <c>AnalysisDeltaApplier</c> and store, whichever declaration was edited.
/// </summary>
public sealed class CSharpWorkerPartialTypeConvergenceTests : IAsyncLifetime
{
    private const string Support = """
        namespace Fx
        {
            public sealed class MarkerAttribute : System.Attribute { }
            public interface IThing { }
            public class Dep { }
            public static class Helper { public static int Run() => 1; }
        }
        """;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-csharp-partial-").FullName;
    private CSharpWorkerPipeline _pipeline = null!;
    private CSharpWorkerPipeline _reference = null!;
    private RecordingDeltaSink _recorder = null!;

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(
            _tempDir, inner => _recorder = new RecordingDeltaSink(inner), []);
        _reference = new CSharpWorkerPipeline(_tempDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        await _reference.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    private static string CanonicalDeclaration(string keyword, string method = "FromA") => $$"""
        namespace Fx
        {
            public partial {{keyword}} C
            {
                public void {{method}}() { }
                partial void Hook();
                public partial int Size { get; }
            }
        }
        """;

    /// <summary>Everything here is sourced at a symbol whose node lives in the canonical file.</summary>
    private static string ContributingDeclaration(string keyword) => $$"""
        namespace Fx
        {
            [Marker]
            public partial {{keyword}} C : IThing
            {
                private Dep _d;
                public void FromB() { Helper.Run(); }
                partial void Hook() { Helper.Run(); }
                public partial int Size { get => Helper.Run(); }
            }
        }
        """;

    /// <summary>
    /// Keeps the base list on purpose: both declarations emit that edge, so dropping it
    /// would change the canonical file's facts and hide whether the rest went stale.
    /// </summary>
    private static string BareDeclaration(string keyword) => $$"""
        namespace Fx
        {
            public partial {{keyword}} C : IThing
            {
                public void Other() { }
                partial void Hook() { }
                public partial int Size { get => 0; }
            }
        }
        """;

    private string PathOf(string name) => Path.Combine(_tempDir, name);

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = PathOf(name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private async Task IndexAsync(string keyword)
    {
        await WriteAsync("A.cs", CanonicalDeclaration(keyword));
        await WriteAsync("B.cs", ContributingDeclaration(keyword));
        await WriteAsync("Support.cs", Support);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);
    }

    private async Task EditAsync(string name, string content)
    {
        var path = PathOf(name);
        var existed = File.Exists(path);
        await File.WriteAllTextAsync(path, content);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            path, existed ? FileChangeType.Changed : FileChangeType.Created, CancellationToken.None);
    }

    private async Task DeleteAsync(string name)
    {
        var path = PathOf(name);
        File.Delete(path);
        await _pipeline.GraphUpdateService.ProcessFileChangeAsync(
            path, FileChangeType.Deleted, CancellationToken.None);
    }

    private async Task<List<CodeEdge>> EdgesAsync()
        => (await _pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync()).ToList();

    private async Task AssertMatchesFreshScanAsync(string step)
    {
        await _reference.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var expectedNodes = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await _reference.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        var actualNodes = CSharpWorkerIncrementalDeltaTests.NodeRecords(
            await _pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync());
        var expectedEdges = CSharpWorkerIncrementalDeltaTests.EdgeRecords(
            await _reference.RepositoryFactory.CreateEdgeRepository().GetAllAsync());
        var actualEdges = CSharpWorkerIncrementalDeltaTests.EdgeRecords(await EdgesAsync());

        Assert.True(
            expectedNodes.SequenceEqual(actualNodes) && expectedEdges.SequenceEqual(actualEdges),
            $"After '{step}' the graph differs from a fresh scan.\n"
            + $"Missing: {string.Join("\n  ", expectedNodes.Except(actualNodes).Concat(expectedEdges.Except(actualEdges)))}\n"
            + $"Stale: {string.Join("\n  ", actualNodes.Except(expectedNodes).Concat(actualEdges.Except(expectedEdges)))}");
    }

    private static bool Links(CodeEdge edge, string kind, string source, string target)
        => edge.Type == kind
           && edge.SourceId == CSharpWorkerPipeline.Id(source)
           && edge.TargetId == CSharpWorkerPipeline.Id(target);

    [Theory]
    [InlineData("class")]
    [InlineData("record")]
    [InlineData("struct")]
    [InlineData("record struct")]
    public async Task EditingTheCanonicalFile_KeepsEdgesContributedByTheOtherFile(string keyword)
    {
        await IndexAsync(keyword);

        await EditAsync("A.cs", CanonicalDeclaration(keyword, method: "FromARenamed"));

        var edges = await EdgesAsync();
        Assert.Contains(edges, e => Links(e, "REFERENCES", "Fx.C", "Fx.MarkerAttribute"));
        Assert.Contains(edges, e => Links(e, "REFERENCES", "Fx.C", "Fx.Dep"));
        Assert.Contains(edges, e => Links(e, "IMPLEMENTS", "Fx.C", "Fx.IThing"));
        Assert.Contains(edges, e => Links(e, "HAS_METHOD", "Fx.C", "Fx.C.FromB()"));
        Assert.Contains(edges, e => Links(e, "HAS_FIELD", "Fx.C", "Fx.C._d"));
        Assert.Contains(edges, e => Links(e, "CALLS", "Fx.C.Hook()", "Fx.Helper.Run()"));
        Assert.Contains(edges, e => Links(e, "CALLS", "Fx.C.Size", "Fx.Helper.Run()"));
        await AssertMatchesFreshScanAsync("edit of the canonical file");
    }

    [Theory]
    [InlineData("class")]
    [InlineData("record")]
    [InlineData("struct")]
    [InlineData("record struct")]
    public async Task RemovingFactsFromTheOtherFile_DropsTheirEdges(string keyword)
    {
        await IndexAsync(keyword);

        await EditAsync("B.cs", BareDeclaration(keyword));

        var edges = await EdgesAsync();
        Assert.DoesNotContain(edges, e => Links(e, "REFERENCES", "Fx.C", "Fx.MarkerAttribute"));
        Assert.DoesNotContain(edges, e => Links(e, "REFERENCES", "Fx.C", "Fx.Dep"));
        Assert.Contains(edges, e => Links(e, "IMPLEMENTS", "Fx.C", "Fx.IThing"));
        Assert.DoesNotContain(edges, e => Links(e, "HAS_METHOD", "Fx.C", "Fx.C.FromB()"));
        Assert.DoesNotContain(edges, e => Links(e, "HAS_FIELD", "Fx.C", "Fx.C._d"));
        Assert.DoesNotContain(edges, e => Links(e, "CALLS", "Fx.C.Hook()", "Fx.Helper.Run()"));
        Assert.DoesNotContain(edges, e => Links(e, "CALLS", "Fx.C.Size", "Fx.Helper.Run()"));
        await AssertMatchesFreshScanAsync("removal from the other file");
    }

    [Theory]
    [InlineData("class")]
    [InlineData("record")]
    [InlineData("struct")]
    [InlineData("record struct")]
    public async Task EverySeriesOfEdits_MatchesAFreshScanAtEachStep(string keyword)
    {
        await IndexAsync(keyword);
        await AssertMatchesFreshScanAsync("initial scan");

        var moved = ContributingDeclaration(keyword).Replace("[Marker]", string.Empty);
        var steps = new (string Name, Func<Task> Apply)[]
        {
            ("edit canonical file", () => EditAsync("A.cs", CanonicalDeclaration(keyword, "Renamed"))),
            ("strip the other file", () => EditAsync("B.cs", BareDeclaration(keyword))),
            ("drop the base list", () => EditAsync("B.cs", BareDeclaration(keyword).Replace(" : IThing", string.Empty))),
            ("restore the other file", () => EditAsync("B.cs", ContributingDeclaration(keyword))),
            ("shift the other file's lines", () => EditAsync("B.cs", "\n\n" + ContributingDeclaration(keyword))),
            ("move the attribute between files", async () =>
            {
                await EditAsync("B.cs", moved);
                await EditAsync("A.cs", CanonicalDeclaration(keyword).Replace("public partial", "[Marker] public partial"));
            }),
            ("add a new canonical file", () => EditAsync("0.cs",
                $"namespace Fx {{ public partial {keyword} C {{ public void FromZero() {{ Helper.Run(); }} }} }}")),
            ("drop the new canonical file", () => DeleteAsync("0.cs")),
            ("empty the canonical file", () => EditAsync("A.cs", "namespace Fx { }")),
            ("refill the canonical file", () => EditAsync("A.cs", CanonicalDeclaration(keyword))),
            ("delete the other file", () => DeleteAsync("B.cs")),
            ("recreate the other file", () => EditAsync("B.cs", ContributingDeclaration(keyword))),
            ("delete the canonical file", () => DeleteAsync("A.cs")),
        };

        foreach (var (name, apply) in steps)
        {
            await apply();
            await AssertMatchesFreshScanAsync(name);
        }
    }

    /// <summary>
    /// In the global namespace an unresolved base keeps the display name, and so the id, of
    /// the type it named. The edge therefore survives its target's deletion unchanged,
    /// while the store drops it as dangling: nothing but the target's presence differs.
    /// </summary>
    [Theory]
    [InlineData("class", "public class Root { }")]
    [InlineData("record", "public record Root;")]
    [InlineData("struct", "public interface Root { }")]
    [InlineData("record struct", "public interface Root { }")]
    public async Task DeletingAndRestoringABaseType_MatchesAFreshScan(string keyword, string root)
    {
        await WriteAsync("A.cs", $"public partial {keyword} G : Root {{ }}");
        await WriteAsync("B.cs", $"public partial {keyword} G {{ public void M() {{ }} }}");
        await WriteAsync("Root.cs", root);
        await _pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        await DeleteAsync("Root.cs");
        await AssertMatchesFreshScanAsync("delete the base type");

        await EditAsync("Root.cs", root);
        await AssertMatchesFreshScanAsync("restore the base type");
    }

    [Fact]
    public async Task EditingOnlyAMethodBody_StaysScopedToThatFile()
    {
        await IndexAsync("class");
        _recorder.Clear();

        // FromB's node lives in B.cs and so do the edges sourced at it, so nothing about
        // the type changes and A.cs has no reason to be re-emitted.
        await EditAsync("B.cs", ContributingDeclaration("class")
            .Replace("public void FromB() { Helper.Run(); }", "public void FromB() {        }"));

        Assert.Equal(
            [PathOf("B.cs")],
            _recorder.Deltas.SelectMany(d => d.ReplacesFiles).Distinct());
        await AssertMatchesFreshScanAsync("method body edit");
    }
}
