using System.Text.Unicode;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// In-place reads of one shard blob of a mapped snapshot: the accessors the materializing loader
    /// uses now and the mapped shard serves reads from later. The constructor validates, in
    /// O(sections + names + files) and never per node or per edge: the shard header and section table
    /// (kinds in order, element sizes, dense 8-byte-aligned layout, every length tied to its counts
    /// where the format fixes one), the first and last entry of each offsets array, the name table
    /// (references, byte order, contiguous postings) and the file buckets (contiguous node ranges,
    /// path order, normalized keys). Everything else is checked on the read that meets it. Every value
    /// from the file is bounds-checked before use, every mapped read goes through
    /// <see cref="MappedRegion.Slice"/>, every hash probe is bounded by the table's capacity, and a bad
    /// value throws <see cref="SnapshotCorruptException"/>. Results are in canonical order (ordinal by
    /// id) and hold no fact twice, as the heap shard's; a result that would break either throws. A null
    /// argument is answered as the heap shard answers it.
    /// </summary>
    internal sealed class SnapshotShardReader
    {
        private static readonly SnapshotSectionKind[] Kinds = Enumerable.Range(1, SnapshotFormat.SectionCount)
            .Select(kind => (SnapshotSectionKind)kind).ToArray();

        private readonly MappedRegion _region;
        private readonly long[] _offset = new long[SnapshotFormat.SectionCount];
        private readonly long[] _length = new long[SnapshotFormat.SectionCount];
        private readonly uint _foldedNameFirstRef;
        private readonly string?[] _strings;
        private string?[]? _fileKeys;

        public SnapshotShardReader(MappedRegion region, long blobOffset, long blobLength, uint directoryNodeCount, uint directoryEdgeCount)
        {
            _region = region;
            if (blobLength < SnapshotFormat.ShardHeaderSize + (long)SnapshotFormat.SectionCount * SnapshotFormat.SectionEntrySize)
                throw new SnapshotCorruptException("A shard blob is shorter than its header and section table.");
            if (_region.ReadU32(blobOffset) != SnapshotFormat.ShardMagic) throw new SnapshotCorruptException("A shard blob's magic is wrong.");
            if (_region.ReadU32(blobOffset + 4) != SnapshotFormat.SectionCount)
                throw new SnapshotCorruptException("A shard blob does not hold the 20 sections of format version 1.");

            NodeCount = Count(blobOffset + 8, "node");
            EdgeCount = Count(blobOffset + 12, "edge");
            IdCount = Count(blobOffset + 16, "id");
            StringCount = Count(blobOffset + 20, "string");
            FileCount = Count(blobOffset + 24, "file");
            NameCount = Count(blobOffset + 28, "name");
            _foldedNameFirstRef = _region.ReadU32(blobOffset + 32);
            if ((uint)NodeCount != directoryNodeCount || (uint)EdgeCount != directoryEdgeCount)
                throw new SnapshotCorruptException("A shard's counts disagree with the directory.");
            if (NameCount > 0 && (_foldedNameFirstRef == 0 || _foldedNameFirstRef - 1L + NameCount > StringCount))
                throw new SnapshotCorruptException("The folded-name reference range lies outside the string table.");

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
            ExpectLength(SnapshotSectionKind.IdHash, SnapshotFormat.HashCapacity(s) * 4);
            ExpectLength(SnapshotSectionKind.EdgeIdHash, SnapshotFormat.HashCapacity(e) * 4);
            ExpectLength(SnapshotSectionKind.BySourceOffsets, (s + 1) * 4);
            ExpectLength(SnapshotSectionKind.ByTargetOffsets, (s + 1) * 4);
            ExpectLength(SnapshotSectionKind.ByTargetEdges, e * 4);
            ExpectLength(SnapshotSectionKind.Files, (long)FileCount * SnapshotFormat.FileEntrySize);
            ExpectLength(SnapshotSectionKind.NameTable, (long)NameCount * SnapshotFormat.NameEntrySize);
            var identifierCapacity = Length(SnapshotSectionKind.IdentifierHash) / 4;
            if (identifierCapacity > SnapshotFormat.HashCapacity(n) || (identifierCapacity & (identifierCapacity - 1)) != 0)
                throw new SnapshotCorruptException("The identifier hash capacity is not a power of two within the node count.");
            if (Length(SnapshotSectionKind.NamePostings) > n * 4) throw new SnapshotCorruptException("There are more name postings than nodes.");
            if (Length(SnapshotSectionKind.CrossShardEdges) > e * 4) throw new SnapshotCorruptException("There are more cross-shard edges than edges.");
            CheckHistogramLength();

            if (U32(SnapshotSectionKind.StringOffsets, 0) != 0
                || U32(SnapshotSectionKind.StringOffsets, StringCount) != Length(SnapshotSectionKind.StringBytes))
                throw new SnapshotCorruptException("The string offsets do not span the string bytes.");
            // Allocated only now that StringOffsets (4 bytes a string) is known to fit the blob.
            _strings = new string?[StringCount];
            // Every edge has a source (the writer persists no null source), so the by-source runs cover
            // every edge; null targets follow the by-target runs.
            if (U32(SnapshotSectionKind.BySourceOffsets, 0) != 0 || U32(SnapshotSectionKind.BySourceOffsets, IdCount) != EdgeCount)
                throw new SnapshotCorruptException("The by-source runs do not cover exactly the edges.");
            if (U32(SnapshotSectionKind.ByTargetOffsets, 0) != 0 || U32(SnapshotSectionKind.ByTargetOffsets, IdCount) > EdgeCount)
                throw new SnapshotCorruptException("The by-target runs do not start at 0 or end beyond the edges.");

            var metadataLength = Length(SnapshotSectionKind.Metadata);
            if (metadataLength < 8) throw new SnapshotCorruptException("The metadata section is too short.");
            MetadataSetCount = U32(SnapshotSectionKind.Metadata, 0);
            if (MetadataSetCount > (metadataLength - 8) / 4
                || U32(SnapshotSectionKind.Metadata, 1) != 4 + 4L * (MetadataSetCount + 1)
                || U32(SnapshotSectionKind.Metadata, 1 + MetadataSetCount) != metadataLength)
                throw new SnapshotCorruptException("The metadata set offsets do not span the section.");

            if (!SnapshotFile.TryParseKeyValues(_region.Slice(Offset(SnapshotSectionKind.ShardMeta), Length(SnapshotSectionKind.ShardMeta)), out var meta)
                || !meta.TryGetValue(SnapshotFormat.ParserIdKey, out var parserId) || string.IsNullOrEmpty(parserId)
                || !meta.TryGetValue(SnapshotFormat.WorkspaceIdKey, out var workspaceId) || string.IsNullOrEmpty(workspaceId))
                throw new SnapshotCorruptException("The shard meta block is malformed or names no shard.");
            Meta = meta;
            Key = new ShardKey(parserId, workspaceId);

            CheckNameTable();
            CheckFiles();

            void ExpectLength(SnapshotSectionKind kind, long expected)
            {
                if (Length(kind) != expected) throw new SnapshotCorruptException($"Section {kind} is {Length(kind)} bytes, its counts need {expected}.");
            }
        }

        /// <summary>
        /// TypeHistogram is <c>u32 nodeKinds, (u32 typeStr, u32 n)*, u32 edgeKinds, (u32 typeStr, u32 n)*</c>
        /// and nothing else: its length follows from the two counts.
        /// </summary>
        private void CheckHistogramLength()
        {
            var words = Length(SnapshotSectionKind.TypeHistogram) / 4;
            if (words < 2) throw new SnapshotCorruptException("The type histogram is too short.");
            long nodeKinds = U32(SnapshotSectionKind.TypeHistogram, 0);
            if (nodeKinds > (words - 2) / 2) throw new SnapshotCorruptException("The type histogram is longer than its section.");
            long edgeKinds = U32(SnapshotSectionKind.TypeHistogram, 1 + 2 * nodeKinds);
            if (words != 2 + 2 * nodeKinds + 2 * edgeKinds) throw new SnapshotCorruptException("The type histogram's length does not match its counts.");
        }

        /// <summary>
        /// The name index, in O(names + name bytes): entry k names folded-name string
        /// <c>foldedNameFirstRef + k</c>; the names strictly increase by UTF-8 bytes (the writer's order,
        /// which an exact lookup's binary search needs); and the postings runs are non-empty, contiguous,
        /// in entry order, start at 0 and end exactly at the end of NamePostings, so no node is listed
        /// under two names and no query can read a run twice.
        /// </summary>
        private void CheckNameTable()
        {
            long expectedStart = 0;
            for (var k = 0; k < NameCount; k++)
            {
                var at = Element(SnapshotSectionKind.NameTable, k, SnapshotFormat.NameEntrySize);
                if (_region.ReadU32(at) != _foldedNameFirstRef + (uint)k)
                    throw new SnapshotCorruptException($"Name {k} does not name folded-name string {_foldedNameFirstRef + k}.");
                if (k > 0 && StringBytes(_foldedNameFirstRef + (uint)k - 1).SequenceCompareTo(StringBytes(_foldedNameFirstRef + (uint)k)) >= 0)
                    throw new SnapshotCorruptException($"Name {k} does not follow name {k - 1} in byte order.");
                long start = _region.ReadU32(at + 4);
                long count = _region.ReadU32(at + 8);
                if (start != expectedStart || count == 0)
                    throw new SnapshotCorruptException($"Name {k}'s postings do not follow the previous name's.");
                expectedStart = start + count;
            }
            if (expectedStart != Length(SnapshotSectionKind.NamePostings) / 4)
                throw new SnapshotCorruptException("The name postings runs do not cover the postings section exactly.");
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

        public int NameCount { get; }

        public long MetadataSetCount { get; }

        /// <summary>The section's absolute offset and length, for tests that damage a section on purpose.</summary>
        internal (long Offset, long Length) SectionRange(SnapshotSectionKind kind) => (Offset(kind), Length(kind));

        public SnapshotShardInfo ReadInfo() => new(
            Meta.GetValueOrDefault(SnapshotFormat.ParserVersionKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateFileKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateIdKey),
            Meta.GetValueOrDefault(SnapshotFormat.StateSha256Key),
            _region.Slice(Offset(SnapshotSectionKind.Coverage), Length(SnapshotSectionKind.Coverage)).ToArray(),
            _region.Slice(Offset(SnapshotSectionKind.Fingerprints), Length(SnapshotSectionKind.Fingerprints)).ToArray());

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
            var metadata = Metadata(_region.ReadU32(record + 68), null, null);
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
            };
        }

        /// <summary>
        /// Whether node record <paramref name="index"/> is a library symbol (flag bit 0, the store's
        /// <c>metadata.external == "true"</c>): the prune exemption a mapped shard reads in place.
        /// </summary>
        public bool IsExternal(int index)
            => (_region.ReadU32(Element(SnapshotSectionKind.Nodes, index, SnapshotFormat.NodeRecordSize) + 72) & SnapshotFormat.NodeFlagExternal) != 0;

        /// <summary>The edge in record slot <paramref name="index"/>, materialized.</summary>
        public CodeEdge ReadEdge(int index)
        {
            var record = Element(SnapshotSectionKind.Edges, index, SnapshotFormat.EdgeRecordSize);
            var sourceSid = _region.ReadU32(record);
            var targetSid = _region.ReadU32(record + 4);
            var source = sourceSid == SnapshotFormat.NoIndex ? null : Id(sourceSid);
            var target = targetSid == SnapshotFormat.NoIndex ? null : Id(targetSid);
            var metaAndForm = _region.ReadU32(record + 28);
            var metadata = Metadata(metaAndForm >> 2, Position(_region.ReadI32(record + 24)), Position(_region.ReadI32(record + 20)));
            return new CodeEdge
            {
                Id = EdgeId(record, metaAndForm, source, target),
                SourceId = source,
                TargetId = target,
                Type = GetString(_region.ReadU32(record + 8)),
                Metadata = metadata,
            };
        }

        // ---- Keyed reads (the in-place accessors) ----

        /// <summary>The sid of <paramref name="id"/> through IdHash, or false when the shard names no such id.</summary>
        public bool TryFindSid(string id, out uint sid)
        {
            ArgumentNullException.ThrowIfNull(id);
            sid = 0;
            if (!SnapshotFormat.TryEncodeStrict(id, out var key)) return false;
            var capacity = Length(SnapshotSectionKind.IdHash) / 4;
            if (capacity == 0) return false;
            var slot = SnapshotFormat.Slot(Fnv1a.Hash(key), capacity);
            for (long probe = 0; probe < capacity; probe++)
            {
                var value = U32(SnapshotSectionKind.IdHash, (slot + probe) & (capacity - 1));
                if (value == 0) return false;
                var candidate = value - 1;
                if (candidate >= (uint)IdCount) throw new SnapshotCorruptException($"IdHash names sid {candidate}.");
                if (StringBytes(IdStringRef(candidate)).SequenceEqual(key))
                {
                    sid = candidate;
                    return true;
                }
            }
            return false;
        }

        public bool TryGetNode(string id, out CodeNode? node)
        {
            node = null;
            if (!TryFindSid(id, out var sid)) return false;
            var index = IdNodeIndex(sid);
            if (index == SnapshotFormat.NoIndex) return false;
            node = ReadNodeOfSid(sid, NodeIndex(index));
            return true;
        }

        /// <summary>Node record <paramref name="index"/>, which must be the record of <paramref name="sid"/>.</summary>
        private CodeNode ReadNodeOfSid(uint sid, int index)
        {
            if (_region.ReadU32(Element(SnapshotSectionKind.Nodes, index, SnapshotFormat.NodeRecordSize)) != sid)
                throw new SnapshotCorruptException($"Sid {sid} names node record {index}, which is another id's.");
            return ReadNode(index);
        }

        /// <summary>The node index holding <paramref name="identifier"/>, through IdentifierHash.</summary>
        public bool TryFindNodeByIdentifier(string identifier, out int nodeIndex)
        {
            ArgumentNullException.ThrowIfNull(identifier);
            nodeIndex = -1;
            if (!SnapshotFormat.TryEncodeStrict(identifier, out var key)) return false;
            var capacity = Length(SnapshotSectionKind.IdentifierHash) / 4;
            if (capacity == 0) return false;
            var slot = SnapshotFormat.Slot(Fnv1a.Hash(key), capacity);
            for (long probe = 0; probe < capacity; probe++)
            {
                var value = U32(SnapshotSectionKind.IdentifierHash, (slot + probe) & (capacity - 1));
                if (value == 0) return false;
                var candidate = NodeIndex(value - 1);
                var record = Element(SnapshotSectionKind.Nodes, candidate, SnapshotFormat.NodeRecordSize);
                var identifierRef = _region.ReadU32(record + 4);
                if (identifierRef != 0 && StringBytes(identifierRef).SequenceEqual(key))
                {
                    nodeIndex = candidate;
                    return true;
                }
            }
            return false;
        }

        public bool TryGetNodeIdByIdentifier(string identifier, out string? id)
        {
            id = null;
            if (!TryFindNodeByIdentifier(identifier, out var index)) return false;
            var sid = _region.ReadU32(Element(SnapshotSectionKind.Nodes, index, SnapshotFormat.NodeRecordSize));
            if (IdNodeIndex(sid) != (uint)index) throw new SnapshotCorruptException($"Node record {index} and the id table disagree.");
            id = Id(sid);
            return true;
        }

        /// <summary>The edge index of <paramref name="id"/>, through EdgeIdHash.</summary>
        public bool TryFindEdge(string id, out int edgeIndex)
        {
            ArgumentNullException.ThrowIfNull(id);
            edgeIndex = -1;
            if (!SnapshotFormat.TryEncodeStrict(id, out var key)) return false;
            var capacity = Length(SnapshotSectionKind.EdgeIdHash) / 4;
            if (capacity == 0) return false;
            var slot = SnapshotFormat.Slot(Fnv1a.Hash(key), capacity);
            for (long probe = 0; probe < capacity; probe++)
            {
                var value = U32(SnapshotSectionKind.EdgeIdHash, (slot + probe) & (capacity - 1));
                if (value == 0) return false;
                var candidate = EdgeIndex(value - 1);
                if (EdgeIdEquals(candidate, key))
                {
                    edgeIndex = candidate;
                    return true;
                }
            }
            return false;
        }

        public bool TryGetEdge(string id, out CodeEdge? edge)
        {
            edge = null;
            if (!TryFindEdge(id, out var index)) return false;
            edge = ReadEdge(index);
            return true;
        }

        /// <summary>Edges whose source is <paramref name="id"/>: one BySourceOffsets run of Edges, in edge id order.</summary>
        public IReadOnlyList<CodeEdge> GetEdgesBySource(string id)
        {
            if (!TryFindSid(id, out var sid)) return Array.Empty<CodeEdge>();
            var (start, end) = Run(SnapshotSectionKind.BySourceOffsets, sid, EdgeCount);
            var result = new List<CodeEdge>((int)(end - start));
            for (var j = start; j < end; j++)
            {
                var record = Element(SnapshotSectionKind.Edges, j, SnapshotFormat.EdgeRecordSize);
                if (_region.ReadU32(record) != sid) throw new SnapshotCorruptException($"Edge {j} is in the by-source run of another sid.");
                AddInIdOrder(result, ReadEdge((int)j));
            }
            return result;
        }

        /// <summary>Appends an edge to a run that must be in strictly increasing edge id order (so no edge twice).</summary>
        private static void AddInIdOrder(List<CodeEdge> run, CodeEdge edge)
        {
            if (run.Count > 0 && string.CompareOrdinal(run[^1].Id, edge.Id) >= 0)
                throw new SnapshotCorruptException($"Edge '{edge.Id}' is out of id order in its run.");
            run.Add(edge);
        }

        /// <summary>Sorts nodes into id order; two nodes with one id are corrupt.</summary>
        private static List<CodeNode> SortedById(List<CodeNode> nodes)
        {
            nodes.Sort(GraphAdjacency.ById);
            for (var i = 1; i < nodes.Count; i++)
                if (string.CompareOrdinal(nodes[i - 1].Id, nodes[i].Id) == 0)
                    throw new SnapshotCorruptException($"Node id '{nodes[i].Id}' appears twice.");
            return nodes;
        }

        /// <summary>Edges whose target is <paramref name="id"/>: one ByTargetOffsets run of ByTargetEdges, in edge id order.</summary>
        public IReadOnlyList<CodeEdge> GetEdgesByTarget(string id)
        {
            if (!TryFindSid(id, out var sid)) return Array.Empty<CodeEdge>();
            var (start, end) = Run(SnapshotSectionKind.ByTargetOffsets, sid, EdgeCount);
            var result = new List<CodeEdge>((int)(end - start));
            for (var k = start; k < end; k++)
            {
                var j = EdgeIndex(U32(SnapshotSectionKind.ByTargetEdges, k));
                var record = Element(SnapshotSectionKind.Edges, j, SnapshotFormat.EdgeRecordSize);
                if (_region.ReadU32(record + 4) != sid) throw new SnapshotCorruptException($"Edge {j} is in the by-target run of another sid.");
                AddInIdOrder(result, ReadEdge(j));
            }
            return result;
        }

        /// <summary>
        /// Node indexes whose folded name equals (or contains) the folded <paramref name="name"/>: a
        /// superset of the heap predicate's matches. A query that is not well-formed UTF-16 cannot be
        /// searched by bytes, so every named node is a candidate. Each node index appears once, in
        /// ascending order.
        /// </summary>
        public List<int> FindNameCandidates(string name, bool exact)
        {
            ArgumentNullException.ThrowIfNull(name);
            var result = new List<int>();
            if (!SnapshotFormat.TryEncodeStrict(name.ToUpperInvariant(), out var key))
            {
                for (var k = 0; k < NameCount; k++) AddPostings(k, result);
                return Distinct(result);
            }

            if (exact)
            {
                int low = 0, high = NameCount - 1;
                while (low <= high)
                {
                    var middle = low + (high - low) / 2;
                    var order = StringBytes(NameRef(middle)).SequenceCompareTo(key);
                    if (order == 0)
                    {
                        AddPostings(middle, result);
                        break;
                    }
                    if (order < 0) low = middle + 1;
                    else high = middle - 1;
                }
                return Distinct(result);
            }

            for (var k = 0; k < NameCount; k++)
                if (StringBytes(NameRef(k)).IndexOf(key) >= 0) AddPostings(k, result);
            return Distinct(result);

            static List<int> Distinct(List<int> indexes)
            {
                indexes.Sort();
                var kept = 0;
                for (var i = 0; i < indexes.Count; i++)
                    if (kept == 0 || indexes[kept - 1] != indexes[i]) indexes[kept++] = indexes[i];
                indexes.RemoveRange(kept, indexes.Count - kept);
                return indexes;
            }
        }

        /// <summary>
        /// Nodes whose name equals or contains <paramref name="name"/>, ordinal case-insensitively, in id
        /// order. A null <paramref name="name"/> is answered as the heap shard answers it: exact, the nodes
        /// whose name is null (a scan, there is no index for them); substring, an
        /// <see cref="ArgumentNullException"/> when any node has a name, otherwise nothing.
        /// </summary>
        public IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact)
        {
            var result = new List<CodeNode>();
            if (name is null)
            {
                if (!exact)
                {
                    if (NameCount > 0) throw new ArgumentNullException(nameof(name));
                    return result;
                }
                for (var i = 0; i < NodeCount; i++)
                    if (_region.ReadU32(Element(SnapshotSectionKind.Nodes, i, SnapshotFormat.NodeRecordSize) + 8) == 0) result.Add(ReadNode(i));
                return SortedById(result);
            }

            foreach (var index in FindNameCandidates(name, exact))
            {
                var node = ReadNode(index);
                if (exact
                    ? string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)
                    : node.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)
                    result.Add(node);
            }
            return SortedById(result);
        }

        /// <summary>Nodes whose file path matches <paramref name="requestedPath"/> as <see cref="FilePathMatcher.Matches"/> does, in id order.</summary>
        public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath)
        {
            var result = new List<CodeNode>();
            if (string.IsNullOrWhiteSpace(requestedPath)) return result;
            var normalized = FilePathMatcher.Normalize(requestedPath);
            var suffix = Path.IsPathRooted(requestedPath) ? null : "/" + normalized;
            var keys = FileKeys();
            for (var f = 0; f < keys.Length; f++)
            {
                var key = keys[f];
                if (key is null) continue;
                if (!string.Equals(key, normalized, StringComparison.OrdinalIgnoreCase)
                    && (suffix is null || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // File node ranges are disjoint (checked at open), so no node is added twice.
                var (first, count) = FileNodes(f);
                for (var i = first; i < first + count; i++) result.Add(ReadNode((int)i));
            }
            return SortedById(result);
        }

        /// <summary>The distinct non-empty file paths, in the store's file-path order (the Files order).</summary>
        public IReadOnlyList<string> GetFilePaths()
        {
            var result = new List<string>(FileCount);
            for (var f = 0; f < FileCount; f++)
            {
                var path = GetString(_region.ReadU32(Element(SnapshotSectionKind.Files, f, SnapshotFormat.FileEntrySize)));
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
            return result;
        }

        /// <summary>Adds the TypeHistogram's node and edge type counts.</summary>
        public void AddStatistics(Dictionary<string, int> nodesByType, Dictionary<string, int> edgesByType)
        {
            long position = 0;
            ReadHistogram(nodesByType, ref position);
            ReadHistogram(edgesByType, ref position);
        }

        /// <summary>Ids of the edges listed in CrossShardEdges.</summary>
        public IReadOnlyList<string> GetCrossShardEdgeIds()
        {
            var count = Length(SnapshotSectionKind.CrossShardEdges) / 4;
            var result = new List<string>((int)count);
            var previous = -1;
            for (long k = 0; k < count; k++)
            {
                var j = EdgeIndex(U32(SnapshotSectionKind.CrossShardEdges, k));
                if (j <= previous) throw new SnapshotCorruptException("The cross-shard edge list is not in strictly increasing edge order.");
                previous = j;
                var record = Element(SnapshotSectionKind.Edges, j, SnapshotFormat.EdgeRecordSize);
                var sourceSid = _region.ReadU32(record);
                var targetSid = _region.ReadU32(record + 4);
                result.Add(EdgeId(record, _region.ReadU32(record + 28),
                    sourceSid == SnapshotFormat.NoIndex ? null : Id(sourceSid),
                    targetSid == SnapshotFormat.NoIndex ? null : Id(targetSid)));
            }
            return result;
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
                if (string.CompareOrdinal(result[j - 1].Id, result[j].Id) == 0)
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

        private uint NameRef(int entry) => _region.ReadU32(Element(SnapshotSectionKind.NameTable, entry, SnapshotFormat.NameEntrySize));

        private void AddPostings(int entry, List<int> into)
        {
            var at = Element(SnapshotSectionKind.NameTable, entry, SnapshotFormat.NameEntrySize);
            long start = _region.ReadU32(at + 4);
            long count = _region.ReadU32(at + 8);
            if (start + count > Length(SnapshotSectionKind.NamePostings) / 4)
                throw new SnapshotCorruptException($"Name {entry}'s postings lie outside the postings section.");
            for (var k = start; k < start + count; k++) into.Add(NodeIndex(U32(SnapshotSectionKind.NamePostings, k)));
        }

        private (long Start, long End) Run(SnapshotSectionKind offsets, uint sid, long limit)
        {
            long start = U32(offsets, sid);
            long end = U32(offsets, sid + 1L);
            if (start > end || end > limit) throw new SnapshotCorruptException($"The {offsets} run of sid {sid} is out of range.");
            return (start, end);
        }

        private (long First, long Count) FileNodes(int file)
        {
            var at = Element(SnapshotSectionKind.Files, file, SnapshotFormat.FileEntrySize);
            long first = _region.ReadU32(at + 8);
            long count = _region.ReadU32(at + 12);
            if (first + count > NodeCount) throw new SnapshotCorruptException($"File {file}'s node range is beyond the nodes.");
            return (first, count);
        }

        private string?[] FileKeys()
        {
            var keys = Volatile.Read(ref _fileKeys);
            if (keys is not null) return keys;
            keys = new string?[FileCount];
            for (var f = 0; f < FileCount; f++)
                keys[f] = GetString(_region.ReadU32(Element(SnapshotSectionKind.Files, f, SnapshotFormat.FileEntrySize) + 4));
            Volatile.Write(ref _fileKeys, keys);
            return keys;
        }

        private string EdgeId(long record, uint metaAndForm, string? source, string? target)
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
                    return string.Concat(source, a, target, b);
                default:
                    throw new SnapshotCorruptException("An edge has an unknown id form.");
            }
        }

        private bool EdgeIdEquals(int index, ReadOnlySpan<byte> key)
        {
            var record = Element(SnapshotSectionKind.Edges, index, SnapshotFormat.EdgeRecordSize);
            var metaAndForm = _region.ReadU32(record + 28);
            var aRef = _region.ReadU32(record + 12);
            switch (metaAndForm & SnapshotFormat.EdgeIdFormMask)
            {
                case SnapshotFormat.EdgeIdFormWhole:
                    return StringBytes(aRef).SequenceEqual(key);
                case SnapshotFormat.EdgeIdFormComposed:
                    var sourceSid = _region.ReadU32(record);
                    var targetSid = _region.ReadU32(record + 4);
                    if (sourceSid == SnapshotFormat.NoIndex || targetSid == SnapshotFormat.NoIndex)
                        throw new SnapshotCorruptException("A composed edge id lacks an endpoint.");
                    return MatchPart(ref key, IdStringRef(sourceSid))
                        && MatchPart(ref key, aRef)
                        && MatchPart(ref key, IdStringRef(targetSid))
                        && MatchPart(ref key, _region.ReadU32(record + 16))
                        && key.IsEmpty;
                default:
                    throw new SnapshotCorruptException("An edge has an unknown id form.");
            }
        }

        private bool MatchPart(ref ReadOnlySpan<byte> key, uint reference)
        {
            var part = StringBytes(reference);
            if (!key.StartsWith(part)) return false;
            key = key[part.Length..];
            return true;
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
        /// that ordinal order), so the dictionary enumerates in ordinal key order. A repeated key is corrupt.
        /// </summary>
        private Dictionary<string, string> Metadata(uint setRef, string? column, string? line)
        {
            var extraTotal = 0;
            var extras = new (string Key, string Value)[4];
            if (column is not null) extras[extraTotal++] = (SnapshotFormat.ColumnKey, column);
            if (line is not null) extras[extraTotal++] = (SnapshotFormat.LineKey, line);
            extras[extraTotal++] = (SnapshotFormat.ParserIdKey, Key.ParserId);
            extras[extraTotal++] = (SnapshotFormat.WorkspaceIdKey, Key.WorkspaceId);

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

            var dictionary = new Dictionary<string, string>((int)pairCount + extraTotal, StringComparer.Ordinal);
            var next = 0;
            for (long p = 0; p < pairCount; p++)
            {
                var at = pairs + 8 * p;
                var key = GetString(_region.ReadU32(at)) ?? throw new SnapshotCorruptException("A metadata key is null.");
                while (next < extraTotal && string.CompareOrdinal(extras[next].Key, key) < 0) Add(extras[next].Key, extras[next++].Value);
                Add(key, GetString(_region.ReadU32(at + 4))!);
            }
            while (next < extraTotal) Add(extras[next].Key, extras[next++].Value);
            return dictionary;

            void Add(string key, string value)
            {
                if (!dictionary.TryAdd(key, value)) throw new SnapshotCorruptException($"Metadata key '{key}' appears twice.");
            }
        }

        private void ReadHistogram(Dictionary<string, int> into, ref long position)
        {
            var length = Length(SnapshotSectionKind.TypeHistogram) / 4;
            long count = U32(SnapshotSectionKind.TypeHistogram, position++);
            if (count > (length - position) / 2) throw new SnapshotCorruptException("The type histogram is longer than its section.");
            for (long k = 0; k < count; k++)
            {
                var type = GetString(U32(SnapshotSectionKind.TypeHistogram, position++));
                var n = U32(SnapshotSectionKind.TypeHistogram, position++);
                if (string.IsNullOrEmpty(type) || n > int.MaxValue) throw new SnapshotCorruptException("A type histogram entry is malformed.");
                var total = (long)into.GetValueOrDefault(type) + n;
                if (total > int.MaxValue) throw new SnapshotCorruptException("A type histogram count overflows.");
                into[type] = (int)total;
            }
        }
    }
}
