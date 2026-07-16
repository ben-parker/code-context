using System.Collections.Concurrent;
using CodeContext.Core.Models;

namespace CodeContext.Core.Repositories.InMemory
{
    /// <summary>
    /// The in-memory graph store. Readers always see a complete committed snapshot:
    /// full-graph (or scoped) replacements build a fresh node/edge state and swap it in
    /// atomically, so API reads during a rescan observe the previous generation rather
    /// than a cleared or half-built graph. Incremental per-node mutations (the single-file
    /// parser path) still write to the live dictionaries; those are serialized by the
    /// index coordinator.
    /// </summary>
    public class InMemoryDatabase
    {
        private sealed class GraphState
        {
            public ConcurrentDictionary<string, CodeNode> Nodes { get; } = new();
            public ConcurrentDictionary<string, CodeEdge> Edges { get; } = new();
        }

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private volatile GraphState _state = new();
        private readonly object _commitLock = new();
        private long _version;
        private long _lastCommittedGeneration;
        private GraphState? _reconciliationState;
        private long _reconciliationGeneration;

        private GraphStatistics? _cachedStatistics;
        private long _cachedStatisticsVersion = -1;

        public ConcurrentDictionary<string, CodeNode> Nodes => _state.Nodes;
        public ConcurrentDictionary<string, CodeEdge> Edges => _state.Edges;
        public CodeGraph? CurrentGraph { get; set; }

        /// <summary>
        /// Captures nodes and edges from one committed state reference. Reading the two
        /// public dictionary properties separately can straddle an atomic generation
        /// swap and produce a graph whose nodes and edges came from different versions.
        /// </summary>
        public CodeGraph CaptureGraph()
        {
            var state = _state;
            return new CodeGraph
            {
                Nodes = state.Nodes.Values.ToList(),
                Edges = state.Edges.Values.ToList(),
            };
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
                _reconciliationState = _state;
                _reconciliationGeneration = _lastCommittedGeneration;
            }
        }

        public void CommitReconciliation()
        {
            lock (_commitLock)
            {
                if (_reconciliationState is null)
                    throw new InvalidOperationException("No graph reconciliation is active.");
                _state = _reconciliationState;
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

        /// <summary>Invalidates cached statistics; called by repositories after in-place mutations.</summary>
        public void NotifyMutation() => Interlocked.Increment(ref _version);

        public bool TryCommitGeneration(
            long generation,
            IEnumerable<CodeNode> nodes,
            IEnumerable<CodeEdge> edges,
            Func<CodeNode, bool>? replacesScope)
        {
            lock (_commitLock)
            {
                var lastGeneration = _reconciliationState is null
                    ? Interlocked.Read(ref _lastCommittedGeneration)
                    : _reconciliationGeneration;
                if (generation <= lastGeneration) return false;

                var next = BuildNextState(
                    _reconciliationState ?? _state, nodes, edges, replacesScope);
                if (_reconciliationState is not null)
                {
                    _reconciliationState = next;
                    _reconciliationGeneration = generation;
                }
                else
                {
                    _state = next;
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
                _state = BuildNextState(_state, nodes, edges, replacesScope: null);
                NotifyMutation();
            }
        }

        public void Reset()
        {
            lock (_commitLock)
            {
                _state = new GraphState();
                CurrentGraph = null;
                NotifyMutation();
            }
        }

        public int PruneFilesNotPresent(IReadOnlyCollection<string> presentFilePaths)
        {
            var keep = new HashSet<string>(presentFilePaths, PathComparer);
            lock (_commitLock)
            {
                var current = _reconciliationState ?? _state;
                var removedNodeIds = new HashSet<string>();
                var next = new GraphState();

                foreach (var node in current.Nodes.Values)
                {
                    if (node.Id is null) continue;
                    if (!string.IsNullOrEmpty(node.FilePath) && !keep.Contains(node.FilePath))
                    {
                        removedNodeIds.Add(node.Id);
                        continue;
                    }
                    next.Nodes.TryAdd(node.Id, node);
                }

                if (removedNodeIds.Count == 0) return 0;

                foreach (var edge in current.Edges.Values)
                {
                    if (edge.Id is null) continue;
                    if (edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId)) continue;
                    if (edge.TargetId is not null && removedNodeIds.Contains(edge.TargetId)) continue;
                    next.Edges.TryAdd(edge.Id, edge);
                }

                if (_reconciliationState is not null) _reconciliationState = next;
                else
                {
                    _state = next;
                    NotifyMutation();
                }
                return removedNodeIds.Count;
            }
        }

        public GraphStatistics GetStatistics()
        {
            var version = Interlocked.Read(ref _version);
            var cached = _cachedStatistics;
            if (cached is not null && Interlocked.Read(ref _cachedStatisticsVersion) == version)
            {
                return cached;
            }

            var state = _state;
            var nodesByType = new Dictionary<string, int>();
            var edgesByType = new Dictionary<string, int>();
            var nodeCount = 0;
            var edgeCount = 0;

            foreach (var node in state.Nodes.Values)
            {
                nodeCount++;
                if (!string.IsNullOrEmpty(node.Type))
                {
                    nodesByType[node.Type] = nodesByType.GetValueOrDefault(node.Type) + 1;
                }
            }

            foreach (var edge in state.Edges.Values)
            {
                edgeCount++;
                if (!string.IsNullOrEmpty(edge.Type))
                {
                    edgesByType[edge.Type] = edgesByType.GetValueOrDefault(edge.Type) + 1;
                }
            }

            var statistics = new GraphStatistics(nodeCount, edgeCount, nodesByType, edgesByType);
            _cachedStatistics = statistics;
            Interlocked.Exchange(ref _cachedStatisticsVersion, version);
            return statistics;
        }

        private GraphState BuildNextState(
            GraphState current,
            IEnumerable<CodeNode> nodes,
            IEnumerable<CodeEdge> edges,
            Func<CodeNode, bool>? replacesScope)
        {
            var next = new GraphState();
            var removedNodeIds = new HashSet<string>();

            if (replacesScope is not null)
            {
                foreach (var node in current.Nodes.Values)
                {
                    if (node.Id is null) continue;
                    if (replacesScope(node))
                    {
                        removedNodeIds.Add(node.Id);
                    }
                    else
                    {
                        next.Nodes.TryAdd(node.Id, node);
                    }
                }
            }

            foreach (var node in nodes)
            {
                if (node.Id is null) continue;
                next.Nodes[node.Id] = node;
            }

            if (replacesScope is not null)
            {
                // Keep edges produced outside the replaced scope; the delta re-emits the
                // rest. An edge from a surviving node into a replaced node is kept only
                // when the delta re-established that target — otherwise it would dangle.
                foreach (var edge in current.Edges.Values)
                {
                    if (edge.Id is null) continue;
                    if (edge.SourceId is not null && removedNodeIds.Contains(edge.SourceId)) continue;
                    if (edge.TargetId is not null
                        && removedNodeIds.Contains(edge.TargetId)
                        && !next.Nodes.ContainsKey(edge.TargetId)) continue;
                    next.Edges.TryAdd(edge.Id, edge);
                }
            }

            foreach (var edge in edges)
            {
                if (edge.Id is null) continue;
                next.Edges[edge.Id] = edge;
            }

            return next;
        }
    }
}
