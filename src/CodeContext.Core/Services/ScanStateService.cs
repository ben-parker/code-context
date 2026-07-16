namespace CodeContext.Core.Services;

public enum ScanPhase
{
    NotStarted,
    Scanning,
    Ready,
    Error
}

public interface IScanStateService
{
    ScanPhase Phase { get; }
    int FilesProcessed { get; }
    int FilesTotal { get; }
    DateTimeOffset? LastScanStartedAt { get; }
    TimeSpan? LastScanDuration { get; }
    string? LastError { get; }
    bool WatcherActive { get; set; }

    /// <summary>
    /// Monotonically increasing identifier of the current (or last) scan operation.
    /// A full-refresh request returns this so clients can wait for that specific
    /// generation to become ready. 0 = no scan has started yet.
    /// </summary>
    long OperationId { get; }

    /// <summary>Atomically transitions to Scanning; false when a scan is already running.</summary>
    bool TryBeginScan();

    /// <summary>Marks the scan Ready if it is still Scanning (an Error outcome is preserved).</summary>
    void CompleteScan();

    /// <summary>Marks the whole scan failed.</summary>
    void FailScan(string error);
}

/// <summary>
/// Singleton scan/readiness state, fed by scan progress reports and surfaced through /api/status
/// so clients can distinguish "host is up" from "index is ready".
/// </summary>
public class ScanStateService : IScanStateService, IScanProgressReporter
{
    private readonly object _lock = new();

    private ScanPhase _phase = ScanPhase.NotStarted;
    private int _filesProcessed;
    private int _filesTotal;
    private DateTimeOffset? _lastScanStartedAt;
    private TimeSpan? _lastScanDuration;
    private string? _lastError;
    private bool _watcherActive;
    private long _operationId;

    public ScanPhase Phase { get { lock (_lock) return _phase; } }
    public int FilesProcessed { get { lock (_lock) return _filesProcessed; } }
    public int FilesTotal { get { lock (_lock) return _filesTotal; } }
    public DateTimeOffset? LastScanStartedAt { get { lock (_lock) return _lastScanStartedAt; } }
    public TimeSpan? LastScanDuration { get { lock (_lock) return _lastScanDuration; } }
    public string? LastError { get { lock (_lock) return _lastError; } }
    public long OperationId { get { lock (_lock) return _operationId; } }

    public bool WatcherActive
    {
        get { lock (_lock) return _watcherActive; }
        set { lock (_lock) _watcherActive = value; }
    }

    public bool TryBeginScan()
    {
        lock (_lock)
        {
            if (_phase == ScanPhase.Scanning) return false;
            _phase = ScanPhase.Scanning;
            _operationId++;
            _filesProcessed = 0;
            _filesTotal = 0;
            _lastError = null;
            _lastScanDuration = null;
            _lastScanStartedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void CompleteScan()
    {
        lock (_lock)
        {
            if (_phase != ScanPhase.Scanning) return;
            _phase = ScanPhase.Ready;
            _lastScanDuration ??= _lastScanStartedAt is { } started
                ? DateTimeOffset.UtcNow - started
                : TimeSpan.Zero;
        }
    }

    public void FailScan(string error)
    {
        lock (_lock)
        {
            _phase = ScanPhase.Error;
            _lastError = error;
        }
    }

    public void ReportProgress(int processed, int total, string currentFile)
    {
        lock (_lock)
        {
            _filesProcessed = processed;
            _filesTotal = total;
        }
    }

    public void ReportComplete(int totalProcessed, TimeSpan elapsed)
    {
        lock (_lock)
        {
            _filesProcessed = totalProcessed;
            _lastScanDuration = elapsed;
            if (_phase == ScanPhase.Scanning) _phase = ScanPhase.Ready;
        }
    }

    public void ReportError(string filePath, string error)
    {
        // Any relevant file that could not be indexed makes the generation incomplete.
        // CompleteScan/ReportComplete preserve Error, so callers never observe a false
        // Ready result after a per-file failure.
        lock (_lock)
        {
            _phase = ScanPhase.Error;
            _lastError = $"{filePath}: {error}";
        }
    }
}
