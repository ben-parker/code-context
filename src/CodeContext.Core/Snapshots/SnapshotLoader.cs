using CodeContext.Core.Repositories.InMemory;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>One shard materialized from a snapshot, with what the snapshot stored beside it.</summary>
    internal sealed record LoadedSnapshotShard(ShardKey Key, InMemoryDatabase.GraphState Shard, SnapshotShardInfo Info);

    /// <summary>
    /// The outcome of <see cref="SnapshotLoader.Load"/>: the header and the materialized shards, or the
    /// check that refused the file. <see cref="Dropped"/> lists shards left out by the caller's filter.
    /// </summary>
    internal sealed record SnapshotLoadResult(
        SnapshotRejection Rejection,
        string? Reason,
        SnapshotHeader? Header,
        IReadOnlyList<LoadedSnapshotShard> Shards,
        IReadOnlyList<ShardKey> Dropped)
    {
        public bool Loaded => Rejection == SnapshotRejection.None;

        /// <summary>See <see cref="SnapshotOpenResult.PassedHostMeta"/>; true for every refusal after the file opened.</summary>
        public bool PassedHostMeta { get; init; }

        /// <summary>The shards in the form <see cref="InMemoryDatabase.PublishLoadedShards"/> takes.</summary>
        public IEnumerable<KeyValuePair<ShardKey, IGraphShard>> ForPublish()
            => Shards.Select(shard => KeyValuePair.Create(shard.Key, (IGraphShard)shard.Shard));

        internal static SnapshotLoadResult Refused(SnapshotRejection rejection, string? reason)
            => new(rejection, reason, null, Array.Empty<LoadedSnapshotShard>(), Array.Empty<ShardKey>());
    }

    /// <summary>
    /// The materializing loader (plan D3, phases 2-7): opens and authenticates a snapshot, drops the
    /// shards the caller does not accept (no registered worker has the parser, or the workspace is not
    /// the host's), and copies every remaining shard into a heap <see cref="InMemoryDatabase.GraphState"/>
    /// equal to the one written. The mapping is released before this returns. A structural failure in
    /// any shard refuses the whole file; nothing is half-loaded. Refused as
    /// <see cref="SnapshotRejection.Structure"/>, besides every check of the reader: a node id, an edge id
    /// or a (non-blank) identifier in two records, of one shard or of two; and a shard that fails
    /// <see cref="SnapshotWriter.CheckPersistable(ShardKey, IGraphShard)"/>, the writer's own rule.
    /// </summary>
    internal static class SnapshotLoader
    {
        public static SnapshotLoadResult Load(
            string path, byte[] key, SnapshotExpectations expected, Func<ShardKey, bool>? acceptShard = null)
        {
            var opened = SnapshotFile.Open(path, key, expected);
            if (opened.File is not { } file)
                return SnapshotLoadResult.Refused(opened.Rejection, opened.Reason) with { PassedHostMeta = opened.PassedHostMeta };
            using (file) return Materialize(file, acceptShard);
        }

        /// <summary>Materializes the shards of an opened file; a structural failure refuses the whole file.</summary>
        internal static SnapshotLoadResult Materialize(SnapshotFile file, Func<ShardKey, bool>? acceptShard = null)
        {
            try
            {
                return MaterializeCore(file, acceptShard);
            }
            catch (SnapshotCorruptException exception)
            {
                return SnapshotLoadResult.Refused(SnapshotRejection.Structure, exception.Message) with { PassedHostMeta = true };
            }
        }

        private static SnapshotLoadResult MaterializeCore(SnapshotFile file, Func<ShardKey, bool>? acceptShard)
        {
            var shards = new List<LoadedSnapshotShard>();
            var dropped = new List<ShardKey>();
            // The store's global invariants, which PublishLoadedShards leaves to the loader: an id, an
            // edge id and an identifier live in at most one shard.
            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var identifiers = new HashSet<string>(StringComparer.Ordinal);

            foreach (var reader in file.Shards)
            {
                if (acceptShard is not null && !acceptShard(reader.Key))
                {
                    dropped.Add(reader.Key);
                    continue;
                }

                var state = new InMemoryDatabase.GraphState(reader.NodeCount, reader.EdgeCount);
                // Nodes through the id table, which checks it: every node reached once, by its own sid, in
                // strictly increasing id order (what a mapped shard's in-place reads rely on).
                var nodes = reader.GetAllNodes();
                var edges = new CodeEdge[reader.EdgeCount];
                foreach (var node in nodes)
                {
                    if (!nodeIds.Add(node.Id!) || !state.Nodes.TryAdd(node.Id!, node))
                        throw new SnapshotCorruptException($"Node id '{node.Id}' appears twice.");
                    if (string.IsNullOrWhiteSpace(node.Identifier)) continue;
                    if (!identifiers.Add(node.Identifier) || !state.Identifiers.TryAdd(node.Identifier, node.Id!))
                        throw new SnapshotCorruptException($"Identifier '{node.Identifier}' appears twice.");
                }
                for (var j = 0; j < edges.Length; j++)
                {
                    var edge = edges[j] = reader.ReadEdge(j);
                    if (!edgeIds.Add(edge.Id!) || !state.Edges.TryAdd(edge.Id!, edge))
                        throw new SnapshotCorruptException($"Edge id '{edge.Id}' appears twice.");
                }

                // The writer's own persistability rule, over the materialized facts: re-signed damage that
                // passes every structural check (a null source, an id without the shard's prefix, a null
                // identifier) still never becomes a published fact.
                var reason = SnapshotWriter.CheckPersistable(
                    reader.Key, nodes.Count, edges.Length, nodes, edges, state.TryGetNodeIdByIdentifier);
                if (reason is not null)
                    throw new SnapshotCorruptException($"Shard {reader.Key} holds a fact the writer never writes: {reason}.");
                shards.Add(new LoadedSnapshotShard(reader.Key, state, reader.ReadInfo()));
            }
            return new SnapshotLoadResult(SnapshotRejection.None, null, file.Header, shards, dropped);
        }
    }
}
