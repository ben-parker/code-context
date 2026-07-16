using System.Text.Json;
using CodeContext.Core.Models;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Repositories.Kuzu;

public class KuzuFileMetadataRepository : IFileMetadataRepository
{
    private readonly IKuzuApi _kuzuApi;
    private readonly ILogger<KuzuFileMetadataRepository> _logger;

    public KuzuFileMetadataRepository(IKuzuApi kuzuApi, ILogger<KuzuFileMetadataRepository> logger)
    {
        _kuzuApi = kuzuApi;
        _logger = logger;
    }

    public async Task<FileMetadata?> GetByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        return await Task.Run(() =>
        {
            var resultJson = _kuzuApi.GetFileMetadata(filePath);

            if (string.IsNullOrEmpty(resultJson))
            {
                _logger.LogWarning("Kuzu API returned empty or null JSON for file path: {FilePath}", filePath);
                return null;
            }

            try
            {
                var dto = KuzuResponseParser.ParseResponse(resultJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.FileMetadataDto);
                return dto != null ? ConvertFromDto(dto) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse FileMetadata response for {FilePath}. Raw JSON: {Json}", filePath, resultJson);
                return null;
            }
        }, cancellationToken);
    }

    public async Task<IEnumerable<FileMetadata>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var resultJson = _kuzuApi.GetAllFileMetadata();
            var dtos = KuzuResponseParser.ParseResponse(resultJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListFileMetadataDto);
            return dtos?.Select(ConvertFromDto).ToList() ?? new List<FileMetadata>();
        }, cancellationToken);
    }

    public async Task<IEnumerable<FileMetadata>> GetByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var resultJson = _kuzuApi.GetFileMetadataByStatus(status.ToString());
            var dtos = KuzuResponseParser.ParseResponse(resultJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListFileMetadataDto);
            return dtos?.Select(ConvertFromDto).ToList() ?? new List<FileMetadata>();
        }, cancellationToken);
    }

    public async Task UpsertAsync(FileMetadata metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        await Task.Run(() =>
        {
            var metadataDto = ConvertToMetadataDto(metadata);
            var metadataJson = JsonSerializer.Serialize(metadataDto, CodeContextJsonContext.Default.FileMetadataDto);
            _kuzuApi.UpsertFileMetadata(metadataJson);
        }, cancellationToken);
    }

    public async Task UpsertBatchAsync(IEnumerable<FileMetadata> metadataList, CancellationToken cancellationToken = default)
    {
        foreach (var metadata in metadataList)
        {
            await UpsertAsync(metadata, cancellationToken);
        }
    }

    public async Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        
        await Task.Run(() =>
        {
            _kuzuApi.DeleteFileMetadata(filePath);
        }, cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => _kuzuApi.ClearFileMetadata(), cancellationToken);
    }

    public async Task<int> GetCountByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var resultJson = _kuzuApi.GetFileMetadataCountByStatus(status.ToString());
            if (string.IsNullOrEmpty(resultJson))
                return 0;
            
            try
            {
                var countResponse = JsonSerializer.Deserialize(resultJson, CodeContextJsonContext.Default.CountResponseDto);
                return countResponse?.Count ?? 0;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse count response for status {Status}. Raw JSON: {Json}", status, resultJson);
                return 0;
            }
        }, cancellationToken);
    }

    public async Task<bool> NeedsProcessingAsync(string filePath, DateTime lastModified, CancellationToken cancellationToken = default)
    {
        var metadata = await GetByFilePathAsync(filePath, cancellationToken);
        if (metadata == null)
        {
            _logger.LogDebug("File {FilePath} needs processing: No metadata found", filePath);
            return true;
        }

        // File needs processing if:
        // 1. Status is not Completed, OR
        // 2. File has been modified after our last scan (allowing for 1 second precision issues)
        var timeDifference = lastModified - metadata.LastModified;
        var hasBeenModified = timeDifference.TotalSeconds > 1;
        
        var needsProcessing = metadata.Status != FileProcessingStatus.Completed || hasBeenModified;
        
        _logger.LogDebug("File {FilePath} needs processing: {NeedsProcessing}. Status: {Status}, StoredTime: {StoredTime}, FileTime: {FileTime}, TimeDiff: {TimeDiff}s", 
            filePath, needsProcessing, metadata.Status, metadata.LastModified, lastModified, timeDifference.TotalSeconds);
        
        return needsProcessing;
    }

    private static FileMetadataDto ConvertToMetadataDto(FileMetadata metadata)
    {
        return new FileMetadataDto(
            metadata.FilePath,
            metadata.LastModified.ToString("O"), // ISO 8601 format
            metadata.LastScanned.ToString("O"),
            metadata.FileHash,
            metadata.Status.ToString(),
            metadata.ErrorMessage
        );
    }

    private static FileMetadata ConvertFromDto(FileMetadataDto dto)
    {
        return new FileMetadata
        {
            FilePath = dto.FilePath,
            LastModified = DateTime.TryParse(dto.LastModified, out var lastModified) ? lastModified : DateTime.MinValue,
            LastScanned = DateTime.TryParse(dto.LastScanned, out var lastScanned) ? lastScanned : DateTime.MinValue,
            FileHash = dto.FileHash,
            Status = Enum.TryParse<FileProcessingStatus>(dto.Status, out var status) ? status : FileProcessingStatus.Pending,
            ErrorMessage = dto.ErrorMessage
        };
    }
}