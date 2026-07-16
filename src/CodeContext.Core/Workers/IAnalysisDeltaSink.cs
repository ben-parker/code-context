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
}
