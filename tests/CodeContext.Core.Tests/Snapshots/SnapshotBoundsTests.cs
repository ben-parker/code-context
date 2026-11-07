using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using static SnapshotTestSupport;
using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Files damaged after writing and re-signed with the right key, so the damage passes
/// authentication and meets the structural checks (a writer bug looks like this). Opening either
/// refuses the file as <see cref="SnapshotRejection.Structure"/>, or every accessor either answers or
/// throws <see cref="SnapshotCorruptException"/>: never another exception, never an access outside
/// the mapping, never an endless probe. The loader never throws.
///
/// The damaged files are opened in memory (<see cref="SnapshotFile.OpenInMemory"/>: the same checks
/// and the same reader as a file, mapped from an anonymous region), so thousands of cases cost no file
/// each; <see cref="SampleOfDamage_ThroughRealFiles_IsContained"/> takes a sample through real files.
/// The fixtures are built once per class.
/// </summary>
public class SnapshotBoundsTests
{
    private static readonly uint[] HostileValues =
        [0, 1, 2, 3, 7, 8, 0x7F, 0xFF, 0x100, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF];

    // The section table is damaged with fewer values: each field already meets every check with these.
    private static readonly uint[] TableValues = [0, 1, 4, 0xFFFF, 0x7FFFFFFF, 0xFFFFFFFF];

    private static readonly Lazy<Fixture> Single = new(() => Fixture.Of(SingleShardDatabase()));

    private static readonly Lazy<Fixture> Multi = new(() => Fixture.Of(MultiShardDatabase()));

    [Fact]
    public void DirectoryDamage_IsContained()
    {
        var (bytes, layout, words) = Single.Value;
        var entry = layout.Shards[0].Entry;
        foreach (var offset in new[] { entry, entry + 8 })
            foreach (var value in HostileValues.Select(v => (ulong)v).Concat([ulong.MaxValue, (ulong)bytes.Length, (ulong)bytes.Length - 40, 1UL << 40]))
                Exercise(Damaged(bytes, b => W64(b, offset, value)), words, $"directory +{offset - entry} = {value}");
        Exercise(Damaged(bytes, b => W32(b, SnapshotFormat.HeaderShardCount, 2)), words, "shard count + 1");
        Exercise(Damaged(bytes, b => W32(b, SnapshotFormat.HeaderShardCount, 0xFFFFFFFF)), words, "shard count max");
        Exercise(Damaged(bytes, b => W64(b, SnapshotFormat.HeaderShardDirectoryOffset, ulong.MaxValue)), words, "directory offset max");
        Exercise(Damaged(bytes, b => W64(b, SnapshotFormat.HeaderShardDirectoryOffset, 4)), words, "directory offset unaligned");
        Exercise(Damaged(bytes, b => W32(b, entry + 48, 0)), words, "directory node count");
    }

    [Fact]
    public void ShardHeaderCounts_AreContained()
    {
        var (bytes, layout, words) = Single.Value;
        var blob = layout.Shards[0].Blob;
        for (var field = 0; field < 10; field++)
            foreach (var value in HostileValues)
                Exercise(Damaged(bytes, b => W32(b, blob + 4 * field, value)), words, $"shard header field {field} = {value}");
    }

    [Fact]
    public void SectionTableDamage_IsContained()
    {
        var (bytes, layout, words) = Single.Value;
        for (var section = 0; section < SnapshotFormat.SectionCount; section++)
        {
            var entry = layout.Shards[0].Blob + SnapshotFormat.ShardHeaderSize + section * SnapshotFormat.SectionEntrySize;
            foreach (var value in TableValues)
            {
                Exercise(Damaged(bytes, b => W32(b, entry, value)), words, $"section {section + 1} kind = {value}");
                Exercise(Damaged(bytes, b => W32(b, entry + 4, value)), words, $"section {section + 1} element size = {value}");
                Exercise(Damaged(bytes, b => W64(b, entry + 8, value)), words, $"section {section + 1} offset = {value}");
                Exercise(Damaged(bytes, b => W64(b, entry + 16, value)), words, $"section {section + 1} length = {value}");
            }
            Exercise(Damaged(bytes, b => W64(b, entry + 8, ulong.MaxValue - 7)), words, $"section {section + 1} offset near max");
            Exercise(Damaged(bytes, b => W64(b, entry + 16, ulong.MaxValue)), words, $"section {section + 1} length max");
        }
    }

    /// <summary>
    /// Every u32 of the fixed-layout sections (string offsets and references, sids, node and edge
    /// indexes, run offsets, hash values, postings, set offsets) replaced by hostile values.
    /// </summary>
    [Fact]
    public void SectionContentDamage_IsContained()
    {
        var (bytes, layout, words) = Single.Value;
        var random = new Random(11);
        foreach (var (kind, (offset, length)) in layout.Shards[0].Sections)
        {
            if (kind is SnapshotSectionKind.StringBytes or SnapshotSectionKind.Coverage or SnapshotSectionKind.Fingerprints) continue;
            for (long at = 0; at + 4 <= length; at += 4)
            {
                // Every word once with a value out of any range, and a sample with the rest.
                Exercise(Damaged(bytes, b => W32(b, offset + at, 0xFFFFFFF0)), words, $"{kind} word {at / 4}");
                if (random.Next(5) == 0) Exercise(Damaged(bytes, b => W32(b, offset + at, HostileValues[random.Next(HostileValues.Length)])), words, $"{kind} word {at / 4} sampled");
                if (random.Next(5) == 0) Exercise(Damaged(bytes, b => W32(b, offset + at, (uint)random.Next(64))), words, $"{kind} word {at / 4} small");
            }
        }
    }

    [Fact]
    public void StringBytesDamage_IsContained()
    {
        var (bytes, layout, words) = Single.Value;
        var (offset, length) = layout.Shards[0].Sections[SnapshotSectionKind.StringBytes];
        for (long at = 0; at < length; at += 5)
            foreach (var value in new byte[] { 0xFF, 0xED })
                Exercise(Damaged(bytes, b => b[offset + at] = value), words, $"string byte {at} = {value:X2}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SeededRandomDamage_IsContained(int seed)
    {
        var (bytes, layout, words) = Single.Value;
        var random = new Random(seed);
        for (var round = 0; round < 100; round++)
        {
            var damaged = Damaged(bytes, b =>
            {
                for (var flips = random.Next(1, 6); flips > 0; flips--)
                    b[random.Next(layout.Directory, b.Length - SnapshotFormat.FooterSize)] = (byte)random.Next(256);
            });
            Exercise(damaged, words, $"seed {seed} round {round}");
        }
    }

    /// <summary>The checks before mapping and the release of a refused file, on disk: a sample of the cases above.</summary>
    [Fact]
    public void SampleOfDamage_ThroughRealFiles_IsContained()
    {
        var (bytes, layout, words) = Single.Value;
        var shard = layout.Shards[0];
        using var temp = new SnapshotTempDirectory();
        var cases = new (string Context, byte[] Bytes)[]
        {
            ("directory blob offset", Damaged(bytes, b => W64(b, shard.Entry, 8))),
            ("shard node count", Damaged(bytes, b => W32(b, shard.Blob + 8, 0xFFFFFFFF))),
            ("section offset", Damaged(bytes, b => W64(b, shard.Blob + SnapshotFormat.ShardHeaderSize + 8, 7))),
            ("id table word", Damaged(bytes, b => W32(b, shard.Sections[SnapshotSectionKind.IdTable].Offset, 0xFFFFFFF0))),
            ("edge record word", Damaged(bytes, b => W32(b, shard.Sections[SnapshotSectionKind.Edges].Offset + 4, 0xFFFFFFF0))),
            ("valid", bytes),
        };
        foreach (var (context, damaged) in cases)
        {
            var path = temp.Snapshot();
            File.WriteAllBytes(path, damaged);
            var opened = SnapshotFile.Open(path, Key, Expect());
            if (opened.File is { } file)
            {
                using (file) ExerciseReaders(file, words, context);
            }
            else
            {
                Assert.Equal(SnapshotRejection.Structure, opened.Rejection);
            }
            var loaded = SnapshotLoader.Load(path, Key, Expect());
            Assert.True(loaded.Loaded || loaded.Rejection == SnapshotRejection.Structure, $"{context}: refused as {loaded.Rejection}");
            File.Delete(path);   // nothing stays open after a refusal or a load
        }
    }

    /// <summary>A hash table with no empty slot: every probe ends after the table's capacity.</summary>
    [Theory]
    [InlineData((uint)SnapshotSectionKind.IdHash)]
    [InlineData((uint)SnapshotSectionKind.IdentifierHash)]
    [InlineData((uint)SnapshotSectionKind.EdgeIdHash)]
    public void FullHashTable_ProbesEnd(uint section)
    {
        var kind = (SnapshotSectionKind)section;
        var (bytes, layout, _) = Single.Value;
        var (offset, length) = layout.Shards[0].Sections[kind];
        var damaged = Damaged(bytes, b =>
        {
            for (long at = 0; at < length; at += 4) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan((int)(offset + at)), 1);
        });
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, damaged);
        using var file = SnapshotFile.Open(path, Key, Expect()).File!;
        var reader = Assert.Single(file.Shards);
        Assert.False(reader.TryFindSid("no such id", out _));
        Assert.False(reader.TryFindNodeByIdentifier("no such identifier", out _));
        Assert.False(reader.TryFindEdge("no such edge", out _));
    }

    // ---- Several shards (T2) ----

    [Fact]
    public void MultiShard_DirectoryDamageAtIndexOne_IsContained()
    {
        var (bytes, layout, words) = Multi.Value;
        Assert.Equal(3, layout.Shards.Count);
        var entry = layout.Shards[1].Entry;
        foreach (var field in new[] { 0, 8 })
            foreach (var value in HostileValues.Select(v => (ulong)v).Concat([(ulong)layout.Shards[0].Blob, (ulong)layout.Shards[2].Blob, (ulong)bytes.Length]))
                Exercise(Damaged(bytes, b => W64(b, entry + field, value)), words, $"directory[1] +{field} = {value}");
        foreach (var field in new[] { 48, 52 })
            foreach (var value in HostileValues)
                Exercise(Damaged(bytes, b => W32(b, entry + field, value)), words, $"directory[1] +{field} = {value}");
    }

    [Fact]
    public void MultiShard_BlobOverlappingTheBlobBefore_IsRefusedByTheLayoutCheck()
    {
        var (bytes, layout, _) = Multi.Value;
        // Blob 1 named as blob 0 itself: a valid shard, so only the layout check can tell.
        var opened = OpenInMemory(Damaged(bytes, b =>
        {
            W64(b, layout.Shards[1].Entry, (ulong)layout.Shards[0].Blob);
            W64(b, layout.Shards[1].Entry + 8, (ulong)layout.Shards[0].BlobLength);
        }));
        AssertStructure(opened, "does not start where the previous part ends");
        AssertStructure(OpenInMemory(Damaged(bytes, b => W64(b, layout.Shards[1].Entry, (ulong)layout.Shards[0].Blob + 8))), "does not start where");
    }

    [Fact]
    public void MultiShard_BlobOverlappingTheDirectoryOrTheFooter_IsRefused()
    {
        var (bytes, layout, _) = Multi.Value;
        AssertStructure(OpenInMemory(Damaged(bytes, b => W64(b, layout.Shards[0].Entry, (ulong)layout.Directory))), "does not start where");
        var last = layout.Shards[^1];
        AssertStructure(OpenInMemory(Damaged(bytes, b => W64(b, last.Entry + 8, (ulong)last.BlobLength + 8))), "runs past the footer");
    }

    [Fact]
    public void MultiShard_KeysOutOfOrderOrDuplicated_AreRefusedAtOpen()
    {
        var (bytes, layout, _) = Multi.Value;
        // The keys are ("fake","wa"), ("fake","wb"), ("fake","wc"); a workspace id is renamed in place.
        AssertStructure(OpenInMemory(Damaged(bytes, b => RenameWorkspace(b, layout.Shards[1], "wb", "wa"))), "key order");
        AssertStructure(OpenInMemory(Damaged(bytes, b => RenameWorkspace(b, layout.Shards[0], "wa", "wd"))), "key order");
        Assert.Null(OpenInMemory(bytes).Reason);
    }

    [Fact]
    public void MultiShard_ContentDamage_IsContained()
    {
        var (bytes, layout, words) = Multi.Value;
        var random = new Random(23);
        foreach (var shard in layout.Shards)
            foreach (var (kind, (offset, length)) in shard.Sections)
            {
                if (length < 4 || kind is SnapshotSectionKind.Coverage or SnapshotSectionKind.Fingerprints) continue;
                for (var round = 0; round < 4; round++)
                {
                    var at = offset + 4 * random.Next(length / 4);
                    Exercise(Damaged(bytes, b => W32(b, at, HostileValues[random.Next(HostileValues.Length)])), words, $"{kind} at {at}");
                }
            }
    }

    private static void RenameWorkspace(byte[] bytes, ShardLayout shard, string from, string to)
    {
        var (offset, length) = shard.Sections[SnapshotSectionKind.ShardMeta];
        var meta = bytes.AsSpan(offset, length);
        var needle = System.Text.Encoding.UTF8.GetBytes("workspaceId").Concat(BitConverter.GetBytes((uint)from.Length)).Concat(System.Text.Encoding.UTF8.GetBytes(from)).ToArray();
        var at = meta.IndexOf(needle);
        Assert.True(at >= 0, $"workspace '{from}' not found in the shard meta");
        System.Text.Encoding.UTF8.GetBytes(to).CopyTo(meta[(at + needle.Length - from.Length)..]);
    }

    private static void AssertStructure(SnapshotOpenResult opened, string reason)
    {
        opened.File?.Dispose();
        Assert.Equal(SnapshotRejection.Structure, opened.Rejection);
        Assert.Contains(reason, opened.Reason, StringComparison.Ordinal);
    }

    // ---- Fixtures and the exercise ----

    private sealed record Fixture(byte[] Bytes, FileLayout Layout, ShardReadMatrix.Vocabulary[] Words)
    {
        public static Fixture Of(InMemoryDatabase database)
        {
            var bytes = WriteBytes(Input(ShardsOf(database)));
            var words = database.ShardKeys
                .Select(key => ShardReadMatrix.Vocabulary.From((IGraphShard)database.GetShardState(key)!))
                .ToArray();
            var layout = LayoutOf(bytes);
            Assert.All(layout.Shards, shard => Assert.True(shard.Sections[SnapshotSectionKind.Nodes].Length > 3 * SnapshotFormat.NodeRecordSize));
            return new Fixture(bytes, layout, words);
        }
    }

    private static InMemoryDatabase SingleShardDatabase()
    {
        var key = new ShardKey("csharp", "w 1/ü");
        var graph = new SnapshotGraphGenerator(1234).Generate(maxNodes: 14, maxEdges: 30);
        return Commit(graph.Where(shard => shard.Key == key).ToArray());
    }

    private static InMemoryDatabase MultiShardDatabase()
    {
        var shards = new[] { new ShardKey("fake", "wa"), new ShardKey("fake", "wb"), new ShardKey("fake", "wc") };
        return Commit(shards.Select((key, s) =>
        {
            var prefix = $"fake:{key.WorkspaceId}:";
            var nodes = Enumerable.Range(0, 6).Select(i => new CodeNode
            {
                Id = $"{prefix}N{i}", Identifier = $"fake:{key.WorkspaceId}.N{i}", Name = $"Name{i % 3}", Type = "class",
                FilePath = $"/r/{key.WorkspaceId}/F{i % 2}.cs", Metadata = Owned(key, ("line", i.ToString())),
            }).ToList();
            var edges = Enumerable.Range(0, 8).Select(i => new CodeEdge
            {
                Id = $"{prefix}N{i % 6}->calls->{(i == 7 ? "fake:other:X" : $"{prefix}N{(i + 1) % 6}")}",
                SourceId = $"{prefix}N{i % 6}",
                TargetId = i == 7 ? "fake:other:X" : $"{prefix}N{(i + 1) % 6}",
                Type = "calls",
                Metadata = Owned(key, ("line", (i + s).ToString())),
            }).GroupBy(edge => edge.Id).Select(group => group.First()).ToList();
            return (key, (IReadOnlyList<CodeNode>)nodes, (IReadOnlyList<CodeEdge>)edges);
        }).ToArray());
    }

    /// <summary>Opens and loads a damaged file and asks every accessor; only the snapshot's own exception may escape a read.</summary>
    private static void Exercise(byte[] damaged, ShardReadMatrix.Vocabulary[] words, string context)
    {
        var opened = OpenInMemory(damaged);
        if (opened.File is not { } file)
        {
            Assert.True(opened.Rejection == SnapshotRejection.Structure, $"{context}: refused as {opened.Rejection}: {opened.Reason}");
            return;
        }
        using (file) ExerciseReaders(file, words, context);
    }

    private static void ExerciseReaders(SnapshotFile file, ShardReadMatrix.Vocabulary[] words, string context)
    {
        var loaded = SnapshotLoader.Materialize(file);
        Assert.True(loaded.Loaded || loaded.Rejection == SnapshotRejection.Structure, $"{context}: refused as {loaded.Rejection}");
        foreach (var reader in file.Shards)
        {
            for (var i = 0; i < Math.Min(reader.NodeCount, 64); i++) Read(() => reader.ReadNode(i), context);
            for (var j = 0; j < Math.Min(reader.EdgeCount, 128); j++) Read(() => reader.ReadEdge(j), context);
            for (uint r = 0; r <= Math.Min((uint)reader.StringCount + 1, 256); r++) Read(() => reader.GetString(r), context);
            Read(() => reader.GetAllNodes(), context);
            Read(() => reader.GetAllEdges(), context);
            Read(() => reader.GetFilePaths(), context);
            Read(() => reader.GetCrossShardEdgeIds(), context);
            Read(() => reader.ReadInfo(), context);
            Read(() => { reader.AddStatistics(new(), new()); return 0; }, context);
            foreach (var vocabulary in words)
            {
                foreach (var id in vocabulary.NodeIds)
                {
                    Read(() => reader.TryGetNode(id, out _), context);
                    Read(() => reader.GetEdgesBySource(id), context);
                    Read(() => reader.GetEdgesByTarget(id), context);
                }
                foreach (var id in vocabulary.EdgeIds) Read(() => reader.TryGetEdge(id, out _), context);
                foreach (var identifier in vocabulary.Identifiers) Read(() => reader.TryGetNodeIdByIdentifier(identifier, out _), context);
                foreach (var name in vocabulary.Names.Take(12))
                {
                    Read(() => reader.FindNodesByName(name, true), context);
                    Read(() => reader.FindNodesByName(name, false), context);
                }
                foreach (var requested in vocabulary.Paths.Take(12)) Read(() => reader.FindNodesByPath(requested), context);
            }
        }
    }

    private static void Read<T>(Func<T> read, string context)
    {
        try
        {
            read();
        }
        catch (SnapshotCorruptException)
        {
        }
        catch (Exception exception)
        {
            Assert.Fail($"{context}: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
        }
    }
}
