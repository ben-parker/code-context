using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;

namespace CodeContext.Core.Repositories.InMemory
{
    /// <summary>
    /// The in-memory graph store, sharded by (parserId, workspaceId). Readers always see a
    /// complete committed snapshot: a commit clones the small shard map, rebuilds only the
    /// touched shard(s), and swaps the whole map through ONE volatile reference, so API reads
    /// during a rescan observe the previous generation rather than a cleared or half-built
    /// graph. A scoped commit therefore costs O(touched shard) instead of O(whole graph), and
    /// untouched shards keep their cached adjacency across other shards' commits. Incremental
    /// per-node mutations (the single-file parser / repository path) still write to the live
    /// dictionaries of the fact's shard; those are serialized by the index coordinator.
    ///
    /// Shards are reached only through <see cref="IGraphShard"/>: reads through its read members,
    /// commits through its commit members, so a shard kind other than the heap shard can be
    /// published without changing what any read or commit means. Every multi-valued read is in
    /// canonical order: ordinal by id inside a shard, shards concatenated in <see cref="ShardKey"/>
    /// order.
    /// </summary>
    public class InMemoryDatabase
    {
        /// <summary>
        /// Routing key for the store's shards. Ownership is stamped onto each fact's metadata
        /// ("parserId"/"workspaceId") by <c>AnalysisDeltaApplier</c>; un-owned facts land in
        /// <see cref="Default"/>. Default record-struct equality over the two strings is ordinal.
        /// </summary>
        internal readonly record struct ShardKey(string ParserId, string WorkspaceId)
        {
            public static readonly ShardKey Default = new(string.Empty, string.Empty);

            public static readonly IComparer<ShardKey> Comparer = new OrdinalComparer();

            private sealed class OrdinalComparer : IComparer<ShardKey>
            {
                public int Compare(ShardKey x, ShardKey y)
                {
                    var p = string.CompareOrdinal(x.ParserId, y.ParserId);
                    return p != 0 ? p : string.CompareOrdinal(x.WorkspaceId, y.WorkspaceId);
                }
            }

            // The single place fact ownership is derived — every routing site funnels through here.
            public static ShardKey ForNode(CodeNode node) => FromMetadata(node.Metadata);

            public static ShardKey ForEdge(CodeEdge edge) => FromMetadata(edge.Metadata);

            /// <summary>
            /// The one normalization used by BOTH the metadata routing path and the commit-scope path:
            /// a non-empty (parser, workspace) pair owns a shard; anything else routes to
            /// <see cref="Default"/>. Empty ids can never split routing between the two paths.
            /// </summary>
            public static ShardKey From(string? parserId, string? workspaceId)
                => !string.IsNullOrEmpty(parserId) && !string.IsNullOrEmpty(workspaceId)
                    ? new ShardKey(parserId, workspaceId)
                    : Default;

            private static ShardKey FromMetadata(IReadOnlyDictionary<string, string>? metadata)
            {
                if (metadata is not null
                    && metadata.TryGetValue("parserId", out var parserId)
                    && metadata.TryGetValue("workspaceId", out var workspaceId))
                {
                    return From(parserId, workspaceId);
                }
                return Default;
            }
        }

        /// <summary>
        /// The heap shard: one shard's node/edge/identifier tables. A committed shard is immutable
        /// and is replaced wholesale (new reference) by a commit; in-place mutations bump
        /// <see cref="Version"/> so the per-shard adjacency, statistics and cross-shard candidate
        /// caches invalidate without a reference change.
        /// </summary>
        internal sealed class GraphState : IGraphShard
        {
            public ConcurrentDictionary<string, CodeNode> Nodes { get; }
            public ConcurrentDictionary<string, string> Identifiers { get; }
            public ConcurrentDictionary<string, CodeEdge> Edges { get; }

            private long _version;
            private volatile CachedAdjacency? _adjacency;
            private volatile HashSet<string>? _crossShardEdgeIds;
            private volatile CachedHistogram? _histogram;

            private sealed record CachedAdjacency(long Version, GraphAdjacency Value);

            private sealed record CachedHistogram(
                long Version, Dictionary<string, int> NodesByType, Dictionary<string, int> EdgesByType);

            public GraphState()
            {
                Nodes = new ConcurrentDictionary<string, CodeNode>();
                Identifiers = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
                Edges = new ConcurrentDictionary<string, CodeEdge>();
            }

            // Rebuild ctor: a fresh shard is populated by a single writer, so one concurrency
            // stripe is enough, and presizing avoids repeated grow/rehash during population.
            public GraphState(int nodeCapacity, int edgeCapacity)
            {
                Nodes = new ConcurrentDictionary<string, CodeNode>(
                    concurrencyLevel: 1, capacity: Math.Max(0, nodeCapacity));
                Identifiers = new ConcurrentDictionary<string, string>(
                    concurrencyLevel: 1, capacity: Math.Max(0, nodeCapacity), StringComparer.Ordinal);
                Edges = new ConcurrentDictionary<string, CodeEdge>(
                    concurrencyLevel: 1, capacity: Math.Max(0, edgeCapacity));
            }

            /// <summary>A heap copy of any shard, for the legacy in-place mutation paths.</summary>
            public static GraphState CopyOf(IGraphShard shard)
            {
                var nodes = shard.GetAllNodes();
                var edges = shard.GetAllEdges();
                var copy = new GraphState(nodes.Count, edges.Count);
                foreach (var node in nodes)
                {
                    copy.Nodes[node.Id!] = node;
                    if (!string.IsNullOrWhiteSpace(node.Identifier)) copy.Identifiers[node.Identifier] = node.Id!;
                }
                foreach (var edge in edges) copy.Edges[edge.Id!] = edge;
                return copy;
            }

            public long Version => Interlocked.Read(ref _version);

            /// <summary>Invalidates the per-shard caches after an in-place write.</summary>
            public void BumpVersion()
            {
                Interlocked.Increment(ref _version);
                _crossShardEdgeIds = null;
            }

            // ---- Read members ----

            public int NodeCount => Nodes.Count;

            public int EdgeCount => Edges.Count;

            public bool TryGetNode(string id, out CodeNode? node)
            {
                var found = Nodes.TryGetValue(id, out var value);
                node = value;
                return found;
            }

            public bool TryGetNodeIdByIdentifier(string identifier, out string? id)
            {
                var found = Identifiers.TryGetValue(identifier, out var value);
                id = value;
                return found;
            }

            public bool TryGetEdge(string id, out CodeEdge? edge)
            {
                var found = Edges.TryGetValue(id, out var value);
                edge = value;
                return found;
            }

            public IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact)
                => GetOrBuildAdjacency().FindNodesByName(name, exact);

            public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath)
                => GetOrBuildAdjacency().FindNodesByPath(requestedPath);

            public IReadOnlyList<CodeEdge> GetEdgesBySource(string id)
                => GetOrBuildAdjacency().GetEdgesBySource(id);

            public IReadOnlyList<CodeEdge> GetEdgesByTarget(string id)
                => GetOrBuildAdjacency().GetEdgesByTarget(id);

            public IReadOnlyList<CodeNode> GetAllNodes() => GetOrBuildAdjacency().Nodes;

            public IReadOnlyList<CodeEdge> GetAllEdges() => GetOrBuildAdjacency().Edges;

            public IReadOnlyList<string> GetFilePaths() => GetOrBuildAdjacency().FilePaths;

            // A pass over the dictionaries, not the adjacency: status polling must not force an
            // adjacency build (and its sort) after every commit of a scan.
            public void AddStatistics(Dictionary<string, int> nodesByType, Dictionary<string, int> edgesByType)
            {
                var version = Version;
                var cached = _histogram;
                if (cached is null || cached.Version != version)
                {
                    var nodeTypes = new Dictionary<string, int>(StringComparer.Ordinal);
                    var edgeTypes = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var node in Nodes.Values)
                        if (!string.IsNullOrEmpty(node.Type))
                            nodeTypes[node.Type] = nodeTypes.GetValueOrDefault(node.Type) + 1;
                    foreach (var edge in Edges.Values)
                        if (!string.IsNullOrEmpty(edge.Type))
                            edgeTypes[edge.Type] = edgeTypes.GetValueOrDefault(edge.Type) + 1;
                    _histogram = cached = new CachedHistogram(version, nodeTypes, edgeTypes);
                }
                foreach (var (type, count) in cached.NodesByType)
                    nodesByType[type] = nodesByType.GetValueOrDefault(type) + count;
                foreach (var (type, count) in cached.EdgesByType)
                    edgesByType[type] = edgesByType.GetValueOrDefault(type) + count;
            }

            /// <summary>
            /// Ids of edges with at least one endpoint that is NOT a node of this shard — the only
            /// edges a removal in ANOTHER shard could dangle. A cheap, id-convention-independent
            /// superset of the cross-shard sweep candidates (built once per shard version).
            /// </summary>
            public IReadOnlyCollection<string> GetCrossShardEdgeIds()
            {
                var cached = _crossShardEdgeIds;
                if (cached is not null) return cached;
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edge in Edges.Values)
                {
                    if (edge.Id is null) continue;
                    var sourceForeign = edge.SourceId is not null && !Nodes.ContainsKey(edge.SourceId);
                    var targetForeign = edge.TargetId is not null && !Nodes.ContainsKey(edge.TargetId);
                    if (sourceForeign || targetForeign) set.Add(edge.Id);
                }
                _crossShardEdgeIds = set;
                return set;
            }

            /// <summary>Version-stamped adjacency snapshot of this shard, rebuilt only when its version changes.</summary>
            public GraphAdjacency GetOrBuildAdjacency()
            {
                var version = Version;
                var cached = _adjacency;
                if (cached is { } c && c.Version == version) return c.Value;

                var adjacency = GraphAdjacency.Build(Nodes.Values, Edges.Values);
                _adjacency = new CachedAdjacency(version, adjacency);
                return adjacency;
            }

            // ---- Commit members ----

            public IGraphShard? ApplyScoped(
                IReadOnlyList<CodeNode> nodes,
                IReadOnlyList<CodeEdge> edges,
                IReadOnlySet<string>? replaceFiles,
                IReadOnlyList<IGraphShard> foreignShards,
                out HashSet<string> removedIds,
                out HashSet<string> removedNotReestablished)
            {
                removedIds = new HashSet<string>(StringComparer.Ordinal);
                removedNotReestablished = new HashSet<string>(StringComparer.Ordinal);
                // One snapshot of the values for both passes (each Values read copies the table).
                var carried = Nodes.Values;
                foreach (var node in carried)
                {
                    if (node.Id is null) continue;
                    if (replaceFiles is null
                        || (!string.IsNullOrEmpty(node.FilePath) && replaceFiles.Contains(node.FilePath)))
                    {
                        removedIds.Add(node.Id);
                    }
                }

                // Nothing removed and nothing supplied: the shard is unchanged, so the caller keeps
                // this reference (and its caches) and only the generation advances.
                if (removedIds.Count == 0 && !HasId(nodes, static node => node.Id) && !HasId(edges, static edge => edge.Id))
                    return null;

                var next = new GraphState(Nodes.Count + nodes.Count, Edges.Count + edges.Count);

                foreach (var node in carried)
                {
                    if (node.Id is null || removedIds.Contains(node.Id)) continue;
                    // Carried facts were already globally unique — no foreign probe needed.
                    next.Nodes.TryAdd(node.Id, node);
                    RegisterIdentifier(next, node, foreignProbe: null);
                }

                foreach (var node in nodes)
                {
                    if (node.Id is null) continue;
                    // A delta node replacing a carried node (or an earlier copy in this delta) takes
                    // over its id; the replaced copy's identifier no longer names anything, so the
                    // map stays exactly { node.Identifier -> node.Id } over the final nodes.
                    if (next.Nodes.TryGetValue(node.Id, out var replaced)) ReleaseIdentifier(next, replaced);
                    RegisterIdentifier(next, node, foreignShards);
                    next.Nodes[node.Id] = node;
                }

                // Carry edges within the shard, mirroring the pre-shard rule exactly: an edge from a
                // surviving node into a replaced node is kept only when the delta re-established that
                // target — otherwise it would dangle.
                foreach (var edge in Edges.Values)
                {
                    if (edge.Id is null) continue;
                    if (edge.SourceId is not null && removedIds.Contains(edge.SourceId)) continue;
                    if (edge.TargetId is not null
                        && removedIds.Contains(edge.TargetId)
                        && !next.Nodes.ContainsKey(edge.TargetId)) continue;
                    next.Edges.TryAdd(edge.Id, edge);
                }

                foreach (var edge in edges)
                {
                    if (edge.Id is null) continue;
                    next.Edges[edge.Id] = edge;
                }

                foreach (var id in removedIds)
                    if (!next.Nodes.ContainsKey(id)) removedNotReestablished.Add(id);

                return next;
            }

            /// <summary>
            /// Returns a rebuilt copy of this foreign shard with dangling edges and delta-id collisions
            /// removed, or null when nothing changed (so the caller keeps the original reference and its
            /// cached adjacency). Dangling-edge drop mirrors the pre-shard global semantics (drop iff
            /// source was removed, or target was removed and not re-established anywhere); a co-resident
            /// delta id is dropped so the delta's copy is the single global survivor.
            /// </summary>
            public IGraphShard? WithoutForeignConflicts(
                IReadOnlySet<string> removedIds,
                IReadOnlySet<string> removedNotReestablished,
                IReadOnlySet<string> deltaNodeIds,
                IReadOnlySet<string> deltaEdgeIds)
            {
                HashSet<string>? dropEdges = null;

                if (removedIds.Count > 0)
                {
                    foreach (var edgeId in GetCrossShardEdgeIds())
                    {
                        if (!Edges.TryGetValue(edgeId, out var edge) || edge.Id is null) continue;
                        var dangles = (edge.SourceId is not null && removedIds.Contains(edge.SourceId))
                            || (edge.TargetId is not null && removedNotReestablished.Contains(edge.TargetId));
                        if (dangles) (dropEdges ??= new HashSet<string>(StringComparer.Ordinal)).Add(edge.Id);
                    }
                }

                if (deltaEdgeIds.Count > 0)
                {
                    foreach (var id in deltaEdgeIds)
                        if (Edges.ContainsKey(id)) (dropEdges ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
                }

                HashSet<string>? dropNodes = null;
                if (deltaNodeIds.Count > 0)
                {
                    foreach (var id in deltaNodeIds)
                        if (Nodes.ContainsKey(id)) (dropNodes ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
                }

                // When a delta reclaims an id, the evicted foreign node's OWN edges die with it — including
                // fully-local ones (both endpoints in-shard) that never appear in the cross-shard candidate
                // set. This deliberately DIVERGES from the old single-map behaviour (which left the edge in
                // place, silently re-pointing it at the unrelated new node with the reclaimed id — a wrong
                // association); dropping matches the store's own removed-node edge rule.
                if (dropNodes is not null)
                {
                    foreach (var edge in Edges.Values)
                    {
                        if (edge.Id is null) continue;
                        if ((edge.SourceId is not null && dropNodes.Contains(edge.SourceId))
                            || (edge.TargetId is not null && dropNodes.Contains(edge.TargetId)))
                        {
                            (dropEdges ??= new HashSet<string>(StringComparer.Ordinal)).Add(edge.Id);
                        }
                    }
                }

                if (dropEdges is null && dropNodes is null) return null;

                var next = new GraphState(
                    Nodes.Count - (dropNodes?.Count ?? 0),
                    Math.Max(0, Edges.Count - (dropEdges?.Count ?? 0)));
                foreach (var pair in Nodes)
                    if (dropNodes is null || !dropNodes.Contains(pair.Key)) next.Nodes.TryAdd(pair.Key, pair.Value);
                foreach (var pair in Identifiers)
                    if (dropNodes is null || !dropNodes.Contains(pair.Value)) next.Identifiers.TryAdd(pair.Key, pair.Value);
                foreach (var pair in Edges)
                    if (dropEdges is null || !dropEdges.Contains(pair.Key)) next.Edges.TryAdd(pair.Key, pair.Value);
                return next;
            }

            public void CollectNodesOutside(IReadOnlySet<string> keepPaths, HashSet<string> into)
            {
                foreach (var node in Nodes.Values)
                {
                    if (node.Id is null) continue;

                    // A library symbol's virtual file is never on disk. It leaves through
                    // the worker's own delta, once nothing uses the symbol.
                    if (node.Metadata?.GetValueOrDefault("external") == "true") continue;
                    if (!string.IsNullOrEmpty(node.FilePath) && !keepPaths.Contains(node.FilePath))
                        into.Add(node.Id);
                }
            }

            /// <summary>
            /// Returns a rebuilt copy of this shard with removed nodes and edges touching any globally-removed
            /// node dropped, or null when the shard is unaffected (keep the original reference).
            /// </summary>
            public IGraphShard? WithoutNodes(IReadOnlySet<string> removedIds)
            {
                var hasNodeRemoval = false;
                foreach (var node in Nodes.Values)
                    if (node.Id is not null && removedIds.Contains(node.Id)) { hasNodeRemoval = true; break; }

                var hasEdgeRemoval = false;
                if (!hasNodeRemoval)
                {
                    // No local node removals: only cross-shard edges can be affected.
                    foreach (var edgeId in GetCrossShardEdgeIds())
                    {
                        if (!Edges.TryGetValue(edgeId, out var edge)) continue;
                        if ((edge.SourceId is not null && removedIds.Contains(edge.SourceId))
                            || (edge.TargetId is not null && removedIds.Contains(edge.TargetId)))
                        {
                            hasEdgeRemoval = true;
                            break;
                        }
                    }
                    if (!hasEdgeRemoval) return null;
                }

                var next = new GraphState(Nodes.Count, Edges.Count);
                foreach (var node in Nodes.Values)
                {
                    if (node.Id is null) continue;
                    if (removedIds.Contains(node.Id)) continue;
                    next.Nodes.TryAdd(node.Id, node);
                    AddIdentifier(next, node, globalIdentifiers: null);
                }
                foreach (var edge in Edges.Values)
                {
                    if (edge.Id is null) continue;
                    if (edge.SourceId is not null && removedIds.Contains(edge.SourceId)) continue;
                    if (edge.TargetId is not null && removedIds.Contains(edge.TargetId)) continue;
                    next.Edges.TryAdd(edge.Id, edge);
                }
                return next;
            }

            private static bool HasId<T>(IReadOnlyList<T> items, Func<T, string?> idOf)
            {
                foreach (var item in items)
                    if (idOf(item) is not null) return true;
                return false;
            }
        }

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static ImmutableSortedDictionary<ShardKey, IGraphShard> EmptyShards { get; } =
            ImmutableSortedDictionary.Create<ShardKey, IGraphShard>(ShardKey.Comparer);

        private volatile ImmutableSortedDictionary<ShardKey, IGraphShard> _shards = EmptyShards;
        private readonly object _commitLock = new();
        private long _version;
        private long _lastCommittedGeneration;
        private ImmutableSortedDictionary<ShardKey, IGraphShard>? _reconciliationState;
        private long _reconciliationGeneration;

        // Each shard's annotation (parser version, coverage), changed only under _commitLock by the
        // commit that carries it, staged and committed or rolled back with a reconciliation. Read only
        // under the lock, so a latch sees the shards and their annotations from one commit.
        private ImmutableSortedDictionary<ShardKey, ShardAnnotation> _annotations = EmptyAnnotations;
        private ImmutableSortedDictionary<ShardKey, ShardAnnotation>? _reconciliationAnnotations;

        private static ImmutableSortedDictionary<ShardKey, ShardAnnotation> EmptyAnnotations { get; } =
            ImmutableSortedDictionary.Create<ShardKey, ShardAnnotation>(ShardKey.Comparer);

        // Each cache publishes {version, payload} as ONE immutable holder swapped through a single
        // volatile reference, so a reader's single reference read observes the version and its payload
        // atomically. A reader can never latch an old payload and then match a version another thread
        // stamped for a newer payload. The only remaining (benign) race is a duplicate rebuild.
        private sealed record CachedStatistics(long Version, GraphStatistics Value);
        private sealed record CachedAdjacency(long Version, CompositeAdjacency Value);

        private volatile CachedStatistics? _cachedStatistics;
        private volatile CachedAdjacency? _cachedAdjacency;

        public CodeGraph? CurrentGraph { get; set; }

        // ---- Routing surface (replaces the former public Nodes/Edges/Identifiers dictionaries) ----

        /// <summary>Total node count across all shards (latched once).</summary>
        public int NodeCount
        {
            get
            {
                var shards = _shards;
                var count = 0;
                foreach (var shard in shards.Values) count += shard.NodeCount;
                return count;
            }
        }

        /// <summary>Total edge count across all shards (latched once).</summary>
        public int EdgeCount
        {
            get
            {
                var shards = _shards;
                var count = 0;
                foreach (var shard in shards.Values) count += shard.EdgeCount;
                return count;
            }
        }

        /// <summary>Probes shards in ordinal <see cref="ShardKey"/> order (ids are globally unique).</summary>
        public bool TryGetNode(string id, out CodeNode? node)
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
            {
                if (shard.TryGetNode(id, out node)) return true;
            }
            node = null;
            return false;
        }

        public bool ContainsNode(string id) => TryGetNode(id, out _);

        public CodeNode? GetNode(string id) => TryGetNode(id, out var node) ? node : null;

        public bool TryGetNodeIdByIdentifier(string identifier, out string? id)
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
            {
                if (shard.TryGetNodeIdByIdentifier(identifier, out id)) return true;
            }
            id = null;
            return false;
        }

        public bool TryGetEdge(string id, out CodeEdge? edge)
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
            {
                if (shard.TryGetEdge(id, out edge)) return true;
            }
            edge = null;
            return false;
        }

        public bool ContainsEdge(string id) => TryGetEdge(id, out _);

        public CodeEdge? GetEdge(string id) => TryGetEdge(id, out var edge) ? edge : null;

        /// <summary>Every node in canonical order: shards in <see cref="ShardKey"/> order, id order inside (latched once).</summary>
        public IEnumerable<CodeNode> EnumerateNodes()
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
                foreach (var node in shard.GetAllNodes())
                    yield return node;
        }

        /// <summary>Every edge in canonical order: shards in <see cref="ShardKey"/> order, id order inside (latched once).</summary>
        public IEnumerable<CodeEdge> EnumerateEdges()
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
                foreach (var edge in shard.GetAllEdges())
                    yield return edge;
        }

        /// <summary>
        /// In-place upsert routed to the fact's metadata-derived shard (created if absent). Encapsulates
        /// identifier derivation, cross-shard duplicate enforcement, stale-identifier cleanup, and the
        /// mutation notification the repository formerly performed inline.
        /// </summary>
        public void UpsertNode(CodeNode node)
        {
            if (node.Id is null) throw new ArgumentException("Node must have an Id", nameof(node));
            lock (_commitLock)
            {
                if (string.IsNullOrEmpty(node.Identifier))
                    node.Identifier = DeriveLegacyIdentifier(node) ?? string.Empty;

                var shard = EnsureHeapShard(ShardKey.ForNode(node));
                shard.Nodes.TryGetValue(node.Id, out var existing);
                RegisterIdentifierAcrossShards(_shards, shard, node);
                shard.Nodes[node.Id] = node;

                if (existing?.Identifier is { Length: > 0 } previousIdentifier
                    && previousIdentifier != node.Identifier
                    && shard.Identifiers.TryGetValue(previousIdentifier, out var previousId)
                    && previousId == node.Id)
                {
                    shard.Identifiers.TryRemove(previousIdentifier, out _);
                }

                // Global last-write-wins: if this id previously lived under different ownership, drop the
                // stale copy so at most one node with this id survives (as the old single dictionary did).
                PurgeNodeFromOtherShards(shard, node.Id);

                shard.BumpVersion();
                NotifyMutation();
            }
        }

        /// <summary>Removes a node (from every shard it appears in) plus its identifiers; returns whether one was found.</summary>
        public bool RemoveNode(string id)
        {
            lock (_commitLock)
            {
                var removedAny = false;
                foreach (var (key, candidate) in _shards)
                {
                    if (!candidate.TryGetNode(id, out _)) continue;
                    var shard = AsHeapShard(key, candidate);
                    if (shard.Nodes.TryRemove(id, out _))
                    {
                        foreach (var entry in shard.Identifiers.Where(entry => entry.Value == id).ToList())
                            shard.Identifiers.TryRemove(entry.Key, out _);
                        shard.BumpVersion();
                        removedAny = true;
                    }
                }
                if (removedAny) NotifyMutation();
                return removedAny;
            }
        }

        /// <summary>In-place edge upsert routed to the edge's metadata-derived shard (created if absent).</summary>
        public void UpsertEdge(CodeEdge edge)
        {
            if (edge.Id is null) throw new ArgumentException("Edge must have an Id", nameof(edge));
            lock (_commitLock)
            {
                var shard = EnsureHeapShard(ShardKey.ForEdge(edge));
                shard.Edges[edge.Id] = edge;
                // Global last-write-wins for the edge id across shards.
                PurgeEdgeFromOtherShards(shard, edge.Id);
                shard.BumpVersion();
                NotifyMutation();
            }
        }

        private void PurgeNodeFromOtherShards(GraphState keep, string id)
        {
            foreach (var (key, candidate) in _shards)
            {
                if (ReferenceEquals(candidate, keep) || !candidate.TryGetNode(id, out _)) continue;
                var shard = AsHeapShard(key, candidate);
                if (shard.Nodes.TryRemove(id, out _))
                {
                    foreach (var entry in shard.Identifiers.Where(entry => entry.Value == id).ToList())
                        shard.Identifiers.TryRemove(entry.Key, out _);
                    shard.BumpVersion();
                }
            }
        }

        private void PurgeEdgeFromOtherShards(GraphState keep, string id)
        {
            foreach (var (key, candidate) in _shards)
            {
                if (ReferenceEquals(candidate, keep) || !candidate.TryGetEdge(id, out _)) continue;
                var shard = AsHeapShard(key, candidate);
                if (shard.Edges.TryRemove(id, out _)) shard.BumpVersion();
            }
        }

        /// <summary>Removes every edge (across all shards) whose source or target is <paramref name="nodeId"/>.</summary>
        public int RemoveEdgesTouchingNode(string nodeId)
        {
            lock (_commitLock)
            {
                var removed = 0;
                foreach (var (key, candidate) in _shards)
                {
                    // The adjacency lookups cannot answer for a null id (a heap shard matches the
                    // edges with a null endpoint), so a null id always materializes the shard.
                    if (candidate is not GraphState
                        && nodeId is not null
                        && candidate.GetEdgesBySource(nodeId).Count == 0
                        && candidate.GetEdgesByTarget(nodeId).Count == 0)
                    {
                        continue;
                    }
                    var shard = AsHeapShard(key, candidate);
                    var toRemove = shard.Edges.Values
                        .Where(e => e.SourceId == nodeId || e.TargetId == nodeId)
                        .Select(e => e.Id)
                        .Where(id => id != null)
                        .ToList();
                    foreach (var edgeId in toRemove)
                        if (shard.Edges.TryRemove(edgeId!, out _)) removed++;
                    if (toRemove.Count > 0) shard.BumpVersion();
                }
                if (removed > 0) NotifyMutation();
                return removed;
            }
        }

        /// <summary>
        /// Captures nodes and edges from one committed shard map, in canonical order. Reading counts
        /// or enumerations separately can straddle an atomic generation swap and produce a graph whose
        /// nodes and edges came from different versions; latching the map once avoids that.
        /// </summary>
        public CodeGraph CaptureGraph()
        {
            var shards = _shards;
            var nodes = new List<CodeNode>();
            var edges = new List<CodeEdge>();
            foreach (var shard in shards.Values)
            {
                nodes.AddRange(shard.GetAllNodes());
                edges.AddRange(shard.GetAllEdges());
            }
            return new CodeGraph { Nodes = nodes, Edges = edges };
        }

        public long LastCommittedGeneration
        {
            get
            {
                lock (_commitLock)
                    return _reconciliationState is null
                        ? Interlocked.Read(ref _lastCommittedGeneration)
                        : _reconciliationGeneration;
            }
        }

        public void BeginReconciliation()
        {
            lock (_commitLock)
            {
                if (_reconciliationState is not null)
                    throw new InvalidOperationException("A graph reconciliation is already active.");
                _reconciliationState = _shards;
                _reconciliationAnnotations = _annotations;
                _reconciliationGeneration = _lastCommittedGeneration;
            }
        }

        public void CommitReconciliation()
        {
            lock (_commitLock)
            {
                if (_reconciliationState is null)
                    throw new InvalidOperationException("No graph reconciliation is active.");
                _shards = _reconciliationState;
                _annotations = _reconciliationAnnotations ?? _annotations;
                _reconciliationState = null;
                _reconciliationAnnotations = null;
                Interlocked.Exchange(ref _lastCommittedGeneration, _reconciliationGeneration);
                NotifyMutation();
            }
        }

        public void RollbackReconciliation()
        {
            lock (_commitLock)
            {
                _reconciliationState = null;
                _reconciliationAnnotations = null;
                _reconciliationGeneration = _lastCommittedGeneration;
            }
        }

        /// <summary>Invalidates cached statistics/adjacency; called after every committed mutation.</summary>
        public void NotifyMutation() => Interlocked.Increment(ref _version);

        public bool TryCommitGeneration(
            long generation,
            IEnumerable<CodeNode> nodes,
            IEnumerable<CodeEdge> edges,
            CommitScope? scope)
        {
            lock (_commitLock)
            {
                var lastGeneration = _reconciliationState is null
                    ? Interlocked.Read(ref _lastCommittedGeneration)
                    : _reconciliationGeneration;
                if (generation <= lastGeneration) return false;

                var current = _reconciliationState ?? _shards;
                var next = scope is null
                    ? PartitionIntoState(nodes, edges)
                    : BuildScopedState(
                        current,
                        nodes as IReadOnlyList<CodeNode> ?? nodes.ToList(),
                        edges as IReadOnlyList<CodeEdge> ?? edges.ToList(),
                        scope);

                // An unscoped commit replaces every shard, so every annotation goes with them; a scoped
                // one changes only its own shard's, and only when it carries one. Foreign shards it
                // sweeps keep theirs.
                var annotations = _reconciliationState is not null ? _reconciliationAnnotations ?? _annotations : _annotations;
                var nextAnnotations = scope is null ? EmptyAnnotations : Annotate(annotations, scope);

                if (_reconciliationState is not null)
                {
                    _reconciliationState = next;
                    _reconciliationAnnotations = nextAnnotations;
                    _reconciliationGeneration = generation;
                }
                else
                {
                    _annotations = nextAnnotations;
                    // An unchanged commit returns the same map: the generation advances, every
                    // shard and cache stays as it is.
                    if (!ReferenceEquals(next, _shards))
                    {
                        _shards = next;
                        NotifyMutation();
                    }
                    Interlocked.Exchange(ref _lastCommittedGeneration, generation);
                }
                return true;
            }
        }

        /// <summary>
        /// <paramref name="current"/> with the annotation <paramref name="scope"/> carries for its shard. A
        /// null coverage keeps the recorded one. An annotation equal to the recorded one (same parser
        /// version, same coverage list) keeps the recorded instance, so an unchanged commit leaves the
        /// snapshot writer's view clean.
        /// </summary>
        private static ImmutableSortedDictionary<ShardKey, ShardAnnotation> Annotate(
            ImmutableSortedDictionary<ShardKey, ShardAnnotation> current, CommitScope scope)
        {
            if (scope.Annotation is not { } supplied) return current;
            var key = ShardKey.From(scope.ParserId, scope.WorkspaceId);
            current.TryGetValue(key, out var existing);
            var coverage = supplied.Coverage ?? existing?.Coverage;
            if (existing is not null
                && string.Equals(existing.ParserVersion, supplied.ParserVersion, StringComparison.Ordinal)
                && ReferenceEquals(existing.Coverage, coverage))
            {
                return current;
            }
            return current.SetItem(key, ReferenceEquals(coverage, supplied.Coverage) ? supplied : supplied with { Coverage = coverage });
        }

        /// <summary>Unconditional full replacement (legacy SaveGraph path — no generation check).</summary>
        public void ReplaceAll(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
        {
            lock (_commitLock)
            {
                _shards = PartitionIntoState(nodes, edges);
                _annotations = EmptyAnnotations;
                NotifyMutation();
            }
        }

        public void Reset()
        {
            lock (_commitLock)
            {
                _shards = EmptyShards;
                _annotations = EmptyAnnotations;
                CurrentGraph = null;
                NotifyMutation();
            }
        }

        /// <summary>
        /// Publishes shards loaded from elsewhere (a snapshot) as one map, in one swap. Allowed only
        /// into a store with no node and no edge and no staged reconciliation; the generation is not
        /// changed. The caller is responsible for the shards' own consistency (global id and
        /// identifier uniqueness), which a loader checks before it calls this. The shards' annotations
        /// (phase 4 restores them from the snapshot) replace every recorded one; none means none.
        /// </summary>
        internal void PublishLoadedShards(
            IEnumerable<KeyValuePair<ShardKey, IGraphShard>> shards,
            IEnumerable<KeyValuePair<ShardKey, ShardAnnotation>>? annotations = null)
        {
            ArgumentNullException.ThrowIfNull(shards);
            lock (_commitLock)
            {
                if (_reconciliationState is not null)
                    throw new InvalidOperationException("Cannot publish loaded shards while a graph reconciliation is active.");
                foreach (var existing in _shards.Values)
                {
                    if (existing.NodeCount > 0 || existing.EdgeCount > 0)
                        throw new InvalidOperationException("Loaded shards can only be published into an empty store.");
                }

                var builder = ImmutableSortedDictionary.CreateBuilder<ShardKey, IGraphShard>(ShardKey.Comparer);
                foreach (var (key, shard) in shards)
                {
                    ArgumentNullException.ThrowIfNull(shard);
                    builder.Add(key, shard);
                }
                var annotationBuilder = ImmutableSortedDictionary.CreateBuilder<ShardKey, ShardAnnotation>(ShardKey.Comparer);
                foreach (var (key, annotation) in annotations ?? [])
                {
                    ArgumentNullException.ThrowIfNull(annotation);
                    annotationBuilder.Add(key, annotation);
                }
                _shards = builder.ToImmutable();
                _annotations = annotationBuilder.ToImmutable();
                NotifyMutation();
            }
        }

        /// <summary>
        /// Swaps one shard for an equivalent one (a compacted or mapped copy) if it is still the
        /// <paramref name="expected"/> reference and no reconciliation is staged. Returns whether the
        /// swap happened. The published map changes by the same single volatile write as a commit;
        /// the generation is not changed.
        /// </summary>
        internal bool TryReplaceShard(ShardKey key, IGraphShard expected, IGraphShard replacement)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(replacement);
            lock (_commitLock)
            {
                if (_reconciliationState is not null) return false;
                if (!_shards.TryGetValue(key, out var current) || !ReferenceEquals(current, expected)) return false;
                _shards = _shards.SetItem(key, replacement);
                NotifyMutation();
                return true;
            }
        }

        /// <summary>
        /// The published shard map and the shards' annotations, latched together under the commit lock. A
        /// staged reconciliation is not visible here until it commits.
        /// </summary>
        internal (ImmutableSortedDictionary<ShardKey, IGraphShard> Shards, ImmutableSortedDictionary<ShardKey, ShardAnnotation> Annotations) LatchPublished()
        {
            lock (_commitLock) return (_shards, _annotations);
        }

        /// <summary>
        /// Latches the published shard map, the shards' annotations and the generation they were committed
        /// at, from one hold of the commit lock: one consistent cut. Returns false, latching nothing usable,
        /// while a reconciliation is staged: the published map is then about to be replaced by the staged one.
        /// </summary>
        internal bool TryLatchCommitted(
            out ImmutableSortedDictionary<ShardKey, IGraphShard> shards,
            out ImmutableSortedDictionary<ShardKey, ShardAnnotation> annotations,
            out long generation)
        {
            lock (_commitLock)
            {
                shards = _shards;
                annotations = _annotations;
                generation = Interlocked.Read(ref _lastCommittedGeneration);
                return _reconciliationState is null;
            }
        }

        public int PruneFilesNotPresent(IReadOnlyCollection<string> presentFilePaths)
        {
            var keep = new HashSet<string>(presentFilePaths, PathComparer);
            lock (_commitLock)
            {
                var current = _reconciliationState ?? _shards;

                // Phase 1: the globally-removed node ids (nodes whose file is no longer present).
                var removedNodeIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var shard in current.Values)
                    shard.CollectNodesOutside(keep, removedNodeIds);

                if (removedNodeIds.Count == 0) return 0;

                // Phase 2: rebuild only shards that lose a node or an edge touching a removed node;
                // untouched shards keep their reference (and cached adjacency).
                var builder = current.ToBuilder();
                foreach (var (key, shard) in current)
                {
                    var rebuilt = shard.WithoutNodes(removedNodeIds);
                    if (rebuilt is not null) builder[key] = rebuilt;
                }

                var next = builder.ToImmutable();
                if (_reconciliationState is not null) _reconciliationState = next;
                else
                {
                    _shards = next;
                    NotifyMutation();
                }
                return removedNodeIds.Count;
            }
        }

        /// <summary>
        /// Returns a version-stamped composite adjacency snapshot of the current committed state,
        /// rebuilt only when the mutation version changes. Untouched shards contribute their reused
        /// per-shard read state; a single-shard graph delegates straight to it.
        /// </summary>
        internal CompositeAdjacency GetAdjacency()
        {
            var version = Interlocked.Read(ref _version);
            var cached = _cachedAdjacency;
            if (cached is { } c && c.Version == version) return c.Value;

            var shards = _shards;
            var composite = CompositeAdjacency.Build(shards);
            _cachedAdjacency = new CachedAdjacency(version, composite);
            return composite;
        }

        // ---- Internal test accessors (shard-level identity assertions) ----

        internal object? GetShardState(ShardKey key)
            => _shards.TryGetValue(key, out var shard) ? shard : null;

        internal GraphAdjacency? GetShardAdjacency(ShardKey key)
            => _shards.TryGetValue(key, out var shard) && shard is GraphState heap ? heap.GetOrBuildAdjacency() : null;

        internal IReadOnlyCollection<ShardKey> ShardKeys => _shards.Keys.ToList();

        internal ShardKey? FindShardOfNode(string id)
        {
            foreach (var (key, shard) in _shards)
                if (shard.TryGetNode(id, out _)) return key;
            return null;
        }

        internal ShardKey? FindShardOfEdge(string id)
        {
            foreach (var (key, shard) in _shards)
                if (shard.TryGetEdge(id, out _)) return key;
            return null;
        }

        /// <summary>
        /// Counts over the committed graph. The per-type maps enumerate in ordinal key order, so a
        /// status response does not depend on the order facts were committed in.
        /// </summary>
        public GraphStatistics GetStatistics()
        {
            var version = Interlocked.Read(ref _version);
            var cached = _cachedStatistics;
            if (cached is { } c && c.Version == version)
            {
                return c.Value;
            }

            var shards = _shards;
            var nodesByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var edgesByType = new Dictionary<string, int>(StringComparer.Ordinal);
            var nodeCount = 0;
            var edgeCount = 0;

            foreach (var shard in shards.Values)
            {
                nodeCount += shard.NodeCount;
                edgeCount += shard.EdgeCount;
                shard.AddStatistics(nodesByType, edgesByType);
            }

            var statistics = new GraphStatistics(nodeCount, edgeCount, OrdinalKeyOrder(nodesByType), OrdinalKeyOrder(edgesByType));
            _cachedStatistics = new CachedStatistics(version, statistics);
            return statistics;
        }

        private static Dictionary<string, int> OrdinalKeyOrder(Dictionary<string, int> counts)
        {
            // A Dictionary filled without removals enumerates in insertion order.
            var ordered = new Dictionary<string, int>(counts.Count);
            foreach (var key in counts.Keys.Order(StringComparer.Ordinal)) ordered[key] = counts[key];
            return ordered;
        }

        // ---- Shard construction ----

        /// <summary>Deduplicates by id (null ids skipped): last occurrence wins, first-seen order kept.</summary>
        private static IEnumerable<T> Deduplicate<T>(IEnumerable<T> items, Func<T, string?> idOf)
        {
            var last = new Dictionary<string, T>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var item in items)
            {
                var id = idOf(item);
                if (id is null) continue;
                if (!last.ContainsKey(id)) order.Add(id);
                last[id] = item;
            }
            foreach (var id in order) yield return last[id];
        }

        /// <summary>Full replacement: partition incoming facts by their metadata-derived shard.</summary>
        private static ImmutableSortedDictionary<ShardKey, IGraphShard> PartitionIntoState(
            IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
        {
            // Collapse duplicate ids to a single surviving copy, last in input order winning — mirroring
            // the old single dictionary's `Nodes[id] = node` last-write-wins even when two copies carry
            // different ownership metadata (they would otherwise land in different shards).
            var nodesByShard = new Dictionary<ShardKey, List<CodeNode>>();
            foreach (var node in Deduplicate(nodes, n => n.Id))
            {
                var key = ShardKey.ForNode(node);
                if (!nodesByShard.TryGetValue(key, out var bucket))
                    nodesByShard[key] = bucket = new List<CodeNode>();
                bucket.Add(node);
            }

            var edgesByShard = new Dictionary<ShardKey, List<CodeEdge>>();
            foreach (var edge in Deduplicate(edges, e => e.Id))
            {
                var key = ShardKey.ForEdge(edge);
                if (!edgesByShard.TryGetValue(key, out var bucket))
                    edgesByShard[key] = bucket = new List<CodeEdge>();
                bucket.Add(edge);
            }

            // Global identifier map preserves the pre-shard behaviour: a duplicate public identifier
            // pointing at a different id is rejected regardless of which shard each fact lands in.
            var globalIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal);
            var builder = ImmutableSortedDictionary.CreateBuilder<ShardKey, IGraphShard>(ShardKey.Comparer);
            foreach (var key in nodesByShard.Keys.Union(edgesByShard.Keys))
            {
                var shardNodes = nodesByShard.GetValueOrDefault(key);
                var shardEdges = edgesByShard.GetValueOrDefault(key);
                var shard = new GraphState(shardNodes?.Count ?? 0, shardEdges?.Count ?? 0);
                if (shardNodes is not null)
                    foreach (var node in shardNodes)
                    {
                        shard.Nodes[node.Id!] = node;
                        AddIdentifier(shard, node, globalIdentifiers);
                    }
                if (shardEdges is not null)
                    foreach (var edge in shardEdges)
                        shard.Edges[edge.Id!] = edge;
                builder.Add(key, shard);
            }
            return builder.ToImmutable();
        }

        /// <summary>
        /// Scoped commit: replace facts owned by (scope.ParserId, scope.WorkspaceId) — or its listed
        /// files — carrying over the rest of that shard, then sweep foreign shards for edges that
        /// dangle onto removed-and-not-reestablished nodes. Untouched shards keep their reference;
        /// an unchanged scoped commit returns <paramref name="current"/> itself.
        /// </summary>
        private static ImmutableSortedDictionary<ShardKey, IGraphShard> BuildScopedState(
            ImmutableSortedDictionary<ShardKey, IGraphShard> current,
            IReadOnlyList<CodeNode> nodes,
            IReadOnlyList<CodeEdge> edges,
            CommitScope scope)
        {
            var key = ShardKey.From(scope.ParserId, scope.WorkspaceId);
            current.TryGetValue(key, out var currentShard);
            var replaceFiles = scope.ReplacesFiles is null
                ? null
                : new HashSet<string>(scope.ReplacesFiles, PathComparer);

            // The foreign shards, probed per-identifier (not eagerly enumerated) so the commit stays
            // O(touched shard + delta × shards) rather than O(total foreign nodes).
            var foreignShards = new List<IGraphShard>();
            foreach (var (otherKey, otherShard) in current)
                if (otherKey != key) foreignShards.Add(otherShard);

            var nextShard = (currentShard ?? new GraphState()).ApplyScoped(
                nodes, edges, replaceFiles, foreignShards, out var removedNodeIds, out var removedNotReestablished);
            if (nextShard is null) return current;

            var builder = current.ToBuilder();
            builder[key] = nextShard;

            // Delta ids let us fold cross-shard id collisions (delta wins) into the same one-swap publish.
            var deltaNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
                if (node.Id is not null) deltaNodeIds.Add(node.Id);
            var deltaEdgeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in edges)
                if (edge.Id is not null) deltaEdgeIds.Add(edge.Id);

            // Rebuild any foreign shard that (a) holds an edge now dangling onto a removed-and-not-
            // reestablished node, or (b) co-resides a delta id — the delta wins globally, so the stale
            // foreign copy is dropped. Both fold into this single swap; untouched shards keep their ref.
            if (removedNodeIds.Count > 0 || deltaNodeIds.Count > 0 || deltaEdgeIds.Count > 0)
            {
                foreach (var (otherKey, otherShard) in current)
                {
                    if (otherKey == key) continue;
                    var rebuilt = otherShard.WithoutForeignConflicts(
                        removedNodeIds, removedNotReestablished, deltaNodeIds, deltaEdgeIds);
                    if (rebuilt is not null) builder[otherKey] = rebuilt;
                }
            }

            return builder.ToImmutable();
        }

        // ---- In-place shard/identifier helpers ----

        /// <summary>The heap shard for <paramref name="key"/>, created (and published) if absent.</summary>
        private GraphState EnsureHeapShard(ShardKey key)
        {
            if (_shards.TryGetValue(key, out var shard)) return AsHeapShard(key, shard);
            var created = new GraphState();
            _shards = _shards.Add(key, created);
            return created;
        }

        /// <summary>
        /// The legacy in-place mutations write heap dictionaries. A shard of another kind is first
        /// replaced by a heap copy of itself, published in place of it. A staged reconciliation that
        /// holds the same shard reference gets the same copy, so the write reaches both maps exactly
        /// as it does when a heap shard is shared between them.
        /// </summary>
        private GraphState AsHeapShard(ShardKey key, IGraphShard shard)
        {
            if (shard is GraphState heap) return heap;
            heap = GraphState.CopyOf(shard);
            _shards = _shards.SetItem(key, heap);
            if (_reconciliationState is not null
                && _reconciliationState.TryGetValue(key, out var staged)
                && ReferenceEquals(staged, shard))
            {
                _reconciliationState = _reconciliationState.SetItem(key, heap);
            }
            return heap;
        }

        private static void RegisterIdentifierAcrossShards(
            ImmutableSortedDictionary<ShardKey, IGraphShard> shards, GraphState target, CodeNode node)
        {
            if (string.IsNullOrWhiteSpace(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;
            foreach (var shard in shards.Values)
            {
                if (shard.TryGetNodeIdByIdentifier(node.Identifier, out var existingId) && existingId != node.Id)
                    throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }
            target.Identifiers[node.Identifier] = node.Id;
        }

        /// <summary>
        /// Registers a carried or delta node's identifier during a scoped shard build: enforces
        /// uniqueness within the shard being built and, for delta nodes, probes each foreign shard's
        /// identifier map directly (O(1) per probe) instead of eagerly copying every foreign
        /// identifier. Throwing leaves no published state (the whole build is pre-swap).
        /// </summary>
        private static void RegisterIdentifier(
            GraphState nextShard, CodeNode node, IReadOnlyList<IGraphShard>? foreignProbe)
        {
            if (string.IsNullOrEmpty(node.Identifier))
                node.Identifier = DeriveLegacyIdentifier(node) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;

            if (nextShard.Identifiers.TryGetValue(node.Identifier, out var heldBy) && heldBy != node.Id)
                throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");

            if (foreignProbe is not null)
            {
                foreach (var foreign in foreignProbe)
                    if (foreign.TryGetNodeIdByIdentifier(node.Identifier, out var foreignId) && foreignId != node.Id)
                        throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }

            nextShard.Identifiers[node.Identifier] = node.Id;
        }

        /// <summary>Drops the identifier of a node copy that is being replaced, if it still names that id.</summary>
        private static void ReleaseIdentifier(GraphState nextShard, CodeNode replaced)
        {
            if (string.IsNullOrWhiteSpace(replaced.Identifier) || string.IsNullOrEmpty(replaced.Id)) return;
            if (nextShard.Identifiers.TryGetValue(replaced.Identifier, out var heldBy) && heldBy == replaced.Id)
                nextShard.Identifiers.TryRemove(replaced.Identifier, out _);
        }

        /// <summary>
        /// Registers a node's public identifier into <paramref name="state"/>, deriving a legacy one when
        /// absent and rejecting a duplicate that points at a different id. When
        /// <paramref name="globalIdentifiers"/> is supplied the uniqueness check spans every shard being
        /// built (preserving the pre-shard global behaviour); otherwise it is checked within the shard.
        /// </summary>
        private static void AddIdentifier(GraphState state, CodeNode node, Dictionary<string, string>? globalIdentifiers)
        {
            if (string.IsNullOrEmpty(node.Identifier))
                node.Identifier = DeriveLegacyIdentifier(node) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;

            if (globalIdentifiers is not null)
            {
                if (!globalIdentifiers.TryAdd(node.Identifier, node.Id)
                    && globalIdentifiers[node.Identifier] != node.Id)
                {
                    throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
                }
                state.Identifiers[node.Identifier] = node.Id;
                return;
            }

            if (!state.Identifiers.TryAdd(node.Identifier, node.Id)
                && state.Identifiers[node.Identifier] != node.Id)
            {
                throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }
        }

        internal static string? DeriveLegacyIdentifier(CodeNode node)
        {
            if (string.IsNullOrEmpty(node.Id)) return null;
            var first = node.Id.IndexOf(':');
            var second = first < 0 ? -1 : node.Id.IndexOf(':', first + 1);
            return second >= 0 && second + 1 < node.Id.Length ? node.Id[(second + 1)..] : node.Id;
        }

        /// <summary>
        /// Read facade over the shards of one published map. A single-shard graph delegates every
        /// member straight to that shard (zero overhead — the common case). Multi-shard probes each
        /// shard in ordinal <see cref="ShardKey"/> order and concatenates, so every list is in
        /// canonical order.
        /// </summary>
        internal sealed class CompositeAdjacency
        {
            private static readonly CodeEdge[] EmptyEdges = Array.Empty<CodeEdge>();
            private static readonly CodeNode[] EmptyNodes = Array.Empty<CodeNode>();
            private static readonly string[] EmptyPaths = Array.Empty<string>();

            private readonly IGraphShard[] _shards;
            private IReadOnlyList<CodeNode>? _nodes;
            private IReadOnlyList<CodeEdge>? _edges;
            private IReadOnlyList<CodeNode>? _nodesView;
            private IReadOnlyList<CodeEdge>? _edgesView;
            private IReadOnlyList<string>? _filePaths;

            private CompositeAdjacency(IGraphShard[] shards) => _shards = shards;

            internal static CompositeAdjacency Build(ImmutableSortedDictionary<ShardKey, IGraphShard> shards)
                => new(shards.Values.ToArray());

            public IReadOnlyList<CodeNode> Nodes
            {
                get
                {
                    if (_shards.Length == 1) return _shards[0].GetAllNodes();
                    if (_shards.Length == 0) return EmptyNodes;
                    return _nodes ??= _shards.SelectMany(s => s.GetAllNodes()).ToList();
                }
            }

            public IReadOnlyList<CodeEdge> Edges
            {
                get
                {
                    if (_shards.Length == 1) return _shards[0].GetAllEdges();
                    if (_shards.Length == 0) return EmptyEdges;
                    return _edges ??= _shards.SelectMany(s => s.GetAllEdges()).ToList();
                }
            }

            // Cached, downcast-proof read-only facades over the whole-graph node/edge lists, built ONCE
            // per composite instance (i.e. once per mutation version). GetAll repository reads return
            // these directly — zero per-call copy — yet the returned object cannot be cast back to the
            // shared List/array backing store, so no reader can mutate another reader's view.
            public IReadOnlyList<CodeNode> NodesView => _nodesView ??= AsReadOnlyView(Nodes);

            public IReadOnlyList<CodeEdge> EdgesView => _edgesView ??= AsReadOnlyView(Edges);

            private static IReadOnlyList<T> AsReadOnlyView<T>(IReadOnlyList<T> source)
                => source is IList<T> list
                    ? new ReadOnlyCollection<T>(list)
                    : new ReadOnlyCollection<T>(source.ToList());

            /// <summary>
            /// Distinct (ordinal) non-empty node file paths over every shard, ordered OrdinalIgnoreCase
            /// then Ordinal.
            /// </summary>
            public IReadOnlyList<string> FilePaths
            {
                get
                {
                    if (_shards.Length == 1) return _shards[0].GetFilePaths();
                    if (_shards.Length == 0) return EmptyPaths;
                    return _filePaths ??= new ReadOnlyCollection<string>(GraphAdjacency.SortPaths(
                        _shards.SelectMany(s => s.GetFilePaths()).Distinct(StringComparer.Ordinal)));
                }
            }

            public IReadOnlyList<CodeEdge> GetEdgesBySource(string sourceId)
            {
                if (_shards.Length == 1) return _shards[0].GetEdgesBySource(sourceId);
                List<CodeEdge>? acc = null;
                foreach (var shard in _shards)
                {
                    var hit = shard.GetEdgesBySource(sourceId);
                    if (hit.Count > 0) (acc ??= new List<CodeEdge>()).AddRange(hit);
                }
                return acc ?? (IReadOnlyList<CodeEdge>)EmptyEdges;
            }

            public IReadOnlyList<CodeEdge> GetEdgesByTarget(string targetId)
            {
                if (_shards.Length == 1) return _shards[0].GetEdgesByTarget(targetId);
                List<CodeEdge>? acc = null;
                foreach (var shard in _shards)
                {
                    var hit = shard.GetEdgesByTarget(targetId);
                    if (hit.Count > 0) (acc ??= new List<CodeEdge>()).AddRange(hit);
                }
                return acc ?? (IReadOnlyList<CodeEdge>)EmptyEdges;
            }

            public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath)
            {
                if (_shards.Length == 1) return _shards[0].FindNodesByPath(requestedPath);
                List<CodeNode>? acc = null;
                foreach (var shard in _shards)
                {
                    var hit = shard.FindNodesByPath(requestedPath);
                    if (hit.Count > 0) (acc ??= new List<CodeNode>()).AddRange(hit);
                }
                return acc ?? (IReadOnlyList<CodeNode>)EmptyNodes;
            }

            public IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact)
            {
                if (_shards.Length == 1) return _shards[0].FindNodesByName(name, exact);
                var acc = new List<CodeNode>();
                foreach (var shard in _shards) acc.AddRange(shard.FindNodesByName(name, exact));
                return acc;
            }
        }
    }
}
