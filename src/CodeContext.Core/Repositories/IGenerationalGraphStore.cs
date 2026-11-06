using CodeContext.Core.Models;
using CodeContext.Core.Repositories.InMemory;

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
        IReadOnlyCollection<string>? ReplacesFiles)
    {
        /// <summary>
        /// What produced the scoped shard, recorded by the store in the same commit as its facts (and
        /// staged and rolled back with them). Null leaves the shard's annotation as it is.
        /// </summary>
        internal ShardAnnotation? Annotation { get; init; }
    }

    /// <summary>
    /// What the store keeps beside one (parser, workspace) shard for the snapshot writer: the parser
    /// version of the commit that last carried an annotation for it, the coverage list last reported for
    /// it, and the fingerprints of the commit that last carried an annotation. Immutable; replaced as a
    /// whole. A null <see cref="Coverage"/> in a commit's annotation keeps the coverage already recorded, as
    /// <see cref="Workers.AnalysisCoverageRegistry"/> keeps its report when a delta carries none; a null
    /// <see cref="Fingerprints"/> clears them.
    ///
    /// The worker-state pairing (plan 3.5, rule 12 and phase 5): <see cref="State"/> names the worker state
    /// file whose contents describe exactly this shard's facts, and <see cref="Lineage"/> says which worker
    /// process produced the facts and through how many of its requests. Both hold only while the shard is
    /// still the <see cref="Anchor"/> they were recorded against: a foreign sweep, a prune, a legacy in-place
    /// write or a shard swap leaves the annotation alone (rule 12) but moves the shard off its anchor, which
    /// voids both. Only <see cref="ParserVersion"/>, <see cref="Coverage"/>, <see cref="Fingerprints"/> and the
    /// state file's name, id and hash are persisted (<see cref="SamePersisted"/>).
    /// </summary>
    internal sealed record ShardAnnotation(string? ParserVersion, IReadOnlyList<CodeContext.Parser.Protocol.ProjectCoverage>? Coverage)
    {
        /// <summary>The last fingerprints the producing commit carried, opaque; null when it carried none.</summary>
        public IReadOnlyList<CodeContext.Parser.Protocol.UnitFingerprint>? Fingerprints { get; init; }

        /// <summary>The worker state file paired with the shard at <see cref="Anchor"/>, or null.</summary>
        public WorkerStateBinding? State { get; init; }

        /// <summary>The worker process and request that produced the shard at <see cref="Anchor"/>, or null when unproven.</summary>
        public ShardLineage? Lineage { get; init; }

        /// <summary>The shard <see cref="State"/> and <see cref="Lineage"/> describe; null when neither is set.</summary>
        public ShardAnchor? Anchor { get; init; }

        /// <summary>Whether <paramref name="shard"/> is still the shard this annotation's state and lineage describe.</summary>
        public bool IsAnchoredTo(IGraphShard? shard) => Anchor is { } anchor && anchor.Matches(shard);

        /// <summary>The state binding, if it still describes <paramref name="shard"/>.</summary>
        public WorkerStateBinding? StateFor(IGraphShard? shard) => IsAnchoredTo(shard) ? State : null;

        /// <summary>The lineage, if it still describes <paramref name="shard"/>.</summary>
        public ShardLineage? LineageFor(IGraphShard? shard) => IsAnchoredTo(shard) ? Lineage : null;

        /// <summary>
        /// Equal in everything a snapshot of <paramref name="shard"/> would hold: parser version, coverage
        /// (entry by entry, by content), fingerprints (by content) and the state binding's name, id and hash
        /// (each only while it describes the shard). Lineage, anchor and byte counts are not persisted and not
        /// compared. Null equals only null.
        /// </summary>
        public static bool SamePersisted(ShardAnnotation? first, ShardAnnotation? second, IGraphShard? shard)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first is null || second is null) return false;
            return string.Equals(first.ParserVersion, second.ParserVersion, StringComparison.Ordinal)
                && InMemory.InMemoryDatabase.SameCoverage(first.Coverage, second.Coverage)
                && SameFingerprints(first.Fingerprints, second.Fingerprints)
                && WorkerStateBinding.SamePersisted(first.StateFor(shard), second.StateFor(shard));
        }

        internal static bool SameFingerprints(
            IReadOnlyList<CodeContext.Parser.Protocol.UnitFingerprint>? first,
            IReadOnlyList<CodeContext.Parser.Protocol.UnitFingerprint>? second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first is null || second is null || first.Count != second.Count) return false;
            for (var i = 0; i < first.Count; i++)
            {
                if (!string.Equals(first[i].Unit, second[i].Unit, StringComparison.Ordinal)
                    || !string.Equals(first[i].Value, second[i].Value, StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// A worker state file paired with a shard: its name inside the cache directory (never a path), the id the
    /// host gave the save, and the SHA-256 the worker reported, lowercase hex. <see cref="Bytes"/> is the file's
    /// length when it was recorded or loaded, for status only; it is not persisted.
    /// </summary>
    internal sealed record WorkerStateBinding(string FileName, string StateId, string Sha256, long Bytes = 0)
    {
        public static bool SamePersisted(WorkerStateBinding? first, WorkerStateBinding? second)
            => ReferenceEquals(first, second)
                || (first is not null && second is not null
                    && string.Equals(first.FileName, second.FileName, StringComparison.Ordinal)
                    && string.Equals(first.StateId, second.StateId, StringComparison.Ordinal)
                    && string.Equals(first.Sha256, second.Sha256, StringComparison.Ordinal));
    }

    /// <summary>
    /// Which worker process produced a shard's facts: <paramref name="Incarnation"/> identifies the process
    /// (unique in this host process), <paramref name="Request"/> is the ordinal of the mutating request whose
    /// commit produced them. <paramref name="Establishes"/> marks a commit that replaced the workspace or
    /// committed an accepted baseline, which starts a lineage; any other commit only extends one.
    /// <paramref name="BaselineStateId"/> is the state id of the baseline the request carried, if any: a
    /// file-scoped establishing commit starts a lineage only while the shard it applies to still carries the
    /// recorded state of that id (the store checks it under its commit lock).
    /// </summary>
    internal sealed record ShardLineage(long Incarnation, long Request, bool Establishes = false, string? BaselineStateId = null)
    {
        public bool Follows(ShardLineage previous) => previous.Incarnation == Incarnation && previous.Request == Request - 1;
    }

    /// <summary>A shard reference and its heap version (bumped only by legacy in-place writes) at one point.</summary>
    internal sealed record ShardAnchor(IGraphShard Shard, long HeapVersion)
    {
        public static ShardAnchor? Of(IGraphShard? shard) => shard is null ? null : new(shard, VersionOf(shard));

        public bool Matches(IGraphShard? shard) => shard is not null && ReferenceEquals(Shard, shard) && HeapVersion == VersionOf(shard);

        private static long VersionOf(IGraphShard shard) => (shard as InMemory.InMemoryDatabase.GraphState)?.Version ?? 0;
    }

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
