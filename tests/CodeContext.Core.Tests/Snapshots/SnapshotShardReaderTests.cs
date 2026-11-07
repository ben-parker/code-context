using System;
using System.Collections.Generic;
using System.Linq;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Repositories.StoreModel;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The in-place accessors of <see cref="SnapshotShardReader"/>, asked directly on the mapped file
/// (no materialized shard), against the heap shard the snapshot was written from: node by id
/// (IdHash), node id by identifier (IdentifierHash), edge by id (EdgeIdHash), edges by source and by
/// target in canonical order, exact and substring names (index candidates a superset, results equal
/// after the heap predicate), by file path, the file path list, the type histogram, cross-shard edge
/// ids and counts. These are the reads the mapped shard of phase 8 serves.
/// </summary>
public class SnapshotShardReaderTests
{
    public static TheoryData<int> Seeds => new(21, 22, 23, 24, 25, 26, 27, 28);

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Accessors_AnswerAsTheHeapShard(int seed)
    {
        using var temp = new SnapshotTempDirectory();
        var database = SnapshotTestSupport.Commit(new SnapshotGraphGenerator(seed).Generate());
        AssertReaderMatchesSource(temp, database, $"seed {seed}");
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(203)]
    public void Accessors_AnswerAsTheHeapShard_OverScenarioStores(int seed)
    {
        using var temp = new SnapshotTempDirectory();
        var database = new InMemoryDatabase();
        var model = new ReferenceGraphModel();
        var generator = new StoreScenarioGenerator(seed, persistableFacts: true);
        ulong sequence = 0;
        for (var step = 1; step <= 120; step++)
        {
            var operation = generator.Next(model);
            operation.ApplyTo(model);
            operation.ApplyTo(database, InMemoryDatabase.GraphState.CopyOf);
            if (step % 30 == 0) AssertReaderMatchesSource(temp, database, $"seed {seed}, step {step}", ++sequence);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void NameCandidates_AreASupersetOfTheHeapMatches(int seed)
    {
        using var temp = new SnapshotTempDirectory();
        var database = SnapshotTestSupport.Commit(new SnapshotGraphGenerator(seed).Generate());
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        using var file = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!;
        foreach (var reader in file.Shards)
        {
            var source = (IGraphShard)database.GetShardState(reader.Key)!;
            var vocabulary = ShardReadMatrix.Vocabulary.From(source);
            foreach (var name in vocabulary.Names)
                foreach (var exact in new[] { true, false })
                {
                    var candidates = reader.FindNameCandidates(name, exact).Select(index => reader.ReadNode(index).Id!).ToHashSet(StringComparer.Ordinal);
                    foreach (var match in source.FindNodesByName(name, exact))
                        Assert.True(candidates.Contains(match.Id!), $"{reader.Key}: '{name}' exact={exact} misses candidate {match.Id}");
                }
        }
    }

    [Fact]
    public void Lookups_OnAShardOfOneNode_AndOnMisses()
    {
        var key = new ShardKey("csharp", "default");
        var database = SnapshotTestSupport.Commit((key,
            [new CodeNode { Id = "csharp:default:A", Identifier = "csharp:A", Name = "Alpha", Type = "Class", FilePath = "/r/A.cs", Metadata = SnapshotTestSupport.Owned(key) }],
            [new CodeEdge { Id = "csharp:default:A=[CALLS]=>other:B", SourceId = "csharp:default:A", TargetId = "other:B", Type = "CALLS", Metadata = SnapshotTestSupport.Owned(key, ("line", "4")) }]));
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        using var file = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!;
        var reader = Assert.Single(file.Shards);

        Assert.Equal((1, 1, 2), (reader.NodeCount, reader.EdgeCount, reader.IdCount));
        Assert.True(reader.TryGetNodeIdByIdentifier("csharp:A", out var id));
        Assert.Equal("csharp:default:A", id);
        Assert.False(reader.TryGetNodeIdByIdentifier("csharp:a", out _));
        Assert.True(reader.TryFindSid("other:B", out _));          // an edge target is an id, not a node
        Assert.False(reader.TryGetNode("other:B", out _));
        Assert.Equal("4", Assert.Single(reader.GetEdgesByTarget("other:B")).Metadata!["line"]);
        Assert.Equal(["csharp:default:A=[CALLS]=>other:B"], reader.GetCrossShardEdgeIds());
        Assert.Equal(["/r/A.cs"], reader.GetFilePaths());
        Assert.Single(reader.FindNodesByName("ALPHA", exact: true));
        Assert.Single(reader.FindNodesByName("lph", exact: false));
        Assert.Empty(reader.FindNodesByName("\uD800", exact: false));
    }

    private static void AssertReaderMatchesSource(SnapshotTempDirectory temp, InMemoryDatabase database, string context, ulong sequence = 1)
    {
        var path = temp.Snapshot(sequence);
        var written = SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database), sequence));
        var opened = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect());
        using var file = opened.File ?? throw new InvalidOperationException($"{opened.Rejection}: {opened.Reason}");
        Assert.Equal(written.Written, file.Shards.Select(reader => reader.Key));
        foreach (var reader in file.Shards)
        {
            var source = (IGraphShard)database.GetShardState(reader.Key)!;
            var vocabulary = ShardReadMatrix.Vocabulary.From(source);
            ShardReadMatrix.AssertEqual(
                ShardReadMatrix.Of(source).Capture(vocabulary),
                ShardReadMatrix.Of(reader).Capture(vocabulary),
                $"{context}, shard {reader.Key}");
        }
    }
}
