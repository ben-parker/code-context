using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The per-shard annotation (parser version, coverage) is part of the store's commit: recorded by the
/// scoped commit that carries it, staged and committed or rolled back with a reconciliation, cleared by a
/// full replacement, kept by foreign sweeps and prune, and latched with the shards in one lock hold.
/// </summary>
public class ShardAnnotationTests
{
    private static readonly ShardKey A = new("fake", "ws-1");
    private static readonly ShardKey B = new("other", "ws-1");

    private static List<ProjectCoverage> Coverage(string name) => [new(name, $"src/{name}.fakeproj", ProjectCoverageStates.Complete, 1)];

    private static CodeNode Node(ShardKey key, string name, string file = "a.fake") => new()
    {
        Id = $"{key.ParserId}:{key.WorkspaceId}:{name}",
        Identifier = $"{key.ParserId}:{name}",
        Name = name,
        Type = "Class",
        FilePath = file,
        Metadata = new Dictionary<string, string> { ["parserId"] = key.ParserId, ["workspaceId"] = key.WorkspaceId },
    };

    private static CommitScope Scope(ShardKey key, ShardAnnotation? annotation, IReadOnlyCollection<string>? files = null)
        => new(key.ParserId, key.WorkspaceId, files) { Annotation = annotation };

    private static void Commit(InMemoryDatabase database, ShardKey key, IReadOnlyList<CodeNode> nodes, ShardAnnotation? annotation,
        IReadOnlyList<CodeEdge>? edges = null)
        => Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, nodes, edges ?? [], Scope(key, annotation)));

    private static (IGraphShard Shard, ShardAnnotation? Annotation) Latched(InMemoryDatabase database, ShardKey key)
    {
        Assert.True(database.TryLatchCommitted(out var shards, out var annotations, out _));
        annotations.TryGetValue(key, out var annotation);
        return (shards[key], annotation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reconciliation_CommitsOrRollsBackTheAnnotationWithTheShard(bool commit)
    {
        var database = new InMemoryDatabase();
        var c1 = Coverage("One");
        Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", c1));
        var (oldShard, _) = Latched(database, A);

        database.BeginReconciliation();
        var c2 = Coverage("Two");
        Commit(database, A, [Node(A, "V2")], new ShardAnnotation("v2", c2));
        if (commit) database.CommitReconciliation();
        else database.RollbackReconciliation();

        var (shard, annotation) = Latched(database, A);
        if (commit)
        {
            Assert.Equal("V2", shard.GetAllNodes().Single().Name);
            Assert.Equal("v2", annotation!.ParserVersion);
            Assert.Same(c2, annotation.Coverage);
        }
        else
        {
            Assert.Same(oldShard, shard);
            Assert.Equal("v1", annotation!.ParserVersion);
            Assert.Same(c1, annotation.Coverage);
        }
    }

    [Fact]
    public async Task CaptureAfterARolledBackRescan_PairsTheOldShardWithItsOwnAnnotation()
    {
        var database = new InMemoryDatabase();
        var c1 = Coverage("One");
        Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", c1));
        database.BeginReconciliation();
        Commit(database, A, [Node(A, "V2")], new ShardAnnotation("v2", Coverage("Two")));
        database.RollbackReconciliation();

        await using var harness = new SnapshotServiceHarness(database: database);
        var capture = harness.Service.Capture()!;
        var shard = capture.View.Shards.Single();
        Assert.Equal("V1", shard.Shard.GetAllNodes().Single().Name);
        Assert.Equal("v1", shard.Annotation!.ParserVersion);
        Assert.Same(c1, shard.Annotation.Coverage);
    }

    [Fact]
    public async Task AnalysisDeltaApplier_RecordsTheDeltasParserVersionAndCoverage_InTheCommit()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var registry = new AnalysisCoverageRegistry();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance, registry);

        AnalysisDelta Delta(long generation, string version, IReadOnlyList<ProjectCoverage>? coverage) => new(
            ParserId: "fake", ParserVersion: version, WorkspaceId: "ws-1", Generation: generation, RequestId: generation,
            ReplacesWorkspace: true, ReplacesFiles: [], Nodes: [new ProtocolNode($"fake:ws-1:N{generation}", $"N{generation}", $"N{generation}", "class", "fake", "a.fake")],
            Edges: [], IsLastForRequest: true, Coverage: coverage);

        var first = Coverage("First");
        Assert.True(await applier.ApplyAsync(Delta(1, "1.0", first)));
        var (_, annotation) = Latched(factory.Database, A);
        Assert.Equal("1.0", annotation!.ParserVersion);
        Assert.Same(first, annotation.Coverage);

        var second = Coverage("Second");
        Assert.True(await applier.ApplyAsync(Delta(2, "2.0", second)));
        (_, annotation) = Latched(factory.Database, A);
        Assert.Equal("2.0", annotation!.ParserVersion);
        Assert.Same(second, annotation.Coverage);

        // A delta without coverage keeps the recorded coverage, as the coverage registry keeps its report.
        Assert.True(await applier.ApplyAsync(Delta(3, "3.0", null)));
        (_, annotation) = Latched(factory.Database, A);
        Assert.Equal("3.0", annotation!.ParserVersion);
        Assert.Same(second, annotation.Coverage);
        Assert.Equal("Second", registry.GetProjects().Single().Name);

        // A delta committed inside a reconciliation that rolls back leaves no trace in the annotation.
        store.BeginReconciliation();
        Assert.True(await applier.ApplyAsync(Delta(4, "4.0", Coverage("Fourth"))));
        store.RollbackReconciliation();
        (_, annotation) = Latched(factory.Database, A);
        Assert.Equal("3.0", annotation!.ParserVersion);
        Assert.Same(second, annotation.Coverage);
    }

    /// <summary>The host's wiring: an applier reporting into the registry that reads through the store.</summary>
    private static (InMemoryRepositoryFactory Factory, IGenerationalGraphStore Store, IAnalysisCoverageRegistry Registry, AnalysisDeltaApplier Applier) Host()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var registry = AnalysisCoverageRegistry.ForStore(factory);
        return (factory, store, registry, new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance, registry));
    }

    private static AnalysisDelta CoverageDelta(long generation, IReadOnlyList<ProjectCoverage>? coverage) => new(
        ParserId: "fake", ParserVersion: "1.0", WorkspaceId: "ws-1", Generation: generation, RequestId: generation,
        ReplacesWorkspace: true, ReplacesFiles: [], Nodes: [new ProtocolNode($"fake:ws-1:N{generation}", $"N{generation}", $"N{generation}", "class", "fake", "a.fake")],
        Edges: [], IsLastForRequest: true, Coverage: coverage);

    private static string Names(IAnalysisCoverageRegistry registry) => string.Join(",", registry.GetProjects().Select(project => project.Name));

    /// <summary>A store loaded from a snapshot whose shard A was built with coverage "Snapshot".</summary>
    private static void LoadSnapshot(InMemoryDatabase database)
    {
        var source = new InMemoryDatabase();
        Commit(source, A, [Node(A, "Loaded")], null);
        database.PublishLoadedShards(source.LatchPublished().Shards, [KeyValuePair.Create(A, new ShardAnnotation("0.9", Coverage("Snapshot")))]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_CoverageServedIsTheSnapshots_UntilTheScanCommits(bool commits)
    {
        var (factory, store, registry, applier) = Host();
        LoadSnapshot(factory.Database);
        Assert.Equal("Snapshot", Names(registry));

        store.BeginReconciliation();
        Assert.True(await applier.ApplyAsync(CoverageDelta(1, Coverage("Scan"))));
        // Staged: the facts served are still the snapshot's, and so is the coverage.
        Assert.Equal("Snapshot", Names(registry));
        Assert.Equal("Loaded", factory.Database.EnumerateNodes().Single().Name);

        if (commits) store.CommitReconciliation();
        else store.RollbackReconciliation();
        Assert.Equal(commits ? "Scan" : "Snapshot", Names(registry));
    }

    [Fact]
    public async Task RolledBackRescan_WithoutACache_RestoresThePreviousCoverage()
    {
        var (_, store, registry, applier) = Host();
        Assert.True(await applier.ApplyAsync(CoverageDelta(1, Coverage("One"))));
        Assert.Equal("One", Names(registry));

        store.BeginReconciliation();
        Assert.True(await applier.ApplyAsync(CoverageDelta(2, Coverage("Two"))));
        Assert.Equal("One", Names(registry));
        store.RollbackReconciliation();
        Assert.Equal("One", Names(registry));
    }

    [Fact]
    public async Task CommitOutsideAReconciliation_ShowsItsCoverageAtOnce()
    {
        // A watcher batch or a single-file refresh: as before, its coverage is served once it commits, and a
        // delta without coverage keeps the recorded one.
        var (_, _, registry, applier) = Host();
        Assert.True(await applier.ApplyAsync(CoverageDelta(1, Coverage("One"))));
        Assert.Equal("One", Names(registry));
        Assert.True(await applier.ApplyAsync(CoverageDelta(2, Coverage("Two"))));
        Assert.Equal("Two", Names(registry));
        Assert.True(await applier.ApplyAsync(CoverageDelta(3, null)));
        Assert.Equal("Two", Names(registry));
    }

    [Fact]
    public void CommitWithoutAnAnnotation_KeepsTheRecordedOne()
    {
        var database = new InMemoryDatabase();
        var recorded = new ShardAnnotation("v1", Coverage("One"));
        Commit(database, A, [Node(A, "V1")], recorded);
        Commit(database, A, [Node(A, "V2")], null);
        Assert.Same(recorded, Latched(database, A).Annotation);

        // An equal annotation keeps the recorded instance, so the snapshot view stays clean.
        Commit(database, A, [Node(A, "V2")], new ShardAnnotation("v1", null));
        Assert.Same(recorded, Latched(database, A).Annotation);
    }

    [Fact]
    public void FullReplacement_ClearsEveryAnnotation()
    {
        foreach (var replace in new System.Action<InMemoryDatabase>[]
                 {
                     database => database.ReplaceAll([Node(A, "X")], []),
                     database => database.Reset(),
                     database => Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, [Node(A, "X")], [], null)),
                 })
        {
            var database = new InMemoryDatabase();
            Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", Coverage("One")));
            Commit(database, B, [Node(B, "W1")], new ShardAnnotation("w1", null));
            replace(database);
            Assert.True(database.TryLatchCommitted(out _, out var annotations, out _));
            Assert.Empty(annotations);
        }
    }

    [Fact]
    public void ForeignSweepAndPrune_KeepTheAnnotation()
    {
        var database = new InMemoryDatabase();
        var target = Node(B, "Target", "b.fake");
        Commit(database, B, [target], new ShardAnnotation("b1", null));
        var edge = new CodeEdge
        {
            Id = $"{A.ParserId}:{A.WorkspaceId}:E",
            SourceId = Node(A, "Source").Id,
            TargetId = target.Id,
            Type = "Uses",
            Metadata = new Dictionary<string, string> { ["parserId"] = A.ParserId, ["workspaceId"] = A.WorkspaceId },
        };
        var annotationA = new ShardAnnotation("a1", Coverage("A"));
        Commit(database, A, [Node(A, "Source")], annotationA, [edge]);
        var (before, _) = Latched(database, A);

        // B drops the node A's edge points to: A is swept (a new shard reference), its annotation stays.
        Commit(database, B, [], new ShardAnnotation("b2", null));
        var (after, annotation) = Latched(database, A);
        Assert.NotSame(before, after);
        Assert.Same(annotationA, annotation);

        Assert.True(database.PruneFilesNotPresent(["b.fake"]) > 0);
        Assert.Same(annotationA, Latched(database, A).Annotation);
    }

    [Fact]
    public void AnnotationChangeAlone_MakesTheViewDirty()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", null));
        var (shards, annotations) = database.LatchPublished();
        var before = SnapshotView.Of(shards, annotations);

        // No fact changes: the same shard reference.
        Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, [], [], Scope(A, new ShardAnnotation("v2", null), [])));
        var (shardsAfter, annotationsAfter) = database.LatchPublished();
        Assert.Same(shards[A], shardsAfter[A]);
        Assert.False(before.SameAs(SnapshotView.Of(shardsAfter, annotationsAfter)));
    }

    [Fact]
    public void PublishLoadedShards_TakesAnnotations()
    {
        var source = new InMemoryDatabase();
        Commit(source, A, [Node(A, "V1")], null);
        var loaded = source.LatchPublished().Shards;
        var annotation = new ShardAnnotation("v9", Coverage("Nine"));

        var target = new InMemoryDatabase();
        target.PublishLoadedShards(loaded, [KeyValuePair.Create(A, annotation)]);
        Assert.Same(annotation, Latched(target, A).Annotation);
    }

    private static List<ProjectCoverage> RichCoverage(string missing) =>
    [
        new("App", "src/App.csproj", ProjectCoverageStates.Partial, 3, "net10.0",
            InactiveTargetFrameworks: ["net8.0"], MissingReferences: [missing], AnalyzerPackages: ["A"],
            UnresolvedNames: 2, CommonUnresolvedNames: ["Foo"], Notes: ["n"]),
        new("Lib", null, ProjectCoverageStates.Complete, 1),
    ];

    [Fact]
    public void EqualCoverageInANewList_KeepsTheRecordedInstance_AndTheViewClean()
    {
        // Every delta deserializes its own list (and the lists inside each entry): equal by content is equal.
        var database = new InMemoryDatabase();
        var recorded = new ShardAnnotation("v1", RichCoverage("Missing.dll"));
        Commit(database, A, [Node(A, "V1")], recorded);
        var (shards, annotations) = database.LatchPublished();
        var before = SnapshotView.Of(shards, annotations);

        Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, [], [],
            Scope(A, new ShardAnnotation("v1", RichCoverage("Missing.dll")), [])));
        Assert.Same(recorded, Latched(database, A).Annotation);
        var (shardsAfter, annotationsAfter) = database.LatchPublished();
        Assert.True(before.SameAs(SnapshotView.Of(shardsAfter, annotationsAfter)));

        // A different list (one string deep inside an entry) replaces it.
        var changed = new ShardAnnotation("v1", RichCoverage("Other.dll"));
        Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, [], [], Scope(A, changed, [])));
        Assert.Same(changed, Latched(database, A).Annotation);
    }

    [Fact]
    public void CoverageComparison_KnowsEveryFieldOfProjectCoverage()
    {
        // The store compares coverage field by field; a field added to the protocol record must be added there.
        Assert.Equal(11, typeof(ProjectCoverage).GetConstructors().Single().GetParameters().Length);
        var baseline = RichCoverage("M")[0];
        var variants = new[]
        {
            baseline with { Name = "x" }, baseline with { Path = "x" }, baseline with { State = "x" },
            baseline with { Files = 9 }, baseline with { TargetFramework = "x" },
            baseline with { InactiveTargetFrameworks = ["x"] }, baseline with { MissingReferences = ["x"] },
            baseline with { AnalyzerPackages = ["x"] }, baseline with { UnresolvedNames = 9 },
            baseline with { CommonUnresolvedNames = ["x"] }, baseline with { Notes = ["x"] },
        };
        Assert.True(InMemoryDatabase.SameCoverage([baseline], RichCoverage("M").Take(1).ToList()));
        Assert.All(variants, variant => Assert.False(InMemoryDatabase.SameCoverage([baseline], [variant])));
        Assert.False(InMemoryDatabase.SameCoverage([baseline], null));
        Assert.True(InMemoryDatabase.SameCoverage(null, null));
    }
}
