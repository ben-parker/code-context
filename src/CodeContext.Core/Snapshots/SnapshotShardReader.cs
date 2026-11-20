using System.Text.Unicode;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// Reads of one shard blob of a mapped snapshot, for the materializing loader. The constructor validates, in
    /// O(sections + files) and never per node or per edge: the shard header and section table (kinds in order,
    /// element sizes, dense 8-byte-aligned layout, every length tied to its counts where the format fixes one), the
    /// first and last string offset, the metadata set offsets and the file buckets (contiguous node ranges, path
    /// order, normalized keys). Everything else is checked on the read that meets it. Every value from the file is
    /// bounds-checked before use, every mapped read goes through <see cref="MappedRegion.Slice"/>, and a bad value
    /// throws <see cref="SnapshotCorruptException"/>. A result holds no fact twice; one that would throws.
    /// </summary>
    internal sealed class SnapshotShardReader
    {
        private static readonly SnapshotSectionKind[] Kinds = Enumerable.Range(1, SnapshotFormat.SectionCount)
            .Select(kind => (SnapshotSectionKind)kind).ToArray();

        private readonly MappedRegion _region;
        private readonly long[] _offset = new long[SnapshotFormat.SectionCount];
        private readonly long[] _length = new long[SnapshotFormat.SectionCount];
        private readonly string?[] _strings;
        private readonly Dictionary<(uint SetRef, int Column, int Line), FactMetadata> _metadata = new();
        private readonly FactMetadataPool _metadataContent = new();

        public SnapshotShardReader(MappedRegion region, long blobOffset, long blobLength, uint directoryNodeCount, uint directoryEdgeCount)
        {
            _region = region;
            if (blobLength < SnapshotFormat.ShardHeaderSize + (long)SnapshotFormat.SectionCount * SnapshotFormat.SectionEntrySize)
                throw new SnapshotCorruptException("A shard blob is shorter than its header and section table.");
            if (_region.ReadU32(blobOffset) != SnapshotFormat.ShardMagic) throw new SnapshotCorruptException("A shard blob's magic is wrong.");
            if (_region.ReadU32(blobOffset + 4) != SnapshotFormat.SectionCount)
                throw new SnapshotCorruptException($"A shard blob does not hold the {SnapshotFormat.SectionCount} sections of format version {SnapshotFormat.FormatVersion}.");

            NodeCount = Count(blobOffset + 8, "node");
            EdgeCount = Count(blobOffset + 12, "edge");
            IdCount = Count(blobOffset + 16, "id");
            StringCount = Count(blobOffset + 20, "string");
            FileCount = Count(blobOffset + 24, "file");
            if ((uint)NodeCount != directoryNodeCount || (uint)EdgeCount != directoryEdgeCount)
                throw new SnapshotCorruptException("A shard's counts disagree with the directory.");

            // Sections in kind order, each at the 8-byte boundary after the previous one, the blob ending
            // at the boundary after the last: no gap, no overlap, nothing outside the blob.
            var tableOffset = blobOffset + SnapshotFormat.ShardHeaderSize;
            var previousEnd = (long)SnapshotFormat.ShardHeaderSize + SnapshotFormat.SectionCount * SnapshotFormat.SectionEntrySize;
            for (var i = 0; i < SnapshotFormat.SectionCount; i++)
            {
                var entry = tableOffset + i * SnapshotFormat.SectionEntrySize;
                var kind = Kinds[i];
                if (_region.ReadU32(entry) != (uint)kind) throw new SnapshotCorruptException($"Section {i} is not of kind {kind}.");
                var elementSize = _region.ReadU32(entry + 4);
                if (elementSize != SnapshotFormat.ElementSize(kind)) throw new SnapshotCorruptException($"Section {kind} has element size {elementSize}.");
                var offset = _region.ReadU64(entry + 8);
                var length = _region.ReadU64(entry + 16);
                if (offset != (ulong)SnapshotFormat.Align8(previousEnd) || offset > (ulong)blobLength || length > (ulong)blobLength - offset)
                    throw new SnapshotCorruptException($"Section {kind} does not start where the previous one ends, or lies outside its blob.");
                if (length % elementSize != 0) throw new SnapshotCorruptException($"Section {kind} is not a whole number of elements.");
                _offset[i] = blobOffset + (long)offset;
                _length[i] = (long)length;
                previousEnd = (long)(offset + length);
            }
            if (SnapshotFormat.Align8(previousEnd) != blobLength) throw new SnapshotCorruptException("The shard blob does not end after its last section.");

            long n = NodeCount, e = EdgeCount, s = IdCount;
            ExpectLength(SnapshotSectionKind.StringOffsets, (StringCount + 1L) * 4);
            ExpectLength(SnapshotSectionKind.Nodes, n * SnapshotFormat.NodeRecordSize);
            ExpectLength(SnapshotSectionKind.Edges, e * SnapshotFormat.EdgeRecordSize);
            ExpectLength(SnapshotSectionKind.IdTable, s * SnapshotFormat.IdEntrySize);
            ExpectLength(SnapshotSectionKind.Files, (long)FileCount * SnapshotFormat.FileEntrySize);

            if (U32(SnapshotSectionKind.StringOffsets, 0) != 0
                || U32(SnapshotSectionKind.StringOffsets, StringCount) != Length(SnapshotSectionKind.StringBytes))
                throw new SnapshotCorruptException("The string offsets do not span the string bytes.");
            // Allocated only now that StringOffsets (4 bytes a string) is known to fit the blob.
            _strings = new string?[StringCount];

            var metadataLength = Length(SnapshotSectionKind.Metadata);
            if (metadataLength < 8) throw new SnapshotCorruptException("The metadata section is too short.");
            MetadataSetCount = U32(SnapshotSectionKind.Metadata, 0);
            if (MetadataSetCount > (metadataLength - 8) / 4
                || U32(SnapshotSectionKind.Metadata, 1) != 4 + 4L * (MetadataSetCount + 1)
                || U32(SnapshotSectionKind.Metadata, 1 + MetadataSetCount) != metadataLength)
                throw new SnapshotCorruptException("The metadata set offsets do not span the section.");

            var partsLength = Length(SnapshotSectionKind.Parts);
            if (partsLength < 8) throw new SnapshotCorruptException("The parts section is too short.");
            PartListCount = U32(SnapshotSectionKind.Parts, 0);
            if (PartListCount > (partsLength - 8) / 4
                || U32(SnapshotSectionKind.Parts, 1) != 4 + 4L * (PartListCount + 1)
                || U32(SnapshotSectionKind.Parts, 1 + PartListCount) != partsLength)
                throw new SnapshotCorruptException("The parts list offsets do not span the section.");

            if (!SnapshotFile.TryParseKeyValues(_region.Slice(Offset(SnapshotSectionKind.ShardMeta), Length(SnapshotSectionKind.ShardMeta)), out var meta)
                || !meta.TryGetValue(SnapshotFormat.ParserIdKey, out var parserId) || string.IsNullOrEmpty(parserId)
                || !meta.TryGetValue(SnapshotFormat.WorkspaceIdKey, out var workspaceId) || string.IsNullOrEmpty(workspaceId))
                throw new SnapshotCorruptException("The shard meta block is malformed or names no shard.");
            Meta = meta;
            Key = new ShardKey(parserId, workspaceId);

            CheckFiles();

            void ExpectLength(SnapshotSectionKind kind, long expected)
            {
                if (Length(kind) != expected) throw new SnapshotCorruptException($"Section {kind} is {Length(kind)} bytes, its counts need {expected}.");
            }
        }

        /// <summary>
        /// The file buckets, in O(files + path bytes): node ranges non-empty, contiguous, from 0, ending
        /// exactly at the node count, so a node is in exactly one bucket; the null path (reference 0)
        /// only first; the other paths strictly increasing in the store's file-path order
        /// (<see cref="Repositories.InMemory.GraphAdjacency.SortPaths"/>), so distinct; and each normalized
        /// key exactly <see cref="FilePathMatcher.Normalize"/> of its path, or 0 for a blank path.
        /// </summary>
        private void CheckFiles()
        {
            long expectedFirst = 0;
            string? previous = null;
            for (var f = 0; f < FileCount; f++)
            {
                var at = Element(SnapshotSectionKind.Files, f, SnapshotFormat.FileEntrySize);
                long first = _region.ReadU32(at + 8);
                long count = _region.ReadU32(at + 12);
                if (first != expectedFirst || count == 0) throw new SnapshotCorruptException($"File {f}'s node range does not follow the previous file's.");
                expectedFirst = first + count;

                var pathRef = _region.ReadU32(at);
                if (pathRef == 0 && f != 0) throw new SnapshotCorruptException($"File {f} has the null path but is not first.");
                var path = GetString(pathRef);
                if (path is not null)
                {
                    if (previous is not null && GraphAdjacency.ComparePaths(previous, path) >= 0)
                        throw new SnapshotCorruptException($"File {f}'s path does not follow the previous file's in path order.");
                    previous = path;
                }
                var keyRef = _region.ReadU32(at + 4);
                if (string.IsNullOrWhiteSpace(path) ? keyRef != 0 : GetString(keyRef) != FilePathMatcher.Normalize(path))
                    throw new SnapshotCorruptException($"File {f}'s normalized key is not its path's.");
            }
            if (expectedFirst != NodeCount) throw new SnapshotCorruptException("The file buckets do not cover the nodes exactly.");
        }

        public ShardKey Key { get; }

        /// <summary>The shard meta entries: parserId, workspaceId and the optional parser version and state binding.</summary>
        public IReadOnlyDictionary<string, string> Meta { get; }

        public int NodeCount { get; }

        public int EdgeCount { get; }

        public int IdCount { get; }

        public int StringCount { get; }

        public int FileCount { get; }

        public long MetadataSetCount { get; }

        public long PartListCount { get; }

        /// <summary>The section's absolute offset and length, for tests that damage a section on purpose.</summary>
        internal (long Offset, long Length) SectionRange(SnapshotSectionKind kind) => (Offset(kind), Length(kind));

        public SnapshotShardInfo ReadInfo() => new(
            Meta.GetValueOrDefault(SnapshotFormat.ParserVersionKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateFileKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateIdKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateSha256Key),
            _region.Slice(Offset(SnapshotSectionKind.Coverage), Length(SnapshotSectionKind.Coverage)).ToArray(),
            _region.Slice(Offset(SnapshotSectionKind.Fingerprints), Length(SnapshotSectionKind.Fingerprints)).ToArray(),
            _region.Slice(Offset(SnapshotSectionKind.Inputs), Length(SnapshotSectionKind.Inputs)).ToArray(),
            long.TryParse(Meta.GetValueOrDefault(SnapshotFormat.InputsReadKey), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var inputsRead) ? inputsRead : 0);

        // ---- Records ----

        /// <summary>
        /// The node in record slot <paramref name="index"/>, materialized. The record's sid names it in
        /// the id table, it lies in the node range of the file it names, and its flags are exactly the
        /// external bit the store's rule gives its metadata.
        /// </summary>
        public CodeNode ReadNode(int index)
        {
            var record = Element(SnapshotSectionKind.Nodes, index, SnapshotFormat.NodeRecordSize);
            var sid = _region.ReadU32(record);
            if (IdNodeIndex(sid) != (uint)index) throw new SnapshotCorruptException($"Node record {index} and the id table disagree.");
            var fileIndex = _region.ReadU32(record + 20);
            if (fileIndex >= (uint)FileCount) throw new SnapshotCorruptException($"Node record {index} names file {fileIndex}.");
            var (first, count) = FileNodes((int)fileIndex);
            if (index < first || index >= first + count) throw new SnapshotCorruptException($"Node record {index} lies outside its file's node range.");
            var metadata = Metadata(_region.ReadU32(record + 68), SnapshotFormat.AbsentPosition, SnapshotFormat.AbsentPosition);
            var flags = _region.ReadU32(record + 72);
            if (flags != (SnapshotFormat.IsExternal(metadata) ? SnapshotFormat.NodeFlagExternal : 0))
                throw new SnapshotCorruptException($"Node record {index}'s flags disagree with its metadata.");
            return new CodeNode
            {
                Id = Id(sid),
                Identifier = GetString(_region.ReadU32(record + 4))!,
                Name = GetString(_region.ReadU32(record + 8)),
                Type = GetString(_region.ReadU32(record + 12)),
                Language = GetString(_region.ReadU32(record + 16)),
                FilePath = GetString(_region.ReadU32(Element(SnapshotSectionKind.Files, fileIndex, SnapshotFormat.FileEntrySize))),
                StartLine = _region.ReadI32(record + 24),
                EndLine = _region.ReadI32(record + 28),
                StartCol = _region.ReadI32(record + 32),
                EndCol = _region.ReadI32(record + 36),
                Namespace = GetString(_region.ReadU32(record + 40)),
                Visibility = GetString(_region.ReadU32(record + 44)),
                Signature = GetString(_region.ReadU32(record + 48)),
                ReturnType = GetString(_region.ReadU32(record + 52)),
                Parameters = GetString(_region.ReadU32(record + 56)),
                Modifiers = GetString(_region.ReadU32(record + 60)),
                Metrics = GetString(_region.ReadU32(record + 64)),
                Metadata = metadata,
                Parts = Parts(_region.ReadU32(record + SnapshotFormat.NodePartsOffset)),
            };
        }

        /// <summary>Parts list <paramref name="listRef"/> (1-based; 0 is none), each part's path a string of the shard.</summary>
        private List<CodeNodePart>? Parts(uint listRef)
        {
            if (listRef == 0) return null;
            if (listRef > PartListCount) throw new SnapshotCorruptException($"Parts list {listRef} is beyond {PartListCount} lists.");
            long start = U32(SnapshotSectionKind.Parts, listRef);
            long end = U32(SnapshotSectionKind.Parts, listRef + 1L);
            if (start < 4 + 4 * (PartListCount + 1) || start > end || end > Length(SnapshotSectionKind.Parts) || start % 4 != 0)
                throw new SnapshotCorruptException($"Parts list {listRef} lies outside the section.");
            var at = Offset(SnapshotSectionKind.Parts) + start;
            long count = _region.ReadU32(at);
            if (count == 0 || end - start != 4 + (long)SnapshotFormat.PartSize * count)
                throw new SnapshotCorruptException($"Parts list {listRef} has the wrong length.");
            var parts = new List<CodeNodePart>((int)count);
            for (long p = 0; p < count; p++)
            {
                var part = at + 4 + SnapshotFormat.PartSize * p;
                var path = GetString(_region.ReadU32(part)) ?? throw new SnapshotCorruptException($"A part of list {listRef} has no path.");
                parts.Add(new CodeNodePart(path, _region.ReadI32(part + 4), _region.ReadI32(part + 8), _region.ReadI32(part + 12), _region.ReadI32(part + 16)));
            }
            return parts;
        }

        /// <summary>The edge in record slot <paramref name="index"/>, materialized.</summary>
        public CodeEdge ReadEdge(int index)
        {
            var record = Element(SnapshotSectionKind.Edges, index, SnapshotFormat.EdgeRecordSize);
            var sourceSid = _region.ReadU32(record);
            var targetSid = _region.ReadU32(record + 4);
            var source = sourceSid == SnapshotFormat.NoIndex ? null : Id(sourceSid);
            var target = targetSid == SnapshotFormat.NoIndex ? null : Id(targetSid);
            var metaAndForm = _region.ReadU32(record + 28);
            var metadata = Metadata(metaAndForm >> 2, _region.ReadI32(record + 24), _region.ReadI32(record + 20));
            var edge = new CodeEdge
            {
                SourceId = source,
                TargetId = target,
                Type = GetString(_region.ReadU32(record + 8)),
                Metadata = metadata,
            };
            // A composed id stays parts (the endpoint ids and two strings of the table); its text is never built.
            edge.UseIdKey(EdgeIdKey(record, metaAndForm, source, target));
            return edge;
        }

        /// <summary>Node record <paramref name="index"/>, which must be the record of <paramref name="sid"/>.</summary>
        private CodeNode ReadNodeOfSid(uint sid, int index)
        {
            if (_region.ReadU32(Element(SnapshotSectionKind.Nodes, index, SnapshotFormat.NodeRecordSize)) != sid)
                throw new SnapshotCorruptException($"Sid {sid} names node record {index}, which is another id's.");
            return ReadNode(index);
        }

        /// <summary>Every node in id order (the id table's order, skipping ids that are no node).</summary>
        public IReadOnlyList<CodeNode> GetAllNodes()
        {
            var result = new List<CodeNode>(NodeCount);
            for (uint sid = 0; sid < (uint)IdCount; sid++)
            {
                var index = IdNodeIndex(sid);
                if (index == SnapshotFormat.NoIndex) continue;
                var node = ReadNodeOfSid(sid, NodeIndex(index));
                if (result.Count > 0 && string.CompareOrdinal(result[^1].Id, node.Id) >= 0)
                    throw new SnapshotCorruptException("The id table is not in strictly increasing id order.");
                result.Add(node);
            }
            if (result.Count != NodeCount) throw new SnapshotCorruptException("The id table does not name every node exactly once.");
            return result;
        }

        /// <summary>Every edge in edge id order.</summary>
        public IReadOnlyList<CodeEdge> GetAllEdges()
        {
            var result = new List<CodeEdge>(EdgeCount);
            for (var j = 0; j < EdgeCount; j++) result.Add(ReadEdge(j));
            result.Sort(GraphAdjacency.EdgeById);
            for (var j = 1; j < result.Count; j++)
                if (EdgeIdKeys.Compare(result[j - 1].IdKey, result[j].IdKey) == 0)
                    throw new SnapshotCorruptException($"Edge id '{result[j].Id}' appears twice.");
            return result;
        }

        // ---- Strings ----

        /// <summary>String reference <paramref name="reference"/> decoded (0 is null).</summary>
        public string? GetString(uint reference)
        {
            if (reference == 0) return null;
            if (reference > (uint)StringCount) throw new SnapshotCorruptException($"String reference {reference} is beyond {StringCount} strings.");
            var cached = _strings[reference - 1];
            if (cached is not null) return cached;
            var bytes = StringBytes(reference);
            if (!Utf8.IsValid(bytes)) throw new SnapshotCorruptException($"String {reference} is not valid UTF-8.");
            var value = SnapshotFormat.StrictUtf8.GetString(bytes);
            _strings[reference - 1] = value;
            return value;
        }

        private ReadOnlySpan<byte> StringBytes(uint reference)
        {
            if (reference == 0 || reference > (uint)StringCount)
                throw new SnapshotCorruptException($"String reference {reference} is beyond {StringCount} strings.");
            long start = U32(SnapshotSectionKind.StringOffsets, reference - 1);
            long end = U32(SnapshotSectionKind.StringOffsets, reference);
            if (start > end || end > Length(SnapshotSectionKind.StringBytes))
                throw new SnapshotCorruptException($"String {reference} lies outside the string bytes.");
            return _region.Slice(Offset(SnapshotSectionKind.StringBytes) + start, end - start);
        }

        // ---- Helpers ----

        private int Count(long offset, string what)
        {
            var value = _region.ReadU32(offset);
            if (value > int.MaxValue) throw new SnapshotCorruptException($"The shard's {what} count {value} is out of range.");
            return (int)value;
        }

        private long Offset(SnapshotSectionKind kind) => _offset[(int)kind - 1];

        private long Length(SnapshotSectionKind kind) => _length[(int)kind - 1];

        /// <summary>The absolute offset of element <paramref name="index"/> of a section; throws when the section has no such element.</summary>
        private long Element(SnapshotSectionKind kind, long index, int size)
        {
            if (index < 0 || index >= Length(kind) / size)
                throw new SnapshotCorruptException($"Element {index} is beyond section {kind}.");
            return Offset(kind) + index * size;
        }

        private uint U32(SnapshotSectionKind kind, long index) => _region.ReadU32(Element(kind, index, 4));

        private int NodeIndex(uint index)
            => index < (uint)NodeCount ? (int)index : throw new SnapshotCorruptException($"Node index {index} is beyond {NodeCount} nodes.");

        private int EdgeIndex(uint index)
            => index < (uint)EdgeCount ? (int)index : throw new SnapshotCorruptException($"Edge index {index} is beyond {EdgeCount} edges.");

        private uint IdStringRef(uint sid)
        {
            if (sid >= (uint)IdCount) throw new SnapshotCorruptException($"Sid {sid} is beyond {IdCount} ids.");
            return _region.ReadU32(Element(SnapshotSectionKind.IdTable, sid, SnapshotFormat.IdEntrySize));
        }

        private uint IdNodeIndex(uint sid)
        {
            if (sid >= (uint)IdCount) throw new SnapshotCorruptException($"Sid {sid} is beyond {IdCount} ids.");
            var index = _region.ReadU32(Element(SnapshotSectionKind.IdTable, sid, SnapshotFormat.IdEntrySize) + 4);
            return index == SnapshotFormat.NoIndex ? index : (uint)NodeIndex(index);
        }

        private string Id(uint sid)
            => GetString(IdStringRef(sid)) ?? throw new SnapshotCorruptException($"Sid {sid} has no string.");

        private (long First, long Count) FileNodes(int file)
        {
            var at = Element(SnapshotSectionKind.Files, file, SnapshotFormat.FileEntrySize);
            long first = _region.ReadU32(at + 8);
            long count = _region.ReadU32(at + 12);
            if (first + count > NodeCount) throw new SnapshotCorruptException($"File {file}'s node range is beyond the nodes.");
            return (first, count);
        }

        /// <summary>The edge's id key (<see cref="CodeEdge.IdKey"/>): the whole string, or the parts of a composed id.</summary>
        private object EdgeIdKey(long record, uint metaAndForm, string? source, string? target)
        {
            var a = GetString(_region.ReadU32(record + 12));
            switch (metaAndForm & SnapshotFormat.EdgeIdFormMask)
            {
                case SnapshotFormat.EdgeIdFormWhole:
                    return a ?? throw new SnapshotCorruptException("An edge has no id.");
                case SnapshotFormat.EdgeIdFormComposed:
                    var b = GetString(_region.ReadU32(record + 16));
                    if (source is null || target is null || a is null || b is null)
                        throw new SnapshotCorruptException("A composed edge id lacks a part.");
                    return Core.EdgeId.FromParts(source, a, target, b);
                default:
                    throw new SnapshotCorruptException("An edge has an unknown id form.");
            }
        }

        private static readonly string[] SmallNumbers = Enumerable.Range(0, 4096)
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        /// <summary>The metadata text of a record's line or column, or null when the record holds none.</summary>
        private static string? Position(int value)
            => value == SnapshotFormat.AbsentPosition ? null
                : (uint)value < (uint)SmallNumbers.Length ? SmallNumbers[value]
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// A fact's metadata: the stored set (in the writer's ordinal key order) merged with the keys the
        /// writer moved out of it, <c>column</c>, <c>line</c>, <c>parserId</c> and <c>workspaceId</c> (in
        /// that ordinal order), so the map enumerates in ordinal key order. A repeated key, or a set that is not in
        /// that order, is corrupt.
        /// Every fact of this shard with the same content shares one immutable instance; the set, the
        /// record's column and line, and the shard fix the content, so a repeated three is answered at once.
        /// </summary>
        private FactMetadata Metadata(uint setRef, int columnValue, int lineValue)
        {
            var cacheKey = (setRef, columnValue, lineValue);
            lock (_metadata)
            {
                if (_metadata.TryGetValue(cacheKey, out var shared)) return shared;
            }
            var built = BuildMetadata(setRef, Position(columnValue), Position(lineValue));
            lock (_metadata)
            {
                if (_metadata.TryGetValue(cacheKey, out var shared)) return shared;
                // Two references can hold one content (a node set and an edge set are stored apart).
                shared = _metadataContent.Get(built);
                _metadata.Add(cacheKey, shared);
                return shared;
            }
        }

        private string[] BuildMetadata(uint setRef, string? column, string? line)
        {
            long pairCount = 0, pairs = 0;
            if (setRef != 0)
            {
                if (setRef > MetadataSetCount) throw new SnapshotCorruptException($"Metadata set {setRef} is beyond {MetadataSetCount} sets.");
                long start = U32(SnapshotSectionKind.Metadata, setRef);
                long end = U32(SnapshotSectionKind.Metadata, setRef + 1L);
                if (start < 4 + 4 * (MetadataSetCount + 1) || start > end || end > Length(SnapshotSectionKind.Metadata) || start % 4 != 0)
                    throw new SnapshotCorruptException($"Metadata set {setRef} lies outside the section.");
                pairs = Offset(SnapshotSectionKind.Metadata) + start;
                pairCount = _region.ReadU32(pairs);
                if (end - start != 4 + 8 * pairCount) throw new SnapshotCorruptException($"Metadata set {setRef} has the wrong length.");
                pairs += 4;
            }

            // The stored pairs and the four keys the writer moved out of the set, merged: both are in ordinal key
            // order, so the result is, and a key is repeated exactly when it does not sort after the one before it.
            // No set, list or delegate per fact: this runs once for every distinct metadata of a load.
            var flat = new string[2 * ((int)pairCount + 2 + (column is null ? 0 : 1) + (line is null ? 0 : 1))];
            var filled = 0;
            var extra = column is not null ? 0 : line is not null ? 1 : 2;
            for (long p = 0; p < pairCount; p++)
            {
                var at = pairs + 8 * p;
                var key = GetString(_region.ReadU32(at)) ?? throw new SnapshotCorruptException("A metadata key is null.");
                while (extra < 4 && string.CompareOrdinal(ExtraKey(extra), key) < 0) AddExtra(flat, ref filled, ref extra, column, line);
                Add(flat, ref filled, key, GetString(_region.ReadU32(at + 4))!);
            }
            while (extra < 4) AddExtra(flat, ref filled, ref extra, column, line);
            return flat;
        }

        /// <summary>The keys the writer holds outside a stored set, in ordinal order: column, line, parserId, workspaceId.</summary>
        private static string ExtraKey(int index) => index switch
        {
            0 => SnapshotFormat.ColumnKey,
            1 => SnapshotFormat.LineKey,
            2 => SnapshotFormat.ParserIdKey,
            _ => SnapshotFormat.WorkspaceIdKey,
        };

        /// <summary>Adds extra key <paramref name="extra"/> and moves to the next one the fact has (a null column or line is absent).</summary>
        private void AddExtra(string[] flat, ref int filled, ref int extra, string? column, string? line)
        {
            Add(flat, ref filled, ExtraKey(extra), extra switch { 0 => column!, 1 => line!, 2 => Key.ParserId, _ => Key.WorkspaceId });
            extra++;
            if (extra == 1 && line is null) extra = 2;
        }

        private static void Add(string[] flat, ref int filled, string key, string value)
        {
            if (filled > 0)
            {
                var order = string.CompareOrdinal(flat[filled - 2], key);
                if (order == 0) throw new SnapshotCorruptException($"Metadata key '{key}' appears twice.");
                if (order > 0) throw new SnapshotCorruptException($"Metadata key '{key}' is out of order.");
            }
            flat[filled++] = key;
            flat[filled++] = value;
        }

    }
}
