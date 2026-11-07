using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using static SnapshotTestSupport;
using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// What an authenticated file must also be before anything is answered from it: the open-time
/// checks of the name table, the file buckets and the section layout (cheap: names and files, never
/// per node or edge), the read-time rule that no result holds a fact twice, and the loader's use of
/// the writer's own persistability rule. Every damaged file here is re-signed, so it passes
/// authentication, as a writer bug would.
/// </summary>
public class SnapshotValidationTests
{
    private static readonly ShardKey Shard = new("csharp", "default");

    /// <summary>
    /// Three nodes (two share a folded name, two share a file), two edges into B (one of them a
    /// verbatim id, stored whole), two edges out of the shard.
    /// </summary>
    private static readonly Lazy<byte[]> SmallFile = new(() =>
    {
        CodeNode Node(string local, string name, string path) => new()
        {
            Id = $"csharp:default:{local}", Identifier = $"csharp:{local}", Name = name, Type = "Class", FilePath = path, Metadata = Owned(Shard),
        };
        CodeEdge Edge(string id, string source, string target) => new()
        {
            Id = id, SourceId = $"csharp:default:{source}", TargetId = target, Type = "CALLS", Metadata = Owned(Shard, ("line", "3")),
        };
        var database = Commit((Shard,
            [Node("A", "Alpha", "/r/A.cs"), Node("B", "alpha", "/r/A.cs"), Node("C", "Gamma", "/r/C.cs")],
            [
                Edge("csharp:default:A=[CALLS]=>csharp:default:B@3:4", "A", "csharp:default:B"),
                Edge("csharp:default:verbatim", "C", "csharp:default:B"),
                Edge("csharp:default:x1", "A", "elsewhere:X"),
                Edge("csharp:default:x2", "C", "elsewhere:Y"),
            ]));
        return WriteBytes(Input(ShardsOf(database)));
    });

    private static (byte[] Bytes, ShardLayout Layout) Small() => (SmallFile.Value, LayoutOf(SmallFile.Value).Shards[0]);

    // ---- P3: the loader refuses facts the writer never writes ----

    [Fact]
    public void EdgeWithNullSource_IsRefusedByTheLoader()
    {
        var (bytes, layout) = Small();
        var damaged = Damaged(bytes, b => W32(b, EdgeRecordOf(b, layout, "csharp:default:verbatim"), SnapshotFormat.NoIndex));
        AssertRefusedByTheLoader(damaged, "source '' of edge 'csharp:default:verbatim'");
    }

    [Fact]
    public void NodeIdWithoutTheShardPrefix_IsRefusedByTheLoader()
    {
        var (bytes, layout) = Small();
        var damaged = Damaged(bytes, b => b[StringStart(b, layout, "csharp:default:A")] = (byte)'Z');
        AssertRefusedByTheLoader(damaged, "node id 'Zsharp:default:A' does not start with 'csharp:default:'");
    }

    [Fact]
    public void EdgeIdWithoutTheShardPrefix_IsRefusedByTheLoader()
    {
        var (bytes, layout) = Small();
        var damaged = Damaged(bytes, b => b[StringStart(b, layout, "csharp:default:x1")] = (byte)'Z');
        AssertRefusedByTheLoader(damaged, "edge id 'Zsharp:default:x1' does not start with");
    }

    [Fact]
    public void NodeWithNullIdentifier_IsRefusedByTheLoader()
    {
        var (bytes, layout) = Small();
        var damaged = Damaged(bytes, b => W32(b, layout.Sections[SnapshotSectionKind.Nodes].Offset + 4, 0));
        AssertRefusedByTheLoader(damaged, "has a null identifier");
    }

    /// <summary>
    /// What is legal: a null metadata value, which a delta can carry and the writer writes as string
    /// reference 0. Not legal (each refused above or by the reader): a null metadata key, a null node
    /// or edge id, a null edge source, a null identifier, an id without the prefix.
    /// </summary>
    [Fact]
    public void NullMetadataValue_RoundTrips()
    {
        var database = Commit((Shard,
            [new CodeNode { Id = "csharp:default:A", Identifier = "A", Metadata = new Dictionary<string, string>(Owned(Shard)) { ["k"] = null! } }],
            []));
        var loaded = LoadInMemory(WriteBytes(Input(ShardsOf(database))));
        Assert.True(loaded.Loaded, loaded.Reason);
        var node = Assert.Single(Assert.Single(loaded.Shards).Shard.Nodes.Values);
        Assert.True(node.Metadata!.TryGetValue("k", out var value));
        Assert.Null(value);
    }

    // ---- T1: an id, an edge id or an identifier in two shards ----

    [Fact]
    public void SameNodeIdInTwoShards_IsRefused()
    {
        // ("fake","ws") and ("fake:ws","z") both accept ids starting "fake:ws:z:" (the parser id is not escaped).
        var a = new ShardKey("fake", "ws");
        var b = new ShardKey("fake:ws", "z");
        AssertRefusedAcrossShards(
            (a, [NodeOf(a, "fake:ws:z:A", "I1")], []),
            (b, [NodeOf(b, "fake:ws:z:A", "I2")], []),
            "Node id 'fake:ws:z:A' appears twice");
    }

    [Fact]
    public void SameIdentifierInTwoShards_IsRefused()
    {
        var a = new ShardKey("fake", "ws");
        var b = new ShardKey("typescript", "default");
        AssertRefusedAcrossShards(
            (a, [NodeOf(a, "fake:ws:A", "shared")], []),
            (b, [NodeOf(b, "typescript:default:B", "shared")], []),
            "Identifier 'shared' appears twice");
    }

    [Fact]
    public void SameEdgeIdInTwoShards_IsRefused()
    {
        var a = new ShardKey("fake", "ws");
        var b = new ShardKey("fake:ws", "z");
        CodeEdge EdgeOf(ShardKey key) => new() { Id = "fake:ws:z:e", SourceId = "fake:ws:z:S", TargetId = "t", Type = "calls", Metadata = Owned(key) };
        AssertRefusedAcrossShards(
            (a, [NodeOf(a, "fake:ws:A", "I1")], [EdgeOf(a)]),
            (b, [NodeOf(b, "fake:ws:z:B", "I2")], [EdgeOf(b)]),
            "Edge id 'fake:ws:z:e' appears twice");
    }

    private static CodeNode NodeOf(ShardKey key, string id, string identifier) => new() { Id = id, Identifier = identifier, Metadata = Owned(key) };

    private static void AssertRefusedAcrossShards(
        (ShardKey Key, CodeNode[] Nodes, CodeEdge[] Edges) first, (ShardKey Key, CodeNode[] Nodes, CodeEdge[] Edges) second, string reason)
    {
        // Two heap shards the store would never hold together, handed to the writer directly.
        var shards = new[] { first, second }
            .Select(shard => KeyValuePair.Create(shard.Key, (IGraphShard)StateOf(shard.Nodes, shard.Edges)))
            .ToList();
        var bytes = WriteBytes(Input(shards));
        var loaded = LoadInMemory(bytes);
        Assert.Equal(SnapshotRejection.Structure, loaded.Rejection);
        Assert.Contains(reason, loaded.Reason, StringComparison.Ordinal);
    }

    // ---- P4: index ranges are checked at open, and no result holds a fact twice ----

    [Fact]
    public void NameEntriesSharingOnePostingsRange_AreRefusedAtOpen()
    {
        // The reviewer's amplification: 2,000 names all pointing at the whole postings section made one
        // substring query cost names x nodes and return 1.2M duplicates.
        var nodes = Enumerable.Range(0, 2000).Select(i => new CodeNode
        {
            Id = $"csharp:default:n{i}", Name = $"Q{i}", Identifier = $"ident{i}", FilePath = "/repo/a.cs", Type = "Method", Metadata = Owned(Shard),
        }).ToList();
        var bytes = WriteBytes(Input(ShardsOf(Commit((Shard, nodes, Array.Empty<CodeEdge>())))));
        var layout = LayoutOf(bytes).Shards[0];
        var (names, length) = layout.Sections[SnapshotSectionKind.NameTable];
        var postings = layout.Sections[SnapshotSectionKind.NamePostings].Length / 4;
        AssertRefusedAtOpen(Damaged(bytes, b =>
        {
            for (var at = names; at < names + length; at += SnapshotFormat.NameEntrySize)
            {
                W32(b, at + 4, 0);
                W32(b, at + 8, (uint)postings);
            }
        }), "postings do not follow");
    }

    [Fact]
    public void NamePostingsThatDoNotCoverTheSection_AreRefusedAtOpen()
    {
        var (bytes, layout) = Small();
        var (names, length) = layout.Sections[SnapshotSectionKind.NameTable];
        var last = names + length - SnapshotFormat.NameEntrySize;
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, last + 8, U32(b, last + 8) + 1)), "cover the postings section");
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, names + 8, 0)), "postings do not follow");
    }

    [Fact]
    public void NameEntryNamingAnotherString_IsRefusedAtOpen()
    {
        var (bytes, layout) = Small();
        var names = layout.Sections[SnapshotSectionKind.NameTable].Offset;
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, names + SnapshotFormat.NameEntrySize, U32(b, names))), "does not name folded-name string");
    }

    [Fact]
    public void NamesOutOfByteOrder_AreRefusedAtOpen()
    {
        var database = Commit((Shard,
            [
                new CodeNode { Id = "csharp:default:1", Identifier = "1", Name = "AB", Metadata = Owned(Shard) },
                new CodeNode { Id = "csharp:default:2", Identifier = "2", Name = "BA", Metadata = Owned(Shard) },
            ],
            []));
        var bytes = WriteBytes(Input(ShardsOf(database)));
        var strings = LayoutOf(bytes).Shards[0].Sections[SnapshotSectionKind.StringBytes].Offset;
        Assert.Equal("ABBA", Encoding.UTF8.GetString(bytes, strings, 4));   // folded names are strings 1 and 2
        AssertRefusedAtOpen(Damaged(bytes, b => Encoding.UTF8.GetBytes("BAAB").CopyTo(b, strings)), "byte order");
    }

    [Fact]
    public void FileRangesThatOverlapOrDoNotCoverTheNodes_AreRefusedAtOpen()
    {
        var (bytes, layout) = Small();
        var files = layout.Sections[SnapshotSectionKind.Files].Offset;
        var second = files + SnapshotFormat.FileEntrySize;
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, second + 8, 0)), "node range does not follow");
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, second + 12, U32(b, second + 12) + 1)), "do not cover the nodes");
    }

    [Fact]
    public void FilesOutOfPathOrder_AreRefusedAtOpen()
    {
        var (bytes, layout) = Small();
        var files = layout.Sections[SnapshotSectionKind.Files].Offset;
        var second = files + SnapshotFormat.FileEntrySize;
        AssertRefusedAtOpen(Damaged(bytes, b =>
        {
            for (var field = 0; field < 8; field += 4)
            {
                var first = U32(b, files + field);
                W32(b, files + field, U32(b, second + field));
                W32(b, second + field, first);
            }
        }), "path order");
    }

    [Fact]
    public void LengthsTiedToCounts_AreChecked()
    {
        var (bytes, layout) = Small();
        // Every edge has a source: the by-source runs end exactly at the edge count.
        var sources = layout.Sections[SnapshotSectionKind.BySourceOffsets];
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, sources.Offset + sources.Length - 4, U32(b, sources.Offset + sources.Length - 4) - 1)), "by-source runs");
        // The histogram's length follows from its two kind counts.
        AssertRefusedAtOpen(Damaged(bytes, b => W32(b, layout.Sections[SnapshotSectionKind.TypeHistogram].Offset, 0)), "type histogram");
        // Sections are dense: one that starts 8 bytes late leaves a gap.
        var crossEntry = layout.Blob + SnapshotFormat.ShardHeaderSize + ((int)SnapshotSectionKind.CrossShardEdges - 1) * SnapshotFormat.SectionEntrySize;
        AssertRefusedAtOpen(Damaged(bytes, b => W64(b, crossEntry + 8, U32(b, crossEntry + 8) + 8UL)), "does not start where the previous one ends");
    }

    [Fact]
    public void PostingListedTwiceInARun_IsReturnedOnce()
    {
        var (bytes, layout) = Small();
        // Folded names: ALPHA (A and B), GAMMA (C). ALPHA's run is postings 0 and 1.
        var postings = layout.Sections[SnapshotSectionKind.NamePostings].Offset;
        var damaged = Damaged(bytes, b => W32(b, postings + 4, U32(b, postings)));
        var opened = OpenInMemory(damaged);
        using var file = opened.File ?? throw new InvalidOperationException(opened.Reason);
        var reader = file.Shards[0];
        foreach (var exact in new[] { true, false })
        {
            var ids = reader.FindNodesByName("alpha", exact).Select(node => node.Id).ToList();
            Assert.Equal(ids.Distinct().Count(), ids.Count);
            Assert.Equal(reader.FindNameCandidates("alpha", exact).Distinct().Count(), reader.FindNameCandidates("alpha", exact).Count);
        }
    }

    [Fact]
    public void EdgeListedTwiceInAByTargetRun_IsCorrupt_NotADuplicate()
    {
        var (bytes, layout) = Small();
        var opened = OpenInMemory(bytes);
        uint sid;
        using (var file = opened.File!) Assert.True(file.Shards[0].TryFindSid("csharp:default:B", out sid));
        var run = (long)U32(bytes, layout.Sections[SnapshotSectionKind.ByTargetOffsets].Offset + 4 * sid);
        var entries = layout.Sections[SnapshotSectionKind.ByTargetEdges].Offset;
        var damaged = Damaged(bytes, b => W32(b, entries + 4 * (run + 1), U32(b, entries + 4 * run)));
        using var damagedFile = OpenInMemory(damaged).File!;
        Assert.Throws<SnapshotCorruptException>(() => damagedFile.Shards[0].GetEdgesByTarget("csharp:default:B"));
    }

    [Fact]
    public void CrossShardEdgeListedTwice_IsCorrupt_NotADuplicate()
    {
        var (bytes, layout) = Small();
        var (cross, length) = layout.Sections[SnapshotSectionKind.CrossShardEdges];
        Assert.True(length >= 8);
        using var file = OpenInMemory(Damaged(bytes, b => W32(b, cross + 4, U32(b, cross)))).File!;
        Assert.Throws<SnapshotCorruptException>(() => file.Shards[0].GetCrossShardEdgeIds());
    }

    [Fact]
    public void IdTableNamingOneNodeForTwoIds_NeverAnswersWithTheOtherNode()
    {
        var (bytes, layout) = Small();
        // Sid of "elsewhere:X" (no node) pointed at node record 0.
        var opened = OpenInMemory(bytes);
        uint sid;
        using (var file = opened.File!) Assert.True(file.Shards[0].TryFindSid("elsewhere:X", out sid));
        var damaged = Damaged(bytes, b => W32(b, layout.Sections[SnapshotSectionKind.IdTable].Offset + 8 * sid + 4, 0));
        using var damagedFile = OpenInMemory(damaged).File!;
        var reader = damagedFile.Shards[0];
        Assert.Throws<SnapshotCorruptException>(() => reader.TryGetNode("elsewhere:X", out _));
        Assert.Throws<SnapshotCorruptException>(() => reader.GetAllNodes());
        Assert.Equal(SnapshotRejection.Structure, LoadInMemory(damaged).Rejection);
    }

    // ---- P5: metadata that does not compare keys ordinally is not persisted ----

    [Fact]
    public void MetadataWithANonOrdinalComparer_SkipsTheShard()
    {
        var good = new ShardKey("typescript", "default");
        IReadOnlyDictionary<string, string> IgnoreCase() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ParserId"] = "csharp", ["parserId"] = "csharp", ["workspaceId"] = "default", ["Line"] = "1",
        };
        foreach (var (what, metadata, persisted) in new (string, IReadOnlyDictionary<string, string>, bool)[]
        {
            ("ordinal", Owned(Shard), true),
            ("default comparer", new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = "default" }, true),
            ("ignore case", IgnoreCase(), false),
            ("read-only wrapper", new ReadOnlyDictionary<string, string>(Owned(Shard)), false),
        })
        {
            var state = StateOf([new CodeNode { Id = "csharp:default:A", Identifier = "A", Metadata = metadata }], []);
            var other = StateOf([new CodeNode { Id = "typescript:default:T", Identifier = "T", Metadata = Owned(good) }], []);
            var written = SnapshotWriter.Write(new System.IO.MemoryStream(), Input([KeyValuePair.Create(Shard, (IGraphShard)state), KeyValuePair.Create(good, (IGraphShard)other)]), Key);
            Assert.True(written.Written.Contains(good), what);
            Assert.True(persisted == written.Written.Contains(Shard), $"{what}: {string.Join("; ", written.Skipped.Select(skip => skip.Reason))}");
            if (!persisted) Assert.Contains("compare keys ordinally", Assert.Single(written.Skipped).Reason, StringComparison.Ordinal);
        }
    }

    // ---- P7f: the external flag ----

    /// <summary>
    /// NodeRecord flag bit 0 is set exactly for the nodes the store's own rule exempts from a prune
    /// (<c>GraphState.CollectNodesOutside</c>): <c>external</c> is the ordinal string "true", so "True",
    /// "TRUE" and "false" are not external. The reader exposes the bit, and a record whose bit disagrees
    /// with its metadata is corrupt.
    /// </summary>
    [Fact]
    public void ExternalFlag_IsTheStoresPruneExemption()
    {
        string?[] values = ["true", "True", "TRUE", "false", " true", null];
        var nodes = values.Select((value, i) => new CodeNode
        {
            Id = $"csharp:default:N{i}", Identifier = $"N{i}", FilePath = $"/r/N{i}.cs",
            Metadata = value is null ? Owned(Shard) : Owned(Shard, ("external", value)),
        }).ToList();
        var database = Commit((Shard, nodes, []));
        var heap = (IGraphShard)database.GetShardState(Shard)!;
        var pruned = new HashSet<string>(StringComparer.Ordinal);
        heap.CollectNodesOutside(new HashSet<string>(StringComparer.Ordinal), pruned);
        var bytes = WriteBytes(Input(ShardsOf(database)));

        using (var file = OpenInMemory(bytes).File!)
        {
            var reader = file.Shards[0];
            var external = Enumerable.Range(0, reader.NodeCount).Where(reader.IsExternal).Select(i => reader.ReadNode(i).Id).ToList();
            Assert.Equal(["csharp:default:N0"], external);
            Assert.Equal(nodes.Select(node => node.Id).Where(id => !pruned.Contains(id!)), external);
        }

        // A record whose flag disagrees with its metadata.
        var records = LayoutOf(bytes).Shards[0].Sections[SnapshotSectionKind.Nodes].Offset;
        var damaged = Damaged(bytes, b =>
        {
            for (var i = 0; i < nodes.Count; i++) W32(b, records + i * SnapshotFormat.NodeRecordSize + 72, U32(b, records + i * SnapshotFormat.NodeRecordSize + 72) ^ 1);
        });
        using var damagedFile = OpenInMemory(damaged).File!;
        Assert.Throws<SnapshotCorruptException>(() => damagedFile.Shards[0].ReadNode(0));
        Assert.Equal(SnapshotRejection.Structure, LoadInMemory(damaged).Rejection);
    }

    // ---- Helpers ----

    private static void AssertRefusedByTheLoader(byte[] damaged, string reason)
    {
        var opened = OpenInMemory(damaged);
        using (opened.File ?? throw new InvalidOperationException($"the damage was caught before the loader: {opened.Reason}")) { }
        var loaded = LoadInMemory(damaged);
        Assert.Equal(SnapshotRejection.Structure, loaded.Rejection);
        Assert.Contains("a fact the writer never writes", loaded.Reason, StringComparison.Ordinal);
        Assert.Contains(reason, loaded.Reason, StringComparison.Ordinal);
    }

    private static void AssertRefusedAtOpen(byte[] damaged, string reason)
    {
        var opened = OpenInMemory(damaged);
        opened.File?.Dispose();
        Assert.Equal(SnapshotRejection.Structure, opened.Rejection);
        Assert.Contains(reason, opened.Reason, StringComparison.Ordinal);
    }

    /// <summary>The absolute offset of the edge record whose id is <paramref name="id"/>.</summary>
    private static long EdgeRecordOf(byte[] bytes, ShardLayout layout, string id)
    {
        using var file = OpenInMemory(bytes).File!;
        var reader = file.Shards[0];
        Assert.True(reader.TryFindEdge(id, out var index));
        return layout.Sections[SnapshotSectionKind.Edges].Offset + (long)index * SnapshotFormat.EdgeRecordSize;
    }

    /// <summary>The absolute offset of the first byte of the string <paramref name="value"/> in StringBytes.</summary>
    private static long StringStart(byte[] bytes, ShardLayout layout, string value)
    {
        var (offset, length) = layout.Sections[SnapshotSectionKind.StringBytes];
        var (offsets, _) = layout.Sections[SnapshotSectionKind.StringOffsets];
        var target = Encoding.UTF8.GetBytes(value);
        for (var k = 0; ; k++)
        {
            var start = U32(bytes, offsets + 4 * k);
            var end = U32(bytes, offsets + 4 * (k + 1));
            Assert.True(end <= length, $"string '{value}' not found");
            if (bytes.AsSpan(offset + (int)start, (int)(end - start)).SequenceEqual(target)) return offset + start;
        }
    }
}
