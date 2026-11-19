namespace CodeContext.Core.Services;

/// <summary>
/// The sole writer-side entry point for indexing work. Startup scans, full refreshes,
/// single-file refreshes, and watcher change notifications all become commands processed
/// by one loop, so graph mutations are ordered and the parser state is never entered
/// concurrently.
/// </summary>
public interface IIndexCoordinator
{
    /// <summary>True while indexing work is queued or executing (used to hold off idle shutdown).</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Queues a raw file-change notification. Changes are coalesced per path over a short
    /// quiet window and processed as one batch. The bounded queue provides backpressure;
    /// unrelated paths are never lost to debouncing.
    /// </summary>
    ValueTask NotifyFileChangedAsync(string fullPath, FileChangeType changeType, CancellationToken ct = default);

    /// <summary>
    /// Queues a directory-level notification: <paramref name="directoryPath"/> was created, moved in, renamed
    /// or removed, or may have been a directory when it went. The coordinator reconciles what the index
    /// holds under it with what the selection policy now includes there, in the same coalesced batch as file
    /// changes; one notification covers the whole subtree, however many files it holds.
    /// </summary>
    ValueTask NotifySubtreeChangedAsync(string directoryPath, CancellationToken ct = default);

    /// <summary>
    /// Requests a full rescan. Returns the operation id observable through /api/status,
    /// or null when a scan is already running.
    /// </summary>
    Task<long?> TryRequestFullRescanAsync(CancellationToken ct = default);

    /// <summary>Re-indexes a single file and completes when it has been processed.</summary>
    Task RefreshFileAsync(string path, CancellationToken ct = default);
}
