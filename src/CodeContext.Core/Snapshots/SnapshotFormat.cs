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
        Files = 7,
        Metadata = 8,
        Coverage = 9,
        Fingerprints = 10,
        Inputs = 11,
        Parts = 12,
    }

    /// <summary>
    /// Constants and record layouts of the snapshot file, format version 4. Version 3 had no
    /// <see cref="SnapshotSectionKind.Parts"/> section (a node's other declarations, <see cref="CodeNode.Parts"/>), its
    /// node records' last word was reserved, and a snapshot written before workers reported parts would describe less
    /// than a scan does: it is refused. Version 1 had no
    /// <see cref="SnapshotSectionKind.Inputs"/> section and no <see cref="FlagUnverified"/>. Version 2 also held ten
    /// index sections per shard (id, identifier and edge-id hashes, by-source and by-target runs, a name table with
    /// postings, cross-shard edges, a type histogram) for a shard read in place from the mapping, which was never
    /// built: every load materializes, and nothing read them. Earlier versions are refused as any other. All integers are
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
        public const uint FormatVersion = 4;
        public const uint EndianTag = 0x01020304;
        public const int HeaderSize = 256;
        public const int FooterSize = 40;
        public const int HmacSize = 32;
        public const int MinimumFileLength = HeaderSize + FooterSize;
        public const int DirectoryEntrySize = 64;
        public const int ShardHeaderSize = 128;
        public const int SectionEntrySize = 24;
        public const int SectionCount = 12;
        public const int NodeRecordSize = 80;
        public const int EdgeRecordSize = 32;
        public const int IdEntrySize = 8;
        public const int FileEntrySize = 24;

        /// <summary>Header flag bit 0: paths were compared case-insensitively by the writer.</summary>
        public const uint FlagPathsCaseInsensitive = 1;

        /// <summary>
        /// Header flag bit 1: written by a host whose verification of a loaded snapshot failed (status <c>error</c>,
        /// label <c>unverified</c>). A host loads it as any other; a process without a worker never answers from it.
        /// </summary>
        public const uint FlagUnverified = 2;

        /// <summary>
        /// The byte offset in a node record of its parts reference: 0 for none, else the 1-based list of the
        /// <see cref="SnapshotSectionKind.Parts"/> section. That section is <c>u32 count</c>, <c>count + 1</c> u32 byte
        /// offsets (from the section's start; the first just after them, the last the section's length), then each
        /// list: <c>u32 parts</c>, then per part <c>u32 path</c> (a string reference, never 0), <c>i32 start line, end
        /// line, start column, end column</c>.
        /// </summary>
        public const int NodePartsOffset = 76;

        /// <summary>The bytes of one part in a parts list.</summary>
        public const int PartSize = 20;

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
        public const int HeaderRootTimeTicks = 96;

        // Host meta keys.
        public const string HostVersionKey = "hostVersion";
        public const string RootPathKey = "rootPath";
        public const string PlatformKey = "platform";

        /// <summary>Optional: the writer's worker set. Read only by a query without an instance.</summary>
        public const string WorkerSetKey = "workerSet";

        // Shard meta keys.
        public const string ParserIdKey = "parserId";
        public const string WorkspaceIdKey = "workspaceId";
        public const string ParserVersionKey = "parserVersion";
        public const string StateFileKey = "stateFile";
        public const string StateIdKey = "stateId";
        public const string StateSha256Key = "stateSha256";

        /// <summary>
        /// Optional: when the shard's worker began the operation that read its inputs, UTC ticks in decimal. Absent (a
        /// file written before it was recorded) leaves the shard's inputs not known to the one-shot check.
        /// </summary>
        public const string InputsReadKey = "inputsReadUtcTicks";

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
            SnapshotSectionKind.Coverage => 1,
            SnapshotSectionKind.Fingerprints => 1,
            SnapshotSectionKind.Inputs => 1,
            _ => 4,
        };

        public static long Align8(long value) => (value + 7) & ~7L;

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
