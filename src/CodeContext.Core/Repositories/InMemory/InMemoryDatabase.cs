using System.Collections.Concurrent;
using System.Collections.Frozen;
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
        /// One shard's node/edge/identifier tables. A committed shard is immutable and is
        /// replaced wholesale (new reference) by a commit; in-place mutations bump
        /// <see cref="Version"/> so the per-shard adjacency and cross-shard candidate caches
        /// invalidate without a reference change.
        /// </summary>
        internal sealed class GraphState
        {
            public ConcurrentDictionary<string, CodeNode> Nodes { get; }
            public ConcurrentDictionary<string, string> Identifiers { get; }
            public ConcurrentDictionary<string, CodeEdge> Edges { get; }

            private long _version;
            private volatile CachedAdjacency? _adjacency;
            private volatile HashSet<string>? _crossShardEdgeIds;

            private sealed record CachedAdjacency(long Version, GraphAdjacency Value);

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

            public long Version => Interlocked.Read(ref _version);

            /// <summary>Invalidates the per-shard adjacency and cross-shard caches after an in-place write.</summary>
            public void BumpVersion()
            {
                Interlocked.Increment(ref _version);
                _crossShardEdgeIds = null;
            }

            /// <summary>
            /// Ids of edges with at least one endpoint that is NOT a node of this shard — the only
            /// edges a removal in ANOTHER shard could dangle. A cheap, id-convention-independent
            /// superset of the cross-shard sweep candidates (built once per shard version).
            /// </summary>
            public HashSet<string> GetCrossShardEdgeIds()
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

                var nodes = new List<CodeNode>(Nodes.Count);
                var edges = new List<CodeEdge>(Edges.Count);
                var bySource = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
                var byTarget = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
                var byFilePath = new Dictionary<string, List<CodeNode>>(StringComparer.OrdinalIgnoreCase);

                foreach (var node in Nodes.Values)
                {
                    nodes.Add(node);
                    if (!string.IsNullOrWhiteSpace(node.FilePath))
                    {
                        var key = FilePathMatcher.Normalize(node.FilePath);
                        if (!byFilePath.TryGetValue(key, out var bucket))
                            byFilePath[key] = bucket = new List<CodeNode>();
                        bucket.Add(node);
                    }
                }

                foreach (var edge in Edges.Values)
                {
                    edges.Add(edge);
                    if (edge.SourceId is not null)
                    {
                        if (!bySource.TryGetValue(edge.SourceId, out var bucket))
                            bySource[edge.SourceId] = bucket = new List<CodeEdge>();
                        bucket.Add(edge);
                    }
                    if (edge.TargetId is not null)
                    {
                        if (!byTarget.TryGetValue(edge.TargetId, out var bucket))
                            byTarget[edge.TargetId] = bucket = new List<CodeEdge>();
                        bucket.Add(edge);
                    }
                }

                var adjacency = new GraphAdjacency
                {
                    Nodes = nodes,
                    Edges = edges,
                    EdgesBySource = bySource.ToFrozenDictionary(
                        entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal),
                    EdgesByTarget = byTarget.ToFrozenDictionary(
                        entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.Ordinal),
                    NodesByFilePath = byFilePath.ToFrozenDictionary(
                        entry => entry.Key, entry => entry.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                };
                _adjacency = new CachedAdjacency(version, adjacency);
                return adjacency;
            }
        }

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static ImmutableSortedDictionary<ShardKey, GraphState> EmptyShards { get; } =
            ImmutableSortedDictionary.Create<ShardKey, GraphState>(ShardKey.Comparer);

        private volatile ImmutableSortedDictionary<ShardKey, GraphState> _shards = EmptyShards;
        private readonly object _commitLock = new();
        private long _version;
        private long _lastCommittedGeneration;
        private ImmutableSortedDictionary<ShardKey, GraphState>? _reconciliationState;
        private long _reconciliationGeneration;

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
                foreach (var shard in shards.Values) count += shard.Nodes.Count;
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
                foreach (var shard in shards.Values) count += shard.Edges.Count;
                return count;
            }
        }

        /// <summary>Probes shards in ordinal <see cref="ShardKey"/> order (ids are globally unique).</summary>
        public bool TryGetNode(string id, out CodeNode? node)
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
            {
                if (shard.Nodes.TryGetValue(id, out node)) return true;
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
                if (shard.Identifiers.TryGetValue(identifier, out id)) return true;
            }
            id = null;
            return false;
        }

        public bool TryGetEdge(string id, out CodeEdge? edge)
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
            {
                if (shard.Edges.TryGetValue(id, out edge)) return true;
            }
            edge = null;
            return false;
        }

        public bool ContainsEdge(string id) => TryGetEdge(id, out _);

        public CodeEdge? GetEdge(string id) => TryGetEdge(id, out var edge) ? edge : null;

        /// <summary>Concatenates shard nodes in ordinal <see cref="ShardKey"/> order (latched once).</summary>
        public IEnumerable<CodeNode> EnumerateNodes()
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
                foreach (var node in shard.Nodes.Values)
                    yield return node;
        }

        public IEnumerable<CodeEdge> EnumerateEdges()
        {
            var shards = _shards;
            foreach (var shard in shards.Values)
                foreach (var edge in shard.Edges.Values)
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

                var shard = EnsureShard(ShardKey.ForNode(node), _shards, out _);
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
                foreach (var shard in _shards.Values)
                {
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
                var shard = EnsureShard(ShardKey.ForEdge(edge), _shards, out _);
                shard.Edges[edge.Id] = edge;
                // Global last-write-wins for the edge id across shards.
                PurgeEdgeFromOtherShards(shard, edge.Id);
                shard.BumpVersion();
                NotifyMutation();
            }
        }

        private void PurgeNodeFromOtherShards(GraphState keep, string id)
        {
            foreach (var shard in _shards.Values)
            {
                if (ReferenceEquals(shard, keep)) continue;
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
            foreach (var shard in _shards.Values)
            {
                if (ReferenceEquals(shard, keep)) continue;
                if (shard.Edges.TryRemove(id, out _)) shard.BumpVersion();
            }
        }

        /// <summary>Removes every edge (across all shards) whose source or target is <paramref name="nodeId"/>.</summary>
        public int RemoveEdgesTouchingNode(string nodeId)
        {
            lock (_commitLock)
            {
                var removed = 0;
                foreach (var shard in _shards.Values)
                {
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
        /// Captures nodes and edges from one committed shard map. Reading counts or enumerations
        /// separately can straddle an atomic generation swap and produce a graph whose nodes and
        /// edges came from different versions; latching the map once avoids that.
        /// </summary>
        public CodeGraph CaptureGraph()
        {
            var shards = _shards;
            var nodes = new List<CodeNode>();
            var edges = new List<CodeEdge>();
            foreach (var shard in shards.Values)
            {
                nodes.AddRange(shard.Nodes.Values);
                edges.AddRange(shard.Edges.Values);
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
                _reconciliationState = null;
                Interlocked.Exchange(ref _lastCommittedGeneration, _reconciliationGeneration);
                NotifyMutation();
            }
        }

        public void RollbackReconciliation()
        {
            lock (_commitLock)
            {
                _reconciliationState = null;
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
                    : BuildScopedState(current, nodes, edges, scope);

                if (_reconciliationState is not null)
                {
                    _reconciliationState = next;
                    _reconciliationGeneration = generation;
                }
                else
                {
                    _shards = next;
                    Interlocked.Exchange(ref _lastCommittedGeneration, generation);
                    NotifyMutation();
                }
                return true;
            }
        }

        /// <summary>Unconditional full replacement (legacy SaveGraph path — no generation check).</summary>
        public void ReplaceAll(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
        {
            lock (_commitLock)
            {
                _shards = PartitionIntoState(nodes, edges);
                NotifyMutation();
            }
        }

        public void Reset()
        {
            lock (_commitLock)
            {
                _shards = EmptyShards;
                CurrentGraph = null;
                NotifyMutation();
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
                {
                    foreach (var node in shard.Nodes.Values)
                    {
                        if (node.Id is null) continue;
                        if (!string.IsNullOrEmpty(node.FilePath) && !keep.Contains(node.FilePath))
                            removedNodeIds.Add(node.Id);
                    }
                }

                if (removedNodeIds.Count == 0) return 0;

                // Phase 2: rebuild only shards that lose a node or an edge touching a removed node;
                // untouched shards keep their reference (and cached adjacency).
                var builder = current.ToBuilder();
                foreach (var (key, shard) in current)
                {
                    var rebuilt = PruneShard(shard, removedNodeIds);
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
        /// per-shard <see cref="GraphAdjacency"/>; a single-shard graph delegates straight to it.
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
            => _shards.TryGetValue(key, out var shard) ? shard.GetOrBuildAdjacency() : null;

        internal IReadOnlyCollection<ShardKey> ShardKeys => _shards.Keys.ToList();

        internal ShardKey? FindShardOfNode(string id)
        {
            foreach (var (key, shard) in _shards)
                if (shard.Nodes.ContainsKey(id)) return key;
            return null;
        }

        internal ShardKey? FindShardOfEdge(string id)
        {
            foreach (var (key, shard) in _shards)
                if (shard.Edges.ContainsKey(id)) return key;
            return null;
        }

        public GraphStatistics GetStatistics()
        {
            var version = Interlocked.Read(ref _version);
            var cached = _cachedStatistics;
            if (cached is { } c && c.Version == version)
            {
                return c.Value;
            }

            var shards = _shards;
            var nodesByType = new Dictionary<string, int>();
            var edgesByType = new Dictionary<string, int>();
            var nodeCount = 0;
            var edgeCount = 0;

            foreach (var shard in shards.Values)
            {
                foreach (var node in shard.Nodes.Values)
                {
                    nodeCount++;
                    if (!string.IsNullOrEmpty(node.Type))
                        nodesByType[node.Type] = nodesByType.GetValueOrDefault(node.Type) + 1;
                }

                foreach (var edge in shard.Edges.Values)
                {
                    edgeCount++;
                    if (!string.IsNullOrEmpty(edge.Type))
                        edgesByType[edge.Type] = edgesByType.GetValueOrDefault(edge.Type) + 1;
                }
            }

            var statistics = new GraphStatistics(nodeCount, edgeCount, nodesByType, edgesByType);
            _cachedStatistics = new CachedStatistics(version, statistics);
            return statistics;
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
        private static ImmutableSortedDictionary<ShardKey, GraphState> PartitionIntoState(
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
            var builder = ImmutableSortedDictionary.CreateBuilder<ShardKey, GraphState>(ShardKey.Comparer);
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
        /// dangle onto removed-and-not-reestablished nodes. Untouched shards keep their reference.
        /// </summary>
        private static ImmutableSortedDictionary<ShardKey, GraphState> BuildScopedState(
            ImmutableSortedDictionary<ShardKey, GraphState> current,
            IEnumerable<CodeNode> nodes,
            IEnumerable<CodeEdge> edges,
            CommitScope scope)
        {
            var key = ShardKey.From(scope.ParserId, scope.WorkspaceId);
            current.TryGetValue(key, out var currentShard);
            var replaceFiles = scope.ReplacesFiles is null
                ? null
                : new HashSet<string>(scope.ReplacesFiles, PathComparer);

            // The foreign shards, probed per-identifier (not eagerly enumerated) so the commit stays
            // O(touched shard + delta × shards) rather than O(total foreign nodes).
            var foreignShards = new List<GraphState>();
            foreach (var (otherKey, otherShard) in current)
                if (otherKey != key) foreignShards.Add(otherShard);

            var deltaNodeCount = nodes is ICollection<CodeNode> nc ? nc.Count : 0;
            var deltaEdgeCount = edges is ICollection<CodeEdge> ec ? ec.Count : 0;
            var nextShard = new GraphState(
                (currentShard?.Nodes.Count ?? 0) + deltaNodeCount,
                (currentShard?.Edges.Count ?? 0) + deltaEdgeCount);
            var removedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            // Identifiers of the shard being built, for within-shard uniqueness across carried + delta.
            var buildIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal);
            // Delta ids let us fold cross-shard id collisions (delta wins) into the same one-swap publish.
            var deltaNodeIds = new HashSet<string>(StringComparer.Ordinal);
            var deltaEdgeIds = new HashSet<string>(StringComparer.Ordinal);

            if (currentShard is not null)
            {
                foreach (var node in currentShard.Nodes.Values)
                {
                    if (node.Id is null) continue;
                    var removed = replaceFiles is null
                        || (!string.IsNullOrEmpty(node.FilePath) && replaceFiles.Contains(node.FilePath));
                    if (removed)
                    {
                        removedNodeIds.Add(node.Id);
                    }
                    else
                    {
                        // Carried facts were already globally unique — no foreign probe needed.
                        nextShard.Nodes.TryAdd(node.Id, node);
                        RegisterBuildIdentifier(nextShard, buildIdentifiers, node, foreignProbe: null);
                    }
                }
            }

            foreach (var node in nodes)
            {
                if (node.Id is null) continue;
                deltaNodeIds.Add(node.Id);
                nextShard.Nodes[node.Id] = node;
                RegisterBuildIdentifier(nextShard, buildIdentifiers, node, foreignProbe: foreignShards);
            }

            if (currentShard is not null)
            {
                // Carry edges within the shard, mirroring the pre-shard rule exactly: an edge from a
                // surviving node into a replaced node is kept only when the delta re-established that
                // target — otherwise it would dangle.
                foreach (var edge in currentShard.Edges.Values)
                {
                    if (edge.Id is null) continue;
                    if (edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId)) continue;
                    if (edge.TargetId is not null
                        && removedNodeIds.Contains(edge.TargetId)
                        && !nextShard.Nodes.ContainsKey(edge.TargetId)) continue;
                    nextShard.Edges.TryAdd(edge.Id, edge);
                }
            }

            foreach (var edge in edges)
            {
                if (edge.Id is null) continue;
                deltaEdgeIds.Add(edge.Id);
                nextShard.Edges[edge.Id] = edge;
            }

            var builder = current.ToBuilder();
            builder[key] = nextShard;

            // Rebuild any foreign shard that (a) holds an edge now dangling onto a removed-and-not-
            // reestablished node, or (b) co-resides a delta id — the delta wins globally, so the stale
            // foreign copy is dropped. Both fold into this single swap; untouched shards keep their ref.
            var removedNotReestablished = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in removedNodeIds)
                if (!nextShard.Nodes.ContainsKey(id)) removedNotReestablished.Add(id);

            if (removedNodeIds.Count > 0 || deltaNodeIds.Count > 0 || deltaEdgeIds.Count > 0)
            {
                foreach (var (otherKey, otherShard) in current)
                {
                    if (otherKey == key) continue;
                    var rebuilt = RebuildForeignShard(
                        otherShard, removedNodeIds, removedNotReestablished, deltaNodeIds, deltaEdgeIds);
                    if (rebuilt is not null) builder[otherKey] = rebuilt;
                }
            }

            return builder.ToImmutable();
        }

        /// <summary>
        /// Returns a rebuilt copy of a foreign shard with dangling edges and delta-id collisions removed,
        /// or null when nothing changed (so the caller keeps the original reference and its cached
        /// adjacency). Dangling-edge drop mirrors the pre-shard global semantics (drop iff source was
        /// removed, or target was removed and not re-established anywhere); a co-resident delta id is
        /// dropped so the delta's copy is the single global survivor.
        /// </summary>
        private static GraphState? RebuildForeignShard(
            GraphState shard,
            HashSet<string> removedNodeIds,
            HashSet<string> removedNotReestablished,
            HashSet<string> deltaNodeIds,
            HashSet<string> deltaEdgeIds)
        {
            HashSet<string>? dropEdges = null;

            if (removedNodeIds.Count > 0)
            {
                foreach (var edgeId in shard.GetCrossShardEdgeIds())
                {
                    if (!shard.Edges.TryGetValue(edgeId, out var edge) || edge.Id is null) continue;
                    var dangles = (edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId))
                        || (edge.TargetId is not null && removedNotReestablished.Contains(edge.TargetId));
                    if (dangles) (dropEdges ??= new HashSet<string>(StringComparer.Ordinal)).Add(edge.Id);
                }
            }

            if (deltaEdgeIds.Count > 0)
            {
                foreach (var id in deltaEdgeIds)
                    if (shard.Edges.ContainsKey(id)) (dropEdges ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
            }

            HashSet<string>? dropNodes = null;
            if (deltaNodeIds.Count > 0)
            {
                foreach (var id in deltaNodeIds)
                    if (shard.Nodes.ContainsKey(id)) (dropNodes ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
            }

            // When a delta reclaims an id, the evicted foreign node's OWN edges die with it — including
            // fully-local ones (both endpoints in-shard) that never appear in the cross-shard candidate
            // set. This deliberately DIVERGES from the old single-map behaviour (which left the edge in
            // place, silently re-pointing it at the unrelated new node with the reclaimed id — a wrong
            // association); dropping matches the store's own removed-node edge rule.
            if (dropNodes is not null)
            {
                foreach (var edge in shard.Edges.Values)
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
                shard.Nodes.Count - (dropNodes?.Count ?? 0),
                Math.Max(0, shard.Edges.Count - (dropEdges?.Count ?? 0)));
            foreach (var pair in shard.Nodes)
                if (dropNodes is null || !dropNodes.Contains(pair.Key)) next.Nodes.TryAdd(pair.Key, pair.Value);
            foreach (var pair in shard.Identifiers)
                if (dropNodes is null || !dropNodes.Contains(pair.Value)) next.Identifiers.TryAdd(pair.Key, pair.Value);
            foreach (var pair in shard.Edges)
                if (dropEdges is null || !dropEdges.Contains(pair.Key)) next.Edges.TryAdd(pair.Key, pair.Value);
            return next;
        }

        /// <summary>
        /// Returns a rebuilt copy of a shard with removed nodes and edges touching any globally-removed
        /// node dropped, or null when the shard is unaffected (keep the original reference).
        /// </summary>
        private static GraphState? PruneShard(GraphState shard, HashSet<string> removedNodeIds)
        {
            var hasNodeRemoval = false;
            foreach (var node in shard.Nodes.Values)
                if (node.Id is not null && removedNodeIds.Contains(node.Id)) { hasNodeRemoval = true; break; }

            var hasEdgeRemoval = false;
            if (!hasNodeRemoval)
            {
                // No local node removals: only cross-shard edges can be affected.
                foreach (var edgeId in shard.GetCrossShardEdgeIds())
                {
                    if (!shard.Edges.TryGetValue(edgeId, out var edge)) continue;
                    if ((edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId))
                        || (edge.TargetId is not null && removedNodeIds.Contains(edge.TargetId)))
                    {
                        hasEdgeRemoval = true;
                        break;
                    }
                }
                if (!hasEdgeRemoval) return null;
            }

            var next = new GraphState(shard.Nodes.Count, shard.Edges.Count);
            foreach (var node in shard.Nodes.Values)
            {
                if (node.Id is null) continue;
                if (removedNodeIds.Contains(node.Id)) continue;
                next.Nodes.TryAdd(node.Id, node);
                AddIdentifier(next, node, globalIdentifiers: null);
            }
            foreach (var edge in shard.Edges.Values)
            {
                if (edge.Id is null) continue;
                if (edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId)) continue;
                if (edge.TargetId is not null && removedNodeIds.Contains(edge.TargetId)) continue;
                next.Edges.TryAdd(edge.Id, edge);
            }
            return next;
        }

        // ---- In-place shard/identifier helpers ----

        private GraphState EnsureShard(
            ShardKey key, ImmutableSortedDictionary<ShardKey, GraphState> shards, out bool created)
        {
            if (shards.TryGetValue(key, out var shard))
            {
                created = false;
                return shard;
            }
            shard = new GraphState();
            _shards = shards.Add(key, shard);
            created = true;
            return shard;
        }

        private static void RegisterIdentifierAcrossShards(
            ImmutableSortedDictionary<ShardKey, GraphState> shards, GraphState target, CodeNode node)
        {
            if (string.IsNullOrWhiteSpace(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;
            foreach (var shard in shards.Values)
            {
                if (shard.Identifiers.TryGetValue(node.Identifier, out var existingId) && existingId != node.Id)
                    throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }
            target.Identifiers[node.Identifier] = node.Id;
        }

        /// <summary>
        /// Registers a delta/carried node's identifier during a scoped shard build: enforces uniqueness
        /// within the shard being built and, for delta nodes, probes each foreign shard's identifier map
        /// directly (O(1) per probe) instead of eagerly copying every foreign identifier. Throwing leaves
        /// no published state (the whole build is pre-swap).
        /// </summary>
        private static void RegisterBuildIdentifier(
            GraphState nextShard,
            Dictionary<string, string> buildIdentifiers,
            CodeNode node,
            IReadOnlyList<GraphState>? foreignProbe)
        {
            if (string.IsNullOrEmpty(node.Identifier))
                node.Identifier = DeriveLegacyIdentifier(node) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;

            if (!buildIdentifiers.TryAdd(node.Identifier, node.Id)
                && buildIdentifiers[node.Identifier] != node.Id)
            {
                throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }

            if (foreignProbe is not null)
            {
                foreach (var foreign in foreignProbe)
                    if (foreign.Identifiers.TryGetValue(node.Identifier, out var foreignId) && foreignId != node.Id)
                        throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
            }

            nextShard.Identifiers[node.Identifier] = node.Id;
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
        /// Read facade over one or more per-shard <see cref="GraphAdjacency"/> snapshots. A single-shard
        /// graph delegates every member straight to that shard's adjacency (zero overhead — the common
        /// case). Multi-shard probes each shard in ordinal <see cref="ShardKey"/> order and concatenates.
        /// </summary>
        internal sealed class CompositeAdjacency
        {
            private static readonly CodeEdge[] EmptyEdges = Array.Empty<CodeEdge>();
            private static readonly CodeNode[] EmptyNodes = Array.Empty<CodeNode>();

            private readonly GraphAdjacency[] _shards;
            private IReadOnlyList<CodeNode>? _nodes;
            private IReadOnlyList<CodeEdge>? _edges;
            private IReadOnlyList<CodeNode>? _nodesView;
            private IReadOnlyList<CodeEdge>? _edgesView;

            private CompositeAdjacency(GraphAdjacency[] shards) => _shards = shards;

            internal static CompositeAdjacency Build(ImmutableSortedDictionary<ShardKey, GraphState> shards)
            {
                var adjacencies = new GraphAdjacency[shards.Count];
                var i = 0;
                foreach (var shard in shards.Values)
                    adjacencies[i++] = shard.GetOrBuildAdjacency();
                return new CompositeAdjacency(adjacencies);
            }

            public IReadOnlyList<CodeNode> Nodes
            {
                get
                {
                    if (_shards.Length == 1) return _shards[0].Nodes;
                    if (_shards.Length == 0) return EmptyNodes;
                    return _nodes ??= _shards.SelectMany(s => s.Nodes).ToList();
                }
            }

            public IReadOnlyList<CodeEdge> Edges
            {
                get
                {
                    if (_shards.Length == 1) return _shards[0].Edges;
                    if (_shards.Length == 0) return EmptyEdges;
                    return _edges ??= _shards.SelectMany(s => s.Edges).ToList();
                }
            }

            // Cached, downcast-proof read-only facades over the whole-graph node/edge lists, built ONCE
            // per composite instance (i.e. once per mutation version). GetAll repository reads return
            // these directly — zero per-call copy — yet the returned object cannot be cast back to the
            // shared List/array backing store (single-shard delegates straight to a GraphAdjacency's List;
            // the multi-shard concat caches its own List), so no reader can mutate another reader's view.
            public IReadOnlyList<CodeNode> NodesView => _nodesView ??= AsReadOnlyView(Nodes);

            public IReadOnlyList<CodeEdge> EdgesView => _edgesView ??= AsReadOnlyView(Edges);

            private static IReadOnlyList<T> AsReadOnlyView<T>(IReadOnlyList<T> source)
                => source is IList<T> list
                    ? new ReadOnlyCollection<T>(list)
                    : new ReadOnlyCollection<T>(source.ToList());

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
        }
    }
}
