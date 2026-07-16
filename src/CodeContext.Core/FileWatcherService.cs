using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CodeContext.Core.Services;

namespace CodeContext.Core;

/// <summary>
/// Watches the root path and forwards raw change notifications to the
/// <see cref="IIndexCoordinator"/>. It performs no debouncing and no parsing itself:
/// coalescing, ordering, and backpressure are the coordinator's job, so a change to one
/// path can never cancel another path's pending work.
/// </summary>
public class FileWatcherService : IHostedService, IDisposable
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly CodeContextOptions _options;
    private readonly IIndexCoordinator _coordinator;
    private readonly IScanStateService _scanState;
    private readonly IRepositoryFileSelector _fileSelector;
    private FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _watcherRecovery;
    private int _recoveryScheduled;

    public FileWatcherService(
        IOptions<CodeContextOptions> options,
        ILogger<FileWatcherService> logger,
        IIndexCoordinator coordinator,
        IScanStateService scanState,
        IRepositoryFileSelector? fileSelector = null)
    {
        _options = options.Value;
        _logger = logger;
        _coordinator = coordinator;
        _scanState = scanState;
        _fileSelector = fileSelector ?? new RepositoryFileSelector(options);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FileWatcherService starting.");

        if (string.IsNullOrEmpty(_options.RootPath) || !Directory.Exists(_options.RootPath))
        {
            // The coordinator reports the scan error; the watcher just has nothing to watch.
            _logger.LogError("RootPath '{RootPath}' is not valid; file watching disabled.", _options.RootPath);
            return Task.CompletedTask;
        }

        StartWatcher();
        return Task.CompletedTask;
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_options.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;
        _scanState.WatcherActive = true;

        _logger.LogInformation($"Watching for changes in: {_options.RootPath}");
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (ShouldIgnore(e.FullPath)) return;
        _logger.LogDebug("File change detected: {ChangeType} - {FullPath}", e.ChangeType, e.FullPath);

        var changeType = e.ChangeType switch
        {
            WatcherChangeTypes.Created => FileChangeType.Created,
            WatcherChangeTypes.Deleted => FileChangeType.Deleted,
            _ => FileChangeType.Changed,
        };
        Publish(e.FullPath, changeType);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogDebug("File renamed: {Old} to {New}", e.OldFullPath, e.FullPath);
        // A rename is a delete of the old path plus a (re)appearance of the new one;
        // both paths must reach the coordinator or one side of the rename is lost.
        if (!ShouldIgnore(e.OldFullPath)) Publish(e.OldFullPath, FileChangeType.Deleted);
        if (!ShouldIgnore(e.FullPath)) Publish(e.FullPath, FileChangeType.Changed);
    }

    private void Publish(string fullPath, FileChangeType changeType)
    {
        try
        {
            // FileSystemWatcher callbacks cannot be awaited. Block only when the bounded
            // coordinator channel is full so backpressure remains genuinely bounded;
            // spawning one fire-and-forget task per event would merely move the
            // unbounded queue into the thread pool and could reorder writes.
            var write = _coordinator.NotifyFileChangedAsync(fullPath, changeType, _lifetimeCts.Token);
            if (!write.IsCompletedSuccessfully)
            {
                write.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enqueue file change for {FullPath}.", fullPath);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error.");
        if (Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) == 0)
        {
            _watcherRecovery = RecoverFromWatcherErrorAsync();
        }
    }

    private async Task RecoverFromWatcherErrorAsync()
    {
        try
        {
            // An InternalBufferOverflowException means raw paths were lost. Queue a
            // complete generation as soon as any current scan finishes; retrying the
            // guarded request avoids falsely assuming the in-flight snapshot included
            // changes that happened after its enumeration.
            while (!_lifetimeCts.IsCancellationRequested)
            {
                if (await _coordinator.TryRequestFullRescanAsync(_lifetimeCts.Token) is not null)
                {
                    return;
                }
                await Task.Delay(250, _lifetimeCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule a recovery rescan after a file-watcher error.");
        }
        finally
        {
            Volatile.Write(ref _recoveryScheduled, 0);
        }
    }

    private bool ShouldIgnore(string path)
    {
        // Ignore files themselves are coordinator control events. They are never
        // parsed, but must trigger matcher invalidation and atomic reconciliation.
        if (_fileSelector.IsIgnoreFile(path)) return false;

        var extension = Path.GetExtension(path);
        if (_options.FilePatterns.Any()
            && !_options.FilePatterns.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return !_fileSelector.IsIncluded(path);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("FileWatcherService stopping.");
        _scanState.WatcherActive = false;
        _lifetimeCts.Cancel();
        _watcher?.Dispose();
        if (_watcherRecovery is { } recovery)
        {
            try { await recovery.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _lifetimeCts.Dispose();
        _watcher?.Dispose();
    }
}
