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
/// Every supported file is owned by a discovered language worker and routed through
/// <see cref="ILanguageWorkerService"/> (the worker parses and streams normalized
/// deltas back, committed atomically as a new generation). Files whose extension has no
/// worker are skipped.
/// </summary>
public class GraphUpdateService : IGraphUpdateService
{
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly ILogger<GraphUpdateService> _logger;
    private readonly CodeContextOptions _options;
    private readonly IFileMetadataRepository _fileMetadataRepository;
    private readonly ILanguageWorkerService? _workerService;
    private readonly IRepositoryFileSelector _fileSelector;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public GraphUpdateService(
        IRepositoryFactory repositoryFactory,
        IOptions<CodeContextOptions> options,
        ILogger<GraphUpdateService> logger,
        IFileMetadataRepository fileMetadataRepository,
        ILanguageWorkerService? workerService = null,
        IRepositoryFileSelector? fileSelector = null)
    {
        _repositoryFactory = repositoryFactory;
        _options = options.Value;
        _logger = logger;
        _fileMetadataRepository = fileMetadataRepository;
        _workerService = workerService;
        _fileSelector = fileSelector ?? new RepositoryFileSelector(options);
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

        foreach (var change in changes)
        {
            // The same selector governs explicit refresh and watcher paths. An
            // excluded file is forwarded as a deletion so stale facts cannot remain
            // after a rule change or a direct refresh of an ignored path.
            var effectiveChange = !_fileSelector.IsIncluded(change.Path)
                ? new FileChange(change.Path, FileChangeType.Deleted)
                : change;
            if (TryGetWorkerForPath(change.Path, out var parserId))
            {
                (workerBatches.TryGetValue(parserId, out var batch)
                    ? batch
                    : workerBatches[parserId] = []).Add(effectiveChange);
            }
            else
            {
                _logger.LogDebug("No worker for extension of file: {FilePath}", change.Path);
            }
        }

        // One failing worker must not abort the rest of the batch: every worker gets its
        // attempt, per-file metadata records failures, and the first failure is rethrown
        // at the end so callers still see it.
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

    private List<string> GetAllSupportedExtensions()
        => (_workerService?.OwnedExtensions ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Groups scan files by their owning worker. Enumeration is already filtered
    /// to worker-owned extensions, so every file here belongs to some worker.</summary>
    private Dictionary<string, List<string>> PartitionByOwner(IEnumerable<string> files)
    {
        var workerGroups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (TryGetWorkerForPath(file, out var parserId))
            {
                (workerGroups.TryGetValue(parserId, out var group)
                    ? group
                    : workerGroups[parserId] = []).Add(file);
            }
        }
        return workerGroups;
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

        var workerGroups = PartitionByOwner(files);
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

        var workerGroups = PartitionByOwner(filesToProcess.Select(f => f.path));
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
        // Worker deltas use scoped immutable commits, so the whole reconciliation stages
        // safely: the previous complete graph stays queryable until the batch commits.
        var store = _repositoryFactory.CreateGraphRepository() as IGenerationalGraphStore;
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
}
