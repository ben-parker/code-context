using System.Collections.Concurrent;
using CodeContext.Core.Models;

namespace CodeContext.Core.Repositories;

public class InMemoryFileMetadataRepository : IFileMetadataRepository, IFileMetadataStatisticsProvider
{
    private readonly ConcurrentDictionary<string, FileMetadata> _metadata = new();
    private readonly object _statisticsLock = new();
    private readonly Dictionary<string, StatisticEntry> _statisticEntries = new();
    private readonly Dictionary<FileProcessingStatus, int> _statusCounts = new();
    private readonly Dictionary<string, int> _extensionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedDictionary<DateTime, int> _scanTimes = new();

    private sealed record StatisticEntry(FileProcessingStatus Status, string Extension, DateTime LastScanned);

    public Task<FileMetadata?> GetByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _metadata.TryGetValue(filePath, out var metadata);
        return Task.FromResult(metadata);
    }

    public Task<IEnumerable<FileMetadata>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<FileMetadata>>(_metadata.Values.ToList());
    }

    public Task<IEnumerable<FileMetadata>> GetByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
    {
        var result = _metadata.Values.Where(m => m.Status == status).ToList();
        return Task.FromResult<IEnumerable<FileMetadata>>(result);
    }

    public Task UpsertAsync(FileMetadata metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_statisticsLock)
        {
            if (_statisticEntries.Remove(metadata.FilePath, out var previous))
            {
                Adjust(previous, -1);
            }

            _metadata[metadata.FilePath] = metadata;
            var entry = new StatisticEntry(
                metadata.Status,
                Path.GetExtension(metadata.FilePath),
                metadata.LastScanned);
            _statisticEntries[metadata.FilePath] = entry;
            Adjust(entry, 1);
        }
        return Task.CompletedTask;
    }

    public async Task UpsertBatchAsync(IEnumerable<FileMetadata> metadataList, CancellationToken cancellationToken = default)
    {
        foreach (var metadata in metadataList)
        {
            await UpsertAsync(metadata, cancellationToken);
        }
    }

    public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_statisticsLock)
        {
            _metadata.TryRemove(filePath, out _);
            if (_statisticEntries.Remove(filePath, out var previous))
            {
                Adjust(previous, -1);
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_statisticsLock)
        {
            _metadata.Clear();
            _statisticEntries.Clear();
            _statusCounts.Clear();
            _extensionCounts.Clear();
            _scanTimes.Clear();
        }
        return Task.CompletedTask;
    }

    public FileMetadataStatistics GetStatistics()
    {
        lock (_statisticsLock)
        {
            return new FileMetadataStatistics(
                _statisticEntries.Count,
                new Dictionary<FileProcessingStatus, int>(_statusCounts),
                new Dictionary<string, int>(_extensionCounts, StringComparer.OrdinalIgnoreCase),
                _scanTimes.Count == 0 ? null : _scanTimes.Last().Key);
        }
    }

    private void Adjust(StatisticEntry entry, int delta)
    {
        AdjustCount(_statusCounts, entry.Status, delta);
        if (!string.IsNullOrEmpty(entry.Extension))
        {
            AdjustCount(_extensionCounts, entry.Extension, delta);
        }
        if (entry.LastScanned != default)
        {
            AdjustCount(_scanTimes, entry.LastScanned, delta);
        }
    }

    private static void AdjustCount<TKey>(IDictionary<TKey, int> counts, TKey key, int delta)
        where TKey : notnull
    {
        counts.TryGetValue(key, out var current);
        var next = current + delta;
        if (next == 0) counts.Remove(key);
        else counts[key] = next;
    }

    public Task<int> GetCountByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
    {
        var count = _metadata.Values.Count(m => m.Status == status);
        return Task.FromResult(count);
    }

    public async Task<bool> NeedsProcessingAsync(string filePath, DateTime lastModified, CancellationToken cancellationToken = default)
    {
        var metadata = await GetByFilePathAsync(filePath, cancellationToken);
        if (metadata == null)
        {
            return true;
        }

        return metadata.Status != FileProcessingStatus.Completed || 
               metadata.LastModified < lastModified;
    }
}
