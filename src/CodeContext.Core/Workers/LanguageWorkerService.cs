using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Workers;

/// <summary>
/// Routes graph mutations for worker-owned file extensions to language worker
/// processes. One workspace per worker for now (the whole watched root); the protocol
/// supports more when monorepo project discovery lands.
/// </summary>
public interface ILanguageWorkerService
{
    /// <summary>Extensions owned by a discovered worker (routed to the worker process
    /// over the parser protocol).</summary>
    IReadOnlyCollection<string> OwnedExtensions { get; }

    bool TryGetParserForExtension(string extension, out string parserId);

    /// <summary>
    /// Finds the worker that declared this file a project input. An exact name counts
    /// anywhere, since a build writes such files where nothing is indexed; an extension
    /// counts only for a file the selector includes, or every package unpacked under an
    /// excluded directory would refresh the workspace.
    /// </summary>
    bool TryGetParserForProjectInput(string path, bool isIncluded, out string parserId);

    /// <summary>
    /// Tells a worker its project inputs changed, with no source file changing. It rereads
    /// its projects and publishes whatever now binds differently.
    /// </summary>
    Task RefreshProjectsAsync(string parserId, CancellationToken ct = default);

    /// <summary>Builds (or replaces) the complete workspace generation from
    /// <paramref name="files"/>.</summary>
    Task IndexWorkspaceAsync(
        string parserId,
        IReadOnlyList<string> files,
        CancellationToken ct = default,
        Action<AnalysisProgress>? progressHandler = null);

    /// <summary>
    /// Applies an ordered change batch. <paramref name="approvedFiles"/> is the
    /// complete current file set for this parser (post-batch); it re-syncs the
    /// worker's workspace so a restarted worker heals itself before the delta.
    /// </summary>
    Task ApplyChangesAsync(
        string parserId,
        IReadOnlyList<FileChange> changes,
        IReadOnlyList<string> approvedFiles,
        CancellationToken ct = default);

    /// <summary>Returns a parser-native syntax tree for an already indexed file.</summary>
    Task<NativeSyntaxTreeResult> GetNativeSyntaxTreeAsync(
        string filePath, int? start = null, int? length = null, int maxDepth = 8,
        CancellationToken ct = default);
}

public sealed class LanguageWorkerService : ILanguageWorkerService, IHostedService, IAsyncDisposable, IWorkerStateSaver
{
    internal const string DefaultWorkspaceId = "default";

    private readonly IWorkerCatalog _catalog;
    private readonly IAnalysisDeltaSink _deltaSink;
    private readonly IParserSessionRegistry _sessionRegistry;
    private readonly CodeContextOptions _options;
    private readonly ParserWorkerOptions _workerOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<LanguageWorkerService> _logger;
    private readonly Dictionary<string, WorkerState> _workers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _extensionToParser = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    private sealed class WorkerState(RegisteredWorker registration)
    {
        public RegisteredWorker Registration { get; } = registration;
        public ParserProcessSupervisor? Supervisor { get; set; }
        public long Generation { get; set; }
        public bool EverIndexed { get; set; }

        /// <summary>The store holds facts of this parser from a loaded snapshot, which only its own index can replace.</summary>
        public bool HoldsLoadedFacts { get; set; }

        /// <summary>How the last successful index answered its baseline (<see cref="BaselineOutcomes"/>), or null when it carried none.</summary>
        public string? LastBaselineOutcome { get; set; }

        /// <summary>
        /// The process was stopped after a quiet period and no index has run since. The first operation re-indexes the
        /// approved files, with the saved state as baseline where there is one, before doing its own work.
        /// </summary>
        public bool Hibernated { get; set; }

        /// <summary>
        /// A wake started and did not complete (its index failed). The process it started may still run, so the worker
        /// is not <see cref="Hibernated"/>, but the next operation re-indexes first, as after hibernation.
        /// </summary>
        public bool WakeIncomplete { get; set; }

        /// <summary>The next operation re-indexes before its own work.</summary>
        public bool NeedsWake => Hibernated || WakeIncomplete;

        /// <summary>When an operation on this worker last started or ended, in UTC ticks of <see cref="Time"/>.</summary>
        public long LastActivityTicks;

        /// <summary>
        /// How long the last successful index that read the workspace from its files took (a scan or a wake, process
        /// start included), or null before one. An index whose baseline was accepted does not count: restoring saved
        /// state is quick because it skips the warm-up the next edit then pays.
        /// </summary>
        public TimeSpan? LastReadDuration { get; set; }
        public HashSet<string> ApprovedFiles { get; } = new(PathComparer);
        public SemaphoreSlim Lock { get; } = new(1, 1);
    }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public LanguageWorkerService(
        IWorkerCatalog catalog,
        IAnalysisDeltaSink deltaSink,
        IParserSessionRegistry sessionRegistry,
        IOptions<CodeContextOptions> options,
        ILoggerFactory loggerFactory,
        ParserWorkerOptions? workerOptions = null)
    {
        _catalog = catalog;
        _deltaSink = deltaSink;
        _sessionRegistry = sessionRegistry;
        _options = options.Value;
        _workerOptions = workerOptions ?? new ParserWorkerOptions();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LanguageWorkerService>();

        foreach (var worker in _catalog.Workers)
        {
            _workers[worker.Manifest.ParserId] = new WorkerState(worker);
            foreach (var extension in worker.Extensions)
            {
                if (!_extensionToParser.TryAdd(extension, worker.Manifest.ParserId))
                {
                    _logger.LogWarning(
                        "Extension {Extension} is already claimed by '{Selected}'; ignoring claim from '{Ignored}'.",
                        extension, _extensionToParser[extension], worker.Manifest.ParserId);
                }
            }
            // Until the first file routes here, the honest state is "nothing needs
            // this parser". The supervisor overwrites this on first use.
            _sessionRegistry.Report(new ParserSessionSnapshot(
                worker.Manifest.ParserId, worker.Manifest.DisplayName, ParserSessionState.NotNeeded));
        }
    }

    public IReadOnlyCollection<string> OwnedExtensions => _extensionToParser.Keys;

    /// <summary>
    /// Where a first index finds a baseline; null (the cache is off) offers none. Set once while the host is
    /// composed, before the coordinator runs.
    /// </summary>
    internal IWorkerBaselineSource? BaselineSource { get; set; }

    /// <summary>The clock of the activity stamps hibernation reads. Set once while the host is composed.</summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>Called with the parser id after each operation on a worker ends (index, batch, project refresh, syntax tree).</summary>
    internal Action<string>? ActivityEnded { get; set; }

    /// <summary>
    /// Wakes a hibernated worker on the coordinator's ordered path (its re-index commits), for a request that does not
    /// arrive through the coordinator: a syntax tree. Null when hibernation is not wired; such a worker never hibernates.
    /// </summary>
    internal Func<string, CancellationToken, Task>? Waker { get; set; }

    /// <summary>
    /// Whether the snapshot on disk holds the committed graph as it is now, so a worker's recorded state goes with a
    /// written snapshot. Null (no writer) means never: a worker that declared <c>persistentState</c> then stays up.
    /// </summary>
    internal Func<bool>? SnapshotWritten { get; set; }

    /// <summary>The parsers this service routes to.</summary>
    internal IReadOnlyCollection<string> ParserIds => _workers.Keys;

    /// <summary>Whether the parser's worker is hibernated (stopped while idle, not yet re-indexed).</summary>
    internal bool IsHibernated(string parserId) => _workers.TryGetValue(parserId, out var state) && state.Hibernated;

    /// <summary>How the parser's last successful index answered its baseline, or null when it was offered none.</summary>
    internal string? LastBaselineOutcomeFor(string parserId)
        => _workers.TryGetValue(parserId, out var state) ? state.LastBaselineOutcome : null;

    /// <summary>Whether the parser's last successful index was a baseline its worker accepted.</summary>
    internal bool AcceptedBaseline(string parserId)
        => _workers.TryGetValue(parserId, out var state) && state.LastBaselineOutcome == BaselineOutcomes.Accepted;

    public bool TryGetParserForExtension(string extension, out string parserId)
        => _extensionToParser.TryGetValue(extension, out parserId!);

    /// <summary>
    /// Whether a scan indexes this parser: it owns at least one extension. A snapshot shard of any other
    /// parser would never be replaced by a scan, so it is not loaded.
    /// </summary>
    internal bool IsIndexedParser(string parserId) => _extensionToParser.ContainsValue(parserId);

    /// <summary>
    /// What decides which files this host indexes and with what, from the manifests alone (no worker is asked):
    /// every discovered worker's id, version, command, project markers and inputs and where its manifest is, and
    /// which worker each extension is routed to. A snapshot records it; a query without an instance answers only
    /// from a snapshot written under the same value, since a worker installed, removed or replaced since then
    /// would index the tree differently. Lowercase hex SHA-256 of a canonical text.
    /// </summary>
    internal string WorkerSetFingerprint()
    {
        var text = new System.Text.StringBuilder();
        void Field(string? value) => text.Append(value?.Length ?? -1).Append(':').Append(value).Append('\n');
        void List(IReadOnlyList<string>? values)
        {
            text.Append(values?.Count ?? -1).Append('[');
            foreach (var value in values ?? []) Field(value);
        }
        foreach (var worker in _workers.Values.Select(state => state.Registration).OrderBy(worker => worker.Manifest.ParserId, StringComparer.Ordinal))
        {
            Field(worker.Manifest.ParserId);
            Field(worker.Manifest.Version);
            Field(worker.Manifest.Command);
            List(worker.Manifest.Args);
            List(worker.Manifest.ProjectMarkers);
            List(worker.Manifest.ProjectInputs);
            Field(worker.ManifestPath);
        }
        foreach (var (extension, parserId) in _extensionToParser.OrderBy(pair => pair.Key.ToUpperInvariant(), StringComparer.Ordinal))
        {
            Field(extension.ToUpperInvariant());
            Field(parserId);
        }
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Each discovered worker's manifest and the directory it is in, where they are on disk: what a check without a
    /// worker looks at besides the tree, so a worker updated in place (its manifest rewritten, a file added, removed
    /// or renamed beside it) is not answered for from facts its predecessor made.
    /// </summary>
    internal IReadOnlyList<string> InstallationPaths()
    {
        var paths = new List<string>();
        foreach (var state in _workers.Values)
        {
            var manifest = state.Registration.ManifestPath;
            if (!Path.IsPathFullyQualified(manifest)) continue;
            paths.Add(manifest);
            if (Path.GetDirectoryName(manifest) is { Length: > 0 } directory) paths.Add(directory);
        }
        return paths;
    }

    /// <summary>
    /// Records, before the first scan, which parsers have facts in the store from a loaded snapshot. Their
    /// first index then runs even with no files, so a language whose files are all gone is emptied.
    /// </summary>
    internal void MarkLoadedFromSnapshot(IReadOnlyCollection<string> parserIds)
    {
        foreach (var parserId in parserIds)
        {
            if (_workers.TryGetValue(parserId, out var state)) state.HoldsLoadedFacts = true;
        }
    }

    public bool TryGetParserForProjectInput(string path, bool isIncluded, out string parserId)
    {
        var name = Path.GetFileName(path);
        foreach (var state in _workers.Values)
        {
            foreach (var input in state.Registration.Manifest.ProjectInputs ?? [])
            {
                if (input.StartsWith('.')
                    ? isIncluded && name.EndsWith(input, StringComparison.OrdinalIgnoreCase)
                    : name.Equals(input, StringComparison.OrdinalIgnoreCase))
                {
                    parserId = state.Registration.Manifest.ParserId;
                    return true;
                }
            }
        }
        parserId = string.Empty;
        return false;
    }

    public async Task RefreshProjectsAsync(string parserId, CancellationToken ct = default)
    {
        var state = GetWorker(parserId);
        Touch(state);
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Before a first index there is nothing to bring up to date.
            if (!state.EverIndexed) return;
            // A re-index that replaced the workspace has read the projects as they are now.
            if (state.NeedsWake && !await WakeCoreAsync(state, state.ApprovedFiles.ToList(), ct).ConfigureAwait(false)) return;

            var supervisor = EnsureSupervisor(state);
            var generation = ++state.Generation;
            await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams(
                DefaultWorkspaceId, _options.RootPath,
                state.Registration.Manifest.ProjectMarkers ?? [], state.ApprovedFiles.ToList()), ct).ConfigureAwait(false);
            await supervisor.ApplyChangesAsync(new ApplyChangesParams(
                DefaultWorkspaceId, generation, []), ct).ConfigureAwait(false);
        }
        finally
        {
            Release(state);
        }
    }

    public async Task IndexWorkspaceAsync(
        string parserId,
        IReadOnlyList<string> files,
        CancellationToken ct = default,
        Action<AnalysisProgress>? progressHandler = null)
    {
        var state = GetWorker(parserId);
        Touch(state);
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (files.Count == 0 && state.Supervisor is null && !state.EverIndexed && !state.HoldsLoadedFacts)
            {
                // Nothing to index, no worker running and nothing loaded to replace: don't
                // spawn a process just to learn there is no work.
                return;
            }

            var started = Time.GetUtcNow();
            var supervisor = EnsureSupervisor(state);
            var generation = ++state.Generation;
            await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams(
                DefaultWorkspaceId, _options.RootPath,
                state.Registration.Manifest.ProjectMarkers ?? [], files), ct).ConfigureAwait(false);
            // The offer rule (plan 3.6): the source checks the shard and its state file; the supervisor drops the
            // baseline again unless the worker declared the capability and this is its process's first mutation.
            var initialized = supervisor.Initialized;
            var baseline = initialized?.Capabilities.PersistentState == true
                ? BaselineSource?.TryGetBaseline(parserId, DefaultWorkspaceId, initialized.ParserVersion)
                : null;
            state.LastBaselineOutcome = null;
            var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams(
                DefaultWorkspaceId, generation, files, baseline), ct, progressHandler).ConfigureAwait(false);
            state.LastBaselineOutcome = result.Baseline;
            RecordIndexDuration(state, started);
            ReplaceApprovedFiles(state, files);
            state.EverIndexed = true;
            state.HoldsLoadedFacts = false;
            // An index is the wake: it ran on a new process, with the baseline where there is one.
            state.Hibernated = false;
            state.WakeIncomplete = false;
        }
        finally
        {
            Release(state);
        }
    }

    public async Task ApplyChangesAsync(
        string parserId,
        IReadOnlyList<FileChange> changes,
        IReadOnlyList<string> approvedFiles,
        CancellationToken ct = default)
    {
        if (changes.Count == 0) return;

        var state = GetWorker(parserId);
        Touch(state);
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.NeedsWake)
            {
                // The re-index reads the files as they are now. A replacement already holds this batch. After an
                // accepted baseline the C# worker has already re-walked what the batch changed (it restores a saved
                // walk only when the file's SHA-256 content hash matches), but the protocol does not say how a worker
                // decides a file is unchanged, so the batch's content changes still follow as touches; its deletions
                // are done.
                if (!await WakeCoreAsync(state, approvedFiles, ct).ConfigureAwait(false)) return;
                changes = changes.Where(change => change.Type != FileChangeType.Deleted).ToList();
                if (changes.Count == 0) return;
            }

            var supervisor = EnsureSupervisor(state);
            var generation = ++state.Generation;
            var changeDtos = changes.Select(ToDto).ToList();

            // Re-opening before every mutation is deliberately idempotent: the worker
            // syncs its cached state against the approved set, so a restarted worker
            // reloads unchanged files instead of resolving against a hollow workspace.
            await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams(
                DefaultWorkspaceId, _options.RootPath,
                state.Registration.Manifest.ProjectMarkers ?? [], approvedFiles), ct).ConfigureAwait(false);
            await supervisor.ApplyChangesAsync(new ApplyChangesParams(
                DefaultWorkspaceId, generation, changeDtos), ct).ConfigureAwait(false);
            ReplaceApprovedFiles(state, approvedFiles);
            state.EverIndexed = true;
        }
        finally
        {
            Release(state);
        }
    }

    public async Task<NativeSyntaxTreeResult> GetNativeSyntaxTreeAsync(
        string filePath, int? start = null, int? length = null, int maxDepth = 8,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A file path is required.", nameof(filePath));
        }
        if (start is < 0 || length is < 0 || maxDepth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(start),
                "start/length must be non-negative and maxDepth must be between 0 and 32.");
        }

        var fullPath = Path.GetFullPath(filePath, _options.RootPath);
        var root = Path.GetFullPath(_options.RootPath);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The requested file is outside the indexed root.", nameof(filePath));
        }

        if (!TryGetParserForExtension(Path.GetExtension(fullPath), out var parserId))
        {
            throw new NotSupportedException(
                $"No language worker owns extension '{Path.GetExtension(fullPath)}'.");
        }

        var state = GetWorker(parserId);
        // A woken worker can hibernate again before this request takes the lock only if the idle time is shorter than
        // the wake itself; a few attempts cover that.
        for (var attempt = 0; ; attempt++)
        {
            Touch(state);
            await state.Lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!state.EverIndexed || !state.ApprovedFiles.Contains(fullPath))
                {
                    throw new InvalidOperationException(
                        "The file is not part of the committed index. Wait for indexing or refresh the file first.");
                }
                if (!state.NeedsWake)
                {
                    var supervisor = EnsureSupervisor(state);
                    return await supervisor.GetNativeSyntaxTreeAsync(new NativeSyntaxTreeParams(
                        DefaultWorkspaceId, fullPath, start, length, maxDepth), ct).ConfigureAwait(false);
                }
                if (Waker is null || attempt >= 3)
                {
                    throw new InvalidOperationException($"The '{parserId}' worker is hibernated and could not be woken. Try again.");
                }
            }
            finally
            {
                Release(state);
            }
            // The wake re-indexes, which commits, so it runs on the coordinator like every other mutation.
            await Waker(parserId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-indexes a hibernated worker, on the coordinator's loop: the first operation after hibernation does this
    /// itself; a request that does not come through the coordinator (a syntax tree) asks for it through
    /// <see cref="Waker"/>. Does nothing for a worker that is not hibernated.
    /// </summary>
    internal async Task WakeAsync(string parserId, CancellationToken ct)
    {
        var state = GetWorker(parserId);
        Touch(state);
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.NeedsWake) await WakeCoreAsync(state, state.ApprovedFiles.ToList(), ct).ConfigureAwait(false);
        }
        finally
        {
            Release(state);
        }
    }

    /// <summary>
    /// The wake, under the worker's lock: a new process indexes <paramref name="files"/>, offered the baseline its
    /// state saved before hibernating where there is one (the start-up verification, run for one worker). True when
    /// the worker accepted the baseline and sent only what changed; false when it replaced the workspace from the
    /// files. Throws as an index does; the worker is then no longer hibernated (a process may run, and the session
    /// reports the failure) but its wake is incomplete, so the next operation re-indexes again.
    /// </summary>
    private async Task<bool> WakeCoreAsync(WorkerState state, IReadOnlyList<string> files, CancellationToken ct)
    {
        // From here a process may run whatever happens; until the index completes, the next operation re-indexes.
        state.Hibernated = false;
        state.WakeIncomplete = true;
        state.LastBaselineOutcome = null;
        var started = Time.GetUtcNow();
        var supervisor = EnsureSupervisor(state);
        var generation = ++state.Generation;
        await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams(
            DefaultWorkspaceId, _options.RootPath,
            state.Registration.Manifest.ProjectMarkers ?? [], files), ct).ConfigureAwait(false);
        var initialized = supervisor.Initialized;
        var baseline = initialized?.Capabilities.PersistentState == true
            ? BaselineSource?.TryGetBaseline(state.Registration.Manifest.ParserId, DefaultWorkspaceId, initialized.ParserVersion)
            : null;
        var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams(
            DefaultWorkspaceId, generation, files, baseline), ct).ConfigureAwait(false);
        ReplaceApprovedFiles(state, files);
        state.LastBaselineOutcome = result.Baseline;
        RecordIndexDuration(state, started);
        state.WakeIncomplete = false;
        _logger.LogInformation("Worker '{ParserId}' woke: {Outcome}.", state.Registration.Manifest.ParserId,
            result.Baseline == BaselineOutcomes.Accepted ? "its saved state was accepted" : "it re-read the workspace");
        return result.Baseline == BaselineOutcomes.Accepted;
    }

    /// <summary>What <see cref="TryHibernateAsync"/> did.</summary>
    internal enum HibernationOutcome
    {
        /// <summary>The process exited; the worker is hibernated.</summary>
        Hibernated,
        /// <summary>An operation ran or started within the idle time.</summary>
        NotIdle,
        /// <summary>No process to stop (never started, already hibernated, stopped), or a failed session.</summary>
        NotRunning,
        /// <summary>A worker with <c>persistentState</c> whose state is not saved with a written snapshot of its shard yet.</summary>
        StateNotSaved,
    }

    /// <summary>
    /// Stops the parser's worker process when it has been idle for <paramref name="idle"/> and can be woken cheaply and
    /// correctly. Runs on the coordinator's loop, as a command between mutations, and under the worker's lock, so no
    /// batch, scan, state save or syntax tree is in flight. A worker that declared <c>persistentState</c> stops only
    /// when its state is recorded with its current shard, the state file exists and the snapshot on disk is current,
    /// so its wake is offered that state; any other worker re-reads its files on wake. Writes nothing.
    /// </summary>
    internal async Task<HibernationOutcome> TryHibernateAsync(string parserId, TimeSpan idle, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_workers.TryGetValue(parserId, out var state)) return HibernationOutcome.NotRunning;
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (state.Hibernated || state.Supervisor is not { IsLive: true } supervisor) return HibernationOutcome.NotRunning;
            // A request that failed (complete:false) leaves the process up and the session failed. Stopping it would
            // show the session as hibernated and drop the failure from status; the next operation that succeeds
            // makes it ready, and its end arms the check again.
            if (supervisor.Snapshot.State == ParserSessionState.Failed) return HibernationOutcome.NotRunning;
            if (Time.GetUtcNow().UtcTicks - Interlocked.Read(ref state.LastActivityTicks) < idle.Ticks) return HibernationOutcome.NotIdle;
            if (supervisor.DeclaresPersistentState
                && (BaselineSource?.TryGetBaseline(parserId, DefaultWorkspaceId, supervisor.Initialized!.ParserVersion) is null
                    || SnapshotWritten?.Invoke() != true))
            {
                return HibernationOutcome.StateNotSaved;
            }
            if (!await supervisor.HibernateAsync(ct).ConfigureAwait(false)) return HibernationOutcome.NotRunning;
            state.Hibernated = true;
            _logger.LogInformation("Worker '{ParserId}' hibernated after {Seconds:0} s without work.", parserId, idle.TotalSeconds);
            return HibernationOutcome.Hibernated;
        }
        finally
        {
            state.Lock.Release();
        }
    }

    /// <summary>When the parser's worker last started or ended an operation, or null for an unknown parser.</summary>
    internal DateTimeOffset? LastActivity(string parserId)
        => _workers.TryGetValue(parserId, out var state)
            ? new DateTimeOffset(Interlocked.Read(ref state.LastActivityTicks), TimeSpan.Zero)
            : null;

    /// <summary>
    /// How long the parser's last successful index that read its files (not one that accepted saved state) took, or
    /// null before one: what <see cref="WorkerHibernationSettings.IdleFor"/> reads.
    /// </summary>
    internal TimeSpan? LastReadDuration(string parserId)
        => _workers.TryGetValue(parserId, out var state) ? state.LastReadDuration : null;

    /// <summary>Records the index that just succeeded; call after <see cref="WorkerState.LastBaselineOutcome"/> is set.</summary>
    private void RecordIndexDuration(WorkerState state, DateTimeOffset started)
    {
        var elapsed = Time.GetUtcNow() - started;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var restored = state.LastBaselineOutcome == BaselineOutcomes.Accepted;
        if (!restored) state.LastReadDuration = elapsed;
        _logger.LogInformation("Worker '{ParserId}' indexed in {Seconds:0.0} s ({How}).", state.Registration.Manifest.ParserId,
            elapsed.TotalSeconds, restored ? "from its saved state" : "from its files");
    }

    private void Touch(WorkerState state) => Interlocked.Exchange(ref state.LastActivityTicks, Time.GetUtcNow().UtcTicks);

    /// <summary>Ends an operation: stamps the activity, releases the lock, and tells hibernation.</summary>
    private void Release(WorkerState state)
    {
        Touch(state);
        state.Lock.Release();
        try
        {
            ActivityEnded?.Invoke(state.Registration.Manifest.ParserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The activity observer failed.");
        }
    }

    async Task<SaveStateResult?> IWorkerStateSaver.SaveStateAsync(
        string parserId, ShardLineage lineage, SaveStateParams parameters, CancellationToken ct)
    {
        if (!_workers.TryGetValue(parserId, out var state)) return null;
        try
        {
            await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        try
        {
            if (state.Supervisor is not { } supervisor
                || !WorkerStatePairing.Holds(lineage, supervisor.Incarnation, supervisor.LastRequest,
                    supervisor.IsLive, supervisor.DeclaresPersistentState))
            {
                return null;
            }

            // The request runs on its own token: when ct fires (the capture's time limit), the coordinator moves
            // on at once, and the request is cancelled in the background with its late result ignored.
            var abandon = new CancellationTokenSource();
            var request = supervisor.SaveStateAsync(parameters, abandon.Token);
            var abandoned = false;
            try
            {
                return await request.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Worker '{ParserId}' did not save its state in time; its shard is written without state.", parserId);
                // Off this thread: the cancel notification and its grace period must not hold the coordinator. The
                // source is disposed once both the cancel and the request are over.
                abandoned = true;
                var cancelling = abandon.CancelAsync();
                var finished = request.ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
                _ = Task.WhenAll(cancelling, finished).ContinueWith(_ => abandon.Dispose(), TaskScheduler.Default);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Worker '{ParserId}' did not save its state ({Message}); its shard is written without state.",
                    parserId, ex.Message);
                return null;
            }
            finally
            {
                if (!abandoned) abandon.Dispose();
            }
        }
        finally
        {
            state.Lock.Release();
        }
    }

    private void ReplaceApprovedFiles(WorkerState state, IReadOnlyList<string> files)
    {
        state.ApprovedFiles.Clear();
        foreach (var file in files)
        {
            state.ApprovedFiles.Add(Path.GetFullPath(file, _options.RootPath));
        }
    }

    private static FileChangeDto ToDto(FileChange change) => new(
        change.Path,
        change.Type switch
        {
            FileChangeType.Created => FileChangeKinds.Created,
            FileChangeType.Deleted => FileChangeKinds.Deleted,
            // The watcher reports renames as delete+create of distinct paths; a bare
            // Renamed without an old path is just a content change at Path.
            FileChangeType.Renamed => FileChangeKinds.Changed,
            _ => FileChangeKinds.Changed,
        });

    private WorkerState GetWorker(string parserId)
        => _workers.TryGetValue(parserId, out var state)
            ? state
            : throw new InvalidOperationException($"No worker is registered for parser '{parserId}'.");

    private ParserProcessSupervisor EnsureSupervisor(WorkerState state)
    {
        if (state.Supervisor is not null)
        {
            return state.Supervisor;
        }

        var supervisor = new ParserProcessSupervisor(
            state.Registration.LaunchSpec,
            _workerOptions,
            _loggerFactory.CreateLogger<ParserProcessSupervisor>(),
            _sessionRegistry,
            _options)
        {
            OriginDeltaHandler = (delta, origin, ct) => _deltaSink.ApplyAsync(delta, origin, ct),
        };
        state.Supervisor = supervisor;
        return supervisor;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Graceful stop: shutdown request → stdin EOF → kill, per worker.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _workers.Values)
        {
            await state.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (state.Supervisor is { } supervisor)
                {
                    try
                    {
                        await supervisor.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Worker '{ParserId}' did not shut down cleanly.",
                            state.Registration.Manifest.ParserId);
                    }
                }
            }
            finally
            {
                state.Lock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        foreach (var state in _workers.Values)
        {
            await state.Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state.Supervisor is { } supervisor)
                {
                    await supervisor.DisposeAsync().ConfigureAwait(false);
                    state.Supervisor = null;
                }
            }
            finally
            {
                state.Lock.Release();
            }
            state.Lock.Dispose();
        }
    }
}
