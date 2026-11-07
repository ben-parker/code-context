using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Repositories.StoreModel;

namespace CodeContext.Core.Tests.Snapshots;

using GraphState = InMemoryDatabase.GraphState;
using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// A store written to a snapshot and loaded back answers every read exactly as the store it was
/// written from: random graphs field by field and read by read, and stores produced by the phase 1
/// scenario generator, snapshotted at several points, compared with the whole store read matrix.
/// </summary>
public class SnapshotRoundTripTests
{
    public static TheoryData<int> GraphSeeds => new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);

    [Theory]
    [MemberData(nameof(GraphSeeds))]
    public async Task RandomGraph_ReproducesEveryFieldAndEveryReadPath(int seed)
    {
        using var temp = new SnapshotTempDirectory();
        var original = SnapshotTestSupport.Commit(new SnapshotGraphGenerator(seed).Generate());
        var shards = SnapshotTestSupport.ShardsOf(original);
        var path = temp.Snapshot();
        var written = SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(shards));
        Assert.DoesNotContain(written.Skipped, skip => !skip.Reason.Contains("no fact", StringComparison.Ordinal));

        var loaded = SnapshotTestSupport.LoadOrFail(path);
        Assert.Equal(7UL, loaded.Header!.StoreGeneration);
        Assert.Equal(written.Written, loaded.Shards.Select(shard => shard.Key));
        foreach (var shard in loaded.Shards)
        {
            var source = (IGraphShard)original.GetShardState(shard.Key)!;
            var vocabulary = ShardReadMatrix.Vocabulary.From(source);
            ShardReadMatrix.AssertEqual(
                ShardReadMatrix.Of(source).Capture(vocabulary),
                ShardReadMatrix.Of(shard.Shard).Capture(vocabulary),
                $"seed {seed}, shard {shard.Key}");
        }

        var reloaded = SnapshotTestSupport.StoreOf(loaded.ForPublish());
        StoreReadMatrix.AssertEqual(
            (await StoreReadMatrix.CaptureAsync(original)).Skip(1).ToList(),
            (await StoreReadMatrix.CaptureAsync(reloaded)).Skip(1).ToList(),
            $"seed {seed}, whole store");
    }

    /// <summary>
    /// Requirement: the loaded store, published into an empty store, reads line for line like the
    /// store the snapshot was written from. Stores come from the phase 1 scenario generator (in the
    /// mode whose owned facts are persistable, and in the default mode, where most shards are not),
    /// snapshotted every 20 operations. The expected store is the source store's persisted shards
    /// published alone; when every skipped shard holds no fact it must also read as the source.
    /// The generation line is compared with the header instead (publishing does not set it).
    /// </summary>
    [Theory]
    [InlineData(101, true)]
    [InlineData(102, true)]
    [InlineData(103, true)]
    [InlineData(104, true)]
    [InlineData(105, true)]
    [InlineData(106, true)]
    [InlineData(107, false)]
    [InlineData(108, false)]
    public async Task ScenarioStores_LoadIntoStoresThatReadTheSame(int seed, bool persistable)
    {
        using var temp = new SnapshotTempDirectory();
        var database = new InMemoryDatabase();
        var model = new ReferenceGraphModel();
        var generator = new StoreScenarioGenerator(seed, persistable);
        var persistedShards = 0;
        var wholeStoreEqual = 0;
        var reasons = new List<string>();
        ulong sequence = 0;

        for (var step = 1; step <= 160; step++)
        {
            var operation = generator.Next(model);
            operation.ApplyTo(model);
            operation.ApplyTo(database, GraphState.CopyOf);
            if (step % 20 != 0) continue;

            var shards = SnapshotTestSupport.ShardsOf(database);
            var path = temp.Snapshot(++sequence);
            var written = SnapshotTestSupport.WriteFile(
                path, SnapshotTestSupport.Input(shards, sequence, (ulong)database.LastCommittedGeneration));
            var loaded = SnapshotTestSupport.LoadOrFail(path);
            var context = $"seed {seed}, step {step}, written [{string.Join(", ", written.Written)}], "
                + $"skipped [{string.Join(", ", written.Skipped.Select(skip => $"{skip.Key}: {skip.Reason}"))}]";
            Assert.Equal((ulong)database.LastCommittedGeneration, loaded.Header!.StoreGeneration);
            persistedShards += written.Written.Count;
            reasons.AddRange(written.Skipped.Select(skip => $"{skip.Key}: {skip.Reason}"));

            var expected = SnapshotTestSupport.StoreOf(shards.Where(pair => written.Written.Contains(pair.Key)));
            var reloaded = SnapshotTestSupport.StoreOf(loaded.ForPublish());
            var reloadedMatrix = (await StoreReadMatrix.CaptureAsync(reloaded, generator.IssuedIdentifiers)).Skip(1).ToList();
            StoreReadMatrix.AssertEqual(
                (await StoreReadMatrix.CaptureAsync(expected, generator.IssuedIdentifiers)).Skip(1).ToList(),
                reloadedMatrix,
                context);

            if (written.Skipped.All(skip => database.GetShardState(skip.Key) is IGraphShard { NodeCount: 0, EdgeCount: 0 }))
            {
                StoreReadMatrix.AssertEqual(
                    (await StoreReadMatrix.CaptureAsync(database, generator.IssuedIdentifiers)).Skip(1).ToList(),
                    reloadedMatrix,
                    context + " (against the source store)");
                wholeStoreEqual++;
            }
        }

        Assert.True(persistedShards > 0, $"seed {seed} persisted no shard");
        if (persistable) Assert.True(wholeStoreEqual > 0, $"seed {seed} never persisted a whole store: {string.Join("; ", reasons)}");
    }

    [Fact]
    public void ShardInfo_RoundTripsOpaque()
    {
        using var temp = new SnapshotTempDirectory();
        var key = new ShardKey("csharp", "default");
        var database = SnapshotTestSupport.Commit((key,
            [new CodeNode { Id = "csharp:default:A", Identifier = "A", Name = "A", Metadata = SnapshotTestSupport.Owned(key) }], []));
        var coverage = Encoding.UTF8.GetBytes("[{\"project\":\"A\",\"ü\":1}]");
        var fingerprints = new byte[] { 0x5B, 0x00, 0xFF, 0x5D };   // opaque: not even valid UTF-8
        var info = new Dictionary<ShardKey, SnapshotShardInfo>
        {
            [key] = new("1.2.3", "worker-csharp.0000000042.5d0c9a1e.state", "5d0c9a1e", new string('a', 64), coverage, fingerprints),
        };
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database), info: info));

        var loaded = Assert.Single(SnapshotTestSupport.LoadOrFail(path).Shards).Info;
        Assert.Equal("1.2.3", loaded.ParserVersion);
        Assert.Equal("worker-csharp.0000000042.5d0c9a1e.state", loaded.StateFileName);
        Assert.Equal("5d0c9a1e", loaded.StateId);
        Assert.Equal(new string('a', 64), loaded.StateSha256);
        Assert.Equal(coverage, loaded.Coverage);
        Assert.Equal(fingerprints, loaded.Fingerprints);
    }

    [Fact]
    public void Writer_IsDeterministic_AndIndependentOfCommitOrder()
    {
        var graph = new SnapshotGraphGenerator(42).Generate();
        var forward = SnapshotTestSupport.Commit(graph);
        var backward = SnapshotTestSupport.Commit(graph.Reverse()
            .Select(shard => (shard.Key, (IReadOnlyList<CodeNode>)shard.Nodes.Reverse().ToList(), (IReadOnlyList<CodeEdge>)shard.Edges.Reverse().ToList()))
            .ToArray());
        var first = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(forward)));
        var again = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(forward)));
        var reordered = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(backward).Reverse()));
        Assert.Equal(first, again);
        Assert.Equal(first, reordered);
    }

    [Fact]
    public void EdgeIds_OfTheWorkerShapes_AreStoredComposed()
    {
        var key = new ShardKey("csharp", "default");
        var source = "csharp:default:A";
        var target = "csharp:default:B";
        string[] ids =
        [
            $"{source}=[CALLS]=>{target}@3:4",      // C# worker
            $"{source}=[calls]=>{target}",          // TypeScript worker, no position
            $"{source}->contains->{target}",        // fake worker
            "csharp:default:verbatim-edge",         // does not decompose
            $"csharp:default:via:{target}:tail",    // holds the target but does not start with the source
        ];
        var database = SnapshotTestSupport.Commit((key,
            [
                new CodeNode { Id = source, Identifier = "A", Metadata = SnapshotTestSupport.Owned(key) },
                new CodeNode { Id = target, Identifier = "B", Metadata = SnapshotTestSupport.Owned(key) },
            ],
            ids.Select(id => new CodeEdge { Id = id, SourceId = source, TargetId = target, Type = "CALLS", Metadata = SnapshotTestSupport.Owned(key) }).ToList()));

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        var opened = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect());
        using var file = opened.File!;
        var reader = Assert.Single(file.Shards);
        var forms = new Dictionary<string, uint>(StringComparer.Ordinal);
        var (edgesOffset, _) = reader.SectionRange(SnapshotSectionKind.Edges);
        for (var j = 0; j < reader.EdgeCount; j++)
            forms[reader.ReadEdge(j).Id!] = file.Region.ReadU32(edgesOffset + j * SnapshotFormat.EdgeRecordSize + 28) & SnapshotFormat.EdgeIdFormMask;
        Assert.Equal(SnapshotFormat.EdgeIdFormComposed, forms[ids[0]]);
        Assert.Equal(SnapshotFormat.EdgeIdFormComposed, forms[ids[1]]);
        Assert.Equal(SnapshotFormat.EdgeIdFormComposed, forms[ids[2]]);
        Assert.Equal(SnapshotFormat.EdgeIdFormWhole, forms[ids[3]]);
        Assert.Equal(SnapshotFormat.EdgeIdFormWhole, forms[ids[4]]);
        foreach (var id in ids) Assert.True(reader.TryGetEdge(id, out var edge) && edge!.Id == id);
    }

    [Theory]
    [InlineData("12", true)]
    [InlineData("-3", true)]
    [InlineData("0", true)]
    [InlineData("2147483647", true)]
    [InlineData("-2147483648", false)]   // the absent marker stays in the metadata set
    [InlineData("007", false)]
    [InlineData("+5", false)]
    [InlineData("-0", false)]
    [InlineData("", false)]
    [InlineData(" 1", false)]
    [InlineData("2147483648", false)]
    [InlineData("abc", false)]
    public void Positions_AreHeldInTheRecordOnlyWhenCanonical(string value, bool canonical)
        => Assert.Equal(canonical, SnapshotWriter.TryCanonicalPosition(value, out _));
}
