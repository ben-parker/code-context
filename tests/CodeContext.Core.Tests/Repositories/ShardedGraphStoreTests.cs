using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Covers the (parserId, workspaceId) sharding of <see cref="InMemoryDatabase"/>: a scoped commit
/// touches only its own shard (untouched shards keep their GraphState AND cached adjacency by
/// reference), edges dangling across shard boundaries are swept globally, and every full-replacement /
/// maintenance / routing path partitions by the ownership metadata the applier stamps.
/// </summary>
public class ShardedGraphStoreTests
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryCodeGraphRepository _repository;
    private IGenerationalGraphStore Store => _repository;

    public ShardedGraphStoreTests() => _repository = new InMemoryCodeGraphRepository(_database);

    private static CodeNode Node(string id, string parserId, string workspaceId, string filePath = "/repo/a.cs")
        => new()
        {
            Id = id,
            Name = id,
            Type = "Class",
            FilePath = filePath,
            StartLine = 1,
            EndLine = 2,
            Metadata = Meta(parserId, workspaceId),
        };

    private static CodeNode Unowned(string id, string filePath = "/repo/u.cs")
        => new() { Id = id, Name = id, Type = "Class", FilePath = filePath, StartLine = 1, EndLine = 2 };

    private static CodeEdge Edge(string id, string source, string target, string? parserId, string? workspaceId)
        => new() { Id = id, SourceId = source, TargetId = target, Type = "CALLS", Metadata = Meta(parserId, workspaceId) };

    private static Dictionary<string, string>? Meta(string? parserId, string? workspaceId)
    {
        if (parserId is null && workspaceId is null) return null;
        var metadata = new Dictionary<string, string>();
        if (parserId is not null) metadata["parserId"] = parserId;
        if (workspaceId is not null) metadata["workspaceId"] = workspaceId;
        return metadata;
    }

    // ---- 1. Shard isolation (the load-bearing assertion of the phase) ------------------------

    [Fact]
    public async Task ScopedCommit_LeavesUntouchedShardStateAndAdjacencyReferenceIdentical()
    {
        var csKey = new ShardKey("csharp", "w");
        var tsKey = new ShardKey("typescript", "w");

        // Seed two shards via scoped commits.
        Assert.True(await Store.TryCommitGenerationAsync(1,
            [Node("csharp:w:a", "csharp", "w")], [], new CommitScope("csharp", "w", null)));
        Assert.True(await Store.TryCommitGenerationAsync(2,
            [Node("typescript:w:x", "typescript", "w")], [], new CommitScope("typescript", "w", null)));

        var tsStateBefore = _database.GetShardState(tsKey);
        var tsAdjBefore = _database.GetShardAdjacency(tsKey);
        var csAdjBefore = _database.GetShardAdjacency(csKey);
        Assert.NotNull(tsStateBefore);
        Assert.NotNull(tsAdjBefore);

        // Reparse only the C# shard.
        Assert.True(await Store.TryCommitGenerationAsync(3,
            [Node("csharp:w:b", "csharp", "w")], [], new CommitScope("csharp", "w", null)));

        // The TypeScript shard was not touched: same GraphState object, same cached adjacency object.
        Assert.Same(tsStateBefore, _database.GetShardState(tsKey));
        Assert.Same(tsAdjBefore, _database.GetShardAdjacency(tsKey));

        // The C# shard WAS rebuilt.
        Assert.NotSame(csAdjBefore, _database.GetShardAdjacency(csKey));
        Assert.True(_database.ContainsNode("csharp:w:b"));
        Assert.False(_database.ContainsNode("csharp:w:a"));
        Assert.True(_database.ContainsNode("typescript:w:x"));
    }

    // ---- 2. Cross-shard dangling sweep -------------------------------------------------------

    [Fact]
    public async Task ScopedCommit_RemovingTarget_SweepsForeignShardEdge()
    {
        // Shard B holds an edge whose target is a shard-A node.
        await Store.TryCommitGenerationAsync(1,
            [Node("A:w:x", "A", "w"), Node("B:w:src", "B", "w")],
            [Edge("B:w:e", "B:w:src", "A:w:x", "B", "w")]);

        // A's node vanishes and is not re-established -> B's edge must be swept.
        await Store.TryCommitGenerationAsync(2, [], [], new CommitScope("A", "w", null));

        Assert.False(_database.ContainsNode("A:w:x"));
        Assert.False(_database.ContainsEdge("B:w:e"), "edge into a vanished cross-shard target must be swept");
        Assert.True(_database.ContainsNode("B:w:src"));
    }

    [Fact]
    public async Task ScopedCommit_ReestablishingTarget_KeepsForeignShardEdge()
    {
        await Store.TryCommitGenerationAsync(1,
            [Node("A:w:x", "A", "w"), Node("B:w:src", "B", "w")],
            [Edge("B:w:e", "B:w:src", "A:w:x", "B", "w")]);

        // A re-establishes the same node id -> the cross-shard edge survives.
        await Store.TryCommitGenerationAsync(2,
            [Node("A:w:x", "A", "w")], [], new CommitScope("A", "w", null));

        Assert.True(_database.ContainsNode("A:w:x"));
        Assert.True(_database.ContainsEdge("B:w:e"), "edge to a re-established cross-shard target must survive");
    }

    [Fact]
    public async Task ScopedCommit_SweepsDefaultShardEdges_WithRemovedSourceOrTarget()
    {
        await Store.TryCommitGenerationAsync(1,
            [Node("A:w:x", "A", "w"), Node("A:w:y", "A", "w"), Unowned("D:t")],
            [
                Edge("D:fromA", "A:w:x", "D:t", null, null),   // Default edge sourced at an A node
                Edge("D:toA", "D:t", "A:w:y", null, null),     // Default edge targeting an A node
            ]);

        await Store.TryCommitGenerationAsync(2, [], [], new CommitScope("A", "w", null));

        Assert.False(_database.ContainsEdge("D:fromA"), "Default edge with a removed source must be swept");
        Assert.False(_database.ContainsEdge("D:toA"), "Default edge with a removed target must be swept");
        Assert.True(_database.ContainsNode("D:t"), "the un-owned node itself is untouched");
    }

    // ---- 3. Full-replacement partitioning ----------------------------------------------------

    [Fact]
    public void ReplaceAll_PartitionsByOwnership_AndScopedCommitReplacesOnlyItsShard()
    {
        _database.ReplaceAll(
            [Node("A:w:a1", "A", "w"), Node("B:w:b1", "B", "w"), Unowned("plain")],
            []);

        Assert.Equal(new ShardKey("A", "w"), _database.FindShardOfNode("A:w:a1"));
        Assert.Equal(new ShardKey("B", "w"), _database.FindShardOfNode("B:w:b1"));
        Assert.Equal(ShardKey.Default, _database.FindShardOfNode("plain"));

        // A scoped commit to A replaces only A's shard; B and the Default facts survive.
        Assert.True(_database.TryCommitGeneration(1,
            [Node("A:w:a2", "A", "w")], [], new CommitScope("A", "w", null)));

        Assert.False(_database.ContainsNode("A:w:a1"));
        Assert.True(_database.ContainsNode("A:w:a2"));
        Assert.True(_database.ContainsNode("B:w:b1"));
        Assert.True(_database.ContainsNode("plain"));
    }

    [Fact]
    public void RestoreThenScopedCommit_ProducesNoDuplicates()
    {
        // A persisted-snapshot restore flows through ReplaceAll with stamped metadata. If the restored
        // facts landed in Default, the next scoped commit could not replace them and would duplicate.
        _database.ReplaceAll(
            [Node("A:w:1", "A", "w", "/repo/a.cs"), Node("A:w:2", "A", "w", "/repo/a.cs")],
            []);

        Assert.True(_database.TryCommitGeneration(1,
            [Node("A:w:1", "A", "w", "/repo/a.cs")], [], new CommitScope("A", "w", null)));

        Assert.Equal(1, _database.NodeCount);
        Assert.True(_database.ContainsNode("A:w:1"));
        Assert.False(_database.ContainsNode("A:w:2"), "the old restored node must be replaced, not duplicated");
        Assert.Single(_database.EnumerateNodes(), n => n.Id == "A:w:1");
    }

    // ---- 4. PruneFilesNotPresent across shards + sweep ---------------------------------------

    [Fact]
    public void PruneFilesNotPresent_AcrossShards_RemovesNodesAndSweepsCrossShardEdges()
    {
        _database.ReplaceAll(
            [
                Node("A:w:akeep", "A", "w", "/repo/keep.cs"),
                Node("A:w:agone", "A", "w", "/repo/gone.cs"),
                Node("B:w:bkeep", "B", "w", "/repo/b_keep.ts"),
            ],
            [
                Edge("A:w:internal", "A:w:akeep", "A:w:agone", "A", "w"),   // internal A edge to a pruned node
                Edge("B:w:cross", "B:w:bkeep", "A:w:agone", "B", "w"),      // cross-shard edge to a pruned node
            ]);

        var removed = _database.PruneFilesNotPresent(["/repo/keep.cs", "/repo/b_keep.ts"]);

        Assert.Equal(1, removed);
        Assert.True(_database.ContainsNode("A:w:akeep"));
        Assert.False(_database.ContainsNode("A:w:agone"));
        Assert.True(_database.ContainsNode("B:w:bkeep"));
        Assert.False(_database.ContainsEdge("A:w:internal"), "intra-shard edge to a pruned node must go");
        Assert.False(_database.ContainsEdge("B:w:cross"), "cross-shard edge to a pruned node must be swept");
    }

    // ---- 5. Reconciliation staging with scoped commits + rollback ----------------------------

    [Fact]
    public async Task Reconciliation_StagesScopedCommits_UntilCommitOrRollback()
    {
        await Store.TryCommitGenerationAsync(1,
            [Node("A:w:a1", "A", "w"), Node("B:w:b1", "B", "w")], []);

        Store.BeginReconciliation();
        Assert.True(await Store.TryCommitGenerationAsync(2,
            [Node("A:w:a2", "A", "w")], [], new CommitScope("A", "w", null)));

        // Readers still observe the previously committed generation while staged.
        Assert.True(_database.ContainsNode("A:w:a1"));
        Assert.False(_database.ContainsNode("A:w:a2"));

        Store.CommitReconciliation();
        Assert.True(_database.ContainsNode("A:w:a2"));
        Assert.False(_database.ContainsNode("A:w:a1"));
        Assert.True(_database.ContainsNode("B:w:b1"), "the untouched shard survives the reconciliation");

        Store.BeginReconciliation();
        Assert.True(await Store.TryCommitGenerationAsync(3,
            [Node("A:w:a3", "A", "w")], [], new CommitScope("A", "w", null)));
        Store.RollbackReconciliation();

        Assert.True(_database.ContainsNode("A:w:a2"), "rollback restores the committed generation");
        Assert.False(_database.ContainsNode("A:w:a3"));
        Assert.Equal(2, Store.LastCommittedGeneration);
    }

    // ---- 6. Cross-shard duplicate identifier -------------------------------------------------

    [Fact]
    public void UpsertNode_DuplicateIdentifierAcrossShards_Throws()
    {
        var a = Node("A:w:1", "A", "w");
        a.Identifier = "Shared.Public.Symbol";
        _database.UpsertNode(a);

        var b = Node("B:w:1", "B", "w");
        b.Identifier = "Shared.Public.Symbol";

        Assert.Throws<InvalidDataException>(() => _database.UpsertNode(b));
    }

    // ---- 7. Upsert routing via the public repository surface ---------------------------------

    [Fact]
    public async Task RepositoryUpsert_RoutesFactsToTheirOwnershipShard()
    {
        var nodes = new InMemoryNodeRepository(_database);

        await nodes.UpsertAsync(Unowned("plain"));
        await nodes.UpsertAsync(Node("A:w:1", "A", "w"));

        Assert.Equal(ShardKey.Default, _database.FindShardOfNode("plain"));
        Assert.Equal(new ShardKey("A", "w"), _database.FindShardOfNode("A:w:1"));

        // Both are still visible through the ordinary read surface.
        Assert.NotNull(await nodes.GetByIdAsync("plain"));
        Assert.NotNull(await nodes.GetByIdAsync("A:w:1"));
    }

    // ---- Global id uniqueness across shards (last-write-wins on every write path) ------------

    [Fact]
    public void UpsertNode_SameIdUnderDifferentOwnership_KeepsSingleGlobalCopy()
    {
        // Ownership metadata drifts between two upserts of the same id.
        _database.UpsertNode(Node("X", "p1", "w1"));
        _database.UpsertNode(Node("X", "p2", "w2"));

        Assert.Equal(1, _database.NodeCount);
        Assert.Equal(new ShardKey("p2", "w2"), _database.FindShardOfNode("X"));

        // And a single removal wipes it everywhere.
        Assert.True(_database.RemoveNode("X"));
        Assert.Equal(0, _database.NodeCount);
        Assert.Null(_database.FindShardOfNode("X"));
    }

    [Fact]
    public void UpsertEdge_SameIdUnderDifferentOwnership_KeepsSingleGlobalCopy()
    {
        _database.UpsertEdge(Edge("e", "s", "t", "p1", "w1"));
        _database.UpsertEdge(Edge("e", "s", "t", "p2", "w2"));

        Assert.Equal(1, _database.EdgeCount);
        Assert.Equal(new ShardKey("p2", "w2"), _database.FindShardOfEdge("e"));
    }

    [Fact]
    public void ReplaceAll_DuplicateIdsAcrossOwners_CollapseToLastInInputOrder()
    {
        _database.ReplaceAll(
            [Node("dup", "p1", "w1", "/first.cs"), Node("dup", "p2", "w2", "/second.cs")],
            []);

        Assert.Equal(1, _database.NodeCount);
        Assert.Equal(new ShardKey("p2", "w2"), _database.FindShardOfNode("dup"));
        Assert.Equal("/second.cs", _database.GetNode("dup")!.FilePath);
    }

    [Fact]
    public async Task ScopedCommit_DeltaIdCollidingWithForeignShard_DeltaWinsGlobally()
    {
        // An out-of-scope foreign shard co-resides an id the A-delta re-emits.
        await Store.TryCommitGenerationAsync(1,
            [Node("shared", "B", "w", "/from-b.ts")], []);

        Assert.True(await Store.TryCommitGenerationAsync(2,
            [Node("shared", "A", "w", "/from-a.cs")], [], new CommitScope("A", "w", null)));

        Assert.Equal(1, _database.NodeCount);
        Assert.Equal(new ShardKey("A", "w"), _database.FindShardOfNode("shared"));
        Assert.Equal("/from-a.cs", _database.GetNode("shared")!.FilePath);
    }

    [Fact]
    public async Task ScopedCommit_DeltaEvictingForeignNode_DropsThatNodesLocalEdges()
    {
        // Foreign shard (p2,w) holds node "Z" and a fully-local edge whose target is Z.
        await Store.TryCommitGenerationAsync(1,
            [Node("p2:w:src", "p2", "w"), Node("Z", "p2", "w")],
            [Edge("F:local", "p2:w:src", "Z", "p2", "w")]);

        // A delta to (p1,w) reclaims id "Z"; the evicted foreign node's local edge must die with it —
        // never survive and re-associate with the new, unrelated "Z".
        Assert.True(await Store.TryCommitGenerationAsync(2,
            [Node("Z", "p1", "w")], [], new CommitScope("p1", "w", null)));

        Assert.Equal(new ShardKey("p1", "w"), _database.FindShardOfNode("Z"));
        Assert.False(_database.ContainsEdge("F:local"), "an evicted node's local edge must not dangle");
        var edgesToZ = await new InMemoryEdgeRepository(_database).GetByTargetIdAsync("Z");
        Assert.DoesNotContain(edgesToZ, e => e.Id == "F:local");
    }
}
