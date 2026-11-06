using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>Everything <see cref="SnapshotService"/> is configured with; the defaults are plan section 3.5's.</summary>
    internal sealed record SnapshotServiceSettings
    {
        /// <summary>The longest any delay may be, so a timer or a timeout can always be set.</summary>
        public static readonly TimeSpan MaxDelayValue = TimeSpan.FromDays(1);

        private readonly TimeSpan _quietPeriod = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _maxDelay = TimeSpan.FromSeconds(120);
        private readonly TimeSpan _shutdownFlushTimeout = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _saveStateTimeout = TimeSpan.FromSeconds(5);

        public bool Enabled { get; init; }

        public required string RootPath { get; init; }

        public string WorkspaceId { get; init; } = CacheDirectory.HostWorkspaceId;

        /// <summary>The key file; null when there is no place for one (no user profile folder): no cache.</summary>
        public required string? KeyPath { get; init; }

        /// <summary>The host's informational version, recorded in every snapshot and required by every reader.</summary>
        public required string HostVersion { get; init; }

        public Guid InstanceId { get; init; }

        /// <summary>Time without a further change before a dirty store is written; 0 to 1 day.</summary>
        public TimeSpan QuietPeriod { get => _quietPeriod; init => _quietPeriod = Cap(value); }

        /// <summary>Time after the store first became dirty by which it is written; 0 to 1 day. When shorter than <see cref="QuietPeriod"/>, it wins.</summary>
        public TimeSpan MaxDelay { get => _maxDelay; init => _maxDelay = Cap(value); }

        public TimeSpan ShutdownFlushTimeout { get => _shutdownFlushTimeout; init => _shutdownFlushTimeout = Cap(value); }

        /// <summary>
        /// How long a capture waits for one worker's <c>workspace/saveState</c> (plan 3.5, step 1) before it writes
        /// that shard without state; 0 to 1 day. Set in code only, never from configuration.
        /// </summary>
        public TimeSpan SaveStateTimeout { get => _saveStateTimeout; init => _saveStateTimeout = Cap(value); }

        public long MaxFileLength { get; init; } = SnapshotFormat.MaxFileLength;

        /// <summary>Test seam: wraps the temp file's stream (a failing disk).</summary>
        public Func<Stream, Stream>? WrapStream { get; init; }

        /// <summary>Test seam: runs between the temp file's flush and its rename (a crash at that point).</summary>
        public Action<string>? BeforeRename { get; init; }

        /// <summary>Test seam: runs as the retire pass after a write starts (a failing directory listing).</summary>
        public Action? BeforeRetire { get; init; }

        /// <summary>Test seam: runs between opening the cache directory and taking the writer lock (a swap at that point).</summary>
        public Action<CacheDirectory>? BeforeLock { get; init; }

        /// <summary>
        /// Resolves the default key path (<c>~/.codecontext/cache.key</c>). Replaced by the test assembly with
        /// one that throws, so a test that turns the cache on without its own key path fails instead of
        /// touching the real profile.
        /// </summary>
        internal static Func<string?> DefaultKeyPath { get; set; } = SnapshotKey.DefaultPath;

        public static SnapshotServiceSettings From(CodeContextOptions options) => From(options, null);

        /// <summary><see cref="From(CodeContextOptions)"/> with the user profile folder given, for tests. No key path is resolved while the cache is off.</summary>
        internal static SnapshotServiceSettings From(CodeContextOptions options, string? userProfile) => new()
        {
            Enabled = options.CacheEnabled,
            RootPath = options.RootPath,
            KeyPath = !options.CacheEnabled ? null
                : !string.IsNullOrEmpty(options.CacheKeyPath) ? options.CacheKeyPath
                : userProfile is null ? DefaultKeyPath() : SnapshotKey.DefaultPath(userProfile),
            HostVersion = AssemblyVersionInfo.GetInformationalVersion(typeof(SnapshotService).Assembly, "0.0.0"),
            InstanceId = Guid.TryParse(options.InstanceId, out var id) ? id : Guid.Empty,
            QuietPeriod = TimeSpan.FromSeconds(Math.Max(0, options.SnapshotQuietSeconds)),
            MaxDelay = TimeSpan.FromSeconds(Math.Max(0, options.SnapshotMaxDelaySeconds)),
        };

        private static TimeSpan Cap(TimeSpan value)
            => value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelayValue ? MaxDelayValue : value;
    }

    /// <summary>What the service is doing for this root.</summary>
    internal enum SnapshotCacheMode
    {
        /// <summary>Not started yet.</summary>
        Starting,

        /// <summary>The option is off, or the cache cannot be used (see <see cref="SnapshotServiceStatus.Reason"/>).</summary>
        Disabled,

        /// <summary>Another instance holds the writer lock; this one never writes or deletes.</summary>
        ReadOnly,

        /// <summary>This instance holds the writer lock and writes snapshots.</summary>
        Writer,

        Stopped,
    }

    /// <summary>The service's state for phase 4's status surface; immutable, replaced as a whole.</summary>
    internal sealed record SnapshotServiceStatus(
        SnapshotCacheMode Mode,
        string? Reason = null,
        ulong? LastSequence = null,
        string? LastFileName = null,
        long? LastSizeBytes = null,
        DateTimeOffset? LastWriteAt = null,
        long? LastWriteMilliseconds = null,
        double? LastCaptureMilliseconds = null,
        string? LastWriteError = null,
        int WritesCompleted = 0,
        int WritesFailed = 0,
        long? LastWorkerStateBytes = null);

    /// <summary>
    /// Writes the snapshot lifecycle of plan section 3.5, write side.
    ///
    /// After every mutation command the coordinator calls <see cref="OnMutationCommandCompleted"/>. The
    /// store is dirty when what a snapshot would hold (<see cref="SnapshotView"/>: each shard reference,
    /// its annotation and its heap version) differs from what the newest snapshot this process wrote
    /// holds. A dirty store is captured after <see cref="SnapshotServiceSettings.QuietPeriod"/> without a
    /// further change, or <see cref="SnapshotServiceSettings.MaxDelay"/> after it first became dirty,
    /// whichever comes first. The capture is a command on the coordinator loop that only latches
    /// references (<see cref="Capture"/>), so it is a consistent cut of one committed generation with no
    /// scan, batch or reconciliation in flight; serializing and every file operation run on one
    /// background task, one write at a time.
    ///
    /// A write: temp file, flush to disk, close, rename to a new unique name, authenticate the result
    /// through <see cref="SnapshotFile.Open"/>, retire older snapshots. A crash at any point leaves the
    /// previous snapshot valid. A failure (disk full, read-only root, a capture over the size limit,
    /// every shard skipped) is logged once per cause, kept in <see cref="Status"/>, and retried after the
    /// next change; it never reaches the coordinator, the host or a reader.
    ///
    /// Only the holder of the writer lock writes or deletes anything. Every delay runs on the injected
    /// <see cref="TimeProvider"/>. When the option is off, start does nothing at all: no directory, no
    /// key, no timer, no observer. Once disabled for any reason, the service holds no lock and no
    /// directory for the rest of the run.
    /// </summary>
    internal sealed class SnapshotService : IHostedService, IDisposable
    {
        /// <summary>
        /// The largest usable sequence. A name carries ten digits, but a name above this is never a
        /// candidate, never raises the counter and is deleted when it can be, so a planted name near the
        /// ten-digit limit cannot exhaust the counter.
        /// </summary>
        internal const ulong MaxSequence = 999_999_999;

        /// <summary>
        /// At start, at most this many candidates may be refused at or after authentication (each such
        /// refusal costs a full read and hash of the file); the rest are not tried. Refusals before
        /// hashing cost nothing and are not counted. Someone who can plant files can therefore make the
        /// cache start cold (deny it), but never make it keep a file that does not authenticate.
        /// </summary>
        internal const int MaxCostlyRefusals = 3;

        private readonly IndexCoordinator _coordinator;
        private readonly InMemoryDatabase? _database;
        private readonly IScanStateService _scanState;
        private readonly SnapshotServiceSettings _settings;
        private readonly SnapshotStartup _startup;
        private readonly TimeProvider _time;
        private readonly ILogger<SnapshotService> _logger;
        private readonly IWorkerStateSaver? _workers;

        private readonly object _gate = new();
        private CacheDirectory? _directory;
        private FileStream? _lock;
        private byte[]? _key;
        private SnapshotHostMeta? _host;
        private ITimer? _timer;
        private ulong _nextSequence = 1;

        // Guarded by _gate.
        private SnapshotView _written = SnapshotView.Empty;
        private SnapshotView? _inFlight;
        private SnapshotView _lastSeen = SnapshotView.Empty;
        private DateTimeOffset _lastChangeAt;
        private DateTimeOffset? _dirtySince;
        private bool _captureRequested;
        // Completed once the requested capture has been handled: written, kept for the final capture, or dropped.
        private TaskCompletionSource _captureHandled = CompletedSource();
        private bool _writing;
        private int _writesInProgress;
        private bool _waitForChange;
        private bool _stopping;
        private Task _writeTask = Task.CompletedTask;
        private string? _lastLoggedFailure;
        private readonly HashSet<string> _loggedSkips = new(StringComparer.Ordinal);
        private SnapshotServiceStatus _status = new(SnapshotCacheMode.Starting);

        public SnapshotService(
            IndexCoordinator coordinator,
            InMemoryDatabase? database,
            IScanStateService scanState,
            SnapshotServiceSettings settings,
            SnapshotStartup startup,
            TimeProvider time,
            ILogger<SnapshotService> logger,
            CacheState? cacheState = null,
            IWorkerStateSaver? workers = null)
        {
            _coordinator = coordinator;
            _database = database;
            _scanState = scanState;
            _settings = settings;
            _startup = startup;
            _time = time;
            _logger = logger;
            _workers = workers;
            cacheState?.AttachWriter(() => Status);
        }

        public SnapshotServiceStatus Status
        {
            get { lock (_gate) return _status; }
        }

        /// <summary>No capture requested and no write in flight.</summary>
        internal bool IsIdle
        {
            get { lock (_gate) return !_captureRequested && !_writing; }
        }

        /// <summary>A capture was requested from the coordinator and its result not yet handled. For tests.</summary>
        internal bool IsCaptureRequested
        {
            get { lock (_gate) return _captureRequested; }
        }

        /// <summary>Writes started and not yet finished; never more than one. For tests.</summary>
        internal int WritesInProgress
        {
            get { lock (_gate) return _writesInProgress; }
        }

        /// <summary>The newest write's task, for tests and for shutdown.</summary>
        internal Task CurrentWrite
        {
            get { lock (_gate) return _writeTask; }
        }

        /// <summary>
        /// Takes over from the coordinator's start-up step (<see cref="SnapshotStartup"/>), now or once it has
        /// run: a writer's key, directory, lock and next sequence, and the view of what was loaded, so that
        /// a start does not rewrite the snapshot it has just loaded.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_settings.Enabled)
            {
                SetStatus(_status with { Mode = SnapshotCacheMode.Disabled, Reason = "the cache is turned off" });
                return Task.CompletedTask;
            }
            _startup.OnCompleted(Adopt);
            return Task.CompletedTask;
        }

        private void Adopt(SnapshotStartupResult result)
        {
            lock (_gate)
            {
                // Stopped while the step ran (a slow load): take nothing over; the step's Dispose releases the lock.
                // The status is set inside this same hold, so a stop cannot run between the check and the set.
                if (_stopping || _status.Mode == SnapshotCacheMode.Stopped) return;
                switch (result.Mode)
                {
                    case SnapshotCacheMode.ReadOnly:
                        _status = _status with { Mode = SnapshotCacheMode.ReadOnly, Reason = result.Reason };
                        return;
                    case SnapshotCacheMode.Writer:
                        break;
                    default:
                        // The start-up step has logged why, and holds nothing.
                        _status = _status with { Mode = SnapshotCacheMode.Disabled, Reason = result.Reason };
                        return;
                }
            }

            try
            {
                lock (_gate)
                {
                    // Stopped before the step ran: the step's own Dispose releases the lock.
                    if (_stopping) return;
                    _lock = _startup.TakeLock();
                    _directory = result.Directory;
                    _key = result.Key;
                    _host = result.Host;
                    _nextSequence = result.NextSequence;
                    _written = result.LoadedView ?? SnapshotView.Empty;
                    _lastSeen = _written;
                }
                var timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                lock (_gate)
                {
                    // A stop that ran meanwhile wins: the mode is not set back to Writer after it.
                    if (_stopping)
                    {
                        timer.Dispose();
                        return;
                    }
                    _timer = timer;
                    _status = _status with { Mode = SnapshotCacheMode.Writer, Reason = null };
                }
                _logger.LogInformation("Snapshot cache enabled: writing snapshots to {Directory}.", result.Directory!.WorkspacePath);

                _coordinator.SetMutationObserver(OnMutationCommandCompleted);
                // The startup scan may have committed before the observer was attached.
                OnMutationCommandCompleted();
            }
            catch (Exception exception)
            {
                // Releases the lock if it was taken, so another instance is not refused for the rest of this run.
                Disable($"the cache could not be prepared: {exception.Message}");
            }
        }

        /// <summary>
        /// Re-checks the three directory levels (see <see cref="CacheDirectory.RecheckLevels"/> for what that
        /// proves on each platform); when one is no longer a plain directory, writing stops for this run.
        /// </summary>
        private bool LevelsStillPlain(CacheDirectory directory)
        {
            if (directory.RecheckLevels() is not { } refusal) return true;
            Disable(refusal);
            return false;
        }

        private static bool IsUsable(ulong sequence) => sequence is >= 1 and <= MaxSequence;

        /// <summary>Called on the coordinator loop after every mutation command. Cheap: O(shards), no I/O.</summary>
        internal void OnMutationCommandCompleted()
        {
            try
            {
                var view = Observe();
                lock (_gate)
                {
                    if (_status.Mode != SnapshotCacheMode.Writer || _stopping) return;
                    var now = _time.GetUtcNow();
                    if (!view.SameAs(_lastSeen))
                    {
                        _lastSeen = view;
                        _lastChangeAt = now;
                        _waitForChange = false;
                    }
                    EvaluateLocked(view, now);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Snapshot scheduling failed.");
            }
        }

        /// <summary>Arms the timer for a dirty store, or disarms it for a clean one.</summary>
        private void EvaluateLocked(SnapshotView view, DateTimeOffset now)
        {
            if (_waitForChange) return;
            if (view.SameAs(_inFlight ?? _written))
            {
                if (!_writing && !_captureRequested)
                {
                    _dirtySince = null;
                    _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
                return;
            }
            _dirtySince ??= now;
            var due = _lastChangeAt + _settings.QuietPeriod;
            var cap = _dirtySince.Value + _settings.MaxDelay;
            if (cap < due) due = cap;
            var delay = due - now;
            // Both delays are at most one day (settings), so the timer can always be set.
            _timer?.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }

        private void OnTimer()
        {
            try
            {
                lock (_gate)
                {
                    if (_status.Mode != SnapshotCacheMode.Writer || _stopping || _captureRequested) return;
                    // A write in flight finishes from its own capture; this one follows it (see FinishWrite).
                    if (_writing) return;
                    if (Observe().SameAs(_written))
                    {
                        _dirtySince = null;
                        return;
                    }
                    _captureRequested = true;
                    _captureHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _coordinator.RequestSnapshotCaptureAsync(CaptureAsync)
                    .ContinueWith(OnCaptured, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Snapshot scheduling failed.");
                // A stop must not wait for a capture that was never requested.
                lock (_gate) _captureHandled.TrySetResult();
            }
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }

        /// <summary>
        /// The capture of plan 3.5, run on the coordinator loop: step 1, then <see cref="Capture()"/> (step 2). For
        /// each shard whose annotation proves it holds exactly what a running worker process has emitted (its
        /// lineage, anchored to the published shard; see <see cref="WorkerStatePairing"/>) and that records
        /// fingerprints, the worker is asked to save its state to a new file, within
        /// <see cref="SnapshotServiceSettings.SaveStateTimeout"/>. A result that checks out is recorded with the shard
        /// (<see cref="InMemoryDatabase.TryRecordWorkerState"/>) before the store is latched, so the capture pairs
        /// them. A shard whose recorded state file still describes it keeps that file and is not saved again. Any
        /// failure leaves the shard without state, which costs its worker a full walk at the next start. Null,
        /// asking nothing, while a reconciliation is staged.
        /// </summary>
        internal async Task<SnapshotCapture?> CaptureAsync(CancellationToken ct)
        {
            var started = Stopwatch.GetTimestamp();
            if (_database is null || !_database.TryLatchCommitted(out var shards, out var annotations, out _)) return null;
            CacheDirectory? directory;
            ulong sequence;
            lock (_gate)
            {
                directory = _status.Mode == SnapshotCacheMode.Writer ? _directory : null;
                sequence = _nextSequence;
            }

            var newFiles = new List<string>();
            var recorded = new List<(ShardKey Key, WorkerStateBinding Binding)>();
            // One budget for all of this capture's saves (each still has its own limit), so several slow workers
            // cannot cost more than a final snapshot may take.
            using var budget = new CancellationTokenSource(_settings.ShutdownFlushTimeout, _time);
            using var saves = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            if (_workers is not null && directory is not null)
            {
                var checkedLevels = false;
                foreach (var (key, shard) in shards)
                {
                    if (key == ShardKey.Default || (shard.NodeCount == 0 && shard.EdgeCount == 0)) continue;
                    if (!annotations.TryGetValue(key, out var annotation) || annotation.StateFor(shard) is not null) continue;
                    if (annotation.LineageFor(shard) is not { } lineage || annotation.Fingerprints is null) continue;
                    var stateId = WorkerStateFile.NewStateId();
                    if (WorkerStateFile.FormatName(key.ParserId, sequence, stateId) is not { } name) continue;
                    // The chain is checked again before a worker is told to create anything in it.
                    if (!checkedLevels)
                    {
                        if (!LevelsStillPlain(directory)) break;
                        checkedLevels = true;
                    }

                    newFiles.Add(name);
                    var binding = await SaveStateAsync(directory, key, lineage, name, stateId, saves.Token).ConfigureAwait(false);
                    if (binding is null || !_database.TryRecordWorkerState(key, shard, annotation, binding))
                        directory.DeleteWorkerStateQuietly(name);
                    else
                        recorded.Add((key, binding));
                }
            }

            var capture = Capture(started);
            if (capture is null)
            {
                // Nothing will reference what was recorded: un-record it before its files go.
                foreach (var (key, binding) in recorded) _database.ClearWorkerState(key, binding);
                foreach (var name in newFiles) directory?.DeleteWorkerStateQuietly(name);
                return null;
            }
            return capture with { NewStateFiles = newFiles };
        }

        /// <summary>
        /// The capture of a graceful stop: as <see cref="CaptureAsync(CancellationToken)"/>, with its saves ended by the
        /// stop's own budget as well as by the coordinator. A capture that runs after the stop gave up asks nothing.
        /// </summary>
        private async Task<SnapshotCapture?> FinalCaptureAsync(CancellationToken coordinator, CancellationToken stop)
        {
            CancellationTokenSource both;
            try
            {
                both = CancellationTokenSource.CreateLinkedTokenSource(coordinator, stop);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            using (both)
            {
                return await CaptureAsync(both.Token).ConfigureAwait(false);
            }
        }

        /// <summary>Runs before a requested capture's result is handled. Only tests set it, to delay that handling.</summary>
        internal Action? BeforeCaptureHandled { get; set; }

        /// <summary>One worker's save, checked: the id echoed, a SHA-256, and a regular file of the reported length under the name given.</summary>
        private async Task<WorkerStateBinding?> SaveStateAsync(
            CacheDirectory directory, ShardKey key, ShardLineage lineage, string name, string stateId, CancellationToken ct)
        {
            using var timeout = new CancellationTokenSource(_settings.SaveStateTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
            SaveStateResult? result;
            try
            {
                result = await _workers!.SaveStateAsync(key.ParserId, lineage,
                    new SaveStateParams(key.WorkspaceId, directory.PathOf(name), stateId), linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Saving the worker state of shard {Shard} failed: {Message}.", key, exception.Message);
                return null;
            }
            if (result is null) return null;
            string? refusal = null;
            if (!result.Saved) refusal = "the worker saved nothing";
            else if (!string.Equals(result.StateId, stateId, StringComparison.Ordinal)) refusal = "the result names another state id";
            else if (!string.Equals(result.WorkspaceId, key.WorkspaceId, StringComparison.Ordinal)) refusal = "the result names another workspace";
            else if (!WorkerStateFile.IsSha256(result.Sha256)) refusal = "the result carries no lowercase hex SHA-256";
            else if (directory.ExistingWorkerStateFile(key.ParserId, name) is not { } file) refusal = "the state file is not there";
            else if (result.Bytes != file.Length) refusal = $"the state file is {file.Length} bytes, the worker reported {result.Bytes}";
            if (refusal is not null)
            {
                _logger.LogWarning("The worker state saved for shard {Shard} is not used: {Reason}.", key, refusal);
                return null;
            }
            return new WorkerStateBinding(name, stateId, result.Sha256!, result.Bytes!.Value);
        }

        /// <summary>
        /// The capture of plan 3.5, step 2, run on the coordinator loop: latches the published shard map,
        /// each shard's annotation (parser version, coverage) and the generation, from one hold of the
        /// store's commit lock. Null while a reconciliation is staged. No I/O, no serialization.
        /// </summary>
        internal SnapshotCapture? Capture() => Capture(Stopwatch.GetTimestamp());

        private SnapshotCapture? Capture(long started)
        {
            if (_database is null || !_database.TryLatchCommitted(out var shards, out var annotations, out var generation)) return null;
            return new SnapshotCapture(SnapshotView.Of(shards, annotations), generation, _time.GetUtcNow(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        private void OnCaptured(Task<SnapshotCapture?> task)
        {
            try
            {
                BeforeCaptureHandled?.Invoke();
                if (task.IsFaulted) _logger.LogError(task.Exception, "Snapshot capture failed.");
                var capture = task.IsCompletedSuccessfully ? task.Result : null;
                var written = false;
                TaskCompletionSource handled;
                lock (_gate) handled = _captureHandled;
                try
                {
                    lock (_gate)
                    {
                        _captureRequested = false;
                        if (_status.Mode == SnapshotCacheMode.Writer && _stopping)
                        {
                            // Stopping: what this capture recorded stays recorded and on disk. The final capture, which
                            // waits for this one, finds those state files paired with their unchanged shards and
                            // writes them without asking the workers again; with no final capture they are
                            // referenced by nothing, and the cleanup at the next start deletes them.
                            written = true;
                            return;
                        }
                        if (_status.Mode != SnapshotCacheMode.Writer) return;
                        // Null: a staged reconciliation (its commit is followed by a mutation notification,
                        // which re-arms), or a coordinator that has stopped or whose queue is full. Not
                        // re-armed here, which against a stopped coordinator would spin.
                        if (capture is null) return;
                        if (capture.View.SameAs(_written))
                        {
                            _dirtySince = null;
                            return;
                        }
                        StartWriteLocked(capture);
                        written = true;
                    }
                }
                finally
                {
                    try
                    {
                        if (!written) DropStateFiles(capture);
                    }
                    finally
                    {
                        handled.TrySetResult();
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Snapshot capture failed.");
            }
        }

        /// <summary>
        /// A capture that will not be written takes the state files it asked for with it. Each binding it recorded is
        /// un-recorded first (only while the store still holds that very binding), then the files are deleted, so the
        /// store never names a state file that is gone.
        /// </summary>
        private void DropStateFiles(SnapshotCapture? capture)
        {
            if (capture is null || capture.NewStateFiles.Count == 0) return;
            var names = new HashSet<string>(capture.NewStateFiles, StringComparer.Ordinal);
            foreach (var shard in capture.View.Shards)
                if (shard.State is { } state && names.Contains(state.FileName)) _database?.ClearWorkerState(shard.Key, state);
            CacheDirectory? directory;
            lock (_gate) directory = _directory;
            foreach (var name in capture.NewStateFiles) directory?.DeleteWorkerStateQuietly(name);
        }

        private Task StartWriteLocked(SnapshotCapture capture)
        {
            _inFlight = capture.View;
            _writing = true;
            _writesInProgress++;
            _dirtySince = null;
            return _writeTask = Task.Run(() => FinishWrite(capture, Write(capture)));
        }

        private enum WriteOutcomeKind { Written, Discarded, Failed, Disabled }

        private sealed record WriteOutcome(
            WriteOutcomeKind Kind, string? Cause = null, string? Message = null,
            ulong Sequence = 0, string? FileName = null, long Length = 0, long Milliseconds = 0);

        /// <summary>The background write of one capture. Never throws.</summary>
        private WriteOutcome Write(SnapshotCapture capture)
        {
            var started = Stopwatch.GetTimestamp();
            string? tempPath = null;
            try
            {
                CacheDirectory? directory;
                lock (_gate) directory = _directory;
                if (directory is null) return new WriteOutcome(WriteOutcomeKind.Disabled);

                var sequence = _nextSequence;
                if (sequence > MaxSequence)
                    return Failed("sequence", "the snapshot sequence would pass 999,999,999; delete the cache directory to start again");

                // The chain is checked again before anything is created in it (see CacheDirectory.RecheckLevels).
                if (!LevelsStillPlain(directory)) return new WriteOutcome(WriteOutcomeKind.Disabled);
                _nextSequence = sequence + 1;

                var name = SnapshotFile.FormatName(sequence, BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4)));
                var finalPath = directory.PathOf(name);
                tempPath = directory.PathOf(CacheDirectory.TempNameFor(name));

                var result = SnapshotWriter.WriteFile(tempPath, Input(capture, sequence), _key!, _settings.MaxFileLength, _settings.WrapStream);
                LogSkipped(result.Skipped);
                if (!result.IsWritten) return Failed("limit", $"nothing was written: {result.NotWritten}");
                if (result.Written.Count == 0 && capture.View.Shards.Count > 0)
                {
                    DeleteQuietly(tempPath);
                    return Failed("skipped", "nothing was written: every shard was skipped ("
                        + string.Join("; ", result.Skipped.Select(skip => $"{skip.Key}: {skip.Reason}")) + ")");
                }

                // A legacy in-place write changed a captured shard while it was serialized: the file may
                // mix two states, so it is discarded and the changed shard captured again.
                foreach (var shard in capture.View.Shards)
                {
                    if (shard.Shard is InMemoryDatabase.GraphState heap && heap.Version != shard.HeapVersion)
                    {
                        DeleteQuietly(tempPath);
                        return new WriteOutcome(WriteOutcomeKind.Discarded);
                    }
                }

                _settings.BeforeRename?.Invoke(tempPath);
                File.Move(tempPath, finalPath, overwrite: false);
                tempPath = null;

                var opened = SnapshotFile.Open(finalPath, _key!, Expectations());
                using (opened.File)
                {
                    if (opened.File is null)
                    {
                        DeleteQuietly(finalPath);
                        return Failed("verify", $"the written snapshot does not open: {opened.Rejection} {opened.Reason}");
                    }
                }

                var written = new WriteOutcome(WriteOutcomeKind.Written, Sequence: sequence, FileName: name, Length: result.Length,
                    Milliseconds: (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                // Checked again before deleting anything; the snapshot itself is written either way.
                if (LevelsStillPlain(directory)) TryRetireOlderThan(directory, sequence, capture);
                return written;
            }
            catch (Exception exception)
            {
                if (tempPath is not null) DeleteQuietly(tempPath);
                return Failed(exception.GetType().Name + ":" + exception.HResult, exception.Message);
            }
        }

        private static WriteOutcome Failed(string cause, string message) => new(WriteOutcomeKind.Failed, cause, message);

        private SnapshotWriteInput Input(SnapshotCapture capture, ulong sequence)
        {
            var info = new Dictionary<ShardKey, SnapshotShardInfo>();
            foreach (var shard in capture.View.Shards)
            {
                var coverage = shard.Annotation?.Coverage is not { } projects
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                        projects as List<ProjectCoverage> ?? projects.ToList(),
                        ParserProtocolJsonContext.Default.ListProjectCoverage);
                var fingerprints = shard.Annotation?.Fingerprints is not { } units
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                        units as List<UnitFingerprint> ?? units.ToList(),
                        ParserProtocolJsonContext.Default.ListUnitFingerprint);
                // The state file only while it still describes the captured shard.
                var state = shard.State;
                info[shard.Key] = new SnapshotShardInfo(
                    ParserVersion: shard.Annotation?.ParserVersion,
                    StateFileName: state?.FileName,
                    StateId: state?.StateId,
                    StateSha256: state?.Sha256,
                    Coverage: coverage,
                    Fingerprints: fingerprints);
            }
            return new SnapshotWriteInput
            {
                Shards = capture.View.Shards.Select(shard => KeyValuePair.Create(shard.Key, shard.Shard)),
                ShardInfo = info,
                Host = _host!,
                Sequence = sequence,
                StoreGeneration = (ulong)Math.Max(0, capture.StoreGeneration),
                CreatedUtcTicks = capture.CapturedAt.UtcTicks,
                InstanceId = _settings.InstanceId,
            };
        }

        /// <summary>
        /// Deletes every other snapshot and temp file with a usable sequence below <paramref name="sequence"/>,
        /// and every one of this code's names outside the usable range; and every worker state file the new
        /// snapshot does not reference, with every state temp file, so state files follow their snapshot. A file
        /// another process has open or mapped stays and is retried at the next write and the next start.
        /// </summary>
        private void TryRetireOlderThan(CacheDirectory directory, ulong sequence, SnapshotCapture capture)
        {
            try
            {
                _settings.BeforeRetire?.Invoke();
                var referenced = ReferencedStateFiles(capture.View);
                var busy = 0;
                foreach (var file in directory.EnumerateOwnFiles())
                {
                    if (file.Kind == CacheFileKind.WorkerState ? referenced.Contains(file.Name)
                        : file.Kind != CacheFileKind.WorkerStateTemp && IsUsable(file.Sequence) && file.Sequence >= sequence) continue;
                    if (!CacheDirectory.TryDelete(file)) busy++;
                }
                if (busy > 0) _logger.LogDebug("{Count} older snapshot file(s) are in use and will be deleted later.", busy);
            }
            catch (Exception exception)
            {
                // The new snapshot is written and verified either way; the retire runs again after the next write.
                _logger.LogDebug(exception, "Retiring older snapshots failed; retried at the next write.");
            }
        }

        private static HashSet<string> ReferencedStateFiles(SnapshotView view)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in view.Shards)
                if (shard.State is { } state) names.Add(state.FileName);
            return names;
        }

        /// <summary>The total length of the worker state files <paramref name="view"/> references; null when it references none.</summary>
        internal static long? WorkerStateBytes(SnapshotView view)
        {
            long? total = null;
            foreach (var shard in view.Shards)
                if (shard.State is { } state) total = (total ?? 0) + state.Bytes;
            return total;
        }

        private void FinishWrite(SnapshotCapture capture, WriteOutcome outcome)
        {
            var firstOfItsCause = false;
            try
            {
                lock (_gate)
                {
                    _writing = false;
                    _writesInProgress--;
                    _inFlight = null;
                    switch (outcome.Kind)
                    {
                        case WriteOutcomeKind.Written:
                            _written = capture.View;
                            _lastLoggedFailure = null;
                            _status = _status with
                            {
                                LastSequence = outcome.Sequence,
                                LastFileName = outcome.FileName,
                                LastSizeBytes = outcome.Length,
                                LastWriteAt = _time.GetUtcNow(),
                                LastWriteMilliseconds = outcome.Milliseconds,
                                LastCaptureMilliseconds = capture.CaptureMilliseconds,
                                LastWriteError = null,
                                WritesCompleted = _status.WritesCompleted + 1,
                                LastWorkerStateBytes = WorkerStateBytes(capture.View),
                            };
                            break;
                        case WriteOutcomeKind.Failed:
                            firstOfItsCause = outcome.Cause != _lastLoggedFailure;
                            _lastLoggedFailure = outcome.Cause;
                            _status = _status with
                            {
                                LastWriteError = outcome.Message,
                                WritesFailed = _status.WritesFailed + 1,
                            };
                            break;
                    }
                    if (!_stopping && _status.Mode == SnapshotCacheMode.Writer)
                    {
                        var view = Observe();
                        var now = _time.GetUtcNow();
                        if (outcome.Kind == WriteOutcomeKind.Failed && view.SameAs(capture.View))
                        {
                            // A failed write is retried after the next change, not in a loop against a full disk.
                            _waitForChange = true;
                            _dirtySince = null;
                        }
                        else
                        {
                            // A discarded file means a shard is being changed in place: wait for it to go
                            // quiet as for any other change, rather than retrying at once.
                            if (outcome.Kind == WriteOutcomeKind.Discarded) _lastChangeAt = now;
                            EvaluateLocked(view, now);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Snapshot bookkeeping failed.");
            }
            LogOutcome(outcome, firstOfItsCause);
        }

        private void LogOutcome(WriteOutcome outcome, bool firstOfItsCause)
        {
            switch (outcome.Kind)
            {
                case WriteOutcomeKind.Written:
                    _logger.LogInformation("Wrote snapshot {Name} ({Bytes} bytes, {Milliseconds} ms).",
                        outcome.FileName, outcome.Length, outcome.Milliseconds);
                    break;
                case WriteOutcomeKind.Discarded:
                    _logger.LogDebug("Discarded a snapshot: a shard changed in place while it was written.");
                    break;
                case WriteOutcomeKind.Failed when firstOfItsCause:
                    _logger.LogWarning("Snapshot not written: {Reason}. Serving and indexing are unaffected; it is retried after the next change.",
                        outcome.Message);
                    break;
                case WriteOutcomeKind.Failed:
                    _logger.LogDebug("Snapshot not written again: {Reason}.", outcome.Message);
                    break;
            }
        }

        private void LogSkipped(IReadOnlyList<SnapshotSkippedShard> skipped)
        {
            foreach (var skip in skipped)
            {
                bool first;
                lock (_gate) first = _loggedSkips.Add($"{skip.Key}\n{skip.Reason}");
                if (first) _logger.LogWarning("Shard {Shard} is not persisted: {Reason}.", skip.Key, skip.Reason);
            }
        }

        /// <summary>
        /// Plan 3.5, graceful shutdown: runs before the coordinator and the worker service stop. Waits for
        /// a capture already requested (which, once stopping, keeps the state files it recorded rather than
        /// deleting them) and a write in flight, then captures a dirty store once more and writes it, all within
        /// <see cref="SnapshotServiceSettings.ShutdownFlushTimeout"/>. Skipped while a scan runs: the store is
        /// then staged and the scan is about to be cancelled.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Task inFlight;
            Task requested;
            lock (_gate)
            {
                if (_status.Mode != SnapshotCacheMode.Writer)
                {
                    if (_status.Mode == SnapshotCacheMode.Starting)
                    {
                        _status = _status with { Mode = SnapshotCacheMode.Stopped };
                        _stopping = true;
                    }
                    return;
                }
                _stopping = true;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                inFlight = _writeTask;
                requested = _captureRequested ? _captureHandled.Task : Task.CompletedTask;
            }
            _coordinator.SetMutationObserver(null);

            using var timeout = new CancellationTokenSource(_settings.ShutdownFlushTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var stop = linked.Token;
            try
            {
                if (_scanState.Phase == ScanPhase.Scanning)
                {
                    // Checked first: a capture requested before the stop may be queued behind the scan's mutation, and
                    // waiting for it would hold the stop for the whole flush timeout.
                    _logger.LogInformation("Shutting down during a scan: no final snapshot.");
                    await inFlight.WaitAsync(stop).ConfigureAwait(false);
                    return;
                }

                // A capture already requested (its workers may be saving) is waited for as well as the write: it keeps
                // what it recorded for the final capture below, which then asks no worker to save again.
                await requested.WaitAsync(stop).ConfigureAwait(false);
                await inFlight.WaitAsync(stop).ConfigureAwait(false);
                if (_scanState.Phase == ScanPhase.Scanning)
                {
                    _logger.LogInformation("Shutting down during a scan: no final snapshot.");
                }
                else if (!Observe().SameAs(Written()))
                {
                    // The final capture's saves end with the stop's budget, not only with the coordinator.
                    var capture = await _coordinator.RequestSnapshotCaptureAsync(ct => FinalCaptureAsync(ct, stop))
                        .WaitAsync(stop).ConfigureAwait(false);
                    Task? write = null;
                    lock (_gate)
                    {
                        if (capture is not null && !capture.View.SameAs(_written) && _status.Mode == SnapshotCacheMode.Writer)
                            write = StartWriteLocked(capture);
                    }
                    if (write is null) DropStateFiles(capture);
                    if (write is not null) await write.WaitAsync(stop).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("The final snapshot was not written within {Seconds} s of shutdown.",
                    _settings.ShutdownFlushTimeout.TotalSeconds);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The final snapshot failed.");
            }
            finally
            {
                lock (_gate)
                {
                    if (_status.Mode == SnapshotCacheMode.Writer) _status = _status with { Mode = SnapshotCacheMode.Stopped };
                    // A write still running here (the flush timed out) keeps the lock only until Dispose,
                    // which releases it at once whether or not that write has finished: the write may then
                    // rename and retire without holding the lock. It touches only this code's names, and a
                    // later writer's start-up cleanup removes whatever it leaves.
                    if (_writeTask.IsCompleted) ReleaseLockLocked();
                }
            }
        }

        public void Dispose()
        {
            _coordinator.SetMutationObserver(null);
            lock (_gate)
            {
                _stopping = true;
                _timer?.Dispose();
                _timer = null;
                // Released even while a timed-out write still runs (see StopAsync).
                ReleaseLockLocked();
            }
        }

        private SnapshotView Written()
        {
            lock (_gate) return _written;
        }

        private SnapshotView Observe()
        {
            if (_database is null) return SnapshotView.Empty;
            var (shards, annotations) = _database.LatchPublished();
            return SnapshotView.Of(shards, annotations);
        }

        private SnapshotExpectations Expectations() => new(_host!.HostVersion, _host.RootPath, _host.PathsCaseInsensitive);

        /// <summary>
        /// Turns the cache off for the rest of this run: releases the writer lock, drops the directory and the
        /// timer, and detaches from the coordinator. Safe from any thread and more than once.
        /// </summary>
        private void Disable(string reason)
        {
            ITimer? timer;
            lock (_gate)
            {
                _status = _status with { Mode = SnapshotCacheMode.Disabled, Reason = reason };
                ReleaseLockLocked();
                _directory = null;
                timer = _timer;
                _timer = null;
            }
            timer?.Dispose();
            _coordinator.SetMutationObserver(null);
            _logger.LogWarning("Snapshot cache disabled for this run: {Reason}.", reason);
        }

        private void SetStatus(SnapshotServiceStatus status)
        {
            lock (_gate) _status = status;
        }

        private void ReleaseLockLocked()
        {
            _lock?.Dispose();
            _lock = null;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the next start's cleanup of temp files.
            }
        }
    }
}
