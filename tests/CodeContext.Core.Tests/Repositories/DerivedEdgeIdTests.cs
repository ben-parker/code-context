using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Repositories;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Plan M1b: the store keeps an edge id as its parts (source + piece + target + piece) only when the parts
/// reproduce the id exactly, and the id as sent otherwise; nothing assumes a worker's id shape. Equality,
/// hashing, ordinal order, delete-by-id, answers and snapshot bytes are those of the text.
/// </summary>
public class DerivedEdgeIdTests
{
    private const string P = "fake:ws-1:";

    [Fact]
    public void Compose_KeepsPartsOnlyWhenTheyReproduceTheIdExactly()
    {
        static string Share(ReadOnlySpan<char> piece) => piece.ToString();
        var source = P + "A";
        var target = P + "B";

        var bundledShape = P + "A=[calls]=>" + P + "B@3:4";
        var parts = Assert.IsType<EdgeId>(EdgeId.Compose(bundledShape, source, target, Share));
        Assert.Same(source, parts.Source);
        Assert.Same(target, parts.Target);
        Assert.Equal("=[calls]=>", parts.Middle);
        Assert.Equal("@3:4", parts.Tail);
        Assert.Equal(bundledShape, parts.ToString());
        Assert.True(parts.Canonical);

        // The target's first occurrence after the source is the split, as the snapshot writer's.
        var twice = P + "A~" + P + "B~" + P + "B";
        var first = Assert.IsType<EdgeId>(EdgeId.Compose(twice, source, target, Share));
        Assert.Equal("~", first.Middle);
        Assert.Equal("~" + P + "B", first.Tail);
        Assert.Equal(twice, first.ToString());

        // Anything else stays the string sent, the same instance.
        foreach (var opaque in new[] { P + "edge-17", "other:" + P + "A" + P + "B", P + "A#7", P + "A" })
            Assert.Same(opaque, EdgeId.Compose(opaque, source, target, Share));
        Assert.Same(bundledShape, EdgeId.Compose(bundledShape, null, target, Share));
        Assert.Same(bundledShape, EdgeId.Compose(bundledShape, source, null, Share));
        Assert.Null(EdgeId.Compose(null, source, target, Share));

        // Empty pieces and empty endpoints reproduce the text too.
        Assert.Equal(source + target, EdgeId.Compose(source + target, source, target, Share).ToString());
        Assert.Equal("xy", EdgeId.Compose("xy", "", "", Share).ToString());

        Assert.False(EdgeId.FromParts("s", "xT", "T", "").Canonical);
        Assert.True(EdgeId.FromParts("s", "x", "T", "T").Canonical);
    }

    /// <summary>
    /// Two keys with the same source and target instances and the same text, split differently (a snapshot can carry
    /// a split no writer makes), are one key: equal, one hash, order 0, and a table takes only the first.
    /// </summary>
    [Fact]
    public void SameTextSplitDifferently_OverTheSameEndpoints_IsOneKey()
    {
        var source = "p:w:a";
        var target = "x";
        var left = EdgeId.FromParts(source, "", target, "xx");
        var right = EdgeId.FromParts(source, "x", target, "x");
        Assert.Equal("p:w:axxx", left.ToString());
        Assert.Equal(left.ToString(), right.ToString());

        Assert.True(EdgeIdKeys.AreEqual(left, right));
        Assert.True(EdgeIdKeys.Comparer.Equals(left, right));
        Assert.Equal(EdgeIdKeys.Comparer.GetHashCode(left), EdgeIdKeys.Comparer.GetHashCode(right));
        Assert.Equal(0, EdgeIdKeys.Compare(left, right));
        var table = new Dictionary<object, int>(EdgeIdKeys.Comparer);
        Assert.True(table.TryAdd(left, 1));
        Assert.False(table.TryAdd(right, 2));
    }

    /// <summary>Equality, hashing, ordinal order and prefix tests of keys of both kinds agree with the text.</summary>
    [Fact]
    public void Keys_CompareHashAndMatchAsTheirText()
    {
        var random = new Random(20261118);
        // A small alphabet, so ids share long prefixes and pieces, with chars on both sides of the surrogate range.
        var alphabet = new[] { 'a', 'b', ':', '=', 'é', '\ud83d', '\ude00', '￿', '\u0000' };
        string Text(int max) => new(Enumerable.Range(0, random.Next(max + 1)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

        var endpoints = Enumerable.Range(0, 6).Select(_ => Text(5)).ToArray();
        var keys = new List<object>();
        for (var i = 0; i < 400; i++)
        {
            var source = endpoints[random.Next(endpoints.Length)];
            var target = endpoints[random.Next(endpoints.Length)];
            var id = random.Next(4) == 0 ? Text(12) : source + Text(3) + target + Text(3);
            keys.Add(EdgeId.Compose(id, source, target, piece => piece.ToString())!);
            keys.Add(new string(id.AsSpan()));
        }
        Assert.Contains(keys, key => key is EdgeId);

        var buffer = new char[64];
        foreach (var left in keys)
        {
            var text = left.ToString()!;
            Assert.Equal(text.Length, EdgeIdKeys.Length(left));
            Assert.Equal(text, EdgeIdKeys.Text(left, buffer).ToString());
            var prefix = text[..random.Next(text.Length + 1)];
            Assert.True(EdgeIdKeys.StartsWith(left, prefix));
            Assert.Equal(text.StartsWith(prefix + "a", StringComparison.Ordinal), EdgeIdKeys.StartsWith(left, prefix + "a"));
            foreach (var right in keys.Take(120))
            {
                var other = right.ToString()!;
                Assert.Equal(Math.Sign(string.CompareOrdinal(text, other)), Math.Sign(EdgeIdKeys.Compare(left, right)));
                var equal = string.Equals(text, other, StringComparison.Ordinal);
                Assert.Equal(equal, EdgeIdKeys.Comparer.Equals(left, right));
                if (equal) Assert.Equal(EdgeIdKeys.Comparer.GetHashCode(left), EdgeIdKeys.Comparer.GetHashCode(right));
            }
        }

        // A table keyed by parts answers a string probe, and the reverse.
        var table = new HashSet<object>(keys.Where(key => key is EdgeId), EdgeIdKeys.Comparer);
        foreach (var key in keys) Assert.Equal(keys.Any(k => k is EdgeId && k.ToString() == key.ToString()), table.Contains(key));
    }

    [Fact]
    public async Task Commit_KeepsDecomposableIdsAsParts_OthersAsSent_AndAnswersAsTheText()
    {
        var (database, applier) = NewStore();
        var sent = Edges();
        Assert.True(await applier.ApplyAsync(Delta("fake", "ws-1", 1, Nodes("A", "B", "C"), sent)));

        var stored = database.EnumerateEdges().ToList();
        Assert.Equal(sent.Select(edge => edge.Id).Order(StringComparer.Ordinal), stored.Select(edge => edge.Id));
        var nodes = database.EnumerateNodes().ToDictionary(node => node.Id!, StringComparer.Ordinal);
        foreach (var edge in stored)
        {
            var original = sent.Single(e => e.Id == edge.Id);
            if (original.Id.StartsWith(original.SourceId, StringComparison.Ordinal)
                && original.Id.IndexOf(original.TargetId, original.SourceId.Length, StringComparison.Ordinal) >= 0)
            {
                var parts = Assert.IsType<EdgeId>(edge.IdKey);
                Assert.Same(nodes[edge.SourceId!].Id, parts.Source);
                Assert.Same(nodes[edge.TargetId!].Id, parts.Target);
            }
            else
            {
                Assert.Same(original.Id, edge.IdKey);
            }
            Assert.True(database.TryGetEdge(new string(original.Id.AsSpan()), out var found));
            Assert.Same(edge, found);
        }
        Assert.Equal(3, stored.Count(edge => edge.IdKey is EdgeId));

        var bySource = database.GetAdjacency().GetEdgesBySource(P + "A");
        Assert.Equal(bySource.Select(edge => edge.Id).Order(StringComparer.Ordinal), bySource.Select(edge => edge.Id));
        Assert.True(bySource.Count >= 3);

        // A file-scoped recommit of the same ids replaces each edge once, whichever kind of key it had.
        Assert.True(await applier.ApplyAsync(Delta("fake", "ws-1", 2, Nodes("A", "B", "C"), Edges(), replacesWorkspace: false)));
        Assert.Equal(stored.Select(edge => edge.Id), database.EnumerateEdges().Select(edge => edge.Id));
    }

    /// <summary>
    /// The delta wins an edge id globally: an id another shard holds as parts is dropped there when a commit
    /// supplies the same id as text that does not decompose (its endpoints are other ids).
    /// </summary>
    [Fact]
    public async Task Commit_OfAnIdAnotherShardHoldsAsParts_DropsTheirCopy()
    {
        var (database, applier) = NewStore();
        Assert.True(await applier.ApplyAsync(Delta("fake", "ws-1", 1, Nodes("A", "B", "C"), Edges())));
        var claimed = P + "A=[calls]=>" + P + "B@3:4";
        Assert.IsType<EdgeId>(database.GetEdge(claimed)!.IdKey);

        var other = new ProtocolNode("fake:ws-2:Z", "fake:Z", "Z", "class", "fake", "z.fake");
        Assert.True(await applier.ApplyAsync(Delta("fake", "ws-2", 1, [other],
            [new ProtocolEdge(claimed, "fake:ws-2:Z", "fake:ws-2:Z", "calls")])));

        var holders = database.EnumerateEdges().Where(edge => edge.Id == claimed).ToList();
        var survivor = Assert.Single(holders);
        Assert.Equal("fake:ws-2:Z", survivor.SourceId);
        Assert.IsType<string>(survivor.IdKey);
        Assert.Equal(database.FindShardOfEdge(claimed), ShardKey.From("fake", "ws-2"));
    }

    /// <summary>A store holding ids as parts writes the bytes the same facts with text ids write, and loads back as parts.</summary>
    [Fact]
    public async Task Snapshot_OfIdsAsParts_IsByteIdenticalToTheText_AndLoadsAsParts()
    {
        var (database, applier) = NewStore();
        Assert.True(await applier.ApplyAsync(Delta("fake", "ws-1", 1, Nodes("A", "B", "C"), Edges())));
        var key = ShardKey.From("fake", "ws-1");

        // The same facts with every id held as text, and one with parts split where the writer would not.
        var nodes = database.EnumerateNodes().ToList();
        var textEdges = database.EnumerateEdges().Select(edge => new CodeEdge
        {
            Id = edge.Id, SourceId = edge.SourceId, TargetId = edge.TargetId, Type = edge.Type, Metadata = edge.Metadata,
        }).ToList();
        var oddEdges = database.EnumerateEdges().Select(edge => new CodeEdge
        {
            Id = edge.Id, SourceId = edge.SourceId, TargetId = edge.TargetId, Type = edge.Type, Metadata = edge.Metadata,
        }).ToList();
        var twice = oddEdges.Single(edge => edge.Id == P + "A~" + P + "B~" + P + "B");
        twice.UseIdKey(EdgeId.FromParts(P + "A", "~" + P + "B~", P + "B", ""));
        Assert.Equal(P + "A~" + P + "B~" + P + "B", twice.Id);

        var parts = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        var text = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input([KeyValuePair.Create(key, (IGraphShard)SnapshotTestSupport.StateOf(nodes, textEdges))]));
        var odd = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input([KeyValuePair.Create(key, (IGraphShard)SnapshotTestSupport.StateOf(nodes, oddEdges))]));
        Assert.Equal(text, parts);
        Assert.Equal(text, odd);

        var loaded = SnapshotTestSupport.LoadInMemory(parts);
        Assert.True(loaded.Loaded);
        var state = Assert.Single(loaded.Shards).Shard;
        Assert.Equal(GraphFacts.Of(database), GraphFacts.Of(state.GetAllNodes(), state.GetAllEdges()));
        Assert.Equal(3, state.GetAllEdges().Count(edge => edge.IdKey is EdgeId));
        Assert.Equal(parts, SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input([KeyValuePair.Create(key, (IGraphShard)state)])));
    }

    private static (InMemoryDatabase Database, AnalysisDeltaApplier Applier) NewStore()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        return (factory.Database, new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance));
    }

    private static List<ProtocolNode> Nodes(params string[] names)
        => names.Select(name => new ProtocolNode(P + name, "fake:" + name, name, "class", "fake", name.ToLowerInvariant() + ".fake")).ToList();

    // Fresh strings per occurrence, as a deserializer produces them: the bundled workers' shape, a third-party
    // opaque id, one that does not start with its source, one without its target, one with the target twice.
    private static List<ProtocolEdge> Edges()
    {
        static string F(string text) => new(text.AsSpan());
        return
        [
            new(F(P + "A=[calls]=>" + P + "B@3:4"), F(P + "A"), F(P + "B"), "calls", new Dictionary<string, string> { ["line"] = "3", ["column"] = "4" }),
            new(F(P + "B|uses|" + P + "C"), F(P + "B"), F(P + "C"), "uses"),
            new(F(P + "edge-17"), F(P + "A"), F(P + "C"), "calls"),
            new(F(P + "zz:" + P + "A>" + P + "B"), F(P + "A"), F(P + "B"), "calls"),
            new(F(P + "A#7"), F(P + "A"), F(P + "C"), "calls"),
            new(F(P + "A~" + P + "B~" + P + "B"), F(P + "A"), F(P + "B"), "calls"),
        ];
    }

    private static AnalysisDelta Delta(string parser, string workspace, long generation, List<ProtocolNode> nodes, List<ProtocolEdge> edges, bool replacesWorkspace = true)
        => new(parser, "1.0", workspace, generation, generation, replacesWorkspace,
            replacesWorkspace ? [] : nodes.Select(node => node.FilePath!).Distinct().ToList(), nodes, edges, IsLastForRequest: true);
}
