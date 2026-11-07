using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Repositories.StoreModel;

namespace CodeContext.Core.Tests.Snapshots;

using GraphState = InMemoryDatabase.GraphState;
using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Which shards a snapshot holds. A shard is persisted only when a load reproduces it exactly and a
/// worker can verify it (<see cref="SnapshotWriter.CheckPersistable"/>); any other shard is skipped
/// with a reason, and every other shard is still written.
/// </summary>
public class SnapshotPersistabilityTests
{
    private static readonly ShardKey Good = new("typescript", "default");
    private static readonly ShardKey Bad = new("csharp", "default");

    public static TheoryData<string> Damages => new(
        "lone surrogate in a name", "lone surrogate in a metadata value", "lone surrogate in a metadata key",
        "lone surrogate in an edge type", "node id without prefix", "edge id without prefix", "edge source without prefix",
        "null edge source", "ownership of another shard", "ownership absent");

    [Theory]
    [MemberData(nameof(Damages))]
    public void DamagedShard_IsSkipped_AndTheOthersWritten(string damage)
    {
        var node = new CodeNode { Id = "csharp:default:A", Identifier = "csharp:A", Name = "A", Metadata = SnapshotTestSupport.Owned(Bad) };
        var edge = new CodeEdge { Id = "csharp:default:A=[CALLS]=>x", SourceId = "csharp:default:A", TargetId = "x", Type = "CALLS", Metadata = SnapshotTestSupport.Owned(Bad) };
        switch (damage)
        {
            case "lone surrogate in a name": node.Name = "A\uD800"; break;
            case "lone surrogate in a metadata value": node.Metadata = SnapshotTestSupport.Owned(Bad, ("kind", "\uDC00")); break;
            case "lone surrogate in a metadata key": edge.Metadata = SnapshotTestSupport.Owned(Bad, ("k\uD800", "v")); break;
            case "lone surrogate in an edge type": edge.Type = "\uD801"; break;
            case "node id without prefix": node.Id = "shared:A"; edge.SourceId = "csharp:default:B"; break;
            case "edge id without prefix": edge.Id = "edge:1"; break;
            case "edge source without prefix": edge.SourceId = "typescript:default:A"; break;
            case "null edge source": edge.SourceId = null; break;
            // A scoped commit routes delta facts to its scope's shard whatever their metadata says.
            case "ownership of another shard": node.Metadata = SnapshotTestSupport.Owned(Good); break;
            case "ownership absent": edge.Metadata = new Dictionary<string, string> { ["line"] = "1" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        var database = SnapshotTestSupport.Commit(
            (Bad, [node], [edge]),
            (Good, [new CodeNode { Id = "typescript:default:T", Identifier = "typescript:T", Name = "T", Metadata = SnapshotTestSupport.Owned(Good) }], []));

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        var written = SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        Assert.Equal([Good], written.Written);
        Assert.Equal(Bad, Assert.Single(written.Skipped).Key);
        Assert.Equal([Good], SnapshotTestSupport.LoadOrFail(path).Shards.Select(shard => shard.Key));
    }

    public static TheoryData<string> StringFields => new(
        "node identifier", "node name", "node type", "node language", "node file path", "node namespace", "node visibility",
        "node signature", "node return type", "node parameters", "node modifiers", "node metrics", "node id",
        "node metadata key", "node metadata value",
        "edge id", "edge source id", "edge target id", "edge type", "edge metadata key", "edge metadata value");

    /// <summary>
    /// A lone surrogate in any string field of a node or an edge: the shard is skipped with a reason,
    /// the writer does not throw, and the other shard is written and loads.
    /// </summary>
    [Theory]
    [MemberData(nameof(StringFields))]
    public void LoneSurrogateInAnyStringField_SkipsTheShard(string field)
    {
        const string Lone = "x\uD800";
        var node = new CodeNode
        {
            Id = "csharp:default:A", Identifier = "csharp:A", Name = "A", Type = "Class", Language = "csharp", FilePath = "/r/A.cs",
            Namespace = "N", Visibility = "public", Signature = "void A()", ReturnType = "void", Parameters = "()", Modifiers = "static",
            Metrics = "{}", Metadata = SnapshotTestSupport.Owned(Bad, ("kind", "k")),
        };
        var edge = new CodeEdge
        {
            Id = "csharp:default:A=[CALLS]=>x", SourceId = "csharp:default:A", TargetId = "x", Type = "CALLS",
            Metadata = SnapshotTestSupport.Owned(Bad, ("via", "v")),
        };
        switch (field)
        {
            case "node identifier": node.Identifier = Lone; break;
            case "node name": node.Name = Lone; break;
            case "node type": node.Type = Lone; break;
            case "node language": node.Language = Lone; break;
            case "node file path": node.FilePath = Lone; break;
            case "node namespace": node.Namespace = Lone; break;
            case "node visibility": node.Visibility = Lone; break;
            case "node signature": node.Signature = Lone; break;
            case "node return type": node.ReturnType = Lone; break;
            case "node parameters": node.Parameters = Lone; break;
            case "node modifiers": node.Modifiers = Lone; break;
            case "node metrics": node.Metrics = Lone; break;
            case "node id": node.Id = "csharp:default:" + Lone; edge.SourceId = node.Id; edge.Id = node.Id + "=[CALLS]=>x"; break;
            case "node metadata key": node.Metadata = SnapshotTestSupport.Owned(Bad, (Lone, "v")); break;
            case "node metadata value": node.Metadata = SnapshotTestSupport.Owned(Bad, ("kind", Lone)); break;
            case "edge id": edge.Id = "csharp:default:" + Lone; break;
            case "edge source id": edge.SourceId = "csharp:default:" + Lone; break;
            case "edge target id": edge.TargetId = Lone; break;
            case "edge type": edge.Type = Lone; break;
            case "edge metadata key": edge.Metadata = SnapshotTestSupport.Owned(Bad, (Lone, "v")); break;
            case "edge metadata value": edge.Metadata = SnapshotTestSupport.Owned(Bad, ("via", Lone)); break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        var database = SnapshotTestSupport.Commit(
            (Bad, [node], [edge]),
            (Good, [new CodeNode { Id = "typescript:default:T", Identifier = "typescript:T", Name = "T", Metadata = SnapshotTestSupport.Owned(Good) }], []));

        using var stream = new System.IO.MemoryStream();
        var written = SnapshotWriter.Write(stream, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)), SnapshotTestSupport.Key);
        var bytes = stream.ToArray();
        Assert.Equal([Good], written.Written);
        Assert.Contains("lone surrogate", Assert.Single(written.Skipped, skip => skip.Key == Bad).Reason, StringComparison.Ordinal);
        Assert.Equal([Good], SnapshotTestSupport.LoadInMemory(bytes).Shards.Select(shard => shard.Key));
    }

    /// <summary>
    /// Coordinator decision (plan section 3.11, "lone surrogate in a string, id without ownership
    /// prefix": shard not persisted; D6: a shard that fails is rebuilt by a scan), pinned here: shard A
    /// holds an edge to node X of shard B, B is not persistable, so only A is written. After the load,
    /// B's worker commits its workspace without X. The loaded store keeps A's edge, dangling, where the
    /// live store would have swept it when X left B. That is accepted, because it equals a fresh scan:
    /// A's worker emits the edge whatever B holds, and a fresh store that commits A and then B without X
    /// keeps it too. The live store's sweep depends on history (X having been there), and no sweep rule
    /// is added for it.
    /// </summary>
    [Fact]
    public async Task SkippedShard_LoadedStoreAfterItsWorkerCommits_ReadsAsAFreshScan_NotAsTheLiveStore()
    {
        var x = new CodeNode { Id = "csharp:default:X", Identifier = "csharp:X", Name = "X", FilePath = "/r/X.cs", Metadata = SnapshotTestSupport.Owned(Bad) };
        // The lone surrogate keeps B out of the snapshot.
        var y = new CodeNode { Id = "csharp:default:Y", Identifier = "csharp:Y", Name = "Y\uD800", FilePath = "/r/Y.cs", Metadata = SnapshotTestSupport.Owned(Bad) };
        IReadOnlyList<CodeNode> aNodes = [new CodeNode { Id = "typescript:default:T", Identifier = "typescript:T", Name = "T", FilePath = "/r/T.ts", Metadata = SnapshotTestSupport.Owned(Good) }];
        IReadOnlyList<CodeEdge> aEdges = [new CodeEdge { Id = "typescript:default:T=[calls]=>csharp:default:X", SourceId = "typescript:default:T", TargetId = "csharp:default:X", Type = "calls", Metadata = SnapshotTestSupport.Owned(Good, ("line", "1")) }];
        var live = SnapshotTestSupport.Commit((Good, aNodes, aEdges), (Bad, [x, y], []));

        var bytes = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(live)));
        var loaded = SnapshotTestSupport.StoreOf(SnapshotTestSupport.LoadInMemory(bytes).ForPublish());
        Assert.Equal([Good], loaded.ShardKeys.Where(key => loaded.GetShardState(key) is IGraphShard { NodeCount: > 0 }));

        // B's worker commits its whole workspace, now without X (Y fixed).
        CodeNode[] bWithoutX = [new CodeNode { Id = "csharp:default:Y", Identifier = "csharp:Y", Name = "Y", FilePath = "/r/Y.cs", Metadata = SnapshotTestSupport.Owned(Bad) }];
        var scopeB = new CommitScope(Bad.ParserId, Bad.WorkspaceId, null);
        Assert.True(loaded.TryCommitGeneration(1, bWithoutX, [], scopeB));
        Assert.True(live.TryCommitGeneration(3, bWithoutX, [], scopeB));
        var fresh = SnapshotTestSupport.Commit((Good, aNodes, aEdges), (Bad, bWithoutX, []));

        var loadedMatrix = (await StoreReadMatrix.CaptureAsync(loaded)).Skip(1).ToList();
        StoreReadMatrix.AssertEqual((await StoreReadMatrix.CaptureAsync(fresh)).Skip(1).ToList(), loadedMatrix, "loaded store after B's commit against a fresh scan");
        Assert.True(loaded.TryGetEdge(aEdges[0].Id!, out _), "the loaded store keeps A's edge, as a fresh scan does");
        Assert.False(live.TryGetEdge(aEdges[0].Id!, out _), "the live store swept A's edge when X left B");
    }

    [Fact]
    public void EscapedWorkspacePrefix_IsTheOneRequired()
    {
        var key = new ShardKey("csharp", "w 1");
        ShardKey Write(string id)
        {
            var database = SnapshotTestSupport.Commit((key, [new CodeNode { Id = id, Identifier = "I", Metadata = SnapshotTestSupport.Owned(key) }], []));
            var written = SnapshotWriter.Write(new System.IO.MemoryStream(), SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)), SnapshotTestSupport.Key);
            return written.Written.SingleOrDefault();
        }
        Assert.Equal(key, Write("csharp:w%201:A"));
        Assert.Equal(default, Write("csharp:w 1:A"));
    }

    [Fact]
    public void DefaultShard_IsNeverPersisted()
    {
        var database = new InMemoryDatabase();
        database.ReplaceAll(
            [
                new CodeNode { Id = "default:only", Identifier = "D", Name = "D" },
                new CodeNode { Id = "typescript:default:T", Identifier = "T", Metadata = SnapshotTestSupport.Owned(Good) },
            ],
            []);
        var written = SnapshotWriter.Write(new System.IO.MemoryStream(), SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)), SnapshotTestSupport.Key);
        Assert.Equal([Good], written.Written);
        Assert.Equal(ShardKey.Default, Assert.Single(written.Skipped).Key);
    }

    [Fact]
    public async Task ShardWhoseFactsWereAllRemoved_IsSkipped_AndTheLoadedStoreReadsTheSame()
    {
        var database = SnapshotTestSupport.Commit(
            (Bad, [new CodeNode { Id = "csharp:default:A", Identifier = "A", FilePath = "/r/A.cs", Metadata = SnapshotTestSupport.Owned(Bad) }], []),
            (Good, [new CodeNode { Id = "typescript:default:T", Identifier = "T", FilePath = "/r/T.ts", Metadata = SnapshotTestSupport.Owned(Good) }], []));
        Assert.True(database.TryCommitGeneration(3, [], [], new CommitScope(Bad.ParserId, Bad.WorkspaceId, ["/r/A.cs"])));
        Assert.Contains(Bad, database.ShardKeys);   // the store keeps the emptied key

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        var written = SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        Assert.Equal([Good], written.Written);
        Assert.Contains("no fact", Assert.Single(written.Skipped).Reason, StringComparison.Ordinal);

        var reloaded = SnapshotTestSupport.StoreOf(SnapshotTestSupport.LoadOrFail(path).ForPublish());
        StoreReadMatrix.AssertEqual(
            (await StoreReadMatrix.CaptureAsync(database)).Skip(1).ToList(),
            (await StoreReadMatrix.CaptureAsync(reloaded)).Skip(1).ToList(),
            "emptied shard");
    }

    [Fact]
    public void DerivedLegacyIdentifier_RoundTrips()
    {
        var database = SnapshotTestSupport.Commit((Good,
            [new CodeNode { Id = "typescript:default:src/a.ts#A", Identifier = "", Metadata = SnapshotTestSupport.Owned(Good) }], []));
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        var shard = Assert.Single(SnapshotTestSupport.LoadOrFail(path).Shards).Shard;
        Assert.True(shard.TryGetNodeIdByIdentifier("src/a.ts#A", out var id));
        Assert.Equal("typescript:default:src/a.ts#A", id);
        Assert.True(shard.TryGetNode(id!, out var node));
        Assert.Equal("src/a.ts#A", node!.Identifier);
    }

    [Fact]
    public void IdentifierThatDoesNotResolveToItsNode_SkipsTheShard()
    {
        var state = new GraphState();
        state.Nodes["typescript:default:A"] = new CodeNode { Id = "typescript:default:A", Identifier = "A", Metadata = SnapshotTestSupport.Owned(Good) };
        var reason = SnapshotWriter.CheckPersistable(Good, state);
        Assert.NotNull(reason);
        Assert.Contains("does not resolve", reason, StringComparison.Ordinal);
    }
}
