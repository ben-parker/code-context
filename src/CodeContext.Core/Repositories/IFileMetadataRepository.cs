using CodeContext.Core.Models;

namespace CodeContext.Core.Repositories;

public interface IFileMetadataRepository
{
    Task<FileMetadata?> GetByFilePathAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IEnumerable<FileMetadata>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IEnumerable<FileMetadata>> GetByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default);
    Task UpsertAsync(FileMetadata metadata, CancellationToken cancellationToken = default);
    Task UpsertBatchAsync(IEnumerable<FileMetadata> metadataList, CancellationToken cancellationToken = default);
    Task DeleteAsync(string filePath, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    Task<int> GetCountByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default);
    Task<bool> NeedsProcessingAsync(string filePath, DateTime lastModified, CancellationToken cancellationToken = default);
}

/// <summary>Cheap maintained aggregates for status polling on hot-path stores.</summary>
public interface IFileMetadataStatisticsProvider
{
    FileMetadataStatistics GetStatistics();
}

public sealed record FileMetadataStatistics(
    int FileCount,
    IReadOnlyDictionary<FileProcessingStatus, int> FilesByStatus,
    IReadOnlyDictionary<string, int> FilesByExtension,
    DateTime? LastScanAt);
