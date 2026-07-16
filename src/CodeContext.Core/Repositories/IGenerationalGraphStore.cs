using CodeContext.Core.Models;

namespace CodeContext.Core.Repositories
{
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
        /// <param name="replacesScope">
        /// Predicate selecting the existing nodes this delta replaces (e.g. "all C# nodes").
        /// Nodes outside the scope — and edges originating from them — are carried over.
        /// Null replaces the entire graph.
        /// </param>
        Task<bool> TryCommitGenerationAsync(
            long generation,
            IReadOnlyList<CodeNode> nodes,
            IReadOnlyList<CodeEdge> edges,
            Func<CodeNode, bool>? replacesScope = null,
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
