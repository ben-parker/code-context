using CodeContext.Core.Models;

namespace CodeContext.Core.Repositories
{
    /// <summary>
    /// Identifies the facts a scoped generation commit replaces. Ownership is the
    /// (parser, workspace) pair stamped onto every fact's metadata by the applier, so the
    /// store routes and partitions by <paramref name="ParserId"/>/<paramref name="WorkspaceId"/>
    /// instead of rediscovering the scope with a predicate. When <paramref name="ReplacesFiles"/>
    /// is null the whole (parser, workspace) shard is replaced; otherwise only facts whose
    /// file path is in the set are replaced and the rest of the shard is carried over.
    /// </summary>
    public sealed record CommitScope(
        string ParserId,
        string WorkspaceId,
        IReadOnlyCollection<string>? ReplacesFiles);

    /// <summary>
    /// Typed, generation-aware graph commits. Implemented by the in-memory store; the
    /// writer side (GraphUpdateService via the coordinator) prefers this over the legacy
    /// JSON reconcile so that JSON stays a process-boundary format only.
    /// </summary>
    public interface IGenerationalGraphStore
    {
        long LastCommittedGeneration { get; }

        /// <summary>
        /// Stages subsequent scoped generation commits and pruning behind the current
        /// reader snapshot. Commit publishes the combined reconciliation in one swap;
        /// rollback leaves the previous complete graph untouched.
        /// </summary>
        void BeginReconciliation();
        void CommitReconciliation();
        void RollbackReconciliation();

        /// <summary>
        /// Atomically replaces part (or all) of the graph with a new generation.
        /// Readers observe either the previous complete snapshot or the new one, never
        /// an intermediate state. Returns false when <paramref name="generation"/> is
        /// not newer than the last committed generation (stale generations cannot commit).
        /// </summary>
        /// <param name="scope">
        /// The (parser, workspace) facts this delta replaces (optionally narrowed to a set
        /// of files). Facts owned by other (parser, workspace) pairs — and their edges — are
        /// carried over untouched. Null replaces the entire graph.
        /// </param>
        Task<bool> TryCommitGenerationAsync(
            long generation,
            IReadOnlyList<CodeNode> nodes,
            IReadOnlyList<CodeEdge> edges,
            CommitScope? scope = null,
            CancellationToken ct = default);

        /// <summary>
        /// Removes nodes (and edges touching them) whose file no longer exists in the
        /// watched tree. Used at the end of a full rescan instead of clearing up front.
        /// </summary>
        Task<int> PruneFilesNotPresentAsync(IReadOnlyCollection<string> presentFilePaths, CancellationToken ct = default);

        /// <summary>Cheap aggregate counts over the committed graph.</summary>
        GraphStatistics GetStatistics();
    }
}
