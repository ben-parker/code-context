using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>
/// Owns one language-worker child process: spawn, initialize handshake with protocol
/// version validation, request routing, crash detection with a bounded restart budget,
/// stderr capture into the host log, cooperative cancellation, and graceful shutdown
/// (shutdown request → stdin EOF → kill as last resort). Workers are private children:
/// they never open ports or register as CodeContext instances; stdin/stdout are the
/// only protocol pipes.
///
/// Public request methods are safe to call concurrently, but workspace mutations are
/// expected to arrive already ordered by the IndexCoordinator.
/// </summary>
public sealed class ParserProcessSupervisor : IAsyncDisposable
{
    private sealed class ActiveMutation
    {
        private readonly Channel<AnalysisProgress>? _progressUpdates;
        private readonly Task _progressDispatch;

        public ActiveMutation(
            string workspaceId,
            long generation,
            DeltaOrigin origin,
            HashSet<string>? expectedFiles = null,
            Action<AnalysisProgress>? progressHandler = null,
            Action<Exception>? progressErrorHandler = null)
        {
            WorkspaceId = workspaceId;
            Generation = generation;
            Origin = origin;
            ExpectedFiles = expectedFiles;
            _progressUpdates = progressHandler is null
                ? null
                : Channel.CreateBounded<AnalysisProgress>(new BoundedChannelOptions(16)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                });
            _progressDispatch = progressHandler is null
                ? Task.CompletedTask
                : DispatchProgressAsync(progressHandler, progressErrorHandler);
        }

        public string WorkspaceId { get; }
        public long Generation { get; }
        public DeltaOrigin Origin { get; }
        public HashSet<string>? ExpectedFiles { get; }
        public int LastFilesProcessed { get; set; } = -1;
        public bool SawTerminalProgress { get; set; }
        public int DeltasReceived { get; set; }
        public bool SawLastDelta { get; set; }
        public bool SawWorkspaceReplacement { get; set; }
        public bool SawFileScopedDelta { get; set; }

        public void PublishProgress(AnalysisProgress progress)
        {
            if (_progressUpdates is not null && !_progressUpdates.Writer.TryWrite(progress))
            {
                throw new InvalidOperationException("The progress observer queue is closed.");
            }
        }

        public async Task CompleteProgressAsync()
        {
            if (_progressUpdates is null) return;
            _progressUpdates.Writer.TryComplete();
            await _progressDispatch.ConfigureAwait(false);
        }

        private async Task DispatchProgressAsync(
            Action<AnalysisProgress> handler,
            Action<Exception>? errorHandler)
        {
            await foreach (var progress in _progressUpdates!.Reader.ReadAllAsync())
            {
                try
                {
                    handler(progress);
                }
                catch (Exception ex)
                {
                    try
                    {
                        errorHandler?.Invoke(ex);
                    }
                    catch
                    {
                        // Diagnostics must never turn an observer failure into a
                        // parser-protocol failure.
                    }
                }
            }
        }
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Process starts in this host process, across supervisors, so an incarnation id names one worker process.</summary>
    private static long s_incarnations;

    private readonly WorkerLaunchSpec _spec;
    private readonly ParserWorkerOptions _options;
    private readonly ILogger _logger;
    private readonly IParserSessionRegistry? _registry;
    private readonly CodeContextOptions _hostOptions;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, ActiveMutation> _activeMutations = new();

    private Process? _process;
    private JsonRpcConnection? _connection;
    private ParserWorkerClient? _client;
    private Task? _stderrPump;
    private InitializeResult? _initializeResult;
    private ParserSessionSnapshot _snapshot;
    private int _restartCount;
    private bool _restartPending;
    private bool _unavailable;
    private volatile bool _stopping;
    private volatile bool _disposed;
    private long _incarnation;
    private long _requests;
    private long _requestsThisIncarnation;

    /// <summary>
    /// Receives every <c>analysis/delta</c> notification, awaited inline on the
    /// connection's read loop so deltas complete before the request's response.
    /// </summary>
    public Func<AnalysisDelta, CancellationToken, Task<bool>>? DeltaHandler { get; set; }

    /// <summary>
    /// Like <see cref="DeltaHandler"/>, and used instead of it when set, with the worker process and request
    /// the delta answers; null for a worker that did not declare <c>persistentState</c>.
    /// </summary>
    internal Func<AnalysisDelta, DeltaOrigin?, CancellationToken, Task<bool>>? OriginDeltaHandler { get; set; }

    /// <summary>The current worker process (unique in this host process), 0 before the first start.</summary>
    internal long Incarnation => Interlocked.Read(ref _incarnation);

    /// <summary>
    /// The ordinal of the last mutating request sent (index or apply), across processes. A request that fails
    /// in any way advances it once more, so no later commit can follow on from it (<see cref="DeltaOrigin"/>).
    /// </summary>
    internal long LastRequest => Interlocked.Read(ref _requests);

    /// <summary>Whether a worker process is running, initialized and connected now.</summary>
    internal bool IsLive => _process is { HasExited: false } && _connection?.Completion is { IsCompleted: false } && _initializeResult is not null;

    /// <summary>The running process's handshake, or null when none is initialized.</summary>
    internal InitializeResult? Initialized => _initializeResult;

    /// <summary>Whether the running worker process declared <c>persistentState</c>.</summary>
    internal bool DeclaresPersistentState => IsLive && _initializeResult?.Capabilities.PersistentState == true;

    public ParserProcessSupervisor(
        WorkerLaunchSpec spec,
        ParserWorkerOptions options,
        ILogger<ParserProcessSupervisor> logger,
        IParserSessionRegistry? registry = null,
        CodeContextOptions? hostOptions = null)
    {
        _spec = spec;
        _options = options;
        _logger = logger;
        _registry = registry;
        _hostOptions = hostOptions ?? new CodeContextOptions();
        _snapshot = new ParserSessionSnapshot(spec.ParserId, spec.DisplayName, ParserSessionState.Starting);
    }

    public ParserSessionSnapshot Snapshot => _snapshot;

    private void Transition(ParserSessionState state, string? message = null, string? lastError = null)
    {
        _snapshot = new ParserSessionSnapshot(
            _spec.ParserId,
            _initializeResult?.DisplayName ?? _spec.DisplayName,
            state,
            message,
            lastError,
            ProcessId: _process is { HasExited: false } p ? p.Id : null,
            RestartCount: _restartCount,
            ParserVersion: _initializeResult?.ParserVersion,
            ProtocolVersion: _initializeResult?.ProtocolVersion);
        _registry?.Report(_snapshot);

        if (state is ParserSessionState.Failed or ParserSessionState.Unavailable)
        {
            _logger.LogWarning("[{ParserId}] session {State}: {Message}", _spec.ParserId, state, lastError ?? message);
        }
        else
        {
            _logger.LogInformation("[{ParserId}] session {State}{Message}", _spec.ParserId, state,
                message is null ? "" : $": {message}");
        }
    }

    /// <summary>
    /// Ensures a healthy, initialized worker, spawning (or respawning after a crash,
    /// within the restart budget) as needed. Throws
    /// <see cref="ParserWorkerUnavailableException"/> once the worker is written off.
    /// </summary>
    public async Task<InitializeResult> EnsureInitializedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_unavailable)
            {
                throw new ParserWorkerUnavailableException(
                    _snapshot.LastError ?? $"Worker '{_spec.ParserId}' is unavailable.");
            }
            if (_stopping)
            {
                throw new ParserWorkerUnavailableException($"Worker '{_spec.ParserId}' is stopped.");
            }

            var negotiatedMinimum = Math.Max(_options.MinProtocolVersion, _spec.MinProtocolVersion);
            var negotiatedMaximum = Math.Min(_options.MaxProtocolVersion, _spec.MaxProtocolVersion);
            if (negotiatedMinimum > negotiatedMaximum)
            {
                _unavailable = true;
                var reason =
                    $"Worker '{_spec.ParserId}' manifest supports protocol v{_spec.MinProtocolVersion}-v{_spec.MaxProtocolVersion}, " +
                    $"but the host supports v{_options.MinProtocolVersion}-v{_options.MaxProtocolVersion}.";
                Transition(ParserSessionState.Unavailable, lastError: reason);
                throw new ParserWorkerUnavailableException(reason);
            }

            // A completed read loop means the incarnation is dead even when the OS
            // hasn't flipped HasExited yet (e.g. right after a crash mid-request).
            var connectionAlive = _connection?.Completion is { IsCompleted: false };
            if (_process is { HasExited: false } && connectionAlive && _initializeResult is not null)
            {
                return _initializeResult;
            }

            if (_process is not null || _restartPending)
            {
                // Previous incarnation died. Restarts are counted against the budget.
                if (_process is not null)
                {
                    await CleanupProcessAsync(kill: true).ConfigureAwait(false);
                }
                _restartPending = false;
                _restartCount++;
                if (_restartCount > _options.MaxRestarts)
                {
                    _unavailable = true;
                    var giveUp = $"Worker '{_spec.ParserId}' failed {_restartCount} times; giving up.";
                    Transition(ParserSessionState.Unavailable, lastError: giveUp);
                    throw new ParserWorkerUnavailableException(giveUp);
                }
                _logger.LogWarning("[{ParserId}] restarting worker (attempt {Attempt}/{Max}).",
                    _spec.ParserId, _restartCount, _options.MaxRestarts);
            }

            try
            {
                return await StartAndInitializeAsync(ct).ConfigureAwait(false);
            }
            catch (ParserWorkerUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanupProcessAsync(kill: true).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupProcessAsync(kill: true).ConfigureAwait(false);
                _restartPending = true;
                Transition(ParserSessionState.Failed, lastError: ex.Message);
                throw new ParserWorkerFailedException(
                    $"Worker '{_spec.ParserId}' failed to start: {ex.Message}", ex);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for one worker: redirected stdio, no
    /// window, working directory from the spec. Environment overlays are applied on top
    /// of the inherited host environment — first <see cref="WorkerLaunchSpec.Environment"/>,
    /// then <see cref="ParserWorkerOptions.WorkerEnvironment"/> for this parser id (options
    /// win on key collision). Each var is upserted (never <c>Add</c>) because names may
    /// already be present in the inherited environment and are case-insensitive on Windows.
    /// Null/missing maps leave the inherited environment untouched. Extracted so the spawn
    /// wiring is unit-testable without launching a process.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(WorkerLaunchSpec spec, ParserWorkerOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (spec.Environment is { } specEnvironment)
        {
            foreach (var (key, value) in specEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }
        if (options.WorkerEnvironment is { } workerEnvironment
            && workerEnvironment.TryGetValue(spec.ParserId, out var parserEnvironment))
        {
            foreach (var (key, value) in parserEnvironment)
            {
                startInfo.Environment[key] = value;
            }
        }

        return startInfo;
    }

    private async Task<InitializeResult> StartAndInitializeAsync(CancellationToken ct)
    {
        Transition(ParserSessionState.Starting);

        var startInfo = BuildStartInfo(_spec, _options);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new ParserWorkerFailedException($"Process.Start returned null for '{_spec.FileName}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Most commonly a missing runtime (e.g. `node` not installed). Surface an
            // actionable message: it becomes the session's lastError in /api/status.
            throw new ParserWorkerFailedException(
                $"Could not start worker command '{_spec.FileName}' for parser '{_spec.ParserId}': {ex.Message} " +
                "Ensure the required runtime is installed and on PATH.", ex);
        }
        _process = process;
        _logger.LogInformation("[{ParserId}] started worker process {Pid} ({FileName}).",
            _spec.ParserId, process.Id, _spec.FileName);

        _stderrPump = PumpStderrAsync(process);

        var connection = new JsonRpcConnection(
            process.StandardOutput.BaseStream,
            process.StandardInput.BaseStream);
        connection.AddNotificationHandler(ParserProtocolMethods.AnalysisDeltaNotification, OnDeltaNotificationAsync);
        connection.AddNotificationHandler(ParserProtocolMethods.AnalysisProgressNotification, OnProgressNotificationAsync);
        _connection = connection;
        _client = new ParserWorkerClient(connection);

        // The read loop's failure mode decides the session's fate: EOF => crash,
        // framing/JSON garbage => protocol violation. Observed via MonitorConnectionAsync.
        var readLoop = connection.StartAsync();
        _ = MonitorConnectionAsync(process, readLoop);

        InitializeResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_options.InitializeTimeout);
            try
            {
                result = await _client.InitializeAsync(
                    new InitializeParams(
                        HostName: "codecontext",
                        HostVersion: typeof(ParserProcessSupervisor).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                        RootPath: _hostOptions.RootPath,
                        MinProtocolVersion: Math.Max(_options.MinProtocolVersion, _spec.MinProtocolVersion),
                        MaxProtocolVersion: Math.Min(_options.MaxProtocolVersion, _spec.MaxProtocolVersion)),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ParserWorkerFailedException(
                    $"Worker '{_spec.ParserId}' did not answer initialize within {_options.InitializeTimeout.TotalSeconds:0}s.");
            }
        }

        var minimumProtocolVersion = Math.Max(_options.MinProtocolVersion, _spec.MinProtocolVersion);
        var maximumProtocolVersion = Math.Min(_options.MaxProtocolVersion, _spec.MaxProtocolVersion);
        if (result.ProtocolVersion < minimumProtocolVersion ||
            result.ProtocolVersion > maximumProtocolVersion)
        {
            _unavailable = true;
            var reason =
                $"Worker '{_spec.ParserId}' speaks protocol v{result.ProtocolVersion}; host supports " +
                $"v{minimumProtocolVersion}-v{maximumProtocolVersion}. Update the worker or the host.";
            await CleanupProcessAsync(kill: true).ConfigureAwait(false);
            Transition(ParserSessionState.Unavailable, lastError: reason);
            throw new ParserWorkerUnavailableException(reason);
        }

        if (!string.Equals(result.ParserId, _spec.ParserId, StringComparison.Ordinal))
        {
            _unavailable = true;
            var reason =
                $"Worker manifest declares parser id '{_spec.ParserId}', but the initialize handshake returned '{result.ParserId}'.";
            await CleanupProcessAsync(kill: true).ConfigureAwait(false);
            Transition(ParserSessionState.Unavailable, lastError: reason);
            throw new ParserWorkerUnavailableException(reason);
        }

        if (result.SpanSemantics.LineBase is not (0 or 1)
            || result.SpanSemantics.ColumnBase is not (0 or 1))
        {
            _unavailable = true;
            var reason = $"Worker '{_spec.ParserId}' returned invalid source-span base semantics.";
            await CleanupProcessAsync(kill: true).ConfigureAwait(false);
            Transition(ParserSessionState.Unavailable, lastError: reason);
            throw new ParserWorkerUnavailableException(reason);
        }

        _initializeResult = result;
        Interlocked.Exchange(ref _incarnation, Interlocked.Increment(ref s_incarnations));
        Interlocked.Exchange(ref _requestsThisIncarnation, 0);
        Transition(ParserSessionState.Ready,
            $"initialized {result.DisplayName} {result.ParserVersion} (protocol v{result.ProtocolVersion})");
        return result;
    }

    private async Task OnDeltaNotificationAsync(JsonElement? paramsElement)
    {
        if (paramsElement is not { } element)
        {
            throw new ParserProtocolViolationException("analysis/delta notification carried no params.");
        }
        var delta = element.Deserialize(ParserProtocolJsonContext.Default.AnalysisDelta);
        if (delta is null)
        {
            throw new ParserProtocolViolationException("analysis/delta deserialized to null.");
        }

        var initialized = _initializeResult
            ?? throw new ParserProtocolViolationException("Worker emitted analysis/delta before initialization completed.");
        if (!string.Equals(delta.ParserId, initialized.ParserId, StringComparison.Ordinal)
            || !string.Equals(delta.ParserVersion, initialized.ParserVersion, StringComparison.Ordinal))
        {
            throw new ParserProtocolViolationException(
                $"analysis/delta identity '{delta.ParserId}' {delta.ParserVersion} does not match the initialized worker " +
                $"'{initialized.ParserId}' {initialized.ParserVersion}.");
        }
        if (!_activeMutations.TryGetValue(delta.RequestId, out var active))
        {
            throw new ParserProtocolViolationException(
                $"analysis/delta references unknown or completed request {delta.RequestId}.");
        }
        if (!string.Equals(delta.WorkspaceId, active.WorkspaceId, StringComparison.Ordinal)
            || delta.Generation != active.Generation)
        {
            throw new ParserProtocolViolationException(
                $"analysis/delta for request {delta.RequestId} reported workspace/generation " +
                $"'{delta.WorkspaceId}'/{delta.Generation}, expected '{active.WorkspaceId}'/{active.Generation}.");
        }
        if (active.SawLastDelta)
        {
            throw new ParserProtocolViolationException(
                $"analysis/delta arrived after the final chunk for request {delta.RequestId}.");
        }
        if (DeltaHandler is null && OriginDeltaHandler is null)
        {
            throw new ParserProtocolViolationException(
                $"Worker emitted analysis/delta for request {delta.RequestId}, but no graph-delta handler is configured.");
        }
        if (delta.Fingerprints is { } fingerprints)
        {
            if (!initialized.Capabilities.PersistentState)
            {
                // Opaque data nothing will hand back to a worker without the capability: not kept.
                delta = delta with { Fingerprints = null };
            }
            else if (fingerprints.Any(fingerprint => fingerprint?.Unit is null || fingerprint.Value is null))
            {
                throw new ParserProtocolViolationException(
                    $"analysis/delta for request {delta.RequestId} carried a fingerprint without a unit or a value.");
            }
        }

        var ownershipPrefix = $"{delta.ParserId}:{Uri.EscapeDataString(delta.WorkspaceId)}:";
        if (delta.Nodes.Any(node => string.IsNullOrWhiteSpace(node.Id)
                || !node.Id.StartsWith(ownershipPrefix, StringComparison.Ordinal))
            || delta.Edges.Any(edge => string.IsNullOrWhiteSpace(edge.Id)
                || !edge.Id.StartsWith(ownershipPrefix, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(edge.SourceId)
                || !edge.SourceId.StartsWith(ownershipPrefix, StringComparison.Ordinal)))
        {
            throw new ParserProtocolViolationException(
                $"Worker facts must be owned by prefix '{ownershipPrefix}' (node ids, edge ids, and edge sources).");
        }

        var normalizedDelta = NormalizeSpans(delta, initialized.SpanSemantics);
        var accepted = OriginDeltaHandler is { } withOrigin
            ? await withOrigin(normalizedDelta, initialized.Capabilities.PersistentState ? active.Origin : null, _shutdown.Token).ConfigureAwait(false)
            : await DeltaHandler!(normalizedDelta, _shutdown.Token).ConfigureAwait(false);
        if (!accepted)
        {
            throw new ParserProtocolViolationException(
                $"The host rejected analysis/delta generation {delta.Generation} for request {delta.RequestId}.");
        }
        active.DeltasReceived++;
        active.SawLastDelta = delta.IsLastForRequest;
        if (delta.ReplacesWorkspace) active.SawWorkspaceReplacement = true;
        else active.SawFileScopedDelta = true;
    }

    private Task OnProgressNotificationAsync(JsonElement? paramsElement)
    {
        if (paramsElement is not { } element)
        {
            throw new ParserProtocolViolationException("analysis/progress notification carried no params.");
        }
        var progress = element.Deserialize(ParserProtocolJsonContext.Default.AnalysisProgress)
            ?? throw new ParserProtocolViolationException("analysis/progress deserialized to null.");
        var initialized = _initializeResult
            ?? throw new ParserProtocolViolationException(
                "Worker emitted analysis/progress before initialization completed.");
        if (!string.Equals(progress.ParserId, initialized.ParserId, StringComparison.Ordinal)
            || !string.Equals(progress.ParserVersion, initialized.ParserVersion, StringComparison.Ordinal))
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress identity '{progress.ParserId}' {progress.ParserVersion} does not match " +
                $"the initialized worker '{initialized.ParserId}' {initialized.ParserVersion}.");
        }
        if (!_activeMutations.TryGetValue(progress.RequestId, out var active)
            || active.ExpectedFiles is null)
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress references unknown, completed, or non-index request {progress.RequestId}.");
        }
        if (!string.Equals(progress.WorkspaceId, active.WorkspaceId, StringComparison.Ordinal)
            || progress.Generation != active.Generation)
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress for request {progress.RequestId} reported workspace/generation " +
                $"'{progress.WorkspaceId}'/{progress.Generation}, expected " +
                $"'{active.WorkspaceId}'/{active.Generation}.");
        }

        var expectedTotal = active.ExpectedFiles.Count;
        if (progress.FilesTotal != expectedTotal
            || progress.FilesProcessed < 0
            || progress.FilesProcessed > progress.FilesTotal
            || progress.FilesProcessed < active.LastFilesProcessed)
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress for request {progress.RequestId} reported invalid or non-monotonic " +
                $"count {progress.FilesProcessed}/{progress.FilesTotal}; expected total {expectedTotal}.");
        }
        if (progress.FilesProcessed > 0
            && (string.IsNullOrEmpty(progress.CurrentFile)
                || !active.ExpectedFiles.Contains(progress.CurrentFile)))
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress for request {progress.RequestId} named a file outside the approved set.");
        }
        if (expectedTotal == 0 && progress.CurrentFile is not null)
        {
            throw new ParserProtocolViolationException(
                $"analysis/progress for empty request {progress.RequestId} must have a null currentFile.");
        }

        active.LastFilesProcessed = progress.FilesProcessed;
        active.SawTerminalProgress = progress.FilesProcessed == progress.FilesTotal;
        active.PublishProgress(progress);
        return Task.CompletedTask;
    }

    private static AnalysisDelta NormalizeSpans(AnalysisDelta delta, SpanSemantics semantics)
    {
        if (semantics.LineBase == 0 && semantics.ColumnBase == 0 && !semantics.EndIsInclusive)
        {
            return delta;
        }

        var nodes = delta.Nodes.Select(node => node with
        {
            StartLine = Math.Max(0, node.StartLine - semantics.LineBase),
            EndLine = Math.Max(0, node.EndLine - semantics.LineBase),
            StartColumn = Math.Max(0, node.StartColumn - semantics.ColumnBase),
            EndColumn = Math.Max(0, node.EndColumn - semantics.ColumnBase)
                + (semantics.EndIsInclusive ? 1 : 0),
        }).ToList();
        var edges = delta.Edges.Select(edge => edge with
        {
            Metadata = NormalizePositionMetadata(edge.Metadata, semantics),
        }).ToList();
        return delta with { Nodes = nodes, Edges = edges };
    }

    private static IReadOnlyDictionary<string, string>? NormalizePositionMetadata(
        IReadOnlyDictionary<string, string>? metadata, SpanSemantics semantics)
    {
        if (metadata is null) return null;
        var normalized = new Dictionary<string, string>(metadata);
        if (metadata.TryGetValue("line", out var line)
            && int.TryParse(line, out var lineNumber))
        {
            normalized["line"] = Math.Max(0, lineNumber - semantics.LineBase).ToString();
        }
        if (metadata.TryGetValue("column", out var column)
            && int.TryParse(column, out var columnNumber))
        {
            normalized["column"] = Math.Max(0, columnNumber - semantics.ColumnBase).ToString();
        }
        return normalized;
    }

    /// <summary>Marks the session Failed when the connection dies outside a deliberate stop.</summary>
    private async Task MonitorConnectionAsync(Process process, Task readLoop)
    {
        string failure;
        try
        {
            await readLoop.ConfigureAwait(false);
            failure = "Worker closed its protocol stream unexpectedly.";
        }
        catch (ParserProtocolViolationException ex)
        {
            failure = $"Worker produced malformed protocol output: {ex.Message}";
        }
        catch (Exception ex)
        {
            failure = $"Worker connection failed: {ex.Message}";
        }

        if (_stopping || _disposed || !ReferenceEquals(process, _process))
        {
            return;
        }

        var exitCode = await TryGetExitCodeAsync(process).ConfigureAwait(false);
        if (exitCode is { } code)
        {
            failure += $" Process exited with code {code}.";
        }
        else
        {
            // Malformed output with the process still alive: the conversation is
            // unrecoverable, so terminate the incarnation before reporting it.
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        _logger.LogError("[{ParserId}] {Failure}", _spec.ParserId, failure);
        _initializeResult = null;
        Transition(ParserSessionState.Failed, lastError: failure);
    }

    private static async Task<int?> TryGetExitCodeAsync(Process process)
    {
        try
        {
            using var brief = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await process.WaitForExitAsync(brief.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task PumpStderrAsync(Process process)
    {
        // Draining stderr continuously is what keeps a chatty worker from blocking on
        // a full pipe; every line lands in the host log with a parser prefix.
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length > 0)
                {
                    _logger.LogInformation("[{ParserId} stderr] {Line}", _spec.ParserId, line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed during shutdown.
        }
    }

    public async Task<OpenWorkspaceResult> OpenWorkspaceAsync(OpenWorkspaceParams parameters, CancellationToken ct = default)
    {
        var client = await GetReadyClientAsync(ct).ConfigureAwait(false);
        return await RunRequestAsync(() => client.OpenWorkspaceAsync(parameters, ct)).ConfigureAwait(false);
    }

    public async Task<IndexWorkspaceResult> IndexWorkspaceAsync(
        IndexWorkspaceParams parameters,
        CancellationToken ct = default,
        Action<AnalysisProgress>? progressHandler = null)
    {
        var expectedFiles = CreateExpectedFileSet(parameters.Files);
        var client = await GetReadyClientAsync(ct).ConfigureAwait(false);
        // A baseline goes only to a worker that declared the capability, and only on the first mutating request
        // of its process: anything the process did before would already have changed what the baseline describes.
        if (parameters.Baseline is not null
            && (_initializeResult?.Capabilities.PersistentState != true || Interlocked.Read(ref _requestsThisIncarnation) > 0))
        {
            parameters = parameters with { Baseline = null };
        }
        var origin = BeginMutation(parameters.Baseline?.StateId);
        Transition(ParserSessionState.Indexing, $"generation {parameters.Generation} ({parameters.Files.Count} files)");
        ActiveMutation? active = null;
        long requestId = 0;
        try
        {
            var result = await RunRequestAsync(() => client.IndexWorkspaceAsync(parameters, ct, id =>
            {
                requestId = id;
                active = new ActiveMutation(
                    parameters.WorkspaceId, parameters.Generation, origin, expectedFiles, progressHandler,
                    ex => _logger.LogWarning(ex,
                        "Progress observer failed for parser '{ParserId}', generation {Generation}.",
                        _spec.ParserId, parameters.Generation));
                if (!_activeMutations.TryAdd(id, active))
                {
                    throw new InvalidOperationException($"Request id {id} is already active.");
                }
            })).ConfigureAwait(false);
            ValidateMutationResult(
                parameters.WorkspaceId, parameters.Generation,
                result.WorkspaceId, result.Generation, result.DeltasEmitted, result.Complete,
                active, "workspace/index");
            ValidateBaselineResult(parameters, result, active!);
            TransitionAfterMutation($"generation {result.Generation} committed");
            return result;
        }
        catch
        {
            EndFailedMutation();
            throw;
        }
        finally
        {
            if (active is not null) await active.CompleteProgressAsync().ConfigureAwait(false);
            if (requestId != 0) _activeMutations.TryRemove(requestId, out _);
        }
    }

    /// <summary>Numbers a mutating request about to be sent to the running process.</summary>
    private DeltaOrigin BeginMutation(string? offeredStateId)
    {
        Interlocked.Increment(ref _requestsThisIncarnation);
        return new DeltaOrigin(Incarnation, Interlocked.Increment(ref _requests), offeredStateId);
    }

    /// <summary>
    /// A mutating request failed after it was numbered (an error, a cancel, a crash, a commit the host refused
    /// or a violation found in its result): the worker may hold state the host does not, so the numbering
    /// skips one and no later commit of this process can extend the lineage of what it committed before.
    /// </summary>
    private void EndFailedMutation() => Interlocked.Increment(ref _requests);

    /// <summary>
    /// The baseline answer must match the request: none without a baseline; with one, <c>accepted</c> and only
    /// file-scoped deltas, or <c>rejected</c> and only workspace replacements. Anything else is a protocol
    /// violation, failing the request like the other result checks.
    /// </summary>
    private void ValidateBaselineResult(IndexWorkspaceParams parameters, IndexWorkspaceResult result, ActiveMutation active)
    {
        string? failure = null;
        if (parameters.Baseline is null)
        {
            if (result.Baseline is not null)
                failure = $"workspace/index answered baseline '{result.Baseline}' to a request that carried none.";
        }
        else if (result.Baseline == BaselineOutcomes.Accepted)
        {
            if (active.SawWorkspaceReplacement)
                failure = "workspace/index accepted its baseline but replaced the workspace.";
        }
        else if (result.Baseline == BaselineOutcomes.Rejected)
        {
            if (active.SawFileScopedDelta)
                failure = "workspace/index rejected its baseline but sent a file-scoped delta.";
        }
        else
        {
            failure = $"workspace/index answered a baseline with '{result.Baseline ?? "nothing"}', not 'accepted' or 'rejected'.";
        }

        if (failure is not null)
        {
            Transition(ParserSessionState.Failed, lastError: failure);
            throw new ParserWorkerFailedException(failure);
        }
        if (result.Baseline == BaselineOutcomes.Rejected)
        {
            _logger.LogInformation("[{ParserId}] baseline rejected: {Reason}", _spec.ParserId, result.BaselineReason ?? "no reason given");
        }
    }

    private static HashSet<string> CreateExpectedFileSet(IReadOnlyList<string> files)
    {
        var unique = new HashSet<string>(PathComparer);
        foreach (var file in files)
        {
            if (!unique.Add(file))
            {
                throw new ArgumentException(
                    $"workspace/index files must be unique; duplicate path: '{file}'.",
                    nameof(files));
            }
        }
        return unique;
    }

    public async Task<ApplyChangesResult> ApplyChangesAsync(ApplyChangesParams parameters, CancellationToken ct = default)
    {
        var client = await GetReadyClientAsync(ct).ConfigureAwait(false);
        var origin = BeginMutation(offeredStateId: null);
        Transition(ParserSessionState.Indexing, $"applying {parameters.Changes.Count} change(s)");
        ActiveMutation? active = null;
        long requestId = 0;
        try
        {
            var result = await RunRequestAsync(() => client.ApplyChangesAsync(parameters, ct, id =>
            {
                requestId = id;
                active = new ActiveMutation(parameters.WorkspaceId, parameters.Generation, origin);
                if (!_activeMutations.TryAdd(id, active))
                {
                    throw new InvalidOperationException($"Request id {id} is already active.");
                }
            })).ConfigureAwait(false);
            ValidateMutationResult(
                parameters.WorkspaceId, parameters.Generation,
                result.WorkspaceId, result.Generation, result.DeltasEmitted, result.Complete,
                active, "workspace/applyChanges");
            TransitionAfterMutation($"generation {result.Generation} committed");
            return result;
        }
        catch
        {
            EndFailedMutation();
            throw;
        }
        finally
        {
            if (requestId != 0) _activeMutations.TryRemove(requestId, out _);
        }
    }

    public async Task<NativeSyntaxTreeResult> GetNativeSyntaxTreeAsync(
        NativeSyntaxTreeParams parameters,
        CancellationToken ct = default)
    {
        var client = await GetReadyClientAsync(ct).ConfigureAwait(false);
        if (_initializeResult?.Capabilities.NativeSyntaxTree != true)
        {
            throw new NotSupportedException(
                $"Worker '{_spec.ParserId}' does not expose native syntax trees.");
        }
        return await RunRequestAsync(() => client.GetNativeSyntaxTreeAsync(parameters, ct)).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>workspace/saveState</c> to the running process. Never starts one: a new process holds no state worth
    /// saving, so with none running this throws <see cref="ParserWorkerUnavailableException"/>; a worker that did
    /// not declare <c>persistentState</c> is never sent the request (<see cref="NotSupportedException"/>). Not a
    /// mutation: nothing is committed and the request numbering does not move.
    /// </summary>
    public async Task<SaveStateResult> SaveStateAsync(SaveStateParams parameters, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = _client;
        if (!IsLive || client is null)
        {
            throw new ParserWorkerUnavailableException($"Worker '{_spec.ParserId}' has no running process to save state.");
        }
        if (_initializeResult?.Capabilities.PersistentState != true)
        {
            throw new NotSupportedException($"Worker '{_spec.ParserId}' does not declare persistentState.");
        }
        // A save leaves the session state alone: its cancel lands in the background after the host has given up on
        // it (the capture's time limit), possibly while a later mutation is indexing, so it must not set Ready.
        return await RunRequestAsync(() => client.SaveStateAsync(parameters, ct), transitionOnCancel: false).ConfigureAwait(false);
    }

    private void ValidateMutationResult(
        string expectedWorkspace,
        long expectedGeneration,
        string actualWorkspace,
        long actualGeneration,
        int deltasEmitted,
        bool complete,
        ActiveMutation? active,
        string method)
    {
        string? failure = null;
        if (active is null)
        {
            failure = $"{method} completed without an active request context.";
        }
        else if (!string.Equals(actualWorkspace, expectedWorkspace, StringComparison.Ordinal)
                 || actualGeneration != expectedGeneration)
        {
            failure = $"{method} response reported '{actualWorkspace}'/{actualGeneration}, " +
                      $"expected '{expectedWorkspace}'/{expectedGeneration}.";
        }
        else if (!complete)
        {
            failure = $"{method} generation {actualGeneration} was incomplete.";
        }
        else if (deltasEmitted <= 0)
        {
            failure = $"{method} generation {actualGeneration} emitted no replacement delta.";
        }
        else if (active.DeltasReceived != deltasEmitted)
        {
            failure = $"{method} claimed {deltasEmitted} delta(s), but the host accepted {active.DeltasReceived}.";
        }
        else if (!active.SawLastDelta)
        {
            failure = $"{method} returned before marking its final analysis/delta chunk.";
        }
        else if (active.ExpectedFiles is not null && !active.SawTerminalProgress)
        {
            failure = $"{method} returned before reporting terminal analysis/progress.";
        }

        if (failure is not null)
        {
            Transition(ParserSessionState.Failed, lastError: failure);
            throw new ParserWorkerFailedException(failure);
        }
    }

    private void TransitionAfterMutation(string message)
    {
        // The current request remains in the dictionary until its finally block. More
        // than one entry means another independent workspace is still indexing.
        if (_activeMutations.Count > 1)
        {
            Transition(ParserSessionState.Indexing, "another workspace generation is still indexing");
        }
        else
        {
            Transition(ParserSessionState.Ready, message);
        }
    }

    private async Task<ParserWorkerClient> GetReadyClientAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _client!;
    }

    /// <summary>
    /// Translates connection loss during a request into a worker failure. Cancellation
    /// and remote JSON-RPC errors pass through: the worker is still healthy then.
    /// </summary>
    private async Task<T> RunRequestAsync<T>(Func<Task<T>> request, bool transitionOnCancel = true)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (ParserConnectionClosedException ex)
        {
            var message = $"Worker '{_spec.ParserId}' died during a request.";
            Transition(ParserSessionState.Failed, lastError: message);
            throw new ParserWorkerFailedException(message, ex);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancel: the worker answered (or was given its grace period);
            // the session stays usable.
            if (transitionOnCancel) Transition(ParserSessionState.Ready, "request cancelled");
            throw;
        }
        // A valid JSON-RPC error (invalid params, unsupported optional operation,
        // etc.) is a request failure, not evidence that the worker process is sick.
        catch (JsonRpcRemoteException)
        {
            throw;
        }
    }

    /// <summary>
    /// Graceful stop: shutdown request (bounded), stdin EOF, wait, then kill the tree.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stopping) return;
            _stopping = true;
            await _shutdown.CancelAsync().ConfigureAwait(false);

            if (_process is { HasExited: false } && _client is not null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_options.ShutdownTimeout);
                    await _client.ShutdownAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogDebug("[{ParserId}] shutdown request failed ({Message}); falling back to EOF.",
                        _spec.ParserId, ex.Message);
                }
            }

            await CleanupProcessAsync(kill: false).ConfigureAwait(false);
            Transition(ParserSessionState.Stopped);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Closes the protocol pipes (the worker's EOF-exit signal), waits briefly, and
    /// kills the process tree if it is still alive (always when <paramref name="kill"/>).
    /// </summary>
    private async Task CleanupProcessAsync(bool kill)
    {
        var process = _process;
        var connection = _connection;
        _connection = null;
        _client = null;
        _initializeResult = null;
        if (ReferenceEquals(process, _process))
        {
            // Detach the incarnation before closing its connection so the monitor
            // recognizes the resulting EOF as intentional cleanup, not a fresh crash.
            _process = null;
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (process is null) return;

        try
        {
            if (!process.HasExited && !kill)
            {
                using var wait = new CancellationTokenSource(_options.ExitAfterEofTimeout);
                try
                {
                    await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("[{ParserId}] worker ignored EOF for {Timeout}s; killing process tree.",
                        _spec.ParserId, _options.ExitAfterEofTimeout.TotalSeconds);
                }
            }
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited/reaped.
        }

        if (_stderrPump is { } pump)
        {
            try { await pump.ConfigureAwait(false); } catch { }
            _stderrPump = null;
        }

        process.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopping = true;
            // No shutdown request here — disposal is the hard-stop path. Closing stdin
            // is the EOF signal; a conforming worker exits on its own.
            await CleanupProcessAsync(kill: false).ConfigureAwait(false);
            if (_snapshot.State != ParserSessionState.Stopped)
            {
                Transition(ParserSessionState.Stopped);
            }
        }
        finally
        {
            _lifecycleLock.Release();
            _lifecycleLock.Dispose();
            _shutdown.Dispose();
        }
    }
}
