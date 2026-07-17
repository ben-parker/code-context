using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using static System.StringComparison;
using CodeContext.Core.Models;
using CodeContext.Core.Services;

namespace CodeContext.Core.Repositories.InMemory
{
    /// <summary>
    /// Immutable, version-stamped adjacency snapshot of one committed <see cref="InMemoryDatabase"/>
    /// state. Built in a single O(N+E) pass by <see cref="InMemoryDatabase.GetAdjacency"/> and cached
    /// until the store's mutation version changes, this turns the repository read paths from full
    /// O(E)/O(N) table scans into O(degree) lookups (the hot path for every <c>ContextService</c> hop).
    ///
    /// Ordering contract: <see cref="Nodes"/>/<see cref="Edges"/> and every per-key array preserve the
    /// enumeration order of the underlying dictionaries at build time, so a consumer projecting these
    /// back into a list sees the same order the pre-index <c>Values.Where(...)</c> scans produced.
    /// </summary>
    internal sealed class GraphAdjacency
    {
        private static readonly CodeEdge[] EmptyEdges = Array.Empty<CodeEdge>();
        private static readonly CodeNode[] EmptyNodes = Array.Empty<CodeNode>();

        /// <summary>All nodes of the snapshot, in store enumeration order.</summary>
        public required IReadOnlyList<CodeNode> Nodes { get; init; }

        /// <summary>All edges of the snapshot, in store enumeration order.</summary>
        public required IReadOnlyList<CodeEdge> Edges { get; init; }

        /// <summary>Edges grouped by <see cref="CodeEdge.SourceId"/> (ordinal keys).</summary>
        public required FrozenDictionary<string, CodeEdge[]> EdgesBySource { get; init; }

        /// <summary>Edges grouped by <see cref="CodeEdge.TargetId"/> (ordinal keys).</summary>
        public required FrozenDictionary<string, CodeEdge[]> EdgesByTarget { get; init; }

        /// <summary>
        /// Nodes grouped by their normalized file path (<see cref="FilePathMatcher.Normalize"/>),
        /// keyed <see cref="StringComparer.OrdinalIgnoreCase"/> to mirror <see cref="FilePathMatcher"/>.
        /// Nodes with a null/blank file path are excluded (they can never match a path query).
        /// </summary>
        public required FrozenDictionary<string, CodeNode[]> NodesByFilePath { get; init; }

        public CodeEdge[] GetEdgesBySource(string sourceId)
            => EdgesBySource.TryGetValue(sourceId, out var edges) ? edges : EmptyEdges;

        public CodeEdge[] GetEdgesByTarget(string targetId)
            => EdgesByTarget.TryGetValue(targetId, out var edges) ? edges : EmptyEdges;

        /// <summary>
        /// Resolves nodes for a requested path using <see cref="NodesByFilePath"/>, replicating
        /// <see cref="FilePathMatcher.Matches"/>: a rooted request resolves to the single exact
        /// (case-insensitive, normalized) key; a relative request resolves to the exact key plus any
        /// key ending in "/" + the normalized request. Set-equivalent to a brute-force
        /// <see cref="FilePathMatcher.Matches"/> scan (validated in GraphAdjacencyTests).
        /// </summary>
        public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                return EmptyNodes;

            var normalizedRequested = FilePathMatcher.Normalize(requestedPath);
            var result = new List<CodeNode>();

            if (NodesByFilePath.TryGetValue(normalizedRequested, out var exact))
                result.AddRange(exact);

            if (!Path.IsPathRooted(requestedPath))
            {
                var suffix = "/" + normalizedRequested;
                foreach (var entry in NodesByFilePath)
                {
                    // The exact key (case-insensitive) was already added above.
                    if (string.Equals(entry.Key, normalizedRequested, OrdinalIgnoreCase))
                        continue;
                    if (entry.Key.EndsWith(suffix, OrdinalIgnoreCase))
                        result.AddRange(entry.Value);
                }
            }

            return result;
        }
    }
}
