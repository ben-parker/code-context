using System.Threading.Channels;
using CodeContext.Core.Snapshots;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Services;

/// <summary>
/// Hosted service that owns all graph mutations. Watcher events, startup scans, full
/// refreshes, and single-file refreshes are commands on one bounded channel, processed
/// sequentially by a single reader loop. File changes are coalesced per path over a
/// quiet window (with a max-latency bound) and applied as one batch, so a burst of
/// events costs one C# reparse and a change to one path can never cancel another path's
/// pending work.
///
/// Maintenance commands (a snapshot capture) share the channel so they run between mutations,
/// never during one, but they are not indexing work: they do not make <see cref="IsBusy"/> true,
/// so they cannot extend the instance's idle lease.
/// </summary>
public sealed class IndexCoordinator : BackgroundService, IIndexCoordinator
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxBatchLatency = TimeSpan.FromSeconds(2);

    private abstract record Command;
    private sealed record FileChangedCommand(string Path, FileChangeType Type) : Command;
    private sealed record FullScanCommand : Command;
    private sealed record RefreshFileCommand(string Path, TaskCompletionSource Completion) : Command;

    /// <summary>Work that only reads the store between mutations and is not indexing.</summary>
    private abstract record MaintenanceCommand : Command
    {
        public abstract void Run();

        /// <summary>Completes a command that will never run (shutdown).</summary>
        public abstract void Abandon();
    }

    /// <summary>Latches a snapshot capture at a point where no scan, batch or reconciliation is in flight.</summary>
    private sealed record SnapshotCommand(Func<SnapshotCapture?> Capture, TaskCompletionSource<SnapshotCapture?> Completion)
        : MaintenanceCommand
    {
        public override void Run()
        {
            try
            {
                Completion.TrySetResult(Capture());
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
        }

        public override void Abandon() => Completion.TrySetResult(null);
    }

    private readonly Channel<Command> _commands = Channel.CreateBounded<Command>(
        new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly IGraphUpdateService _graphUpdateService;
    private readonly IScanStateService _scanState;
    private readonly IScanProgressReporter _progressReporter;
    private readonly CodeContextOptions _options;
    private readonly ILogger<IndexCoordinator> _logger;
    private readonly IRepositoryFileSelector _fileSelector;

    private int _busy;
    private int _queuedMaintenance;
    private Action? _mutationObserver;

    public IndexCoordinator(
        IGraphUpdateService graphUpdateService,
        IScanStateService scanState,
        IOptions<CodeContextOptions> options,
        ILoggerFactory loggerFactory,
        IRepositoryFileSelector? fileSelector = null)
    {
        _graphUpdateService = graphUpdateService;
        _scanState = scanState;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<IndexCoordinator>();
        _fileSelector = fileSelector ?? new RepositoryFileSelector(options);

        var consoleReporter = new ConsoleScanProgressReporter(
            loggerFactory.CreateLogger<ConsoleScanProgressReporter>());
        _progressReporter = scanState is IScanProgressReporter stateReporter
            ? new CompositeScanProgressReporter(consoleReporter, stateReporter)
            : consoleReporter;
    }

    public bool IsBusy => Volatile.Read(ref _busy) == 1
        || _commands.Reader.Count - Volatile.Read(ref _queuedMaintenance) > 0
        || _scanState.Phase == ScanPhase.Scanning;

    /// <summary>
    /// Registers the callback run on the coordinator loop after every mutation command (startup
    /// scan, full scan, file batch, single-file refresh), whether or not it changed anything. One
    /// observer; null detaches it. An exception from it is logged and never reaches the loop.
    /// </summary>
    internal void SetMutationObserver(Action? observer) => Volatile.Write(ref _mutationObserver, observer);

    /// <summary>
    /// Queues <paramref name="capture"/> to run on the coordinator loop between commands, and returns
    /// what it returned. A capture that arrives while a file batch is being coalesced runs after that
    /// batch is applied. Completes with null when the coordinator has stopped or its queue is full.
    /// Never makes <see cref="IsBusy"/> true.
    /// </summary>
    internal Task<SnapshotCapture?> RequestSnapshotCaptureAsync(Func<SnapshotCapture?> capture)
    {
        var command = new SnapshotCommand(
            capture, new TaskCompletionSource<SnapshotCapture?>(TaskCreationOptions.RunContinuationsAsynchronously));
        // Counted after it is queued: until then IsBusy counts it as work (errs toward busy), and a loop
        // that dequeues it first drives the count to -1 for an instant, which errs the same way.
        if (_commands.Writer.TryWrite(command)) Interlocked.Increment(ref _queuedMaintenance);
        else command.Abandon();
        return command.Completion.Task;
    }

    public async ValueTask NotifyFileChangedAsync(string fullPath, FileChangeType changeType, CancellationToken ct = default)
    {
        await _commands.Writer.WriteAsync(new FileChangedCommand(fullPath, changeType), ct);
    }

    public async Task<long?> TryRequestFullRescanAsync(CancellationToken ct = default)
    {
        if (!_scanState.TryBeginScan())
        {
            return null;
        }

        // OperationId was just advanced by TryBeginScan and cannot advance again until
        // this scan finishes, so reading it here is race-free.
        var operationId = _scanState.OperationId;
        try
        {
            await _commands.Writer.WriteAsync(new FullScanCommand(), ct);
        }
        catch (Exception ex)
        {
            // Cancellation or channel closure before enqueue must not leave status
            // permanently stuck in Scanning.
            _scanState.FailScan(ex is ChannelClosedException
                ? "Host is shutting down."
                : "Full rescan request was cancelled before it could be queued.");
            throw;
        }
        return operationId;
    }

    public async Task RefreshFileAsync(string path, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new RefreshFileCommand(path, completion), ct);
        await completion.Task.WaitAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.RootPath) || !Directory.Exists(_options.RootPath))
        {
            _logger.LogError("RootPath '{RootPath}' is not valid. Please check your configuration.", _options.RootPath);
            _scanState.FailScan($"RootPath '{_options.RootPath}' is not valid.");
            return;
        }

        try
        {
            // Startup scan: resumable, so an instance restarted over a warm metadata
            // store only re-processes what changed. Runs after the HTTP host has bound,
            // and inside the try so a shutdown mid-scan still drains queued refresh
            // waiters in the finally below.
            if (_scanState.TryBeginScan())
            {
                await RunScanAsync(resumable: true, stoppingToken);
                NotifyMutationObserver();
            }

            while (await _commands.Reader.WaitToReadAsync(stoppingToken))
            {
                if (!_commands.Reader.TryRead(out var command)) continue;
                command = await ProcessCommandAsync(command, stoppingToken);
                // ProcessCommandAsync may hand back a command it read while draining.
                while (command is not null)
                {
                    command = await ProcessCommandAsync(command, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // host shutdown
        }
        finally
        {
            _commands.Writer.TryComplete();
            // Fail any refresh callers still waiting so they don't hang on shutdown.
            while (_commands.Reader.TryRead(out var remaining))
            {
                if (remaining is RefreshFileCommand refresh)
                {
                    refresh.Completion.TrySetCanceled();
                }
                else if (remaining is MaintenanceCommand maintenance)
                {
                    Interlocked.Decrement(ref _queuedMaintenance);
                    maintenance.Abandon();
                }
            }
        }
    }

    /// <summary>Processes one command; returns a follow-up command if one was read while draining.</summary>
    private async Task<Command?> ProcessCommandAsync(Command command, CancellationToken ct)
    {
        if (command is MaintenanceCommand maintenance)
        {
            // Not indexing work: _busy stays as it is.
            Interlocked.Decrement(ref _queuedMaintenance);
            maintenance.Run();
            return null;
        }

        Volatile.Write(ref _busy, 1);
        var notified = false;
        try
        {
            switch (command)
            {
                case FileChangedCommand firstChange:
                    return await ProcessChangeBatchAsync(firstChange, ct);

                case FullScanCommand:
                    await RunScanAsync(resumable: false, ct);
                    return null;

                case RefreshFileCommand refresh:
                    try
                    {
                        if (_fileSelector.IsIgnoreFile(refresh.Path))
                        {
                            _fileSelector.Invalidate();
                            if (_scanState.TryBeginScan()) await RunScanAsync(resumable: false, ct);
                        }
                        else
                        {
                            await _graphUpdateService.ProcessFileChangesAsync(
                                [new FileChange(refresh.Path, FileChangeType.Changed)], ct);
                        }
                        // Before the caller is released, so the caller observes the observer's reaction.
                        notified = true;
                        NotifyMutationObserver();
                        refresh.Completion.TrySetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        refresh.Completion.TrySetCanceled(ct);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        refresh.Completion.TrySetException(ex);
                    }
                    return null;

                default:
                    return null;
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            if (!notified) NotifyMutationObserver();
        }
    }

    private void NotifyMutationObserver()
    {
        try
        {
            Volatile.Read(ref _mutationObserver)?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The mutation observer failed.");
        }
    }

    /// <summary>
    /// Coalesces file changes per path until the stream goes quiet (or the max-latency
    /// bound is hit), then applies them as a single batch. A non-change command read
    /// while draining ends the batch and is returned to the caller, except a maintenance
    /// command, which waits and runs once the batch is applied.
    /// </summary>
    private async Task<Command?> ProcessChangeBatchAsync(FileChangedCommand first, CancellationToken ct)
    {
        var pending = new Dictionary<string, FileChangeType>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        AddPending(pending, first);

        Command? interrupting = null;
        List<MaintenanceCommand>? deferred = null;
        var applied = false;
        try
        {
            interrupting = await CoalesceAndApplyAsync(pending, ct, maintenance => (deferred ??= []).Add(maintenance));
            applied = true;
        }
        finally
        {
            // A maintenance command read while coalescing sees the batch's result; on shutdown it is abandoned.
            if (deferred is not null)
            {
                foreach (var maintenance in deferred)
                {
                    if (applied) maintenance.Run();
                    else maintenance.Abandon();
                }
            }
        }
        return interrupting;
    }

    private async Task<Command?> CoalesceAndApplyAsync(
        Dictionary<string, FileChangeType> pending, CancellationToken ct, Action<MaintenanceCommand> defer)
    {
        Command? interrupting = null;
        var deadline = DateTimeOffset.UtcNow + MaxBatchLatency;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var wait = remaining < QuietWindow ? remaining : QuietWindow;

            var next = await TryReadWithinAsync(wait, ct);
            if (next is null) break; // quiet window elapsed

            if (next is FileChangedCommand change)
            {
                AddPending(pending, change);
            }
            else if (next is MaintenanceCommand maintenance)
            {
                // Maintenance does not end the batch; it runs once the batch is applied.
                Interlocked.Decrement(ref _queuedMaintenance);
                defer(maintenance);
            }
            else
            {
                interrupting = next;
                break;
            }
        }

        var batch = pending
            .Select(kvp => new FileChange(kvp.Key, kvp.Value))
            .ToList();

        if (batch.Count > 0)
        {
            _logger.LogInformation("Processing batch of {Count} file change(s).", batch.Count);
            try
            {
                if (batch.Any(change => _fileSelector.IsIgnoreFile(change.Path)))
                {
                    _fileSelector.Invalidate();
                    if (_scanState.TryBeginScan()) await RunScanAsync(resumable: false, ct);
                }
                else
                {
                    await _graphUpdateService.ProcessFileChangesAsync(batch, ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process file change batch.");
            }
        }

        return interrupting;
    }

    private static void AddPending(Dictionary<string, FileChangeType> pending, FileChangedCommand change)
    {
        pending[change.Path] = pending.TryGetValue(change.Path, out var existing)
            ? Coalesce(existing, change.Type)
            : change.Type;
    }

    private static FileChangeType Coalesce(FileChangeType existing, FileChangeType incoming) => (existing, incoming) switch
    {
        (_, FileChangeType.Deleted) => FileChangeType.Deleted,
        (FileChangeType.Deleted, FileChangeType.Created) => FileChangeType.Changed,
        (FileChangeType.Created, _) => FileChangeType.Created,
        _ => incoming,
    };

    private async Task<Command?> TryReadWithinAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_commands.Reader.TryRead(out var immediate)) return immediate;
        if (timeout <= TimeSpan.Zero) return null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            if (await _commands.Reader.WaitToReadAsync(cts.Token) && _commands.Reader.TryRead(out var command))
            {
                return command;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // quiet window elapsed
        }
        return null;
    }

    private async Task RunScanAsync(bool resumable, CancellationToken ct)
    {
        Volatile.Write(ref _busy, 1);
        try
        {
            if (resumable)
            {
                await _graphUpdateService.PerformResumableScanAsync(_options.RootPath, _progressReporter, ct);
            }
            else
            {
                await _graphUpdateService.PerformInitialScanAsync(_options.RootPath, _progressReporter, ct);
            }
            _scanState.CompleteScan();
        }
        catch (OperationCanceledException)
        {
            _scanState.FailScan("Scan cancelled by shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed.");
            _scanState.FailScan(ex.Message);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
