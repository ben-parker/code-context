using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Workers;

/// <summary>
/// Receives streamed <see cref="AnalysisDelta"/> chunks from worker supervisors and
/// commits completed requests into the active graph backend.
/// <see cref="AnalysisDeltaApplier"/> is the atomic, generational implementation for
/// the default in-memory store; <see cref="JsonReconcileDeltaSink"/> adapts deltas to
/// the legacy JSON reconcile for backends without generation support (Kuzu, interim).
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
