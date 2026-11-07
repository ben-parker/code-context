using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Workers;

/// <summary>
/// Receives streamed <see cref="AnalysisDelta"/> chunks from worker supervisors and
/// commits completed requests into the active graph store.
/// <see cref="AnalysisDeltaApplier"/> provides atomic, generational commits.
/// </summary>
public interface IAnalysisDeltaSink
{
    /// <summary>
    /// Accepts one chunk. Returns false only when the chunk is stale or conflicts
    /// with another request; true means it was buffered or (for the final chunk)
    /// that the complete request committed.
    /// </summary>
    Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ApplyAsync(AnalysisDelta, CancellationToken)"/> with the worker process and request the chunk
    /// answers, which the commit records as the shard's lineage. A sink that does not record lineage ignores it.
    /// </summary>
    internal Task<bool> ApplyAsync(AnalysisDelta delta, DeltaOrigin? origin, CancellationToken ct) => ApplyAsync(delta, ct);
}

/// <summary>
/// The worker process (<paramref name="Incarnation"/>) and mutating request (<paramref name="Request"/>, numbered
/// by the supervisor) a delta answers, and the state id of the baseline that request carried
/// (<paramref name="OfferedStateId"/>, null when it carried none). Given only for a worker that declared
/// <c>persistentState</c>.
/// </summary>
internal sealed record DeltaOrigin(long Incarnation, long Request, string? OfferedStateId)
{
    public bool BaselineOffered => OfferedStateId is not null;
}
