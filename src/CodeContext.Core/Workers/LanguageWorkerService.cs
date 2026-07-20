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

public sealed class LanguageWorkerService : ILanguageWorkerService, IHostedService, IAsyncDisposable
{
    private const string DefaultWorkspaceId = "default";

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

    public bool TryGetParserForExtension(string extension, out string parserId)
        => _extensionToParser.TryGetValue(extension, out parserId!);

    public async Task IndexWorkspaceAsync(
        string parserId,
        IReadOnlyList<string> files,
        CancellationToken ct = default,
        Action<AnalysisProgress>? progressHandler = null)
    {
        var state = GetWorker(parserId);
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (files.Count == 0 && state.Supervisor is null && !state.EverIndexed)
            {
                // Nothing to index and no worker running: don't spawn a process just
                // to learn there is no work.
                return;
            }

            var supervisor = EnsureSupervisor(state);
            var generation = ++state.Generation;
            await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams(
                DefaultWorkspaceId, _options.RootPath,
                state.Registration.Manifest.ProjectMarkers ?? [], files), ct).ConfigureAwait(false);
            await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams(
                DefaultWorkspaceId, generation, files), ct, progressHandler).ConfigureAwait(false);
            ReplaceApprovedFiles(state, files);
            state.EverIndexed = true;
        }
        finally
        {
            state.Lock.Release();
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
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
            state.Lock.Release();
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
        await state.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!state.EverIndexed || !state.ApprovedFiles.Contains(fullPath))
            {
                throw new InvalidOperationException(
                    "The file is not part of the committed index. Wait for indexing or refresh the file first.");
            }
            var supervisor = EnsureSupervisor(state);
            return await supervisor.GetNativeSyntaxTreeAsync(new NativeSyntaxTreeParams(
                DefaultWorkspaceId, fullPath, start, length, maxDepth), ct).ConfigureAwait(false);
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
            DeltaHandler = (delta, ct) => _deltaSink.ApplyAsync(delta, ct),
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
