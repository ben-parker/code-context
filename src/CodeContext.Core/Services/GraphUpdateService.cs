using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Services;

/// <summary>
/// Writer side of the graph. Not internally synchronized: all mutating entry points are
/// expected to be invoked from the single <see cref="IndexCoordinator"/> loop, which
/// serializes scans, refreshes, and watcher batches.
///
/// Files whose extension is owned by a discovered language worker are routed through
/// <see cref="ILanguageWorkerService"/> (the worker parses and streams normalized
/// deltas back); remaining extensions fall back to in-process
/// <see cref="ILanguageParser"/> implementations until their own worker extraction.
/// </summary>
public class GraphUpdateService : IGraphUpdateService
{
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly IEnumerable<ILanguageParser> _parsers;
    private readonly ILogger<GraphUpdateService> _logger;
    private readonly CodeContextOptions _options;
    private readonly IFileMetadataRepository _fileMetadataRepository;
    private readonly IParserSessionRegistry? _sessionRegistry;
    private readonly ILanguageWorkerService? _workerService;
    private readonly IRepositoryFileSelector _fileSelector;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public GraphUpdateService(
        IRepositoryFactory repositoryFactory,
        IEnumerable<ILanguageParser> parsers,
        IOptions<CodeContextOptions> options,
        ILogger<GraphUpdateService> logger,
        IFileMetadataRepository fileMetadataRepository,
        IParserSessionRegistry? sessionRegistry = null,
        ILanguageWorkerService? workerService = null,
        IRepositoryFileSelector? fileSelector = null)
    {
        _repositoryFactory = repositoryFactory;
        _parsers = parsers;
        _options = options.Value;
        _logger = logger;
        _fileMetadataRepository = fileMetadataRepository;
        _sessionRegistry = sessionRegistry;
        _workerService = workerService;
        _fileSelector = fileSelector ?? new RepositoryFileSelector(options);
    }

    /// <summary>
    /// Reports an in-process parser's outcome as a session state so /api/status can
    /// distinguish "no results because parsing failed" from "no references". Worker
    /// supervisors report their own richer snapshots; this covers parsers that still
    /// run in-process (until their Phase 3/4 extraction).
    /// </summary>
    private void ReportParserSession(ILanguageParser parser, ParserSessionState state, string? error = null)
    {
        if (_sessionRegistry is null) return;
        var name = (parser as IParserDiagnostics)?.DisplayName ?? DeriveParserName(parser);
        _sessionRegistry.Report(new ParserSessionSnapshot(
            ParserId: name.ToLowerInvariant(),
            DisplayName: name,
            State: state,
            Message: state == ParserSessionState.Ready ? null : error,
            LastError: error));
    }

    private static string DeriveParserName(ILanguageParser parser)
    {
        var name = parser.GetType().Name;
        return name.EndsWith("Parser", StringComparison.Ordinal) ? name[..^"Parser".Length] : name;
    }

    /// <summary>
    /// Collects per-parser outcomes across one batch/scan so the session state is
    /// reported once per operation with failure taking precedence. Per-file reporting
    /// would be last-write-wins under parallel processing: a file that parsed after a
    /// failing one would flip the session back to Ready and mask the failure.
    /// </summary>
    private sealed class InProcessParserOutcomes
    {
        // Value is the first failure message, or null while all files succeeded.
        private readonly ConcurrentDictionary<ILanguageParser, string?> _outcomes = new();

        public void RecordSuccess(ILanguageParser parser)
            => _outcomes.TryAdd(parser, null);

        public void RecordFailure(ILanguageParser parser, string error)
            => _outcomes.AddOrUpdate(parser, error, (_, existing) => existing ?? error);

        public void Flush(GraphUpdateService service)
        {
            foreach (var (parser, error) in _outcomes)
            {
                service.ReportParserSession(
                    parser,
                    error is null ? ParserSessionState.Ready : ParserSessionState.Failed,
                    error);
            }
        }
    }

    private bool TryGetWorkerForPath(string path, out string parserId)
    {
        parserId = string.Empty;
        return _workerService is not null
            && _workerService.TryGetParserForExtension(Path.GetExtension(path), out parserId);
    }

    public Task ProcessFileChangeAsync(string filePath, FileChangeType changeType, CancellationToken ct)
        => ProcessFileChangesAsync([new FileChange(filePath, changeType)], ct);

    public async Task ProcessFileChangesAsync(IReadOnlyList<FileChange> changes, CancellationToken ct)
    {
        var workerBatches = new Dictionary<string, List<FileChange>>(StringComparer.Ordinal);
        var otherChanges = new List<FileChange>();

        foreach (var change in changes)
        {
            // The same selector governs explicit refresh and watcher paths. An
            // excluded file is forwarded as a deletion so stale facts cannot remain
            // after a rule change or a direct refresh of an ignored path.
            var effectiveChange = !_fileSelector.IsIncluded(change.Path)
                ? new FileChange(change.Path, FileChangeType.Deleted)
                : change;
            var extension = Path.GetExtension(change.Path);
            if (TryGetWorkerForPath(change.Path, out var parserId))
            {
                (workerBatches.TryGetValue(parserId, out var batch)
                    ? batch
                    : workerBatches[parserId] = []).Add(effectiveChange);
            }
            else if (_parsers.Any(p => p.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
            {
                otherChanges.Add(effectiveChange);
            }
            else
            {
                _logger.LogDebug("No parser found for file: {FilePath}", change.Path);
            }
        }

        // One failing parser (worker or in-process) must not abort the rest of the
        // batch: every change gets its attempt, per-file metadata records failures,
        // and the first failure is rethrown at the end so callers still see it.
        var failures = new List<Exception>();

        foreach (var (parserId, batch) in workerBatches)
        {
            try
            {
                await ProcessWorkerBatchAsync(parserId, batch, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        var outcomes = new InProcessParserOutcomes();
        foreach (var change in otherChanges)
        {
            try
            {
                await ProcessFileChangeOldWayAsync(change.Path, change.Type, ct, outcomes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
        outcomes.Flush(this);

        if (failures.Count > 0)
        {
            throw failures[0];
        }
    }

    /// <summary>
    /// Applies a batch of changes owned by one language worker: one applyChanges
    /// round-trip, one atomic graph commit via the worker's streamed delta.
    /// </summary>
    private async Task ProcessWorkerBatchAsync(string parserId, List<FileChange> changes, CancellationToken ct)
    {
        var updatedPaths = new List<string>();
        var effectiveChanges = new List<FileChange>();
        foreach (var change in changes)
        {
            if (change.Type == FileChangeType.Deleted)
            {
                await _fileMetadataRepository.DeleteAsync(change.Path, ct);
                effectiveChanges.Add(change);
                continue;
            }

            if (!File.Exists(change.Path))
            {
                // Disappeared between the event and now: forward as a delete so the
                // worker replaces the file's facts with nothing.
                await _fileMetadataRepository.DeleteAsync(change.Path, ct);
                effectiveChanges.Add(new FileChange(change.Path, FileChangeType.Deleted));
                continue;
            }

            await _fileMetadataRepository.UpsertAsync(new FileMetadata
            {
                FilePath = change.Path,
                Status = FileProcessingStatus.Processing,
                LastModified = File.GetLastWriteTimeUtc(change.Path),
            }, ct);
            updatedPaths.Add(change.Path);
            effectiveChanges.Add(change);
        }

        try
        {
            var approvedFiles = await GetKnownFilesForParserAsync(parserId, ct);
            await _workerService!.ApplyChangesAsync(parserId, effectiveChanges, approvedFiles, ct);

            foreach (var path in updatedPaths)
            {
                await _fileMetadataRepository.UpsertAsync(new FileMetadata
                {
                    FilePath = path,
                    Status = FileProcessingStatus.Completed,
                    LastModified = File.GetLastWriteTimeUtc(path),
                    LastScanned = DateTime.UtcNow,
                }, ct);
            }
            _logger.LogDebug("Worker '{ParserId}' applied batch of {Count} change(s).", parserId, changes.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker '{ParserId}' failed to apply a batch of {Count} change(s).",
                parserId, changes.Count);
            foreach (var path in updatedPaths)
            {
                await _fileMetadataRepository.UpsertAsync(new FileMetadata
                {
                    FilePath = path,
                    Status = FileProcessingStatus.Failed,
                    ErrorMessage = ex.Message,
                }, ct);
            }
            throw;
        }
    }

    /// <summary>The complete current file set owned by one worker, from file metadata
    /// (the batch's own upserts/deletes have already been applied when this runs).</summary>
    private async Task<List<string>> GetKnownFilesForParserAsync(string parserId, CancellationToken ct)
    {
        var files = new List<string>();
        foreach (var metadata in await _fileMetadataRepository.GetAllAsync(ct))
        {
            if (TryGetWorkerForPath(metadata.FilePath, out var owner)
                && string.Equals(owner, parserId, StringComparison.Ordinal))
            {
                files.Add(metadata.FilePath);
            }
        }
        return files;
    }

    private async Task ProcessFileChangeOldWayAsync(
        string filePath, FileChangeType changeType, CancellationToken ct, InProcessParserOutcomes outcomes)
    {
        // Fallback for extensions without a worker - use the original single-file approach
        var extension = Path.GetExtension(filePath);
        var parser = _parsers.FirstOrDefault(p =>
            p.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));

        if (parser == null)
        {
            _logger.LogDebug($"No parser found for file: {filePath}");
            return;
        }

        var metadata = new FileMetadata
        {
            FilePath = filePath,
            Status = FileProcessingStatus.Processing,
        };

        if (parser is IParserDiagnostics { IsAvailable: false } diagnostics)
        {
            var reason = diagnostics.UnavailableReason
                ?? $"Parser '{diagnostics.DisplayName}' is unavailable.";
            metadata.Status = FileProcessingStatus.Failed;
            metadata.ErrorMessage = reason;
            await _fileMetadataRepository.UpsertAsync(metadata, ct);
            // Unavailable is terminal for the whole parser, not one file: report it
            // immediately so it wins over any per-batch Ready/Failed aggregate.
            ReportParserSession(parser, ParserSessionState.Unavailable, reason);
            throw new InvalidOperationException(reason);
        }

        try
        {
            if (changeType != FileChangeType.Deleted)
            {
                metadata.LastModified = File.GetLastWriteTimeUtc(filePath);
            }
            await _fileMetadataRepository.UpsertAsync(metadata, ct);

            if (changeType == FileChangeType.Deleted)
            {
                await HandleFileDeletedAsync(filePath, ct);
                await _fileMetadataRepository.DeleteAsync(filePath, ct);
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            metadata.FileHash = ComputeFileHash(content);
            var graph = parser.ParseFile(filePath, content);
            await UpdateGraphAsync(filePath, graph, ct);

            metadata.Status = FileProcessingStatus.Completed;
            metadata.LastScanned = DateTime.UtcNow;
            await _fileMetadataRepository.UpsertAsync(metadata, ct);
            outcomes.RecordSuccess(parser);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing file: {filePath}");
            outcomes.RecordFailure(parser, $"{filePath}: {ex.Message}");
            metadata.Status = FileProcessingStatus.Failed;
            metadata.ErrorMessage = ex.Message;
            await _fileMetadataRepository.UpsertAsync(metadata, ct);
            throw;
        }
    }

    private List<string> GetAllSupportedExtensions()
        => _parsers.SelectMany(p => p.SupportedExtensions)
            .Concat(_workerService?.OwnedExtensions ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Groups scan files by their owning worker; the remainder goes to
    /// in-process parsers.</summary>
    private (Dictionary<string, List<string>> WorkerGroups, List<string> OtherFiles) PartitionByOwner(
        IEnumerable<string> files)
    {
        var workerGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var otherFiles = new List<string>();
        foreach (var file in files)
        {
            if (TryGetWorkerForPath(file, out var parserId))
            {
                (workerGroups.TryGetValue(parserId, out var group)
                    ? group
                    : workerGroups[parserId] = []).Add(file);
            }
            else
            {
                otherFiles.Add(file);
            }
        }
        return (workerGroups, otherFiles);
    }

    public async Task PerformInitialScanAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken ct)
        => await RunReconciliationAsync(
            () => PerformInitialScanCoreAsync(rootPath, progressReporter, ct), ct);

    private async Task PerformInitialScanCoreAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken ct)
    {
        _logger.LogInformation($"Performing full scan of: {rootPath}");
        var startTime = DateTime.UtcNow;

        // Full rescan rebuilds everything, but the previous complete graph stays
        // queryable until the new generation commits — no upfront clear.
        var files = _fileSelector.EnumerateIncludedFiles(GetAllSupportedExtensions()).ToList();

        _logger.LogInformation($"Found {files.Count} files to process");

        var (workerGroups, otherFiles) = PartitionByOwner(files);
        var totalCount = files.Count;
        var processedCount = 0;
        var failures = new List<Exception>();

        if (_workerService is not null)
        {
            // Every discovered worker gets a generation, even an empty one: deleting
            // the last file of a language must still replace that scope with nothing.
            foreach (var parserId in WorkerParserIds())
            {
                var groupFiles = workerGroups.GetValueOrDefault(parserId) ?? [];
                try
                {
                    foreach (var file in groupFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        await _fileMetadataRepository.UpsertAsync(new FileMetadata
                        {
                            FilePath = file,
                            Status = FileProcessingStatus.Processing,
                            LastModified = File.GetLastWriteTimeUtc(file),
                        }, ct);
                    }

                    await _workerService.IndexWorkspaceAsync(parserId, groupFiles, ct);

                    foreach (var file in groupFiles)
                    {
                        await _fileMetadataRepository.UpsertAsync(new FileMetadata
                        {
                            FilePath = file,
                            Status = FileProcessingStatus.Completed,
                            LastModified = File.GetLastWriteTimeUtc(file),
                            LastScanned = DateTime.UtcNow,
                        }, ct);
                        var current = Interlocked.Increment(ref processedCount);
                        progressReporter?.ReportProgress(current, totalCount, file);
                    }

                    if (groupFiles.Count > 0)
                    {
                        _logger.LogInformation("Worker '{ParserId}' indexed {Count} files.", parserId, groupFiles.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker '{ParserId}' failed to index {Count} files.",
                        parserId, groupFiles.Count);
                    foreach (var file in groupFiles)
                    {
                        await _fileMetadataRepository.UpsertAsync(new FileMetadata
                        {
                            FilePath = file,
                            Status = FileProcessingStatus.Failed,
                            ErrorMessage = ex.Message,
                        }, ct);
                        progressReporter?.ReportError(file, ex.Message);
                    }
                    // Remember the failure but keep scanning other languages: one
                    // broken worker must not hide every other parser's results.
                    failures.Add(ex);
                }
            }
        }

        if (otherFiles.Count > 0)
        {
            var outcomes = new InProcessParserOutcomes();
            await Parallel.ForEachAsync(otherFiles, ct, async (file, innerCt) =>
            {
                try
                {
                    await ProcessFileChangeOldWayAsync(file, FileChangeType.Created, innerCt, outcomes);
                    var current = Interlocked.Increment(ref processedCount);
                    progressReporter?.ReportProgress(current, totalCount, file);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One broken file (or one unavailable parser) must not abort the
                    // scan: the file is already marked Failed, remember the error and
                    // keep going so pruning and progress still complete.
                    progressReporter?.ReportError(file, ex.Message);
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            });
            outcomes.Flush(this);
        }

        await PruneMissingFilesAsync(files, ct);

        var elapsed = DateTime.UtcNow - startTime;
        progressReporter?.ReportComplete(processedCount, elapsed);
        _logger.LogInformation("Full scan complete");

        if (failures.Count > 0)
        {
            throw failures[0];
        }
    }

    private IEnumerable<string> WorkerParserIds()
    {
        if (_workerService is null) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var extension in _workerService.OwnedExtensions)
        {
            if (_workerService.TryGetParserForExtension(extension, out var parserId) && seen.Add(parserId))
            {
                yield return parserId;
            }
        }
    }

    /// <summary>
    /// After a full rescan, removes graph facts and metadata for files that no longer
    /// exist in the watched tree. This replaces the old clear-then-rebuild approach.
    /// </summary>
    private async Task PruneMissingFilesAsync(IReadOnlyCollection<string> presentFiles, CancellationToken ct)
    {
        if (_repositoryFactory.CreateGraphRepository() is IGenerationalGraphStore store)
        {
            var pruned = await store.PruneFilesNotPresentAsync(presentFiles, ct);
            if (pruned > 0)
            {
                _logger.LogInformation("Pruned {Count} nodes from files no longer present.", pruned);
            }
        }

        var present = new HashSet<string>(presentFiles, PathComparer);
        foreach (var metadata in await _fileMetadataRepository.GetAllAsync(ct))
        {
            if (!present.Contains(metadata.FilePath))
            {
                await _fileMetadataRepository.DeleteAsync(metadata.FilePath, ct);
            }
        }
    }

    public async Task PerformResumableScanAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken ct)
        => await RunReconciliationAsync(
            () => PerformResumableScanCoreAsync(rootPath, progressReporter, ct), ct);

    private async Task PerformResumableScanCoreAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken ct)
    {
        _logger.LogInformation($"Performing resumable scan of: {rootPath}");
        var startTime = DateTime.UtcNow;

        var allFiles = _fileSelector.EnumerateIncludedFiles(GetAllSupportedExtensions()).ToList();

        _logger.LogInformation($"Found {allFiles.Count} total files");

        // Check which files need processing
        var filesToProcess = new List<(string path, DateTime lastModified)>();
        foreach (var file in allFiles)
        {
            try
            {
                var lastModified = File.GetLastWriteTimeUtc(file);
                if (await _fileMetadataRepository.NeedsProcessingAsync(file, lastModified, ct))
                {
                    filesToProcess.Add((file, lastModified));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Could not check file: {file}");
                filesToProcess.Add((file, DateTime.UtcNow));
            }
        }

        _logger.LogInformation($"Need to process {filesToProcess.Count} files");

        // Files deleted while the instance was down never show up in filesToProcess, so
        // stale graph facts and metadata must be pruned here as well — even when there
        // is otherwise nothing to do.
        await PruneMissingFilesAsync(allFiles, ct);

        if (filesToProcess.Count == 0)
        {
            progressReporter?.ReportComplete(0, DateTime.UtcNow - startTime);
            return;
        }

        // Create initial metadata for files that don't have any
        var newMetadata = new List<FileMetadata>();
        foreach (var (file, lastModified) in filesToProcess)
        {
            var existing = await _fileMetadataRepository.GetByFilePathAsync(file, ct);
            if (existing == null)
            {
                newMetadata.Add(new FileMetadata
                {
                    FilePath = file,
                    LastModified = lastModified,
                    Status = FileProcessingStatus.Pending
                });
            }
        }

        if (newMetadata.Count != 0)
        {
            await _fileMetadataRepository.UpsertBatchAsync(newMetadata, ct);
        }

        var (workerGroups, _) = PartitionByOwner(filesToProcess.Select(f => f.path));
        var otherFilesToProcess = filesToProcess
            .Where(f => !TryGetWorkerForPath(f.path, out _))
            .ToList();
        var processedCount = 0;
        var totalCount = filesToProcess.Count;
        var failures = new List<Exception>();

        // Worker-owned languages resolve cross-file semantics inside the worker, so a
        // resumable scan feeds it the complete current file set for the language, not
        // just the changed subset — the graph facts of unchanged files depend on it.
        foreach (var (parserId, changedFiles) in workerGroups)
        {
            var ownedFiles = allFiles.Where(f =>
                TryGetWorkerForPath(f, out var owner)
                && string.Equals(owner, parserId, StringComparison.Ordinal)).ToList();
            try
            {
                await _workerService!.IndexWorkspaceAsync(parserId, ownedFiles, ct);

                foreach (var file in changedFiles)
                {
                    await _fileMetadataRepository.UpsertAsync(new FileMetadata
                    {
                        FilePath = file,
                        Status = FileProcessingStatus.Completed,
                        LastModified = File.GetLastWriteTimeUtc(file),
                        LastScanned = DateTime.UtcNow,
                    }, ct);
                    var current = Interlocked.Increment(ref processedCount);
                    progressReporter?.ReportProgress(current, totalCount, file);
                }
                _logger.LogInformation(
                    "Worker '{ParserId}' reindexed {Total} files ({Changed} changed).",
                    parserId, ownedFiles.Count, changedFiles.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker '{ParserId}' failed the resumable reindex.", parserId);
                foreach (var file in changedFiles)
                {
                    await _fileMetadataRepository.UpsertAsync(new FileMetadata
                    {
                        FilePath = file,
                        Status = FileProcessingStatus.Failed,
                        ErrorMessage = ex.Message,
                    }, ct);
                    progressReporter?.ReportError(file, ex.Message);
                }
                failures.Add(ex);
            }
        }

        // Process other files individually
        if (otherFilesToProcess.Count > 0)
        {
            var outcomes = new InProcessParserOutcomes();
            await Parallel.ForEachAsync(otherFilesToProcess, new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, async (fileInfo, innerCt) =>
            {
                try
                {
                    await ProcessFileChangeOldWayAsync(fileInfo.path, FileChangeType.Created, innerCt, outcomes);
                    var current = Interlocked.Increment(ref processedCount);
                    progressReporter?.ReportProgress(current, totalCount, fileInfo.path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Same resilience contract as the full scan: record, continue.
                    progressReporter?.ReportError(fileInfo.path, ex.Message);
                    lock (failures)
                    {
                        failures.Add(ex);
                    }
                }
            });
            outcomes.Flush(this);
        }

        var elapsed = DateTime.UtcNow - startTime;
        progressReporter?.ReportComplete(processedCount, elapsed);
        _logger.LogInformation($"Resumable scan complete: {processedCount} files processed in {elapsed}");

        if (failures.Count > 0)
        {
            throw failures[0];
        }
    }

    private async Task RunReconciliationAsync(Func<Task> operation, CancellationToken ct)
    {
        // Worker deltas use scoped immutable commits and can be staged safely. Legacy
        // in-process parsers still mutate repository seams directly, so retain their
        // historical per-file resilience until they are extracted to workers.
        var store = !_parsers.Any()
            ? _repositoryFactory.CreateGraphRepository() as IGenerationalGraphStore
            : null;
        store?.BeginReconciliation();
        try
        {
            await operation();
            store?.CommitReconciliation();
        }
        catch
        {
            store?.RollbackReconciliation();
            // Metadata written while staging must not claim the rolled-back graph is
            // current. Mark surviving entries pending so the next resumable scan
            // replays them; missing files are still pruned on that scan.
            if (store is not null)
            {
                var pending = (await _fileMetadataRepository.GetAllAsync(CancellationToken.None))
                    .Select(metadata => new FileMetadata
                    {
                        FilePath = metadata.FilePath,
                        LastModified = metadata.LastModified,
                        LastScanned = metadata.LastScanned,
                        FileHash = metadata.FileHash,
                        Status = metadata.Status == FileProcessingStatus.Failed
                            ? FileProcessingStatus.Failed
                            : FileProcessingStatus.Pending,
                        ErrorMessage = metadata.Status == FileProcessingStatus.Failed
                            ? metadata.ErrorMessage
                            : null,
                    }).ToList();
                await _fileMetadataRepository.UpsertBatchAsync(pending, CancellationToken.None);
            }
            throw;
        }
    }

    private async Task UpdateGraphAsync(string filePath, CodeGraph graph, CancellationToken ct)
    {
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var edgeRepo = _repositoryFactory.CreateEdgeRepository();

        // First, delete existing nodes and edges for this file
        var existingNodes = await nodeRepo.GetAllAsync();
        var fileNodes = existingNodes.Where(n => n.FilePath == filePath).ToList();

        foreach (var node in fileNodes)
        {
            if (node.Id != null)
            {
                await edgeRepo.DeleteByNodeIdAsync(node.Id, ct);
                await nodeRepo.DeleteAsync(node.Id, ct);
            }
        }

        // Now add new nodes and edges
        foreach (var node in graph.Nodes)
        {
            await nodeRepo.UpsertAsync(node);
        }

        foreach (var edge in graph.Edges)
        {
            await edgeRepo.UpsertAsync(edge);
        }

        _logger.LogDebug($"Updated graph for {filePath}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
    }

    private async Task HandleFileDeletedAsync(string filePath, CancellationToken ct)
    {
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var edgeRepo = _repositoryFactory.CreateEdgeRepository();

        // Delete all nodes and edges for this file
        var existingNodes = await nodeRepo.GetAllAsync();
        var fileNodes = existingNodes.Where(n => n.FilePath == filePath).ToList();

        foreach (var node in fileNodes)
        {
            if (node.Id != null)
            {
                await edgeRepo.DeleteByNodeIdAsync(node.Id, ct);
                await nodeRepo.DeleteAsync(node.Id, ct);
            }
        }

        _logger.LogDebug($"Deleted {fileNodes.Count} nodes for file: {filePath}");
    }

    private static string ComputeFileHash(string content)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToBase64String(hashBytes);
    }
}
