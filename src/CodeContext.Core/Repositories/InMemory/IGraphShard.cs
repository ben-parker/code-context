using System.Collections.Generic;

namespace CodeContext.Core.Repositories.InMemory
{
    /// <summary>
    /// One (parserId, workspaceId) shard of <see cref="InMemoryDatabase"/>. Implemented today by the
    /// heap shard (<see cref="InMemoryDatabase.GraphState"/>); a mapped shard implements the same
    /// members later, rule for rule, so every read and every commit gives the same answer whichever
    /// kind holds the facts.
    ///
    /// Read members return results in canonical order: ordinal by id
    /// (<see cref="System.StringComparer.Ordinal"/> over the UTF-16 form). The store concatenates
    /// shards in <see cref="InMemoryDatabase.ShardKey"/> order. Returned lists may be the shard's own
    /// backing arrays, typed read-only to avoid a copy per read: callers must not mutate them or cast
    /// them back to a mutable type. Once handed out, a list never changes; a shard does not modify a
    /// published array (a later version of the shard builds new ones).
    ///
    /// Commit members run under the store's commit lock, never mutate this shard, and return a new
    /// shard, or null when the shard would be unchanged (the caller keeps this reference).
    /// </summary>
    internal interface IGraphShard
    {
        // ---- Read members ----

        int NodeCount { get; }

        int EdgeCount { get; }

        bool TryGetNode(string id, out CodeNode? node);

        /// <summary>The shard's identifier map is exactly <c>{ node.Identifier -> node.Id }</c> over its nodes.</summary>
        bool TryGetNodeIdByIdentifier(string identifier, out string? id);

        bool TryGetEdge(string id, out CodeEdge? edge);

        /// <summary>
        /// Nodes whose name equals (<paramref name="exact"/>) or contains <paramref name="name"/>,
        /// ordinal case-insensitively, in id order.
        /// </summary>
        IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact);

        /// <summary>
        /// Nodes whose file path matches <paramref name="requestedPath"/> with the semantics of
        /// <see cref="Services.FilePathMatcher.Matches"/>, in id order.
        /// </summary>
        IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath);

        /// <summary>Edges whose source is <paramref name="id"/>, in edge id order.</summary>
        IReadOnlyList<CodeEdge> GetEdgesBySource(string id);

        /// <summary>Edges whose target is <paramref name="id"/>, in edge id order.</summary>
        IReadOnlyList<CodeEdge> GetEdgesByTarget(string id);

        /// <summary>Every node, in id order.</summary>
        IReadOnlyList<CodeNode> GetAllNodes();

        /// <summary>Every edge, in edge id order.</summary>
        IReadOnlyList<CodeEdge> GetAllEdges();

        /// <summary>
        /// The distinct (ordinal) non-empty file paths of the shard's nodes, ordered
        /// <see cref="System.StringComparer.OrdinalIgnoreCase"/> then <see cref="System.StringComparer.Ordinal"/>.
        /// </summary>
        IReadOnlyList<string> GetFilePaths();

        /// <summary>Adds this shard's per-type node and edge counts (empty or null types skipped).</summary>
        void AddStatistics(Dictionary<string, int> nodesByType, Dictionary<string, int> edgesByType);

        /// <summary>
        /// Ids of edges with at least one endpoint that is not a node of this shard: the only edges
        /// a removal in another shard can leave dangling. A superset of the sweep candidates.
        /// </summary>
        IReadOnlyCollection<string> GetCrossShardEdgeIds();

        // ---- Commit members ----

        /// <summary>
        /// A scoped commit into this shard. <paramref name="replaceFiles"/> null replaces every node of
        /// the shard; otherwise only nodes whose raw file path is in the set (compared by the set's own
        /// comparer; a null or empty path is never replaced). Carried edges survive unless their source
        /// was removed, or their target was removed and the delta does not re-supply it. Delta nodes and
        /// edges overwrite by id. A delta identifier already held by a different id, in this shard
        /// (after the delta so far) or in a <paramref name="foreignShards"/> member, throws
        /// <see cref="System.IO.InvalidDataException"/> before anything is published.
        /// Returns null, with both sets empty, when nothing is removed and the delta has no fact.
        /// </summary>
        IGraphShard? ApplyScoped(
            IReadOnlyList<CodeNode> nodes,
            IReadOnlyList<CodeEdge> edges,
            IReadOnlySet<string>? replaceFiles,
            IReadOnlyList<IGraphShard> foreignShards,
            out HashSet<string> removedIds,
            out HashSet<string> removedNotReestablished);

        /// <summary>
        /// Sweeps this shard after a commit to another shard: drops edges sourced at a removed id or
        /// targeting a removed-and-not-re-established id, drops facts whose id the delta re-supplied
        /// (the delta wins globally) and every edge touching such an evicted node.
        /// </summary>
        IGraphShard? WithoutForeignConflicts(
            IReadOnlySet<string> removedIds,
            IReadOnlySet<string> removedNotReestablished,
            IReadOnlySet<string> deltaNodeIds,
            IReadOnlySet<string> deltaEdgeIds);

        /// <summary>
        /// Adds to <paramref name="into"/> the id of every node whose non-empty file path is not in
        /// <paramref name="keepPaths"/>, except library symbols (<c>metadata.external == "true"</c>).
        /// </summary>
        void CollectNodesOutside(IReadOnlySet<string> keepPaths, HashSet<string> into);

        /// <summary>Drops the given nodes and every edge with an endpoint among them.</summary>
        IGraphShard? WithoutNodes(IReadOnlySet<string> removedIds);
    }
}
