using CodeContext.Core.Repositories;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Workers;

/// <summary>
/// Where a worker's first index finds its baseline (plan 3.6): the shard-level half of the offer rule. Null
/// unless the shard is still the one a recorded state file describes, its parser version is
/// <paramref name="parserVersion"/>, its state id, hash and fingerprints are recorded, and the state file exists
/// in the cache directory under one of this code's names.
/// </summary>
internal interface IWorkerBaselineSource
{
    IndexBaseline? TryGetBaseline(string parserId, string workspaceId, string parserVersion);
}

/// <summary>Step 1 of a snapshot capture (plan 3.5): asks a worker to save its state.</summary>
internal interface IWorkerStateSaver
{
    /// <summary>
    /// Sends <c>workspace/saveState</c> when the parser's worker process is running, declared
    /// <c>persistentState</c>, and is the process <paramref name="lineage"/> names with no mutating request
    /// since the one it names (<see cref="WorkerStatePairing.Holds"/>). Returns the worker's result, or null
    /// when nothing was sent, the request failed, or <paramref name="ct"/> fired first (the request is then
    /// cancelled in the background and its late result ignored). Never throws for a worker's failure.
    /// </summary>
    Task<SaveStateResult?> SaveStateAsync(string parserId, ShardLineage lineage, SaveStateParams parameters, CancellationToken ct);
}

/// <summary>
/// The pairing proof between a worker's saved state and the host's shard (plan phase 5). A worker process may
/// save state for a shard, and the state may be recorded with it, only when the shard's facts are exactly what
/// that process has emitted:
/// <list type="number">
/// <item>the shard's lineage names that process, so the facts were established by it: a published workspace
/// replacement, or a published commit of a baseline it accepted, applied to a shard that still carried the recorded
/// state of the offered id (<see cref="ShardLineage.Establishes"/>, <see cref="ShardLineage.BaselineStateId"/>); and
/// every later commit of that process extended the lineage one request at a time;</item>
/// <item>the lineage's request is the last mutating request the process was sent, so every request it has
/// completed since has been published, in order (a request that failed, was cancelled, or whose commit was
/// refused, and a commit rolled back with a reconciliation, leave a gap); and</item>
/// <item>the shard is still the reference and heap version the lineage was recorded against, so no foreign
/// sweep, prune, legacy in-place write or other commit changed it since.</item>
/// </list>
/// A worker restart is a new incarnation, and a shard loaded from a snapshot has no lineage, so neither pairs
/// until that process establishes the shard again.
/// </summary>
internal static class WorkerStatePairing
{
    public static bool Holds(ShardLineage? lineage, long incarnation, long lastRequest, bool live, bool declaresPersistentState)
        => lineage is not null
            && live
            && declaresPersistentState
            && lineage.Incarnation == incarnation
            && lineage.Request == lastRequest;
}
