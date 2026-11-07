using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Pins the format-version-1 layout: a tiny graph written with a fixed key, time, sequence,
/// generation and instance id must give exactly the checked-in bytes. A change here is a format
/// change and needs a new <c>formatVersion</c>. To regenerate after a deliberate change, set
/// <c>CODECONTEXT_SNAPSHOT_GOLDEN_OUT</c> to the absolute path of the fixture file and run this test.
/// </summary>
public class SnapshotGoldenTests
{
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Snapshots", "tiny-graph.ccsnap.hex");

    [Fact]
    public void TinyGraph_MatchesCheckedInBytes()
    {
        var bytes = TinyGraph();
        var output = Environment.GetEnvironmentVariable("CODECONTEXT_SNAPSHOT_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, ToHex(bytes));

        var expected = Convert.FromHexString(new string(File.ReadAllText(FixturePath).Where(char.IsAsciiHexDigit).ToArray()));
        Assert.Equal(expected.Length, bytes.Length);
        var first = Enumerable.Range(0, bytes.Length).FirstOrDefault(i => bytes[i] != expected[i], -1);
        Assert.True(first < 0, $"the snapshot layout changed at byte {first}");
    }

    [Fact]
    public void TinyGraph_DirectoryHashesEachBlob_AndTheFooterAuthenticatesTheFile()
    {
        var bytes = TinyGraph();
        var directory = (int)BitConverter.ToUInt64(bytes, SnapshotFormat.HeaderShardDirectoryOffset);
        var blobOffset = (int)BitConverter.ToUInt64(bytes, directory);
        var blobLength = (int)BitConverter.ToUInt64(bytes, directory + 8);
        Assert.Equal(SHA256.HashData(bytes.AsSpan(blobOffset, blobLength)), bytes.AsSpan(directory + 16, 32).ToArray());
        Assert.Equal(HMACSHA256.HashData(SnapshotTestSupport.Key, bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32).ToArray());
        Assert.Equal("CCSNAP\r\n"u8.ToArray(), bytes.AsSpan(0, 8).ToArray());
        Assert.Equal("CCSNEND\n"u8.ToArray(), bytes.AsSpan(bytes.Length - 40, 8).ToArray());
        Assert.Equal(0, blobOffset % 8);
    }

    /// <summary>
    /// Reads the fixture's bytes by the offsets of plan section 3.2 alone (no <see cref="SnapshotFile"/>,
    /// no <see cref="SnapshotShardReader"/>): the header fields, host meta, the first directory entry, the
    /// first shard header, the section table (kinds, element sizes, alignment, dense layout), the hash
    /// capacity rule, one node record (the external node with a null file path) and one edge record
    /// (the composed fake-worker edge).
    /// </summary>
    [Fact]
    public void TinyGraph_DecodesByThePlanOffsets()
    {
        var bytes = Convert.FromHexString(new string(File.ReadAllText(FixturePath).Where(char.IsAsciiHexDigit).ToArray()));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
        ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));

        // Header.
        Assert.Equal("CCSNAP\r\n"u8.ToArray(), bytes[..8]);
        Assert.Equal(1u, U32(8));
        Assert.Equal(0x01020304u, U32(12));
        Assert.Equal(256u, U32(16));
        Assert.Equal(1u, U32(20));                                 // paths compared case-insensitively
        Assert.Equal((ulong)bytes.Length, U64(24));
        Assert.Equal(42UL, U64(32));
        Assert.Equal(3UL, U64(40));
        Assert.Equal(new DateTime(2026, 11, 5, 10, 12, 3, DateTimeKind.Utc).Ticks, (long)U64(48));
        Assert.Equal(256UL, U64(56));
        var hostMetaLength = (int)U32(64);
        Assert.Equal(2u, U32(68));
        var directory = (int)U64(72);
        Assert.Equal(new Guid("5d0c9a1e-7b3f-4c0e-9a6d-2f8b1c4e7a90"), new Guid(bytes.AsSpan(80, 16)));
        Assert.All(bytes[96..256], b => Assert.Equal(0, b));

        // Host meta: u32 count, (u16 keyLength, key, u32 valueLength, value)*.
        var meta = new Dictionary<string, string>();
        var p = 256 + 4;
        for (var i = 0; i < (int)U32(256); i++)
        {
            int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p));
            var metaKey = Encoding.UTF8.GetString(bytes, p + 2, keyLength);
            p += 2 + keyLength;
            var valueLength = (int)U32(p);
            meta[metaKey] = Encoding.UTF8.GetString(bytes, p + 4, valueLength);
            p += 4 + valueLength;
        }
        Assert.Equal(256 + hostMetaLength, p);
        Assert.Equal(SnapshotTestSupport.HostVersion, meta["hostVersion"]);
        Assert.Equal(SnapshotTestSupport.RootPath, meta["rootPath"]);
        Assert.Equal("win-x64", meta["platform"]);
        Assert.Equal((p + 7) & ~7, directory);

        // Directory entry 0: u64 blobOffset, u64 blobLength, 32 bytes blobSha256, u32 nodeCount, u32 edgeCount, u32 flags, u32 reserved.
        var blob = (int)U64(directory);
        var blobLength = (int)U64(directory + 8);
        Assert.Equal((directory + 2 * 64 + 7) & ~7, blob);
        Assert.Equal(SHA256.HashData(bytes.AsSpan(blob, blobLength)), bytes[(directory + 16)..(directory + 48)]);
        Assert.Equal(3u, U32(directory + 48));
        Assert.Equal(3u, U32(directory + 52));
        Assert.Equal(0u, U32(directory + 56));
        Assert.Equal(0u, U32(directory + 60));

        // Shard header: magic "SHRD", sectionCount, N, E, S, stringCount, F, nameCount, foldedNameFirstRef, flags.
        Assert.Equal("SHRD"u8.ToArray(), bytes[blob..(blob + 4)]);
        Assert.Equal(20u, U32(blob + 4));
        Assert.Equal(3u, U32(blob + 8));
        Assert.Equal(3u, U32(blob + 12));
        var idCount = (int)U32(blob + 16);
        var fileCount = (int)U32(blob + 24);
        Assert.Equal(1u, U32(blob + 32));

        // Section table: 20 entries of u32 kind, u32 elementSize, u64 offset, u64 length, dense and 8-byte aligned.
        var elementSizes = new uint[] { 1, 4, 1, 80, 32, 8, 4, 4, 4, 4, 4, 4, 24, 12, 4, 4, 4, 4, 1, 1 };
        var sections = new (int Offset, int Length)[21];
        var end = 128 + 20 * 24;
        for (var k = 1; k <= 20; k++)
        {
            var entry = blob + 128 + (k - 1) * 24;
            Assert.Equal((uint)k, U32(entry));
            Assert.Equal(elementSizes[k - 1], U32(entry + 4));
            var offset = (int)U64(entry + 8);
            Assert.Equal((end + 7) & ~7, offset);
            sections[k] = (blob + offset, (int)U64(entry + 16));
            end = offset + sections[k].Length;
        }
        Assert.Equal((end + 7) & ~7, blobLength);
        // Hash capacity: the next power of two at or above count / 0.75.
        static int Capacity(int count) { var capacity = 1; while (capacity * 3 < count * 4) capacity <<= 1; return count == 0 ? 0 : capacity; }
        Assert.Equal(4 * Capacity(idCount), sections[7].Length);
        Assert.Equal(4 * Capacity(3), sections[9].Length);
        Assert.Equal(fileCount * 24, sections[13].Length);

        string? Str(uint reference)
        {
            if (reference == 0) return null;
            var start = (int)U32(sections[2].Offset + 4 * (int)(reference - 1));
            var stop = (int)U32(sections[2].Offset + 4 * (int)reference);
            return Encoding.UTF8.GetString(bytes, sections[3].Offset + start, stop - start);
        }
        string Id(uint sid) => Str(U32(sections[6].Offset + 8 * (int)sid))!;

        // NodeRecord, 80 bytes: the external node; a null file path is the first bucket, with pathStr 0.
        var record = Enumerable.Range(0, 3).Select(i => sections[4].Offset + 80 * i).Single(at => Id(U32(at)) == "fake:ws:Ext");
        Assert.Equal("fake:Ext", Str(U32(record + 4)));
        Assert.Equal("Straße", Str(U32(record + 8)));
        Assert.Equal("class", Str(U32(record + 12)));
        Assert.Equal(0u, U32(record + 20));
        Assert.Equal(0u, U32(sections[13].Offset));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(record + 24)));
        Assert.Equal(1u, U32(record + 72));                        // flags: external
        Assert.Equal(0u, U32(record + 76));

        // EdgeRecord, 32 bytes: the fake worker's composed id, line held in the record, column absent.
        var edge = Enumerable.Range(0, 3).Select(j => sections[5].Offset + 32 * j).Single(at => (U32(at + 28) & 3) == 1);
        Assert.Equal("fake:ws:A.Class", Id(U32(edge)));
        Assert.Equal("fake:ws:A.Class.Method", Id(U32(edge + 4)));
        Assert.Equal("contains", Str(U32(edge + 8)));
        Assert.Equal("->contains->", Str(U32(edge + 12)));
        Assert.Equal("", Str(U32(edge + 16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(edge + 20)));
        Assert.Equal(int.MinValue, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(edge + 24)));
        Assert.Equal(1u, U32(edge + 28));                          // form 1, metadata set 0
    }

    /// <summary>
    /// Two shards; in the first a node with a null file path, a non-ASCII name and the external flag,
    /// a canonical and a non-canonical <c>line</c>, a non-canonical <c>column</c>, a null target, a
    /// composed and a verbatim edge id; in the second a cross-shard edge with both positions canonical.
    /// </summary>
    private static byte[] TinyGraph()
    {
        var key = new ShardKey("fake", "ws");
        var other = new ShardKey("typescript", "default");
        var database = SnapshotTestSupport.Commit((key,
            [
                new CodeNode
                {
                    Id = "fake:ws:A.Class", Identifier = "fake:A.Class", Name = "AClass", Type = "class", Language = "fake",
                    FilePath = "/r/A.cs", StartLine = 1, EndLine = 10, StartCol = 1, EndCol = 1,
                    Metadata = SnapshotTestSupport.Owned(key, ("gen", "1")),
                },
                new CodeNode
                {
                    Id = "fake:ws:A.Class.Method", Identifier = "fake:A.Class.Method", Name = "AMethod", Type = "method", Language = "fake",
                    FilePath = "/r/A.cs", StartLine = 2, EndLine = 5, StartCol = 1, EndCol = 1, Signature = "void AMethod()",
                    Metadata = SnapshotTestSupport.Owned(key),
                },
                new CodeNode
                {
                    Id = "fake:ws:Ext", Identifier = "fake:Ext", Name = "Straße", Type = "class", Language = "fake",
                    FilePath = null, StartLine = 7, Metadata = SnapshotTestSupport.Owned(key, ("external", "true")),
                },
            ],
            [
                new CodeEdge
                {
                    Id = "fake:ws:A.Class->contains->fake:ws:A.Class.Method", SourceId = "fake:ws:A.Class", TargetId = "fake:ws:A.Class.Method",
                    Type = "contains", Metadata = SnapshotTestSupport.Owned(key, ("line", "2")),
                },
                new CodeEdge
                {
                    Id = "fake:ws:ref:1", SourceId = "fake:ws:A.Class.Method", TargetId = "other:B", Type = "REFERENCES",
                    Metadata = SnapshotTestSupport.Owned(key, ("line", "03")),
                },
                new CodeEdge
                {
                    Id = "fake:ws:unresolved:1", SourceId = "fake:ws:A.Class.Method", TargetId = null, Type = "REFERENCES",
                    Metadata = SnapshotTestSupport.Owned(key, ("line", "5"), ("column", "007")),
                },
            ]),
            (other,
            [
                new CodeNode
                {
                    Id = "typescript:default:src/b.ts#B", Identifier = "src/b.ts#B", Name = "Béta", Type = "function", Language = "typescript",
                    FilePath = "/r/src/b.ts", StartLine = 3, EndLine = 4, Metadata = SnapshotTestSupport.Owned(other),
                },
            ],
            [
                new CodeEdge
                {
                    Id = "typescript:default:src/b.ts#B=[calls]=>fake:ws:A.Class", SourceId = "typescript:default:src/b.ts#B", TargetId = "fake:ws:A.Class",
                    Type = "calls", Metadata = SnapshotTestSupport.Owned(other, ("line", "3"), ("column", "9")),
                },
            ]));
        var info = new Dictionary<ShardKey, SnapshotShardInfo>
        {
            [key] = new("1.0.0", null, null, null, Encoding.UTF8.GetBytes("[]"), null),
        };
        return SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database), sequence: 42, generation: 3, info: info));
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 32)
            builder.Append(Convert.ToHexString(bytes, i, Math.Min(32, bytes.Length - i)).ToLowerInvariant()).Append('\n');
        return builder.ToString();
    }
}
