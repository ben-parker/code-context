using System.Collections.Concurrent;

namespace CodeContext.Core.Workers;

/// <summary>
/// Per-parser/session states surfaced through /api/status. These are a compatibility
/// contract with the agent skill: empty query results are conclusive only when the
/// relevant session is Ready.
/// </summary>
public enum ParserSessionState
{
    /// <summary>No files under the root need this parser.</summary>
    NotNeeded,
    /// <summary>Process spawn/handshake (or in-process warm-up) in progress.</summary>
    Starting,
    /// <summary>A generation is being built.</summary>
    Indexing,
    /// <summary>The latest requested generation committed successfully.</summary>
    Ready,
    /// <summary>Prerequisites missing or protocol-incompatible; won't be retried.</summary>
    Unavailable,
    /// <summary>The last operation failed; may recover on a later request.</summary>
    Failed,
    /// <summary>Shut down deliberately.</summary>
    Stopped,
}

/// <summary>
/// A point-in-time view of one parser session. Process fields are null for
/// in-process parsers (which report here until their Phase 3/4 worker extraction).
/// </summary>
public sealed record ParserSessionSnapshot(
    string ParserId,
    string DisplayName,
    ParserSessionState State,
    string? Message = null,
    string? LastError = null,
    int? ProcessId = null,
    int RestartCount = 0,
    string? ParserVersion = null,
    int? ProtocolVersion = null)
{
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Single source of truth for parser session state. Process supervisors push full
/// snapshots; in-process parsers push simple state reports. Reads are cheap — the
/// agent skill polls status every 1-2s during indexing.
/// </summary>
public interface IParserSessionRegistry
{
    /// <summary>Upserts the session keyed by <see cref="ParserSessionSnapshot.ParserId"/>.
    /// A snapshot without an explicit <c>LastError</c> keeps the previous one so a
    /// later Ready report doesn't erase the most recent failure detail.</summary>
    void Report(ParserSessionSnapshot snapshot);

    IReadOnlyList<ParserSessionSnapshot> GetSnapshots();
}

public sealed class ParserSessionRegistry : IParserSessionRegistry
{
    private readonly ConcurrentDictionary<string, ParserSessionSnapshot> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Report(ParserSessionSnapshot snapshot)
    {
        _sessions.AddOrUpdate(
            snapshot.ParserId,
            snapshot,
            (_, previous) => snapshot.LastError is null && previous.LastError is not null
                ? snapshot with { LastError = previous.LastError }
                : snapshot);
    }

    public IReadOnlyList<ParserSessionSnapshot> GetSnapshots()
        => _sessions.Values.OrderBy(s => s.ParserId, StringComparer.OrdinalIgnoreCase).ToList();
}
