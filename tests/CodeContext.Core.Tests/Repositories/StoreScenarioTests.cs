using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Tests.Repositories.StoreModel;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

using GraphState = InMemoryDatabase.GraphState;
using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The heap store against the <see cref="ReferenceGraphModel"/>: after every generated operation
/// the result (or exception) and the whole read matrix must be equal. Also pins the two store
/// fixes of the shard-interface phase (identifier map exactly over the final nodes; an empty
/// scoped commit keeps the shard), canonical read order, and the snapshot hooks
/// (<c>PublishLoadedShards</c>, <c>TryReplaceShard</c>).
/// </summary>
public class StoreScenarioTests
{
    private const int Operations = 200;

    private static readonly int[] SeedValues = [1, 2, 3, 5, 8, 13, 21, 34];

    public static TheoryData<int> Seeds => new(SeedValues);

    /// <summary>The heap store; a swap installs a heap copy of the live shard.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public Task HeapStore_MatchesReferenceModel(int seed)
        => StoreScenarioRunner.RunAsync(seed, Operations, GraphState.CopyOf);

    /// <summary>
    /// The same scenarios with every swap installing a non-heap shard (<see cref="DelegatingShard"/>)
    /// that stays non-heap across commits, so the legacy mutations and every commit path run over a
    /// shard of another kind, as they will over a mapped shard.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public Task NonHeapShardStore_MatchesReferenceModel(int seed)
        => StoreScenarioRunner.RunAsync(seed, Operations, DelegatingShard.Of);

    [Fact]
    public void Generator_CoversEveryOutcome()
    {
        // The scenario test is only as strong as what the generator reaches. Over the seeds, every
        // kind of outcome must occur: accepted and stale commits, duplicate identifiers, empty
        // commits, shadowing deltas, cross-shard id reclaims, prunes that remove, every
        // reconciliation step including the out-of-turn ones, every legacy mutation and its
        // rejections, ReplaceAll and Reset, swaps, and the input shapes the product must tolerate.
        // A store runs alongside, with swaps installing the non-heap shard, so it can also be
        // asserted that every kind of operation runs while a non-heap shard is live.
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string kind) => outcomes[kind] = outcomes.GetValueOrDefault(kind) + 1;

        foreach (var seed in SeedValues)
        {
            var model = new ReferenceGraphModel();
            var database = new InMemoryDatabase();
            var generator = new StoreScenarioGenerator(seed);
            for (var step = 0; step < Operations; step++)
            {
                var operation = generator.Next(model);
                var before = model.Current;
                var published = model.Published;
                if (database.ShardKeys.Any(key => database.GetShardState(key) is DelegatingShard))
                    Count($"nonHeapLive:{operation.GetType().Name}");
                var result = operation.ApplyTo(model);
                operation.ApplyTo(database, DelegatingShard.Of);
                switch (operation)
                {
                    case StoreOperation.Commit commit:
                        if (result == "False") Count("stale");
                        else if (result.Contains("InvalidDataException")) Count("duplicateIdentifier");
                        else if (commit.Scope is null) Count("replaceAll");
                        else if (commit.Scope.ReplacesFiles is null) Count("wholeShard");
                        else Count("scoped");
                        if (commit.Scope is { ReplacesFiles.Count: 0 } && commit.Nodes.Count == 0 && commit.Edges.Count == 0)
                            Count("empty");
                        if (result != "True") break;
                        if (commit.Nodes.Any(node => node.Id is null)) Count("nullNodeIdInCommit");
                        if (commit.Edges.Any(edge => edge.Id is null)) Count("nullEdgeIdInCommit");
                        if (commit.Edges.Any(edge => edge.Id is not null && (edge.SourceId is null || edge.TargetId is null)))
                            Count("nullEndpointInCommit");
                        if (commit.Nodes.Any(node => node.Id is not null && node.FilePath == StoreUniverse.BlankPath)) Count("blankPath");
                        if (commit.Nodes.Any(node => node.Id is not null && node.FilePath?.StartsWith("external:", StringComparison.Ordinal) == true))
                            Count("externalPath");
                        if (commit.Scope is not null)
                        {
                            var key = ShardKey.From(commit.Scope.ParserId, commit.Scope.WorkspaceId);
                            if (commit.Nodes.Any(node => node.Id is not null && ShardKey.ForNode(node) != key)) Count("nodeOwnedElsewhere");
                            if (commit.Edges.Any(edge => edge.Id is not null && ShardKey.ForEdge(edge) != key)) Count("edgeOwnedElsewhere");
                            if (commit.Scope.ReplacesFiles?.Contains(null!) == true) Count("nullFileEntry");
                            if (commit.Scope.ReplacesFiles?.Contains(string.Empty) == true) Count("emptyFileEntry");
                            foreach (var node in commit.Nodes)
                            {
                                if (node.Id is null || !before.Nodes.TryGetValue(node.Id, out var held)) continue;
                                if (held.Shard != key) Count("reclaimedFromOtherShard");
                                else if (held.Fact.Identifier != node.Identifier) Count("shadowWithNewIdentifier");
                            }
                            if (commit.Edges.Any(edge => edge.TargetId is not null && !before.Nodes.ContainsKey(edge.TargetId)))
                                Count("danglingEdge");
                        }
                        break;
                    case StoreOperation.Prune prune:
                        Count(result == "0" ? "pruneNothing" : "pruneRemoved");
                        var paths = before.Nodes.Values.Select(owned => owned.Fact.FilePath).Where(path => path is not null).ToHashSet(StringComparer.Ordinal);
                        if (prune.PresentFiles.Any(keep => !paths.Contains(keep) && paths.Contains(keep, StringComparer.OrdinalIgnoreCase)))
                            Count("pruneKeepCaseVariant");
                        break;
                    case StoreOperation.UpsertNode upsert:
                        if (upsert.Node.Id is null) Count("UpsertNode:nullId");
                        else if (result.Contains("InvalidDataException")) Count("UpsertNode:duplicateIdentifier");
                        else if (published.Nodes.TryGetValue(upsert.Node.Id, out var held) && held.Shard != ShardKey.ForNode(upsert.Node))
                            Count("UpsertNode:movedShard");
                        else Count(held is null ? "UpsertNode:new" : "UpsertNode:replaced");
                        break;
                    case StoreOperation.UpsertEdge upsert:
                        Count(upsert.Edge.Id is null ? "UpsertEdge:nullId"
                            : published.Edges.TryGetValue(upsert.Edge.Id, out var edge) && edge.Shard != ShardKey.ForEdge(upsert.Edge)
                                ? "UpsertEdge:movedShard" : "UpsertEdge:ok");
                        break;
                    case StoreOperation.RemoveNode:
                        Count($"RemoveNode:{result}");
                        break;
                    case StoreOperation.RemoveEdgesTouchingNode remove:
                        Count(remove.NodeId is null ? "RemoveEdgesTouchingNode:null" : result == "0" ? "RemoveEdgesTouchingNode:none" : "RemoveEdgesTouchingNode:removed");
                        break;
                    case StoreOperation.SwapShard:
                        Count($"SwapShard:{result}");
                        break;
                    default:
                        Count($"{operation.GetType().Name}:{(result == "ok" ? "ok" : "throws")}");
                        break;
                }
            }
        }

        string[] operationKinds =
        [
            nameof(StoreOperation.Commit), nameof(StoreOperation.ReplaceAll), nameof(StoreOperation.Reset),
            nameof(StoreOperation.Prune), nameof(StoreOperation.BeginReconciliation), nameof(StoreOperation.CommitReconciliation),
            nameof(StoreOperation.RollbackReconciliation), nameof(StoreOperation.UpsertNode), nameof(StoreOperation.RemoveNode),
            nameof(StoreOperation.UpsertEdge), nameof(StoreOperation.RemoveEdgesTouchingNode), nameof(StoreOperation.SwapShard),
        ];
        string[] required =
        [
            "stale", "duplicateIdentifier", "replaceAll", "wholeShard", "scoped", "empty",
            "reclaimedFromOtherShard", "shadowWithNewIdentifier", "danglingEdge", "pruneNothing", "pruneRemoved",
            "BeginReconciliation:ok", "BeginReconciliation:throws", "CommitReconciliation:ok",
            "CommitReconciliation:throws", "RollbackReconciliation:ok",
            "nullNodeIdInCommit", "nullEdgeIdInCommit", "nullEndpointInCommit", "blankPath", "externalPath",
            "nodeOwnedElsewhere", "edgeOwnedElsewhere", "nullFileEntry", "emptyFileEntry", "pruneKeepCaseVariant",
            "ReplaceAll:ok", "ReplaceAll:throws", "Reset:ok",
            "UpsertNode:nullId", "UpsertNode:duplicateIdentifier", "UpsertNode:movedShard", "UpsertNode:new", "UpsertNode:replaced",
            "UpsertEdge:nullId", "UpsertEdge:movedShard", "UpsertEdge:ok",
            "RemoveNode:True", "RemoveNode:False",
            "RemoveEdgesTouchingNode:null", "RemoveEdgesTouchingNode:none", "RemoveEdgesTouchingNode:removed",
            "SwapShard:True", "SwapShard:False",
            .. operationKinds.Select(kind => $"nonHeapLive:{kind}"),
        ];
        var missing = required.Where(kind => !outcomes.ContainsKey(kind)).ToList();
        Assert.True(missing.Count == 0,
            $"never generated: {string.Join(", ", missing)}; seen: {string.Join(", ", outcomes.Select(pair => $"{pair.Key}={pair.Value}"))}");
    }

    [Fact]
    public void Generator_IsDeterministicPerSeed()
    {
        static List<string> Run(int seed)
        {
            var model = new ReferenceGraphModel();
            var generator = new StoreScenarioGenerator(seed);
            var lines = new List<string>();
            for (var step = 0; step < 50; step++)
            {
                var operation = generator.Next(model);
                lines.Add(operation.Describe() + " => " + operation.ApplyTo(model));
            }
            return lines;
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));
    }

    // ---- Store fix 1: the identifier map is exactly { node.Identifier -> node.Id } ----------

    [Fact]
    public async Task ScopedCommit_ShadowingDelta_DropsTheOldIdentifier()
    {
        var database = new InMemoryDatabase();
        var nodes = new InMemoryNodeRepository(database);
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:x", "csharp:Old.Name", "/repo/A.cs")], [], new CommitScope("cs", "w", null)));

        // A delta for another file re-supplies the same id with a new identifier: the carried copy
        // is shadowed, and its identifier must stop resolving in the same generation.
        Assert.True(database.TryCommitGeneration(2,
            [Node("cs:w:x", "csharp:New.Name", "/repo/B.cs")], [], new CommitScope("cs", "w", ["/repo/B.cs"])));

        Assert.Null(await nodes.GetByIdentifierAsync("csharp:Old.Name"));
        Assert.Equal("cs:w:x", (await nodes.GetByIdentifierAsync("csharp:New.Name"))?.Id);

        // The freed identifier can be taken by another node without a duplicate error.
        Assert.True(database.TryCommitGeneration(3,
            [Node("cs:w:y", "csharp:Old.Name", "/repo/C.cs")], [], new CommitScope("cs", "w", ["/repo/C.cs"])));
        Assert.Equal("cs:w:y", (await nodes.GetByIdentifierAsync("csharp:Old.Name"))?.Id);
    }

    [Fact]
    public async Task ScopedCommit_ShadowedIdentifierTakenByAnotherNodeOfTheSameDelta_Succeeds()
    {
        var database = new InMemoryDatabase();
        var nodes = new InMemoryNodeRepository(database);
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:x", "csharp:I1", "/repo/A.cs")], [], new CommitScope("cs", "w", null)));

        // One delta, for another file, moves the carried X to I2 and gives I1 to a new node Y. X is
        // supplied first, so by the time Y registers, I1 no longer names anything.
        Assert.True(database.TryCommitGeneration(2,
            [Node("cs:w:x", "csharp:I2", "/repo/B.cs"), Node("cs:w:y", "csharp:I1", "/repo/B.cs")],
            [], new CommitScope("cs", "w", ["/repo/B.cs"])));

        Assert.Equal("cs:w:x", (await nodes.GetByIdentifierAsync("csharp:I2"))?.Id);
        Assert.Equal("cs:w:y", (await nodes.GetByIdentifierAsync("csharp:I1"))?.Id);
        Assert.True(database.TryGetNodeIdByIdentifier("csharp:I1", out var holder));
        Assert.Equal("cs:w:y", holder);
        Assert.Equal("csharp:I2", database.GetNode("cs:w:x")?.Identifier);

        // In the other order Y meets I1 while the carried X still holds it: rule 2 of the plan
        // checks a delta identifier against the carried nodes as they stand so far, so that throws
        // and nothing is published.
        var reversed = new InMemoryDatabase();
        Assert.True(reversed.TryCommitGeneration(1,
            [Node("cs:w:x", "csharp:I1", "/repo/A.cs")], [], new CommitScope("cs", "w", null)));
        Assert.Throws<InvalidDataException>(() => reversed.TryCommitGeneration(2,
            [Node("cs:w:y", "csharp:I1", "/repo/B.cs"), Node("cs:w:x", "csharp:I2", "/repo/B.cs")],
            [], new CommitScope("cs", "w", ["/repo/B.cs"])));
        Assert.Equal("csharp:I1", reversed.GetNode("cs:w:x")?.Identifier);
        Assert.False(reversed.ContainsNode("cs:w:y"));
    }

    [Fact]
    public async Task ScopedCommit_RepeatedIdInOneDelta_KeepsOnlyTheLastIdentifier()
    {
        var database = new InMemoryDatabase();
        var nodes = new InMemoryNodeRepository(database);
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:x", "csharp:First", "/repo/A.cs"), Node("cs:w:x", "csharp:Second", "/repo/A.cs")],
            [], new CommitScope("cs", "w", null)));

        Assert.Null(await nodes.GetByIdentifierAsync("csharp:First"));
        Assert.Equal("cs:w:x", (await nodes.GetByIdentifierAsync("csharp:Second"))?.Id);
    }

    [Fact]
    public void ScopedCommit_DuplicateIdentifier_ThrowsBeforeAnythingIsPublished()
    {
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:x", "csharp:Taken", "/repo/A.cs")], [], new CommitScope("cs", "w", null)));
        Assert.True(database.TryCommitGeneration(2,
            [Node("ts:w:y", "ts:Other", "/repo/b.ts")], [], new CommitScope("ts", "w", null)));
        var before = database.GetShardState(new ShardKey("ts", "w"));

        var error = Assert.Throws<InvalidDataException>(() => database.TryCommitGeneration(3,
            [Node("ts:w:z", "ts:Fresh", "/repo/c.ts"), Node("ts:w:q", "csharp:Taken", "/repo/c.ts")],
            [], new CommitScope("ts", "w", null)));

        Assert.Equal("Duplicate public symbol identifier 'csharp:Taken'.", error.Message);
        Assert.Same(before, database.GetShardState(new ShardKey("ts", "w")));
        Assert.Equal(2, database.LastCommittedGeneration);
        Assert.False(database.ContainsNode("ts:w:z"));
    }

    // ---- Store fix 2: an empty scoped commit keeps the shard reference ----------------------

    [Fact]
    public void EmptyScopedCommit_KeepsTheShardReference()
    {
        var database = new InMemoryDatabase();
        var key = new ShardKey("cs", "w");
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:a", "csharp:A", "/repo/A.cs")], [Edge("cs:w:e", "cs:w:a", "cs:w:a")], new CommitScope("cs", "w", null)));
        var shard = database.GetShardState(key);
        var adjacency = database.GetShardAdjacency(key);
        var composite = database.GetAdjacency();

        Assert.True(database.TryCommitGeneration(2, [], [], new CommitScope("cs", "w", [])));

        Assert.Equal(2, database.LastCommittedGeneration);
        Assert.Same(shard, database.GetShardState(key));
        Assert.Same(adjacency, database.GetShardAdjacency(key));
        Assert.Same(composite, database.GetAdjacency());

        // A stale empty commit is still rejected, and an empty commit to a shard that does not
        // exist does not create one.
        Assert.False(database.TryCommitGeneration(2, [], [], new CommitScope("cs", "w", [])));
        Assert.True(database.TryCommitGeneration(3, [], [], new CommitScope("ts", "w", [])));
        Assert.Equal([key], database.ShardKeys);

        // Inside a reconciliation it only advances the staged generation.
        database.BeginReconciliation();
        Assert.True(database.TryCommitGeneration(4, [], [], new CommitScope("cs", "w", [])));
        Assert.Equal(4, database.LastCommittedGeneration);
        database.CommitReconciliation();
        Assert.Same(shard, database.GetShardState(key));
        Assert.Equal(4, database.LastCommittedGeneration);
    }

    // ---- Canonical order --------------------------------------------------------------------

    [Fact]
    public async Task ReadsAreInOrdinalIdOrder_AcrossShards()
    {
        // Ids are committed in reverse order, into dictionaries of different capacities, across
        // three shards plus the un-owned one. Every list must come back id-ordered inside a shard,
        // shards in ShardKey order — not in commit or bucket order.
        var database = new InMemoryDatabase();
        var nodeRepository = new InMemoryNodeRepository(database);
        var edgeRepository = new InMemoryEdgeRepository(database);
        string[] locals = ["b", "B", "a", "aa", "a0", "Z", "_"];
        var generation = 0L;
        foreach (var (parser, workspace) in new[] { ("ts", "w"), ("cs", "w2"), ("cs", "w1") })
        {
            var nodes = locals.Reverse().Select(local => Node($"{parser}:{workspace}:{local}", $"{parser}:{workspace}#{local}",
                local.Length % 2 == 0 ? "/repo/src/Shared.cs" : $"/repo/{parser}/File.cs", "Thing")).ToList();
            var edges = locals.Reverse().Select(local => Edge($"{parser}:{workspace}:e{local}", $"{parser}:{workspace}:{local}", "hub")).ToList();
            Assert.True(database.TryCommitGeneration(++generation, nodes, edges, new CommitScope(parser, workspace, null)));
        }
        database.UpsertNode(new CodeNode { Id = "zz-unowned", Name = "Thing", FilePath = "/repo/src/Shared.cs" });
        database.UpsertNode(new CodeNode { Id = "aa-unowned", Name = "Thing", FilePath = "/repo/src/Shared.cs" });

        var shardOrder = new[] { "", "cs:w1:", "cs:w2:", "ts:w:" };
        static IEnumerable<string> Canonical(IEnumerable<string> ids, string[] shardOrder)
            => shardOrder.SelectMany(prefix => ids
                .Where(id => prefix.Length == 0 ? id.EndsWith("-unowned") : id.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));

        var all = (await nodeRepository.GetAllAsync()).Select(node => node.Id!).ToList();
        Assert.Equal(Canonical(all, shardOrder), all);
        Assert.Equal(all, database.EnumerateNodes().Select(node => node.Id));
        Assert.Equal(all, (await new InMemoryCodeGraphRepository(database).GetGraphAsync())!.Nodes.Select(node => node.Id));

        var byName = (await nodeRepository.FindByNameAsync("Thing", exact: true)).Select(node => node.Id!).ToList();
        Assert.Equal(all, byName);

        // A relative path matches buckets of several files in several shards.
        var byPath = (await nodeRepository.FindByFilePathAsync("File.cs")).Select(node => node.Id!).ToList();
        Assert.Equal(Canonical(byPath, shardOrder), byPath);
        Assert.Equal(15, byPath.Count);
        var shared = (await nodeRepository.FindByFilePathAsync("src/Shared.cs")).Select(node => node.Id!).ToList();
        Assert.Equal(Canonical(shared, shardOrder), shared);

        var toHub = (await edgeRepository.GetByTargetIdAsync("hub")).Select(edge => edge.Id!).ToList();
        Assert.Equal(21, toHub.Count);
        Assert.Equal(new[] { "cs:w1:", "cs:w2:", "ts:w:" }.SelectMany(prefix => toHub
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)), toHub);
        var allEdges = (await edgeRepository.GetAllAsync()).Select(edge => edge.Id!).ToList();
        Assert.Equal(toHub, allEdges);

        Assert.Equal(["/repo/cs/File.cs", "/repo/src/Shared.cs", "/repo/ts/File.cs"], await nodeRepository.GetFilePathsAsync());
    }

    [Fact]
    public async Task FilePaths_AreDistinctAndOrderedCaseInsensitivelyThenOrdinally()
    {
        var database = new InMemoryDatabase();
        database.ReplaceAll(
        [
            new CodeNode { Id = "1", FilePath = "/b/x.cs" },
            new CodeNode { Id = "2", FilePath = "/B/x.cs" },
            new CodeNode { Id = "3", FilePath = "/a/y.cs" },
            new CodeNode { Id = "4", FilePath = "/b/x.cs" },
            new CodeNode { Id = "5", FilePath = "" },
            new CodeNode { Id = "6", FilePath = null },
            new CodeNode { Id = "7", FilePath = "/A/y.cs", Metadata = new Dictionary<string, string> { ["parserId"] = "p", ["workspaceId"] = "w" } },
        ], []);

        Assert.Equal(["/A/y.cs", "/a/y.cs", "/B/x.cs", "/b/x.cs"], await new InMemoryNodeRepository(database).GetFilePathsAsync());
    }

    // ---- Snapshot hooks (no production caller yet) ------------------------------------------

    [Fact]
    public void PublishLoadedShards_PublishesIntoAnEmptyStoreOnly()
    {
        var source = new InMemoryDatabase();
        Assert.True(source.TryCommitGeneration(1,
            [Node("cs:w:a", "csharp:A", "/repo/A.cs")], [Edge("cs:w:e", "cs:w:a", "ts:w:b")], new CommitScope("cs", "w", null)));
        Assert.True(source.TryCommitGeneration(2,
            [Node("ts:w:b", "ts:B", "/repo/b.ts")], [], new CommitScope("ts", "w", null)));
        var loaded = source.ShardKeys
            .Select(key => KeyValuePair.Create(key, (IGraphShard)source.GetShardState(key)!))
            .ToList();

        var target = new InMemoryDatabase();
        Assert.True(target.TryCommitGeneration(5, [], [], new CommitScope("cs", "w", [])));
        target.PublishLoadedShards(loaded);

        Assert.Equal(5, target.LastCommittedGeneration);
        Assert.Equal(2, target.NodeCount);
        Assert.True(target.ContainsEdge("cs:w:e"));
        Assert.Same(loaded[0].Value, target.GetShardState(loaded[0].Key));

        // Not into a store that holds facts, and not during a reconciliation.
        Assert.Throws<InvalidOperationException>(() => target.PublishLoadedShards(loaded));
        var staged = new InMemoryDatabase();
        staged.BeginReconciliation();
        Assert.Throws<InvalidOperationException>(() => staged.PublishLoadedShards(loaded));
        Assert.Equal(0, staged.NodeCount);
    }

    [Fact]
    public void TryReplaceShard_SwapsOnlyTheExpectedReferenceOutsideReconciliation()
    {
        var database = new InMemoryDatabase();
        var key = new ShardKey("cs", "w");
        Assert.True(database.TryCommitGeneration(1,
            [Node("cs:w:a", "csharp:A", "/repo/A.cs")], [], new CommitScope("cs", "w", null)));
        var original = (IGraphShard)database.GetShardState(key)!;
        var copy = GraphState.CopyOf(original);

        Assert.False(database.TryReplaceShard(new ShardKey("ts", "w"), original, copy));
        Assert.False(database.TryReplaceShard(key, copy, copy));

        database.BeginReconciliation();
        Assert.False(database.TryReplaceShard(key, original, copy));
        database.RollbackReconciliation();

        var before = database.GetAdjacency();
        Assert.True(database.TryReplaceShard(key, original, copy));
        Assert.Same(copy, database.GetShardState(key));
        Assert.NotSame(before, database.GetAdjacency());
        Assert.Equal(1, database.LastCommittedGeneration);
        Assert.Equal("cs:w:a", database.GetNode("cs:w:a")?.Id);

        // After a commit the captured reference is stale, so the swap is refused.
        Assert.True(database.TryCommitGeneration(2,
            [Node("cs:w:b", "csharp:B", "/repo/B.cs")], [], new CommitScope("cs", "w", ["/repo/B.cs"])));
        Assert.False(database.TryReplaceShard(key, copy, GraphState.CopyOf(copy)));
    }

    // ---- Legacy in-place mutations over a non-heap shard ---------------------------------------

    public static TheoryData<string, string, string> LegacyMutationCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var mutation in LegacyMutations.Keys)
            foreach (var context in new[] { "outside", "stagedThenCommit", "stagedThenRollback", "stagedAfterInnerCommitThenCommit" })
                foreach (var install in new[] { "publish", "swap" })
                    data.Add(mutation, context, install);
        return data;
    }

    /// <summary>
    /// A legacy mutation materializes a non-heap shard into a heap copy before writing. The store
    /// with non-heap shards must then read exactly as the all-heap store after the same operations,
    /// inside and outside a staged reconciliation, after its commit and after its rollback: with a
    /// heap shard the staged map shares the written object, so the non-heap shard's copy must reach
    /// the staged map too.
    /// </summary>
    [Theory]
    [MemberData(nameof(LegacyMutationCases))]
    public async Task LegacyMutation_OverANonHeapShard_ReadsAsOverAHeapShard(string mutation, string context, string install)
    {
        var heap = SeedLegacyStore();
        var nonHeap = install == "swap" ? SeedLegacyStore() : new InMemoryDatabase();
        if (install == "swap")
        {
            foreach (var key in nonHeap.ShardKeys)
            {
                var live = (IGraphShard)nonHeap.GetShardState(key)!;
                Assert.True(nonHeap.TryReplaceShard(key, live, DelegatingShard.Of(live)));
            }
        }
        else
        {
            // Same generation as the seeded store, then the seeded shards published as non-heap copies.
            Assert.True(nonHeap.TryCommitGeneration(heap.LastCommittedGeneration, [], [], new CommitScope("none", "none", [])));
            nonHeap.PublishLoadedShards(heap.ShardKeys
                .Select(key => KeyValuePair.Create(key, DelegatingShard.Of((IGraphShard)heap.GetShardState(key)!)))
                .ToList());
        }
        Assert.All(nonHeap.ShardKeys, key => Assert.IsType<DelegatingShard>(nonHeap.GetShardState(key)));

        var steps = new List<(string Label, Func<InMemoryDatabase, string> Apply)>();
        if (context != "outside") steps.Add(("begin", Apply(database => database.BeginReconciliation())));
        if (context == "stagedAfterInnerCommitThenCommit")
        {
            // The staged map gets its own copy of the csharp/w1 shard; the live one stays shared by nobody.
            steps.Add(("inner commit", database => database.TryCommitGeneration(10,
                [LegacyNode(StoreUniverse.LocalId(W1, 5), W1, "I:inner", StoreUniverse.FilePath(W1, 3))], [],
                new CommitScope(W1.ParserId, W1.WorkspaceId, [StoreUniverse.FilePath(W1, 3)])).ToString()));
        }
        steps.Add((mutation, LegacyMutations[mutation]));
        if (context.EndsWith("Commit", StringComparison.Ordinal)) steps.Add(("commit", Apply(database => database.CommitReconciliation())));
        if (context.EndsWith("Rollback", StringComparison.Ordinal)) steps.Add(("rollback", Apply(database => database.RollbackReconciliation())));
        steps.Add(("later commit", database => database.TryCommitGeneration(20,
            [LegacyNode(StoreUniverse.LocalId(W1, 6), W1, "I:later", StoreUniverse.FilePath(W1, 2))], [],
            new CommitScope(W1.ParserId, W1.WorkspaceId, [StoreUniverse.FilePath(W1, 2)])).ToString()));

        string[] probes = ["I:new", "I:moved", "I:inner", "I:later", "I:fresh"];
        foreach (var (label, apply) in steps)
        {
            var where = $"{mutation} / {context} / {install}, after {label}";
            Assert.Equal(Outcome(() => apply(heap)), Outcome(() => apply(nonHeap)));
            StoreReadMatrix.AssertEqual(
                await StoreReadMatrix.CaptureAsync(heap, probes), await StoreReadMatrix.CaptureAsync(nonHeap, probes), where);
        }
    }

    [Fact]
    public void LegacyUpsertIntoANewShard_DuringAReconciliation_IsLiveUntilItsCommit()
    {
        // Today's behaviour, kept: a legacy upsert that creates a shard while a reconciliation is
        // staged adds it to the live map only, so readers see it at once and the reconciliation's
        // commit (which publishes the staged map) drops it; a rollback keeps it.
        foreach (var commit in new[] { true, false })
        {
            var database = SeedLegacyStore();
            database.BeginReconciliation();
            database.UpsertNode(LegacyNode(StoreUniverse.LocalId(Ts, 0), Ts, "I:fresh", StoreUniverse.FilePath(Ts, 0)));
            Assert.True(database.ContainsNode(StoreUniverse.LocalId(Ts, 0)));
            if (commit) database.CommitReconciliation();
            else database.RollbackReconciliation();
            Assert.Equal(!commit, database.ContainsNode(StoreUniverse.LocalId(Ts, 0)));
        }
    }

    private static readonly ShardKey W1 = StoreUniverse.OwnedShards[0];
    private static readonly ShardKey W2 = StoreUniverse.OwnedShards[1];
    private static readonly ShardKey Ts = StoreUniverse.OwnedShards[2];

    private static readonly Dictionary<string, Func<InMemoryDatabase, string>> LegacyMutations = new(StringComparer.Ordinal)
    {
        ["UpsertNode:newIdentifier"] = Apply(database => database.UpsertNode(
            LegacyNode(StoreUniverse.LocalId(W1, 1), W1, "I:new", StoreUniverse.FilePath(W1, 1)))),
        ["UpsertNode:movesShard"] = Apply(database => database.UpsertNode(
            LegacyNode(StoreUniverse.LocalId(W1, 2), W2, "I:moved", StoreUniverse.FilePath(W2, 0)))),
        ["UpsertNode:newShard"] = Apply(database => database.UpsertNode(
            LegacyNode(StoreUniverse.LocalId(Ts, 0), Ts, "I:fresh", StoreUniverse.FilePath(Ts, 0)))),
        ["UpsertNode:duplicateIdentifier"] = Apply(database => database.UpsertNode(
            LegacyNode(StoreUniverse.LocalId(W2, 4), W2, $"I:{StoreUniverse.LocalId(W1, 0)}", StoreUniverse.FilePath(W2, 0)))),
        ["RemoveNode"] = database => database.RemoveNode(StoreUniverse.LocalId(W1, 1)).ToString(),
        ["UpsertEdge"] = Apply(database => database.UpsertEdge(
            LegacyEdge("csharp:w1:e:new", W1, StoreUniverse.LocalId(W1, 0), StoreUniverse.LocalId(W1, 3)))),
        ["UpsertEdge:movesShard"] = Apply(database => database.UpsertEdge(
            LegacyEdge("shared-edge:0", W1, StoreUniverse.LocalId(W1, 3), StoreUniverse.LocalId(W2, 1)))),
        ["RemoveEdgesTouchingNode"] = database => database.RemoveEdgesTouchingNode(StoreUniverse.LocalId(W1, 1)).ToString(),
        ["RemoveEdgesTouchingNode:null"] = database => database.RemoveEdgesTouchingNode(null!).ToString(),
    };

    /// <summary>Two owned shards and the un-owned one, with local, cross-shard and dangling edges (generation 3).</summary>
    private static InMemoryDatabase SeedLegacyStore()
    {
        var database = new InMemoryDatabase();
        var w1 = Enumerable.Range(0, 4)
            .Select(i => LegacyNode(StoreUniverse.LocalId(W1, i), W1, $"I:{StoreUniverse.LocalId(W1, i)}", StoreUniverse.FilePath(W1, i % 2)))
            .ToList();
        Assert.True(database.TryCommitGeneration(1, w1,
        [
            LegacyEdge("csharp:w1:e:0-1", W1, StoreUniverse.LocalId(W1, 0), StoreUniverse.LocalId(W1, 1)),
            LegacyEdge("csharp:w1:e:1-2", W1, StoreUniverse.LocalId(W1, 1), StoreUniverse.LocalId(W1, 2)),
            LegacyEdge("csharp:w1:e:2-w2", W1, StoreUniverse.LocalId(W1, 2), StoreUniverse.LocalId(W2, 0)),
            LegacyEdge("csharp:w1:e:3-shared", W1, StoreUniverse.LocalId(W1, 3), StoreUniverse.SharedId(0)),
            LegacyEdge("csharp:w1:e:0-null", W1, StoreUniverse.LocalId(W1, 0), null),
        ], new CommitScope(W1.ParserId, W1.WorkspaceId, null)));
        var w2 = Enumerable.Range(0, 3)
            .Select(i => LegacyNode(StoreUniverse.LocalId(W2, i), W2, $"I:{StoreUniverse.LocalId(W2, i)}", StoreUniverse.FilePath(W2, i)))
            .ToList();
        Assert.True(database.TryCommitGeneration(2, w2,
        [
            LegacyEdge("csharp:w2:e:0-w1", W2, StoreUniverse.LocalId(W2, 0), StoreUniverse.LocalId(W1, 1)),
            LegacyEdge("shared-edge:0", W2, StoreUniverse.LocalId(W2, 1), StoreUniverse.LocalId(W2, 2)),
        ], new CommitScope(W2.ParserId, W2.WorkspaceId, null)));
        Assert.True(database.TryCommitGeneration(3,
            [LegacyNode("default:only", ShardKey.Default, "I:default:only", "/repo/shared/Common.cs")],
            [LegacyEdge("default:e", ShardKey.Default, "default:only", StoreUniverse.LocalId(W1, 1))],
            new CommitScope("", "", null)));
        return database;
    }

    private static Func<InMemoryDatabase, string> Apply(Action<InMemoryDatabase> action) => database =>
    {
        action(database);
        return "ok";
    };

    private static string Outcome(Func<string> apply)
    {
        try
        {
            return apply();
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            return $"{exception.GetType().FullName}: {exception.Message}";
        }
    }

    private static Dictionary<string, string> OwnerOf(ShardKey shard)
        => shard == ShardKey.Default
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["parserId"] = shard.ParserId, ["workspaceId"] = shard.WorkspaceId };

    private static CodeNode LegacyNode(string id, ShardKey owner, string identifier, string path)
        => new() { Id = id, Identifier = identifier, Name = "Alpha", Type = "Class", FilePath = path, Metadata = OwnerOf(owner) };

    private static CodeEdge LegacyEdge(string id, ShardKey owner, string source, string? target)
        => new() { Id = id, SourceId = source, TargetId = target, Type = "CALLS", Metadata = OwnerOf(owner) };

    private static Dictionary<string, string> Owner(string id)
    {
        var parts = id.Split(':');
        return new Dictionary<string, string> { ["parserId"] = parts[0], ["workspaceId"] = parts[1] };
    }

    private static CodeNode Node(string id, string identifier, string path, string name = "N")
        => new() { Id = id, Identifier = identifier, Name = name, Type = "Class", FilePath = path, Metadata = Owner(id) };

    private static CodeEdge Edge(string id, string source, string target)
        => new() { Id = id, SourceId = source, TargetId = target, Type = "CALLS", Metadata = Owner(id) };
}
