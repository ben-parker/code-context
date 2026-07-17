using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class InMemoryFileMetadataRepositoryTests
{
    private readonly InMemoryFileMetadataRepository _repository;

    public InMemoryFileMetadataRepositoryTests()
    {
        _repository = new InMemoryFileMetadataRepository();
    }

    [Fact]
    public async Task GetByFilePathAsync_WithValidPath_ReturnsMetadata()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.GetByFilePathAsync("/test/path.cs");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("/test/path.cs", result.FilePath);
        Assert.Equal(FileProcessingStatus.Completed, result.Status);
    }

    [Fact]
    public async Task GetByFilePathAsync_WithNonExistentPath_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByFilePathAsync("/non/existent/path.cs");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByFilePathAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _repository.GetByFilePathAsync("/test/path.cs", cts.Token);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("/test/path.cs", result.FilePath);
    }

    [Fact]
    public async Task GetAllAsync_WithEmptyRepository_ReturnsEmptyList()
    {
        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllAsync_WithPopulatedRepository_ReturnsAllMetadata()
    {
        // Arrange
        var metadata1 = CreateTestMetadata("/test/path1.cs");
        var metadata2 = CreateTestMetadata("/test/path2.cs");
        
        await _repository.UpsertAsync(metadata1);
        await _repository.UpsertAsync(metadata2);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count());
        Assert.Contains(result, m => m.FilePath == "/test/path1.cs");
        Assert.Contains(result, m => m.FilePath == "/test/path2.cs");
    }

    [Fact]
    public async Task GetAllAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _repository.GetAllAsync(cts.Token);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task GetByStatusAsync_WithMatchingStatus_ReturnsFilteredResults()
    {
        // Arrange
        var completedMetadata = CreateTestMetadata("/test/completed.cs", FileProcessingStatus.Completed);
        var processingMetadata = CreateTestMetadata("/test/processing.cs", FileProcessingStatus.Processing);
        var failedMetadata = CreateTestMetadata("/test/failed.cs", FileProcessingStatus.Failed);
        
        await _repository.UpsertAsync(completedMetadata);
        await _repository.UpsertAsync(processingMetadata);
        await _repository.UpsertAsync(failedMetadata);

        // Act
        var result = await _repository.GetByStatusAsync(FileProcessingStatus.Completed);

        // Assert
        Assert.Single(result);
        Assert.Equal("/test/completed.cs", result.First().FilePath);
    }

    [Fact]
    public async Task GetByStatusAsync_WithNoMatchingStatus_ReturnsEmptyList()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.GetByStatusAsync(FileProcessingStatus.Processing);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByStatusAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _repository.GetByStatusAsync(FileProcessingStatus.Completed, cts.Token);

        // Assert
        Assert.Single(result);
    }

    [Fact]
    public async Task UpsertAsync_WithNewMetadata_InsertsMetadata()
    {
        // Arrange
        var metadata = CreateTestMetadata("/new/path.cs");

        // Act
        await _repository.UpsertAsync(metadata);

        // Assert
        var result = await _repository.GetByFilePathAsync("/new/path.cs");
        Assert.NotNull(result);
        Assert.Equal("/new/path.cs", result.FilePath);
    }

    [Fact]
    public async Task UpsertAsync_WithExistingMetadata_UpdatesMetadata()
    {
        // Arrange
        var originalMetadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Processing);
        await _repository.UpsertAsync(originalMetadata);

        var updatedMetadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);

        // Act
        await _repository.UpsertAsync(updatedMetadata);

        // Assert
        var result = await _repository.GetByFilePathAsync("/test/path.cs");
        Assert.NotNull(result);
        Assert.Equal(FileProcessingStatus.Completed, result.Status);
    }

    [Fact]
    public async Task UpsertAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.UpsertAsync(metadata, cts.Token);

        // Assert
        var result = await _repository.GetByFilePathAsync("/test/path.cs");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task UpsertBatchAsync_WithMultipleMetadata_InsertsAll()
    {
        // Arrange
        var metadata1 = CreateTestMetadata("/test/path1.cs");
        var metadata2 = CreateTestMetadata("/test/path2.cs");
        var metadata3 = CreateTestMetadata("/test/path3.cs");
        
        var metadataList = new List<FileMetadata> { metadata1, metadata2, metadata3 };

        // Act
        await _repository.UpsertBatchAsync(metadataList);

        // Assert
        var allMetadata = await _repository.GetAllAsync();
        Assert.Equal(3, allMetadata.Count());
    }

    [Fact]
    public async Task UpsertBatchAsync_WithEmptyList_DoesNotThrow()
    {
        // Arrange
        var emptyList = new List<FileMetadata>();

        // Act & Assert
        var exception = await Record.ExceptionAsync(() => _repository.UpsertBatchAsync(emptyList));
        Assert.Null(exception);
    }

    [Fact]
    public async Task UpsertBatchAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        var metadataList = new List<FileMetadata> { metadata };
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.UpsertBatchAsync(metadataList, cts.Token);

        // Assert
        var result = await _repository.GetByFilePathAsync("/test/path.cs");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingPath_RemovesMetadata()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);

        // Act
        await _repository.DeleteAsync("/test/path.cs");

        // Assert
        var result = await _repository.GetByFilePathAsync("/test/path.cs");
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentPath_DoesNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(() => _repository.DeleteAsync("/non/existent/path.cs"));
        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteAsync("/test/path.cs", cts.Token);

        // Assert
        var result = await _repository.GetByFilePathAsync("/test/path.cs");
        Assert.Null(result);
    }

    [Fact]
    public async Task ClearAsync_WithPopulatedRepository_RemovesAllMetadata()
    {
        // Arrange
        var metadata1 = CreateTestMetadata("/test/path1.cs");
        var metadata2 = CreateTestMetadata("/test/path2.cs");
        
        await _repository.UpsertAsync(metadata1);
        await _repository.UpsertAsync(metadata2);

        // Act
        await _repository.ClearAsync();

        // Assert
        var result = await _repository.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ClearAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs");
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.ClearAsync(cts.Token);

        // Assert
        var result = await _repository.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetCountByStatusAsync_WithMatchingStatus_ReturnsCorrectCount()
    {
        // Arrange
        var completedMetadata1 = CreateTestMetadata("/test/completed1.cs", FileProcessingStatus.Completed);
        var completedMetadata2 = CreateTestMetadata("/test/completed2.cs", FileProcessingStatus.Completed);
        var processingMetadata = CreateTestMetadata("/test/processing.cs", FileProcessingStatus.Processing);
        
        await _repository.UpsertAsync(completedMetadata1);
        await _repository.UpsertAsync(completedMetadata2);
        await _repository.UpsertAsync(processingMetadata);

        // Act
        var result = await _repository.GetCountByStatusAsync(FileProcessingStatus.Completed);

        // Assert
        Assert.Equal(2, result);
    }

    [Fact]
    public async Task GetCountByStatusAsync_WithNoMatchingStatus_ReturnsZero()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.GetCountByStatusAsync(FileProcessingStatus.Processing);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task GetCountByStatusAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _repository.GetCountByStatusAsync(FileProcessingStatus.Completed, cts.Token);

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task NeedsProcessingAsync_WithNonExistentFile_ReturnsTrue()
    {
        // Act
        var result = await _repository.NeedsProcessingAsync("/non/existent/path.cs", DateTime.Now);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task NeedsProcessingAsync_WithCompletedFileAndOlderLastModified_ReturnsFalse()
    {
        // Arrange
        var lastModified = DateTime.Now.AddHours(-1);
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed, lastModified);
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.NeedsProcessingAsync("/test/path.cs", lastModified.AddMinutes(-30));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task NeedsProcessingAsync_WithCompletedFileAndNewerLastModified_ReturnsTrue()
    {
        // Arrange
        var lastModified = DateTime.Now.AddHours(-1);
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed, lastModified);
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.NeedsProcessingAsync("/test/path.cs", lastModified.AddMinutes(30));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task NeedsProcessingAsync_WithNonCompletedStatus_ReturnsTrue()
    {
        // Arrange
        var lastModified = DateTime.Now.AddHours(-1);
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Processing, lastModified);
        await _repository.UpsertAsync(metadata);

        // Act
        var result = await _repository.NeedsProcessingAsync("/test/path.cs", lastModified.AddMinutes(-30));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task NeedsProcessingAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var metadata = CreateTestMetadata("/test/path.cs", FileProcessingStatus.Completed);
        await _repository.UpsertAsync(metadata);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _repository.NeedsProcessingAsync("/test/path.cs", DateTime.Now, cts.Token);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConcurrentOperations_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var operationCount = 100;

        // Act - Concurrent upserts
        for (int i = 0; i < operationCount; i++)
        {
            var metadata = CreateTestMetadata($"/test/path{i}.cs");
            tasks.Add(_repository.UpsertAsync(metadata));
        }

        await Task.WhenAll(tasks);

        // Assert
        var allMetadata = await _repository.GetAllAsync();
        Assert.Equal(operationCount, allMetadata.Count());
    }

    [Fact]
    public async Task ConcurrentBatchOperations_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var batchCount = 10;
        var itemsPerBatch = 10;

        // Act - Concurrent batch upserts
        for (int i = 0; i < batchCount; i++)
        {
            var batch = new List<FileMetadata>();
            for (int j = 0; j < itemsPerBatch; j++)
            {
                batch.Add(CreateTestMetadata($"/test/batch{i}_path{j}.cs"));
            }
            tasks.Add(_repository.UpsertBatchAsync(batch));
        }

        await Task.WhenAll(tasks);

        // Assert
        var allMetadata = await _repository.GetAllAsync();
        Assert.Equal(batchCount * itemsPerBatch, allMetadata.Count());
    }

    [Fact]
    public async Task MaintainedStatistics_TrackInsertUpdateAndDelete()
    {
        var older = CreateTestMetadata("/test/a.cs", FileProcessingStatus.Processing);
        older.LastScanned = DateTime.UtcNow.AddMinutes(-2);
        var newer = CreateTestMetadata("/test/b.ts", FileProcessingStatus.Completed);
        newer.LastScanned = DateTime.UtcNow.AddMinutes(-1);
        await _repository.UpsertAsync(older);
        await _repository.UpsertAsync(newer);

        var initial = _repository.GetStatistics();
        Assert.Equal(2, initial.FileCount);
        Assert.Equal(1, initial.FilesByStatus[FileProcessingStatus.Processing]);
        Assert.Equal(1, initial.FilesByExtension[".ts"]);
        Assert.Equal(newer.LastScanned, initial.LastScanAt);

        var completed = CreateTestMetadata("/test/a.cs", FileProcessingStatus.Completed);
        completed.LastScanned = DateTime.UtcNow;
        await _repository.UpsertAsync(completed);
        await _repository.DeleteAsync("/test/b.ts");

        var updated = _repository.GetStatistics();
        Assert.Equal(1, updated.FileCount);
        Assert.False(updated.FilesByStatus.ContainsKey(FileProcessingStatus.Processing));
        Assert.Equal(1, updated.FilesByStatus[FileProcessingStatus.Completed]);
        Assert.Equal(completed.LastScanned, updated.LastScanAt);
    }

    private static FileMetadata CreateTestMetadata(string filePath, FileProcessingStatus status = FileProcessingStatus.Completed, DateTime? lastModified = null)
    {
        return new FileMetadata
        {
            FilePath = filePath,
            LastModified = lastModified ?? DateTime.Now.AddHours(-1),
            LastScanned = DateTime.Now,
            Status = status,
            ErrorMessage = status == FileProcessingStatus.Failed ? "Test error" : null
        };
    }
}
