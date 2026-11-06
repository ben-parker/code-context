using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace CodeContext.Core.Snapshots
{
    /// <summary>The section kinds of a shard blob (plan section 3.2). Every blob holds each kind once, in this order.</summary>
    internal enum SnapshotSectionKind : uint
    {
        ShardMeta = 1,
        StringOffsets = 2,
        StringBytes = 3,
        Nodes = 4,
        Edges = 5,
        IdTable = 6,
        IdHash = 7,
        IdentifierHash = 8,
        EdgeIdHash = 9,
        BySourceOffsets = 10,
        ByTargetOffsets = 11,
        ByTargetEdges = 12,
        Files = 13,
        NameTable = 14,
        NamePostings = 15,
        Metadata = 16,
        CrossShardEdges = 17,
        TypeHistogram = 18,
        Coverage = 19,
        Fingerprints = 20,
    }

    /// <summary>
    /// Constants and record layouts of the snapshot file, format version 1. All integers are
    /// little-endian. Offsets in the file header and shard directory are absolute; offsets inside
    /// a shard blob are relative to the blob start. Every blob and section starts on an 8-byte
    /// boundary, and the layout is dense: host meta at 256, the directory at the next 8-byte
    /// boundary after it, each blob at the next boundary after the previous one, the footer at the
    /// next boundary after the last blob; inside a blob each section starts at the next boundary
    /// after the previous one and the blob ends at the boundary after its last section.
    ///
    /// Limits, as the code enforces them: a file is at most <see cref="MaxFileLength"/> (1 GiB),
    /// refused before authentication by a reader and not written by the writer. Per shard, node,
    /// edge and string counts stay below 2^30 and every section and the whole blob fit one array
    /// (<see cref="Array.MaxLength"/> bytes, just under 2 GiB, which the file cap makes moot); a
    /// metadata set reference stays below 2^30 (it shares a u32 with the 2-bit edge id form). A
    /// shard beyond a per-shard limit is skipped, never an exception.
    /// </summary>
    internal static class SnapshotFormat
    {
        public const uint FormatVersion = 1;
        public const uint EndianTag = 0x01020304;
        public const int HeaderSize = 256;
        public const int FooterSize = 40;
        public const int HmacSize = 32;
        public const int MinimumFileLength = HeaderSize + FooterSize;
        public const int DirectoryEntrySize = 64;
        public const int ShardHeaderSize = 128;
        public const int SectionEntrySize = 24;
        public const int SectionCount = 20;
        public const int NodeRecordSize = 80;
        public const int EdgeRecordSize = 32;
        public const int IdEntrySize = 8;
        public const int FileEntrySize = 24;
        public const int NameEntrySize = 12;

        /// <summary>Header flag bit 0: paths were compared case-insensitively by the writer.</summary>
        public const uint FlagPathsCaseInsensitive = 1;

        /// <summary>NodeRecord flag bit 0: <c>metadata.external == "true"</c>.</summary>
        public const uint NodeFlagExternal = 1;

        /// <summary>A null edge endpoint, and the node index of an id that is no node of the shard.</summary>
        public const uint NoIndex = 0xFFFFFFFF;

        /// <summary>The <c>line</c>/<c>column</c> value of an edge record whose metadata holds no canonical value.</summary>
        public const int AbsentPosition = int.MinValue;

        public const uint EdgeIdFormWhole = 0;
        public const uint EdgeIdFormComposed = 1;
        public const uint EdgeIdFormMask = 3;

        /// <summary>The longest host meta block a reader accepts before authentication.</summary>
        public const int MaxHostMetaLength = 1 << 20;

        /// <summary>
        /// The longest snapshot file: a reader refuses a longer file before hashing it (a planted file
        /// with a public-looking header must not cost a full read), and the writer does not write one.
        /// </summary>
        public const long MaxFileLength = 1L << 30;

        /// <summary>The largest count of nodes, edges or strings in one shard (below 2^30).</summary>
        public const int MaxShardCount = (1 << 30) - 1;

        /// <summary>
        /// The store's own rule for a library symbol (<c>GraphState.CollectNodesOutside</c>, the prune
        /// exemption): metadata <c>external</c> is exactly the ordinal string <c>"true"</c>. NodeRecord flag
        /// bit 0 holds it, and phase 8 prunes a mapped shard by that bit.
        /// </summary>
        public static bool IsExternal(IReadOnlyDictionary<string, string>? metadata)
            => metadata?.GetValueOrDefault(ExternalKey) == "true";

        public const string ExternalKey = "external";

        public static ReadOnlySpan<byte> HeaderMagic => "CCSNAP\r\n"u8;

        public static ReadOnlySpan<byte> FooterMagic => "CCSNEND\n"u8;

        /// <summary>"SHRD" read as a little-endian u32.</summary>
        public const uint ShardMagic = 0x44524853;

        // File header field offsets.
        public const int HeaderFormatVersion = 8;
        public const int HeaderEndianTag = 12;
        public const int HeaderHeaderSize = 16;
        public const int HeaderFlags = 20;
        public const int HeaderFileLength = 24;
        public const int HeaderSequence = 32;
        public const int HeaderStoreGeneration = 40;
        public const int HeaderCreatedUtcTicks = 48;
        public const int HeaderHostMetaOffset = 56;
        public const int HeaderHostMetaLength = 64;
        public const int HeaderShardCount = 68;
        public const int HeaderShardDirectoryOffset = 72;
        public const int HeaderInstanceId = 80;

        // Host meta keys.
        public const string HostVersionKey = "hostVersion";
        public const string RootPathKey = "rootPath";
        public const string PlatformKey = "platform";

        // Shard meta keys.
        public const string ParserIdKey = "parserId";
        public const string WorkspaceIdKey = "workspaceId";
        public const string ParserVersionKey = "parserVersion";
        public const string StateFileKey = "stateFile";
        public const string StateIdKey = "stateId";
        public const string StateSha256Key = "stateSha256";

        // Edge metadata keys held in the record when canonical.
        public const string LineKey = "line";
        public const string ColumnKey = "column";

        public static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>The element size of a section kind.</summary>
        public static uint ElementSize(SnapshotSectionKind kind) => kind switch
        {
            SnapshotSectionKind.ShardMeta => 1,
            SnapshotSectionKind.StringBytes => 1,
            SnapshotSectionKind.Nodes => NodeRecordSize,
            SnapshotSectionKind.Edges => EdgeRecordSize,
            SnapshotSectionKind.IdTable => IdEntrySize,
            SnapshotSectionKind.Files => FileEntrySize,
            SnapshotSectionKind.NameTable => NameEntrySize,
            SnapshotSectionKind.Coverage => 1,
            SnapshotSectionKind.Fingerprints => 1,
            _ => 4,
        };

        /// <summary>
        /// Capacity of a hash section holding <paramref name="count"/> keys: the next power of two at or
        /// above <c>count / 0.75</c>, and 0 for no key.
        /// </summary>
        public static long HashCapacity(long count)
        {
            if (count <= 0) return 0;
            var minimum = (count * 4 + 2) / 3;
            long capacity = 1;
            while (capacity < minimum) capacity <<= 1;
            return capacity;
        }

        public static long Align8(long value) => (value + 7) & ~7L;

        /// <summary>The slot a 64-bit key hash starts probing at.</summary>
        public static long Slot(ulong hash, long capacity) => (long)((hash ^ (hash >> 32)) & (ulong)(capacity - 1));

        /// <summary>
        /// The UTF-8 form of <paramref name="value"/>, or false when it holds a lone surrogate. No
        /// replacement character is ever substituted, so a lookup key never matches a different string.
        /// </summary>
        public static bool TryEncodeStrict(string value, out byte[] bytes)
        {
            var buffer = new byte[Encoding.UTF8.GetMaxByteCount(value.Length)];
            if (Utf8.FromUtf16(value, buffer, out _, out var written, replaceInvalidSequences: false) != System.Buffers.OperationStatus.Done)
            {
                bytes = Array.Empty<byte>();
                return false;
            }
            bytes = written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
            return true;
        }

        /// <summary>Whether a string survives a strict UTF-8 round trip (no lone surrogate).</summary>
        public static bool IsWellFormed(string? value)
            => value is null || value.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0 || !HasLoneSurrogate(value);

        private static bool HasLoneSurrogate(string value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) return true;
                    i++;
                }
                else if (char.IsLowSurrogate(c))
                {
                    return true;
                }
            }
            return false;
        }

        public static uint ReadU32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

        public static ulong ReadU64(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(span[offset..]);
    }
}
