using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using static System.StringComparison;
using CodeContext.Core.Services;

namespace CodeContext.Core.Repositories.InMemory
{
    /// <summary>
    /// Immutable, version-stamped adjacency snapshot of one heap shard
    /// (<see cref="InMemoryDatabase.GraphState"/>). Built in one pass over the shard's dictionaries
    /// and cached until the shard's version changes, this turns the repository read paths from full
    /// O(E)/O(N) table scans into O(degree) lookups (the hot path for every <c>ContextService</c> hop).
    ///
    /// Ordering contract (canonical order): <see cref="Nodes"/>, <see cref="Edges"/>, every per-key
    /// bucket and every lookup result are in ordinal id order, whatever order or capacity the
    /// dictionaries were built with. A mapped shard returns the same sequences, so the two kinds of
    /// shard give byte-identical answers and an overlay can merge with a base without a sort.
    /// </summary>
    internal sealed class GraphAdjacency
    {
        private static readonly CodeEdge[] EmptyEdges = Array.Empty<CodeEdge>();
        private static readonly CodeNode[] EmptyNodes = Array.Empty<CodeNode>();

        internal static readonly Comparison<CodeNode> ById =
            static (left, right) => string.CompareOrdinal(left.Id, right.Id);

        internal static readonly Comparison<CodeEdge> EdgeById =
            static (left, right) => string.CompareOrdinal(left.Id, right.Id);

        // Sorting is deferred to first use everywhere: the edit path (commit, then a keyed query)
        // rebuilds this snapshot and should sort only what it reads.
        private readonly CodeNode[] _unorderedNodes;
        private readonly SortedBucket<CodeNode> _nodes;
        private readonly SortedBucket<CodeEdge> _edges;
        private IReadOnlyList<CodeNode>? _nodesView;
        private IReadOnlyList<CodeEdge>? _edgesView;
        private IReadOnlyList<string>? _filePaths;

        private GraphAdjacency(
            CodeNode[] unorderedNodes,
            CodeEdge[] unorderedEdges,
            BucketIndex<CodeEdge> edgesBySource,
            BucketIndex<CodeEdge> edgesByTarget,
            BucketIndex<CodeNode> nodesByFilePath)
        {
            _unorderedNodes = unorderedNodes;
            _nodes = new SortedBucket<CodeNode>((CodeNode[])unorderedNodes.Clone(), ById);
            _edges = new SortedBucket<CodeEdge>(unorderedEdges, EdgeById);
            _edgesBySource = edgesBySource;
            _edgesByTarget = edgesByTarget;
            _nodesByFilePath = nodesByFilePath;
        }

        /// <summary>All nodes of the snapshot, in id order.</summary>
        public IReadOnlyList<CodeNode> Nodes
            => Volatile.Read(ref _nodesView) ?? CachedView(ref _nodesView, _nodes.Items);

        /// <summary>All edges of the snapshot, in id order.</summary>
        public IReadOnlyList<CodeEdge> Edges
            => Volatile.Read(ref _edgesView) ?? CachedView(ref _edgesView, _edges.Items);

        // A racing reader may wrap the same sorted array too; either wrapper may be published.
        private static IReadOnlyList<T> CachedView<T>(ref IReadOnlyList<T>? field, T[] items)
        {
            IReadOnlyList<T> view = new ReadOnlyCollection<T>(items);
            Volatile.Write(ref field, view);
            return view;
        }

        // Edges grouped by source and by target id (ordinal keys).
        private readonly BucketIndex<CodeEdge> _edgesBySource;
        private readonly BucketIndex<CodeEdge> _edgesByTarget;

        // Nodes grouped by their normalized file path (FilePathMatcher.Normalize), keyed
        // OrdinalIgnoreCase to mirror FilePathMatcher. Nodes with a null/blank file path are
        // excluded (they can never match a path query).
        private readonly BucketIndex<CodeNode> _nodesByFilePath;

        /// <summary>Builds the snapshot of one shard's node and edge tables.</summary>
        public static GraphAdjacency Build(ICollection<CodeNode> nodeValues, ICollection<CodeEdge> edgeValues)
        {
            var nodes = new CodeNode[nodeValues.Count];
            nodeValues.CopyTo(nodes, 0);

            var byFilePath = new Dictionary<string, List<CodeNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
            {
                if (string.IsNullOrWhiteSpace(node.FilePath)) continue;
                var key = FilePathMatcher.Normalize(node.FilePath);
                if (!byFilePath.TryGetValue(key, out var bucket))
                    byFilePath[key] = bucket = new List<CodeNode>();
                bucket.Add(node);
            }

            var edges = new CodeEdge[edgeValues.Count];
            edgeValues.CopyTo(edges, 0);
            var bySource = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
            var byTarget = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
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

            return new GraphAdjacency(
                nodes,
                edges,
                new BucketIndex<CodeEdge>(bySource, EdgeById),
                new BucketIndex<CodeEdge>(byTarget, EdgeById),
                new BucketIndex<CodeNode>(byFilePath, ById));
        }

        /// <summary>
        /// Lists grouped by key, each sorted into id order on first read. A shard has a bucket per
        /// distinct source, target and file, so the buckets are slots of one array with one array
        /// of flags rather than an object each. A bucket's sort runs once, under a lock on its
        /// array; the array is handed out only after it, and never changes again.
        /// </summary>
        private sealed class BucketIndex<T>
        {
            private readonly FrozenDictionary<string, int> _slots;
            private readonly T[][] _buckets;
            private readonly bool[] _sorted;
            private readonly Comparison<T> _comparison;

            public BucketIndex(Dictionary<string, List<T>> groups, Comparison<T> comparison)
            {
                _buckets = new T[groups.Count][];
                _sorted = new bool[groups.Count];
                var slots = new KeyValuePair<string, int>[groups.Count];
                var slot = 0;
                foreach (var (key, items) in groups)
                {
                    _buckets[slot] = items.ToArray();
                    _sorted[slot] = items.Count < 2;
                    slots[slot] = KeyValuePair.Create(key, slot);
                    slot++;
                }
                _slots = slots.ToFrozenDictionary(groups.Comparer);
                _comparison = comparison;
            }

            /// <summary>Every key with its slot, in the frozen dictionary's (unspecified) order.</summary>
            public FrozenDictionary<string, int> Slots => _slots;

            public bool TryGetValue(string key, out T[] items)
            {
                if (_slots.TryGetValue(key, out var slot))
                {
                    items = Sorted(slot);
                    return true;
                }
                items = null!;
                return false;
            }

            public T[] Sorted(int slot)
            {
                var items = _buckets[slot];
                if (!Volatile.Read(ref _sorted[slot]))
                {
                    lock (items)
                    {
                        if (!_sorted[slot])
                        {
                            Array.Sort(items, _comparison);
                            Volatile.Write(ref _sorted[slot], true);
                        }
                    }
                }
                return items;
            }
        }

        /// <summary>
        /// One list (all nodes, all edges) sorted into id order on first read. The sort runs once,
        /// under a lock on its array; the array is handed out only after it, and never changes again.
        /// </summary>
        private sealed class SortedBucket<T>
        {
            private readonly T[] _items;
            private readonly Comparison<T> _comparison;
            private volatile bool _sorted;

            public SortedBucket(T[] items, Comparison<T> comparison)
            {
                _items = items;
                _comparison = comparison;
                _sorted = items.Length < 2;
            }

            public T[] Items
            {
                get
                {
                    if (!_sorted)
                    {
                        lock (_items)
                        {
                            if (!_sorted)
                            {
                                Array.Sort(_items, _comparison);
                                _sorted = true;
                            }
                        }
                    }
                    return _items;
                }
            }
        }

        // The bucket's own sorted array, typed IReadOnlyList to avoid a copy per read. Callers must
        // not mutate it (a stray .Sort()/Array.Clear() would corrupt the frozen snapshot and the Empty
        // singletons); the snapshot never changes it after handing it out.
        public IReadOnlyList<CodeEdge> GetEdgesBySource(string sourceId)
            => _edgesBySource.TryGetValue(sourceId, out var edges) ? edges : EmptyEdges;

        public IReadOnlyList<CodeEdge> GetEdgesByTarget(string targetId)
            => _edgesByTarget.TryGetValue(targetId, out var edges) ? edges : EmptyEdges;

        /// <summary>
        /// Resolves nodes for a requested path using the file-path buckets, replicating
        /// <see cref="FilePathMatcher.Matches"/>: a rooted request resolves to the single exact
        /// (case-insensitive, normalized) key; a relative request resolves to the exact key plus any
        /// key ending in "/" + the normalized request. Set-equivalent to a brute-force
        /// <see cref="FilePathMatcher.Matches"/> scan (validated in GraphAdjacencyTests). In id order.
        /// </summary>
        public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                return EmptyNodes;

            var normalizedRequested = FilePathMatcher.Normalize(requestedPath);
            var result = new List<CodeNode>();
            var buckets = 0;

            if (_nodesByFilePath.TryGetValue(normalizedRequested, out var exact))
            {
                result.AddRange(exact);
                buckets++;
            }

            if (!Path.IsPathRooted(requestedPath))
            {
                var suffix = "/" + normalizedRequested;
                foreach (var entry in _nodesByFilePath.Slots)
                {
                    // The exact key (case-insensitive) was already added above.
                    if (string.Equals(entry.Key, normalizedRequested, OrdinalIgnoreCase))
                        continue;
                    if (entry.Key.EndsWith(suffix, OrdinalIgnoreCase))
                    {
                        result.AddRange(_nodesByFilePath.Sorted(entry.Value));
                        buckets++;
                    }
                }
            }

            // Each bucket is in id order; several buckets (and the frozen dictionary's key order)
            // need one sort to merge.
            if (buckets > 1) result.Sort(ById);
            return result;
        }

        /// <summary>Nodes whose name equals or contains <paramref name="name"/> (ordinal, case-insensitive), in id order.</summary>
        public IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact)
        {
            var result = new List<CodeNode>();
            foreach (var node in _unorderedNodes)
            {
                if (exact
                    ? string.Equals(node.Name, name, OrdinalIgnoreCase)
                    : node.Name?.Contains(name, OrdinalIgnoreCase) ?? false)
                {
                    result.Add(node);
                }
            }
            if (result.Count > 1) result.Sort(ById);
            return result;
        }

        /// <summary>Distinct (ordinal) non-empty node file paths, ordered OrdinalIgnoreCase then Ordinal.</summary>
        public IReadOnlyList<string> FilePaths
        {
            get
            {
                var paths = Volatile.Read(ref _filePaths);
                if (paths is not null) return paths;
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                foreach (var node in _unorderedNodes)
                    if (!string.IsNullOrEmpty(node.FilePath)) distinct.Add(node.FilePath);
                paths = new ReadOnlyCollection<string>(SortPaths(distinct));
                Volatile.Write(ref _filePaths, paths);
                return paths;
            }
        }

        /// <summary>The order every file-path list of the store uses.</summary>
        internal static string[] SortPaths(IEnumerable<string> paths)
        {
            var array = new List<string>(paths).ToArray();
            Array.Sort(array, static (left, right) =>
            {
                var folded = string.Compare(left, right, OrdinalIgnoreCase);
                return folded != 0 ? folded : string.CompareOrdinal(left, right);
            });
            return array;
        }
    }
}
