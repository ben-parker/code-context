using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>What the host records about itself in a snapshot, and what a reader requires to match.</summary>
    internal sealed record SnapshotHostMeta(string HostVersion, string RootPath, string Platform, bool PathsCaseInsensitive);

    /// <summary>
    /// What a snapshot stores beside one shard's facts: the parser version and worker state binding
    /// (each optional), and the coverage and fingerprints JSON, opaque UTF-8 bytes handed back as written.
    /// </summary>
    internal sealed record SnapshotShardInfo(
        string? ParserVersion = null,
        string? StateFileName = null,
        string? StateId = null,
        string? StateSha256 = null,
        byte[]? Coverage = null,
        byte[]? Fingerprints = null)
    {
        public static readonly SnapshotShardInfo Empty = new();
    }

    /// <summary>One capture to write: a shard map and everything the header records.</summary>
    internal sealed class SnapshotWriteInput
    {
        public required IEnumerable<KeyValuePair<ShardKey, IGraphShard>> Shards { get; init; }

        /// <summary>Per-shard meta; a shard without an entry is written with <see cref="SnapshotShardInfo.Empty"/>.</summary>
        public IReadOnlyDictionary<ShardKey, SnapshotShardInfo>? ShardInfo { get; init; }

        public required SnapshotHostMeta Host { get; init; }

        public ulong Sequence { get; init; }

        public ulong StoreGeneration { get; init; }

        public long CreatedUtcTicks { get; init; }

        public Guid InstanceId { get; init; }
    }

    /// <summary>A shard the writer left out, and why.</summary>
    internal sealed record SnapshotSkippedShard(ShardKey Key, string Reason);

    /// <summary>
    /// The outcome of a write: the file length, which shards went in, which did not, and bytes per
    /// section kind. When <see cref="NotWritten"/> is set nothing was written at all (no byte to the
    /// stream, no file): it says why, <see cref="Length"/> is the length the file would have had and
    /// <see cref="Written"/> is empty.
    /// </summary>
    internal sealed record SnapshotWriteResult(
        long Length,
        IReadOnlyList<ShardKey> Written,
        IReadOnlyList<SnapshotSkippedShard> Skipped,
        IReadOnlyDictionary<SnapshotSectionKind, long> SectionBytes,
        string? NotWritten = null)
    {
        public bool IsWritten => NotWritten is null;
    }

    /// <summary>
    /// Serializes a shard map into the snapshot format of plan section 3.2, consuming each shard only
    /// through <see cref="IGraphShard"/>. The output is a pure function of the shards' contents and the
    /// input's fields: nothing depends on dictionary enumeration order, so the same capture gives
    /// byte-identical files.
    ///
    /// A shard is persisted only when it can be verified and reproduced exactly; otherwise it is
    /// skipped with a reason and the others are written (see <see cref="CheckPersistable"/>).
    /// </summary>
    internal static class SnapshotWriter
    {
        private const int ChunkSize = 1 << 20;

        /// <summary>
        /// Writes the snapshot of <paramref name="input"/> to <paramref name="output"/>, or nothing when the
        /// file would be longer than <see cref="SnapshotFormat.MaxFileLength"/> (see
        /// <see cref="SnapshotWriteResult.NotWritten"/>).
        /// </summary>
        public static SnapshotWriteResult Write(Stream output, SnapshotWriteInput input, byte[] key)
            => Write(output, input, key, SnapshotFormat.MaxFileLength);

        /// <summary><see cref="Write(Stream, SnapshotWriteInput, byte[])"/> with the length cap given, for tests.</summary>
        internal static SnapshotWriteResult Write(Stream output, SnapshotWriteInput input, byte[] key, long maxFileLength)
        {
            ArgumentNullException.ThrowIfNull(output);
            var plan = Plan(input, key, maxFileLength);
            if (plan.Result.IsWritten) Emit(output, plan, key);
            return plan.Result;
        }

        /// <summary>
        /// Writes a snapshot to <paramref name="path"/> (created new, never overwritten; mode 0600 on Unix)
        /// and flushes it to disk. Creates no file when the snapshot would exceed
        /// <see cref="SnapshotFormat.MaxFileLength"/>, and deletes its partial file when writing fails.
        /// Naming, the temp-then-rename protocol and retiring older files are the caller's.
        /// </summary>
        public static SnapshotWriteResult WriteFile(string path, SnapshotWriteInput input, byte[] key)
            => WriteFile(path, input, key, SnapshotFormat.MaxFileLength);

        /// <summary>
        /// <see cref="WriteFile(string, SnapshotWriteInput, byte[])"/> with the length cap given and, for
        /// tests that make a write fail part way, a wrapper around the file stream.
        /// </summary>
        internal static SnapshotWriteResult WriteFile(
            string path, SnapshotWriteInput input, byte[] key, long maxFileLength, Func<Stream, Stream>? wrapStream = null)
        {
            var plan = Plan(input, key, maxFileLength);
            if (!plan.Result.IsWritten) return plan.Result;

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            // Created here, so from here on a failure deletes it; a path that already exists is never touched.
            var stream = new FileStream(path, options);
            try
            {
                using (stream)
                {
                    // A test wrapper is flushed and disposed first, so the durability flush sees every byte.
                    var target = wrapStream?.Invoke(stream);
                    if (target is null || ReferenceEquals(target, stream))
                    {
                        Emit(stream, plan, key);
                    }
                    else
                    {
                        using (target)
                        {
                            Emit(target, plan, key);
                            target.Flush();
                        }
                    }
                    stream.Flush(flushToDisk: true);
                }
            }
            catch
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left for the caller's cleanup of temp files; the write's own exception matters more.
                }
                throw;
            }
            return plan.Result;
        }

        /// <summary>A capture laid out and serialized in memory, ready to be emitted.</summary>
        private sealed class WritePlan
        {
            public required SnapshotWriteResult Result { get; init; }
            public required byte[] Header { get; init; }
            public required byte[] HostMeta { get; init; }
            public required long DirectoryOffset { get; init; }
            public required byte[] Directory { get; init; }
            public required List<ShardBlob> Blobs { get; init; }
            public required long[] BlobOffsets { get; init; }
            public required long FooterOffset { get; init; }
        }

        private static WritePlan Plan(SnapshotWriteInput input, byte[] key, long maxFileLength)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(key);
            if (key.Length != SnapshotKey.Length) throw new ArgumentException("The snapshot key must be 32 bytes.", nameof(key));
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Snapshots are written only by little-endian processes.");

            var written = new List<ShardKey>();
            var skipped = new List<SnapshotSkippedShard>();
            var blobs = new List<ShardBlob>();
            var sectionBytes = new SortedDictionary<SnapshotSectionKind, long>();
            foreach (SnapshotSectionKind kind in Enum.GetValues<SnapshotSectionKind>()) sectionBytes[kind] = 0;

            foreach (var (shardKey, shard) in input.Shards.OrderBy(pair => pair.Key, ShardKey.Comparer))
            {
                var reason = CheckPersistable(shardKey, shard);
                if (reason is not null)
                {
                    skipped.Add(new SnapshotSkippedShard(shardKey, reason));
                    continue;
                }
                var info = input.ShardInfo?.GetValueOrDefault(shardKey) ?? SnapshotShardInfo.Empty;
                ShardBlob? blob;
                try
                {
                    blob = ShardBlobBuilder.Build(shardKey, shard, info, out reason);
                }
                catch (InvalidOperationException exception)
                {
                    // A section or the blob larger than the largest array: the shard is beyond the
                    // format's limits (see SnapshotFormat). Every limit check throws this type.
                    blob = null;
                    reason = exception.Message;
                }
                if (blob is null)
                {
                    skipped.Add(new SnapshotSkippedShard(shardKey, reason!));
                    continue;
                }
                blobs.Add(blob);
                written.Add(shardKey);
                for (var i = 0; i < SnapshotFormat.SectionCount; i++)
                    sectionBytes[(SnapshotSectionKind)(i + 1)] += blob.SectionLengths[i];
            }

            var hostMeta = EncodeKeyValues(
            [
                (SnapshotFormat.HostVersionKey, input.Host.HostVersion),
                (SnapshotFormat.RootPathKey, input.Host.RootPath),
                (SnapshotFormat.PlatformKey, input.Host.Platform),
            ]);

            long hostMetaOffset = SnapshotFormat.HeaderSize;
            var directoryOffset = SnapshotFormat.Align8(hostMetaOffset + hostMeta.Length);
            var position = SnapshotFormat.Align8(directoryOffset + (long)blobs.Count * SnapshotFormat.DirectoryEntrySize);
            var blobOffsets = new long[blobs.Count];
            for (var i = 0; i < blobs.Count; i++)
            {
                blobOffsets[i] = position;
                position = SnapshotFormat.Align8(position + blobs[i].Bytes.Length);
            }
            var footerOffset = position;
            var fileLength = footerOffset + SnapshotFormat.FooterSize;

            var header = new byte[SnapshotFormat.HeaderSize];
            SnapshotFormat.HeaderMagic.CopyTo(header);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderFormatVersion), SnapshotFormat.FormatVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderEndianTag), SnapshotFormat.EndianTag);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderHeaderSize), SnapshotFormat.HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderFlags),
                input.Host.PathsCaseInsensitive ? SnapshotFormat.FlagPathsCaseInsensitive : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderFileLength), (ulong)fileLength);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderSequence), input.Sequence);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderStoreGeneration), input.StoreGeneration);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderCreatedUtcTicks), input.CreatedUtcTicks);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderHostMetaOffset), (ulong)hostMetaOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderHostMetaLength), (uint)hostMeta.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(SnapshotFormat.HeaderShardCount), (uint)blobs.Count);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(SnapshotFormat.HeaderShardDirectoryOffset), (ulong)directoryOffset);
            if (!input.InstanceId.TryWriteBytes(header.AsSpan(SnapshotFormat.HeaderInstanceId, 16)))
                throw new InvalidOperationException("The instance id could not be written.");

            // Directory entry: u64 blobOffset, u64 blobLength, 32 bytes blobSha256, u32 nodeCount,
            // u32 edgeCount, u32 flags (0), u32 reserved (0). blobSha256 is for phase 8's copy-through of
            // an unchanged blob into the next file; the HMAC covers it, and no reader verifies it at load.
            var directory = new byte[blobs.Count * SnapshotFormat.DirectoryEntrySize];
            for (var i = 0; i < blobs.Count; i++)
            {
                var entry = directory.AsSpan(i * SnapshotFormat.DirectoryEntrySize, SnapshotFormat.DirectoryEntrySize);
                BinaryPrimitives.WriteUInt64LittleEndian(entry, (ulong)blobOffsets[i]);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], (ulong)blobs[i].Bytes.Length);
                SHA256.HashData(blobs[i].Bytes, entry[16..48]);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[48..], (uint)blobs[i].NodeCount);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[52..], (uint)blobs[i].EdgeCount);
            }

            var result = fileLength > maxFileLength
                ? new SnapshotWriteResult(fileLength, Array.Empty<ShardKey>(), skipped, sectionBytes,
                    $"the snapshot would be {fileLength} bytes, above the {maxFileLength}-byte limit")
                : new SnapshotWriteResult(fileLength, written, skipped, sectionBytes);
            return new WritePlan
            {
                Result = result,
                Header = header,
                HostMeta = hostMeta,
                DirectoryOffset = directoryOffset,
                Directory = directory,
                Blobs = blobs,
                BlobOffsets = blobOffsets,
                FooterOffset = footerOffset,
            };
        }

        private static void Emit(Stream output, WritePlan plan, byte[] key)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            var sink = new AuthenticatedSink(output, hmac);
            sink.Write(plan.Header);
            sink.Write(plan.HostMeta);
            sink.PadTo(plan.DirectoryOffset);
            sink.Write(plan.Directory);
            for (var i = 0; i < plan.Blobs.Count; i++)
            {
                sink.PadTo(plan.BlobOffsets[i]);
                sink.Write(plan.Blobs[i].Bytes);
            }
            sink.PadTo(plan.FooterOffset);
            sink.Write(SnapshotFormat.FooterMagic);
            var mac = hmac.GetHashAndReset();
            output.Write(mac);
            if (sink.Position + mac.Length != plan.Result.Length)
                throw new InvalidOperationException("The snapshot layout did not add up.");
        }

        /// <summary>
        /// Why <paramref name="shard"/> cannot be persisted, or null when it can. The rule, checked in
        /// this order:
        /// the <see cref="ShardKey.Default"/> shard is never persisted;
        /// a shard with no node and no edge is skipped (nothing to persist; an empty key is no fact);
        /// the shard key survives a strict UTF-8 round trip;
        /// every node id, edge id and edge source id is non-null and starts with
        /// <c>&lt;parserId&gt;:&lt;Uri.EscapeDataString(workspaceId)&gt;:</c>, the prefix the host enforces on worker deltas
        /// (an edge target may be anything, or null);
        /// every node's identifier is non-null (the store always sets one; a record cannot tell null from absent);
        /// every node and edge has metadata that is a <see cref="Dictionary{TKey, TValue}"/> comparing keys
        /// ordinally (default or <see cref="StringComparer.Ordinal"/>), because a loaded fact's metadata is
        /// one, and a lookup in any other comparer can answer differently (a metadata value may be null);
        /// that metadata carries <c>parserId</c> and <c>workspaceId</c> equal to the shard key
        /// (a scoped commit routes delta facts to its scope's shard whatever their metadata says, so a
        /// disagreeing or absent value can occur, and would not be reproduced by a load);
        /// every node's non-blank identifier resolves to that node through the shard's identifier map;
        /// every string (fields, metadata keys and values) survives a strict UTF-8 round trip;
        /// counts stay below 2^30 and every section and the blob fit one array (checked while building).
        ///
        /// The loader applies the same rule to every shard it materializes and refuses the file when a
        /// shard fails it: a load never publishes a fact the writer would not have written.
        /// </summary>
        internal static string? CheckPersistable(ShardKey key, IGraphShard shard)
            => CheckPersistable(key, shard.NodeCount, shard.EdgeCount, shard.GetAllNodes(), shard.GetAllEdges(), shard.TryGetNodeIdByIdentifier);

        /// <summary>Resolves an identifier through a shard's identifier map.</summary>
        internal delegate bool IdentifierResolver(string identifier, out string? id);

        /// <summary>
        /// <see cref="CheckPersistable(ShardKey, IGraphShard)"/> over the facts directly, in any order (the
        /// reason names the first failing fact in the order given). The one rule the writer and the loader share.
        /// </summary>
        internal static string? CheckPersistable(
            ShardKey key, int nodeCount, int edgeCount, IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges, IdentifierResolver resolve)
        {
            if (key == ShardKey.Default || string.IsNullOrEmpty(key.ParserId) || string.IsNullOrEmpty(key.WorkspaceId))
                return "the un-owned default shard is never persisted";
            if (nodeCount == 0 && edgeCount == 0) return "the shard holds no fact";
            if (!SnapshotFormat.IsWellFormed(key.ParserId) || !SnapshotFormat.IsWellFormed(key.WorkspaceId))
                return "the shard key is not well-formed UTF-16";

            var prefix = $"{key.ParserId}:{Uri.EscapeDataString(key.WorkspaceId)}:";
            foreach (var node in nodes)
            {
                if (node.Id is null || !node.Id.StartsWith(prefix, StringComparison.Ordinal))
                    return $"node id '{node.Id}' does not start with '{prefix}'";
                if (node.Identifier is null) return $"node '{node.Id}' has a null identifier";
                if (!OrdinalMetadata(node.Metadata)) return $"node '{node.Id}' has metadata that does not compare keys ordinally";
                if (!OwnedBy(node.Metadata, key)) return $"node '{node.Id}' carries ownership metadata of another shard";
                if (!string.IsNullOrWhiteSpace(node.Identifier)
                    && (!resolve(node.Identifier, out var holder) || holder != node.Id))
                    return $"identifier '{node.Identifier}' of node '{node.Id}' does not resolve to it";
                if (!WellFormed(node)) return $"node '{node.Id}' holds a string with a lone surrogate";
            }
            foreach (var edge in edges)
            {
                if (edge.Id is null || !edge.Id.StartsWith(prefix, StringComparison.Ordinal))
                    return $"edge id '{edge.Id}' does not start with '{prefix}'";
                if (edge.SourceId is null || !edge.SourceId.StartsWith(prefix, StringComparison.Ordinal))
                    return $"source '{edge.SourceId}' of edge '{edge.Id}' does not start with '{prefix}'";
                if (!OrdinalMetadata(edge.Metadata)) return $"edge '{edge.Id}' has metadata that does not compare keys ordinally";
                if (!OwnedBy(edge.Metadata, key)) return $"edge '{edge.Id}' carries ownership metadata of another shard";
                if (!SnapshotFormat.IsWellFormed(edge.Id) || !SnapshotFormat.IsWellFormed(edge.SourceId)
                    || !SnapshotFormat.IsWellFormed(edge.TargetId) || !SnapshotFormat.IsWellFormed(edge.Type)
                    || !WellFormed(edge.Metadata))
                    return $"edge '{edge.Id}' holds a string with a lone surrogate";
            }
            return null;
        }

        private static bool OrdinalMetadata(IReadOnlyDictionary<string, string>? metadata)
            => metadata is Dictionary<string, string> dictionary
                && (ReferenceEquals(dictionary.Comparer, EqualityComparer<string>.Default) || ReferenceEquals(dictionary.Comparer, StringComparer.Ordinal));

        private static bool OwnedBy(IReadOnlyDictionary<string, string>? metadata, ShardKey key)
            => metadata is not null
                && metadata.TryGetValue(SnapshotFormat.ParserIdKey, out var parserId) && parserId == key.ParserId
                && metadata.TryGetValue(SnapshotFormat.WorkspaceIdKey, out var workspaceId) && workspaceId == key.WorkspaceId;

        private static bool WellFormed(CodeNode node)
            => SnapshotFormat.IsWellFormed(node.Id) && SnapshotFormat.IsWellFormed(node.Identifier)
                && SnapshotFormat.IsWellFormed(node.Name) && SnapshotFormat.IsWellFormed(node.Type)
                && SnapshotFormat.IsWellFormed(node.FilePath) && SnapshotFormat.IsWellFormed(node.Namespace)
                && SnapshotFormat.IsWellFormed(node.Visibility) && SnapshotFormat.IsWellFormed(node.Signature)
                && SnapshotFormat.IsWellFormed(node.Language) && SnapshotFormat.IsWellFormed(node.ReturnType)
                && SnapshotFormat.IsWellFormed(node.Parameters) && SnapshotFormat.IsWellFormed(node.Modifiers)
                && SnapshotFormat.IsWellFormed(node.Metrics) && WellFormed(node.Metadata);

        private static bool WellFormed(IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null) return true;
            foreach (var (k, v) in metadata)
                if (!SnapshotFormat.IsWellFormed(k) || !SnapshotFormat.IsWellFormed(v)) return false;
            return true;
        }

        /// <summary><c>u32 count</c>, then per entry <c>u16 keyLength, key, u32 valueLength, value</c>, UTF-8.</summary>
        internal static byte[] EncodeKeyValues(IReadOnlyList<(string Key, string Value)> entries)
        {
            var buffer = new LittleEndianBuffer();
            buffer.WriteU32((uint)entries.Count);
            foreach (var (k, v) in entries)
            {
                var keyBytes = SnapshotFormat.StrictUtf8.GetBytes(k);
                var valueBytes = SnapshotFormat.StrictUtf8.GetBytes(v);
                if (keyBytes.Length > ushort.MaxValue) throw new ArgumentException($"Meta key '{k}' is too long.");
                buffer.WriteU16((ushort)keyBytes.Length);
                buffer.WriteBytes(keyBytes);
                buffer.WriteU32((uint)valueBytes.Length);
                buffer.WriteBytes(valueBytes);
            }
            return buffer.ToArray();
        }

        /// <summary>The canonical decimal int32 form of a metadata value, excluding the absent marker.</summary>
        internal static bool TryCanonicalPosition(string? value, out int position)
        {
            position = SnapshotFormat.AbsentPosition;
            if (value is null
                || !int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                || parsed == SnapshotFormat.AbsentPosition
                || parsed.ToString(CultureInfo.InvariantCulture) != value)
                return false;
            position = parsed;
            return true;
        }

        private sealed class AuthenticatedSink(Stream output, IncrementalHash hmac)
        {
            private static readonly byte[] Zeros = new byte[8];

            public long Position { get; private set; }

            public void Write(ReadOnlySpan<byte> bytes)
            {
                while (bytes.Length > 0)
                {
                    var chunk = bytes[..Math.Min(bytes.Length, ChunkSize)];
                    hmac.AppendData(chunk);
                    output.Write(chunk);
                    Position += chunk.Length;
                    bytes = bytes[chunk.Length..];
                }
            }

            public void PadTo(long offset)
            {
                while (Position < offset) Write(Zeros.AsSpan(0, (int)Math.Min(Zeros.Length, offset - Position)));
            }
        }

        internal sealed class ShardBlob
        {
            public required byte[] Bytes { get; init; }
            public required int NodeCount { get; init; }
            public required int EdgeCount { get; init; }
            public required long[] SectionLengths { get; init; }
        }

        /// <summary>Builds one position-independent shard blob.</summary>
        private sealed class ShardBlobBuilder
        {
            private readonly Dictionary<string, uint> _stringRefs = new(StringComparer.Ordinal);
            private readonly List<string> _strings = new();

            public static ShardBlob? Build(ShardKey key, IGraphShard shard, SnapshotShardInfo info, out string? reason)
                => new ShardBlobBuilder().BuildCore(key, shard, info, out reason);

            private ShardBlob? BuildCore(ShardKey key, IGraphShard shard, SnapshotShardInfo info, out string? reason)
            {
                reason = null;
                var allNodes = shard.GetAllNodes();   // id order
                var allEdges = shard.GetAllEdges();   // id order
                if (allNodes.Count > SnapshotFormat.MaxShardCount || allEdges.Count > SnapshotFormat.MaxShardCount)
                {
                    reason = "the shard holds too many facts";
                    return null;
                }

                // Ids: every node id, edge source and edge target, in ordinal order; the index is the sid.
                var idSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in allNodes) idSet.Add(node.Id!);
                foreach (var edge in allEdges)
                {
                    idSet.Add(edge.SourceId!);
                    if (edge.TargetId is not null) idSet.Add(edge.TargetId);
                }
                var ids = idSet.ToArray();
                Array.Sort(ids, StringComparer.Ordinal);
                var sidOf = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
                for (var i = 0; i < ids.Length; i++) sidOf[ids[i]] = i;

                // File buckets: the null path first, then raw paths in the store's file-path order.
                var byPath = new Dictionary<string, List<CodeNode>>(StringComparer.Ordinal);
                var nullPathNodes = new List<CodeNode>();
                foreach (var node in allNodes)
                {
                    if (node.FilePath is null) { nullPathNodes.Add(node); continue; }
                    if (!byPath.TryGetValue(node.FilePath, out var bucket)) byPath[node.FilePath] = bucket = new List<CodeNode>();
                    bucket.Add(node);
                }
                var files = new List<(string? Path, List<CodeNode> Nodes)>();
                if (nullPathNodes.Count > 0) files.Add((null, nullPathNodes));
                foreach (var path in GraphAdjacency.SortPaths(byPath.Keys)) files.Add((path, byPath[path]));

                var nodes = new List<CodeNode>(allNodes.Count);
                var fileOfNode = new List<int>(allNodes.Count);
                for (var f = 0; f < files.Count; f++)
                    foreach (var node in files[f].Nodes) { nodes.Add(node); fileOfNode.Add(f); }
                var nodeIndexOfSid = new uint[ids.Length];
                Array.Fill(nodeIndexOfSid, SnapshotFormat.NoIndex);
                for (var i = 0; i < nodes.Count; i++) nodeIndexOfSid[sidOf[nodes[i].Id!]] = (uint)i;

                // Edges by (source sid, edge id); edges arrive in id order, so a stable sort on the sid.
                // Each endpoint is looked up once (ids are long; hashing them dominates the build).
                var sourceSidById = new int[allEdges.Count];
                var targetSidById = new int[allEdges.Count];
                for (var k = 0; k < allEdges.Count; k++)
                {
                    sourceSidById[k] = sidOf[allEdges[k].SourceId!];
                    targetSidById[k] = allEdges[k].TargetId is { } targetId ? sidOf[targetId] : -1;
                }
                var edgeOrder = Enumerable.Range(0, allEdges.Count).ToArray();
                var sortKeys = (int[])sourceSidById.Clone();
                Array.Sort(sortKeys, edgeOrder);   // not stable: restore id order inside each source run
                for (int runStart = 0, k = 1; k <= edgeOrder.Length; k++)
                {
                    if (k < edgeOrder.Length && sortKeys[k] == sortKeys[runStart]) continue;
                    Array.Sort(edgeOrder, runStart, k - runStart);
                    runStart = k;
                }
                var edges = new List<CodeEdge>(edgeOrder.Length);
                var sourceSid = new int[edgeOrder.Length];
                var targetSid = new int[edgeOrder.Length];
                var recordOfIdOrder = new int[edgeOrder.Length];
                for (var j = 0; j < edgeOrder.Length; j++)
                {
                    edges.Add(allEdges[edgeOrder[j]]);
                    sourceSid[j] = sourceSidById[edgeOrder[j]];
                    targetSid[j] = targetSidById[edgeOrder[j]];
                    recordOfIdOrder[edgeOrder[j]] = j;
                }

                // Names: folded names grouped, postings in id order (node indexes of nodes in id order).
                var postingsByName = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
                var nodeIndexById = new Dictionary<string, uint>(nodes.Count, StringComparer.Ordinal);
                for (var i = 0; i < nodes.Count; i++) nodeIndexById[nodes[i].Id!] = (uint)i;
                foreach (var node in allNodes)
                {
                    if (node.Name is null) continue;
                    var folded = node.Name.ToUpperInvariant();
                    if (!postingsByName.TryGetValue(folded, out var postings)) postingsByName[folded] = postings = new List<uint>();
                    postings.Add(nodeIndexById[node.Id!]);
                }
                var names = postingsByName.Keys
                    .Select(name => (Name: name, Bytes: SnapshotFormat.StrictUtf8.GetBytes(name)))
                    .ToList();
                names.Sort(static (left, right) => left.Bytes.AsSpan().SequenceCompareTo(right.Bytes));

                // Strings: folded names occupy the contiguous reference range from 1, in NameTable order;
                // every other distinct string follows in ordinal order.
                foreach (var (name, _) in names) Intern(name);
                const uint FoldedNameFirstRef = 1;
                var others = new HashSet<string>(StringComparer.Ordinal);
                void Collect(string? value) { if (value is not null && !_stringRefs.ContainsKey(value)) others.Add(value); }

                foreach (var id in ids) Collect(id);
                foreach (var (path, _) in files)
                {
                    Collect(path);
                    if (!string.IsNullOrWhiteSpace(path)) Collect(FilePathMatcher.Normalize(path));
                }

                var nodeSets = new List<string[]>(nodes.Count);
                foreach (var node in nodes)
                {
                    Collect(node.Identifier); Collect(node.Name); Collect(node.Type); Collect(node.Language);
                    Collect(node.Namespace); Collect(node.Visibility); Collect(node.Signature); Collect(node.ReturnType);
                    Collect(node.Parameters); Collect(node.Modifiers); Collect(node.Metrics);
                    var set = MetadataPairs(node.Metadata, null);
                    foreach (var value in set) Collect(value);
                    nodeSets.Add(set);
                }

                var edgeParts = new List<(uint Form, string A, string? B, int Line, int Column, string[] Set)>(edges.Count);
                foreach (var edge in edges)
                {
                    Collect(edge.Type);
                    var id = edge.Id!;
                    var source = edge.SourceId!;
                    var target = edge.TargetId;
                    uint form = SnapshotFormat.EdgeIdFormWhole;
                    string a = id;
                    string? b = null;
                    if (target is not null && id.StartsWith(source, StringComparison.Ordinal))
                    {
                        var at = id.IndexOf(target, source.Length, StringComparison.Ordinal);
                        if (at >= 0)
                        {
                            // By construction id == source + middle + target + tail.
                            form = SnapshotFormat.EdgeIdFormComposed;
                            a = id[source.Length..at];
                            b = id[(at + target.Length)..];
                        }
                    }
                    Collect(a);
                    Collect(b);

                    string? lineText = null, columnText = null;
                    edge.Metadata?.TryGetValue(SnapshotFormat.LineKey, out lineText);
                    edge.Metadata?.TryGetValue(SnapshotFormat.ColumnKey, out columnText);
                    var hasLine = TryCanonicalPosition(lineText, out var line);
                    var hasColumn = TryCanonicalPosition(columnText, out var column);
                    var set = MetadataPairs(edge.Metadata, (hasLine, hasColumn));
                    foreach (var value in set) Collect(value);
                    edgeParts.Add((form, a, b, line, column, set));
                }

                var sortedOthers = others.ToArray();
                Array.Sort(sortedOthers, StringComparer.Ordinal);
                foreach (var value in sortedOthers) Intern(value);
                if (_strings.Count > SnapshotFormat.MaxShardCount)
                {
                    reason = "the shard holds too many strings";
                    return null;
                }

                // ---- Sections ----
                var sections = new byte[SnapshotFormat.SectionCount][];

                var meta = new List<(string, string)>
                {
                    (SnapshotFormat.ParserIdKey, key.ParserId),
                    (SnapshotFormat.WorkspaceIdKey, key.WorkspaceId),
                };
                if (info.ParserVersion is not null) meta.Add((SnapshotFormat.ParserVersionKey, info.ParserVersion));
                if (info.StateFileName is not null) meta.Add((SnapshotFormat.StateFileKey, info.StateFileName));
                if (info.StateId is not null) meta.Add((SnapshotFormat.StateIdKey, info.StateId));
                if (info.StateSha256 is not null) meta.Add((SnapshotFormat.StateSha256Key, info.StateSha256));
                sections[0] = EncodeKeyValues(meta);

                var stringOffsets = new LittleEndianBuffer();
                var stringBytes = new LittleEndianBuffer();
                var stringUtf8 = new byte[_strings.Count][];
                stringOffsets.WriteU32(0);
                for (var i = 0; i < _strings.Count; i++)
                {
                    stringUtf8[i] = SnapshotFormat.StrictUtf8.GetBytes(_strings[i]);
                    stringBytes.WriteBytes(stringUtf8[i]);
                    if (stringBytes.Length > uint.MaxValue)
                    {
                        reason = "the shard's strings exceed 4 GiB";
                        return null;
                    }
                    stringOffsets.WriteU32((uint)stringBytes.Length);
                }
                sections[1] = stringOffsets.ToArray();
                sections[2] = stringBytes.ToArray();

                // Metadata sets, numbered in first use: nodes in record order, then edges in record order.
                var setRefs = new Dictionary<string[], uint>(SetComparer.Instance);
                var setList = new List<string[]>();
                uint SetRef(string[] set)
                {
                    if (set.Length == 0) return 0;
                    if (setRefs.TryGetValue(set, out var existing)) return existing;
                    setList.Add(set);
                    var created = (uint)setList.Count;
                    setRefs[set] = created;
                    return created;
                }

                var nodeRecords = new LittleEndianBuffer(nodes.Count * SnapshotFormat.NodeRecordSize);
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    nodeRecords.WriteU32((uint)sidOf[node.Id!]);
                    nodeRecords.WriteU32(Ref(node.Identifier));
                    nodeRecords.WriteU32(Ref(node.Name));
                    nodeRecords.WriteU32(Ref(node.Type));
                    nodeRecords.WriteU32(Ref(node.Language));
                    nodeRecords.WriteU32((uint)fileOfNode[i]);
                    nodeRecords.WriteI32(node.StartLine);
                    nodeRecords.WriteI32(node.EndLine);
                    nodeRecords.WriteI32(node.StartCol);
                    nodeRecords.WriteI32(node.EndCol);
                    nodeRecords.WriteU32(Ref(node.Namespace));
                    nodeRecords.WriteU32(Ref(node.Visibility));
                    nodeRecords.WriteU32(Ref(node.Signature));
                    nodeRecords.WriteU32(Ref(node.ReturnType));
                    nodeRecords.WriteU32(Ref(node.Parameters));
                    nodeRecords.WriteU32(Ref(node.Modifiers));
                    nodeRecords.WriteU32(Ref(node.Metrics));
                    nodeRecords.WriteU32(SetRef(nodeSets[i]));
                    nodeRecords.WriteU32(SnapshotFormat.IsExternal(node.Metadata) ? SnapshotFormat.NodeFlagExternal : 0);
                    nodeRecords.WriteU32(0);
                }
                sections[3] = nodeRecords.ToArray();

                var edgeRecords = new LittleEndianBuffer(edges.Count * SnapshotFormat.EdgeRecordSize);
                for (var j = 0; j < edges.Count; j++)
                {
                    var edge = edges[j];
                    var parts = edgeParts[j];
                    var setRef = SetRef(parts.Set);
                    if (setRef > uint.MaxValue >> 2)
                    {
                        reason = "the shard holds too many metadata sets";
                        return null;
                    }
                    edgeRecords.WriteU32((uint)sourceSid[j]);
                    edgeRecords.WriteU32(targetSid[j] < 0 ? SnapshotFormat.NoIndex : (uint)targetSid[j]);
                    edgeRecords.WriteU32(Ref(edge.Type));
                    edgeRecords.WriteU32(Ref(parts.A));
                    edgeRecords.WriteU32(parts.Form == SnapshotFormat.EdgeIdFormComposed ? Ref(parts.B) : 0);
                    edgeRecords.WriteI32(parts.Line);
                    edgeRecords.WriteI32(parts.Column);
                    edgeRecords.WriteU32((setRef << 2) | parts.Form);
                }
                sections[4] = edgeRecords.ToArray();

                var idTable = new LittleEndianBuffer(ids.Length * SnapshotFormat.IdEntrySize);
                for (var s = 0; s < ids.Length; s++)
                {
                    idTable.WriteU32(Ref(ids[s]));
                    idTable.WriteU32(nodeIndexOfSid[s]);
                }
                sections[5] = idTable.ToArray();

                sections[6] = HashTable(ids.Length, s => stringUtf8[Ref(ids[s]) - 1], s => (uint)s + 1);

                var identified = new List<int>();
                for (var i = 0; i < nodes.Count; i++)
                    if (!string.IsNullOrWhiteSpace(nodes[i].Identifier)) identified.Add(i);
                sections[7] = HashTable(identified.Count, k => stringUtf8[Ref(nodes[identified[k]].Identifier) - 1], k => (uint)identified[k] + 1);

                sections[8] = HashTable(edges.Count, j => SnapshotFormat.StrictUtf8.GetBytes(edges[j].Id!), j => (uint)j + 1);

                // Runs: by source over Edges (null sources would follow BySourceOffsets[S]); by target
                // over ByTargetEdges, null targets after ByTargetOffsets[S].
                var bySource = new uint[ids.Length + 1];
                foreach (var sid in sourceSid) bySource[sid + 1]++;
                for (var s = 0; s < ids.Length; s++) bySource[s + 1] += bySource[s];
                sections[9] = U32Array(bySource);

                var byTargetCounts = new uint[ids.Length + 1];
                foreach (var sid in targetSid)
                    if (sid >= 0) byTargetCounts[sid + 1]++;
                for (var s = 0; s < ids.Length; s++) byTargetCounts[s + 1] += byTargetCounts[s];
                sections[10] = U32Array(byTargetCounts);

                var byTargetEdges = new uint[edges.Count];
                var cursor = (uint[])byTargetCounts.Clone();
                var nullTargetCursor = byTargetCounts[ids.Length];
                foreach (var j in recordOfIdOrder)
                {
                    if (targetSid[j] < 0) byTargetEdges[nullTargetCursor++] = (uint)j;
                    else byTargetEdges[cursor[targetSid[j]]++] = (uint)j;
                }
                sections[11] = U32Array(byTargetEdges);

                var fileTable = new LittleEndianBuffer(files.Count * SnapshotFormat.FileEntrySize);
                var firstNode = 0u;
                foreach (var (path, bucket) in files)
                {
                    fileTable.WriteU32(Ref(path));
                    fileTable.WriteU32(string.IsNullOrWhiteSpace(path) ? 0 : Ref(FilePathMatcher.Normalize(path)));
                    fileTable.WriteU32(firstNode);
                    fileTable.WriteU32((uint)bucket.Count);
                    fileTable.WriteU32(0);
                    fileTable.WriteU32(0);
                    firstNode += (uint)bucket.Count;
                }
                sections[12] = fileTable.ToArray();

                var nameTable = new LittleEndianBuffer(names.Count * SnapshotFormat.NameEntrySize);
                var namePostings = new LittleEndianBuffer();
                var postingStart = 0u;
                for (var k = 0; k < names.Count; k++)
                {
                    var postings = postingsByName[names[k].Name];
                    nameTable.WriteU32(FoldedNameFirstRef + (uint)k);
                    nameTable.WriteU32(postingStart);
                    nameTable.WriteU32((uint)postings.Count);
                    foreach (var index in postings) namePostings.WriteU32(index);
                    postingStart += (uint)postings.Count;
                }
                sections[13] = nameTable.ToArray();
                sections[14] = namePostings.ToArray();

                var metadata = new LittleEndianBuffer();
                metadata.WriteU32((uint)setList.Count);
                var setOffset = 4L + 4L * (setList.Count + 1);
                foreach (var set in setList)
                {
                    metadata.WriteU32((uint)setOffset);
                    setOffset += 4 + 4L * set.Length;
                }
                metadata.WriteU32((uint)setOffset);
                foreach (var set in setList)
                {
                    metadata.WriteU32((uint)(set.Length / 2));
                    foreach (var value in set) metadata.WriteU32(Ref(value));
                }
                if (metadata.Length > uint.MaxValue)
                {
                    reason = "the shard's metadata sets exceed 4 GiB";
                    return null;
                }
                sections[15] = metadata.ToArray();

                var cross = new LittleEndianBuffer();
                for (var j = 0; j < edges.Count; j++)
                {
                    if (nodeIndexOfSid[sourceSid[j]] == SnapshotFormat.NoIndex
                        || (targetSid[j] >= 0 && nodeIndexOfSid[targetSid[j]] == SnapshotFormat.NoIndex))
                        cross.WriteU32((uint)j);
                }
                sections[16] = cross.ToArray();

                var histogram = new LittleEndianBuffer();
                WriteHistogram(histogram, nodes.Select(node => node.Type));
                WriteHistogram(histogram, edges.Select(edge => edge.Type));
                sections[17] = histogram.ToArray();

                sections[18] = info.Coverage ?? Array.Empty<byte>();
                sections[19] = info.Fingerprints ?? Array.Empty<byte>();

                // ---- Blob ----
                var blob = new LittleEndianBuffer();
                blob.WriteU32(SnapshotFormat.ShardMagic);
                blob.WriteU32(SnapshotFormat.SectionCount);
                blob.WriteU32((uint)nodes.Count);
                blob.WriteU32((uint)edges.Count);
                blob.WriteU32((uint)ids.Length);
                blob.WriteU32((uint)_strings.Count);
                blob.WriteU32((uint)files.Count);
                blob.WriteU32((uint)names.Count);
                blob.WriteU32(FoldedNameFirstRef);
                blob.WriteU32(0);
                blob.PadTo(SnapshotFormat.ShardHeaderSize);

                var tableOffset = blob.Length;
                blob.PadTo(tableOffset + SnapshotFormat.SectionCount * SnapshotFormat.SectionEntrySize);
                var lengths = new long[SnapshotFormat.SectionCount];
                for (var i = 0; i < SnapshotFormat.SectionCount; i++)
                {
                    blob.PadTo(SnapshotFormat.Align8(blob.Length));
                    var offset = blob.Length;
                    blob.WriteBytes(sections[i]);
                    lengths[i] = sections[i].Length;
                    var kind = (SnapshotSectionKind)(i + 1);
                    var entry = tableOffset + i * SnapshotFormat.SectionEntrySize;
                    blob.PatchU32(entry, (uint)kind);
                    blob.PatchU32(entry + 4, SnapshotFormat.ElementSize(kind));
                    blob.PatchU64(entry + 8, (ulong)offset);
                    blob.PatchU64(entry + 16, (ulong)sections[i].Length);
                }
                blob.PadTo(SnapshotFormat.Align8(blob.Length));

                return new ShardBlob
                {
                    Bytes = blob.ToArray(),
                    NodeCount = nodes.Count,
                    EdgeCount = edges.Count,
                    SectionLengths = lengths,
                };
            }

            private void Intern(string value)
            {
                if (_stringRefs.ContainsKey(value)) return;
                _strings.Add(value);
                _stringRefs[value] = (uint)_strings.Count;
            }

            private uint Ref(string? value) => value is null ? 0 : _stringRefs[value];

            /// <summary>
            /// The metadata pairs a set stores, flattened (key, value, ...) in ordinal key order: everything
            /// but the ownership keys (re-added from the shard key on load) and, for an edge, a canonical
            /// <c>line</c>/<c>column</c> held in the record.
            /// </summary>
            private static string[] MetadataPairs(IReadOnlyDictionary<string, string>? metadata, (bool Line, bool Column)? inRecord)
            {
                if (metadata is null) return Array.Empty<string>();
                var pairs = new List<KeyValuePair<string, string>>(metadata.Count);
                foreach (var pair in metadata)
                {
                    if (pair.Key == SnapshotFormat.ParserIdKey || pair.Key == SnapshotFormat.WorkspaceIdKey) continue;
                    if (inRecord is { } held
                        && ((held.Line && pair.Key == SnapshotFormat.LineKey) || (held.Column && pair.Key == SnapshotFormat.ColumnKey)))
                        continue;
                    pairs.Add(pair);
                }
                pairs.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
                var flat = new string[pairs.Count * 2];
                for (var i = 0; i < pairs.Count; i++)
                {
                    flat[2 * i] = pairs[i].Key;
                    flat[2 * i + 1] = pairs[i].Value;
                }
                return flat;
            }

            private void WriteHistogram(LittleEndianBuffer buffer, IEnumerable<string?> types)
            {
                var counts = new SortedDictionary<string, uint>(StringComparer.Ordinal);
                foreach (var type in types)
                    if (!string.IsNullOrEmpty(type)) counts[type] = counts.GetValueOrDefault(type) + 1;
                buffer.WriteU32((uint)counts.Count);
                foreach (var (type, n) in counts)
                {
                    buffer.WriteU32(Ref(type));
                    buffer.WriteU32(n);
                }
            }

            private static byte[] HashTable(int count, Func<int, byte[]> keyOf, Func<int, uint> valueOf)
            {
                var capacity = SnapshotFormat.HashCapacity(count);
                EnsureSectionFits(capacity * 4);
                var table = new uint[capacity];
                for (var i = 0; i < count; i++)
                {
                    var slot = SnapshotFormat.Slot(Fnv1a.Hash(keyOf(i)), capacity);
                    while (table[slot] != 0) slot = (slot + 1) & (capacity - 1);
                    table[slot] = valueOf(i);
                }
                return U32Array(table);
            }

            private static byte[] U32Array(uint[] values)
            {
                EnsureSectionFits(values.Length * 4L);
                var bytes = new byte[values.Length * 4L];
                for (var i = 0; i < values.Length; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)(i * 4L)), values[i]);
                return bytes;
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> (which skips the shard, "beyond the format's
        /// limits") for a section that cannot be one array: above <see cref="Array.MaxLength"/> bytes. Without
        /// it a u32 section above about 536M entries would fail with another exception outside the catch.
        /// </summary>
        internal static void EnsureSectionFits(long byteLength)
        {
            if (byteLength > Array.MaxLength)
                throw new InvalidOperationException($"A snapshot section of {byteLength} bytes exceeds the largest array.");
        }

        private sealed class SetComparer : IEqualityComparer<string[]>
        {
            public static readonly SetComparer Instance = new();

            public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

            public int GetHashCode(string[] obj)
            {
                var hash = new HashCode();
                foreach (var value in obj) hash.Add(value, StringComparer.Ordinal);
                return hash.ToHashCode();
            }
        }
    }

    /// <summary>A growable little-endian byte buffer for building snapshot sections.</summary>
    internal sealed class LittleEndianBuffer
    {
        private byte[] _bytes;
        private int _length;

        public LittleEndianBuffer(int capacity = 256) => _bytes = new byte[Math.Max(16, capacity)];

        public long Length => _length;

        public void WriteU16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);

        public void WriteU32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);

        public void WriteI32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

        public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

        public void PadTo(long length)
        {
            if (length > _length) Reserve((int)(length - _length)).Clear();
        }

        public void PatchU32(long offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan((int)offset, 4), value);

        public void PatchU64(long offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(_bytes.AsSpan((int)offset, 8), value);

        public byte[] ToArray() => _bytes.AsSpan(0, _length).ToArray();

        private Span<byte> Reserve(int count)
        {
            if ((long)_length + count > Array.MaxLength) throw new InvalidOperationException("A snapshot section exceeds the largest array.");
            if (_length + count > _bytes.Length)
            {
                var grown = new byte[(int)Math.Min(Array.MaxLength, Math.Max((long)_bytes.Length * 2, (long)_length + count))];
                _bytes.AsSpan(0, _length).CopyTo(grown);
                _bytes = grown;
            }
            var span = _bytes.AsSpan(_length, count);
            _length += count;
            return span;
        }
    }
}
