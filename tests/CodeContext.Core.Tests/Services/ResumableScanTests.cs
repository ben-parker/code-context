using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Workers;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Services;

/// <summary>Resumable-scan behavior with C# routed through the real worker: changed
/// files drive the "needs processing" decision, but the worker reindexes its complete
/// file set so cross-file facts stay correct.</summary>
public class ResumableScanTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly IScanProgressReporter _mockProgressReporter;
    private CSharpWorkerPipeline _pipeline = null!;

    private IRepositoryFactory _repositoryFactory => _pipeline.RepositoryFactory;
    private GraphUpdateService _service => _pipeline.GraphUpdateService;
    private IFileMetadataRepository _fileMetadataRepository => _pipeline.FileMetadataRepository;

    public ResumableScanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _mockProgressReporter = Substitute.For<IScanProgressReporter>();
    }

    public Task InitializeAsync()
    {
        _pipeline = new CSharpWorkerPipeline(_tempDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _pipeline.DisposeAsync();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task PerformResumableScanAsync_FirstRun_ProcessesAllFiles()
    {
        // Arrange
        var file1 = Path.Combine(_tempDir, "File1.cs");
        var file2 = Path.Combine(_tempDir, "File2.cs");
        var file3 = Path.Combine(_tempDir, "File3.cs");
        
        await File.WriteAllTextAsync(file1, "public class Class1 { }");
        await File.WriteAllTextAsync(file2, "public class Class2 { }");
        await File.WriteAllTextAsync(file3, "public class Class3 { }");

        // Act
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        Assert.Equal(3, nodes.Count());

        var metadata = await _fileMetadataRepository.GetAllAsync();
        Assert.Equal(3, metadata.Count());
        Assert.All(metadata, m => Assert.Equal(FileProcessingStatus.Completed, m.Status));

        _mockProgressReporter.Received().ReportComplete(3, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task PerformResumableScanAsync_SecondRun_SkipsUnchangedFiles()
    {
        // Arrange
        var file1 = Path.Combine(_tempDir, "File1.cs");
        var file2 = Path.Combine(_tempDir, "File2.cs");
        
        await File.WriteAllTextAsync(file1, "public class Class1 { }");
        await File.WriteAllTextAsync(file2, "public class Class2 { }");

        // First scan
        await _service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None);

        // Act - Second scan without changes
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        _mockProgressReporter.Received().ReportComplete(0, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task PerformResumableScanAsync_FileModified_ReprocessesOnlyModifiedFile()
    {
        // Arrange
        var file1 = Path.Combine(_tempDir, "File1.cs");
        var file2 = Path.Combine(_tempDir, "File2.cs");
        
        await File.WriteAllTextAsync(file1, "public class Class1 { }");
        await File.WriteAllTextAsync(file2, "public class Class2 { }");

        // First scan
        await _service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None);

        // Modify one file
        await Task.Delay(10); // Ensure different timestamp
        await File.WriteAllTextAsync(file1, "public class Class1 { public void Method() { } }");

        // Act - Second scan after modification
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        _mockProgressReporter.Received().ReportComplete(1, Arg.Any<TimeSpan>());
        
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        var class1Node = nodes.FirstOrDefault(n => n.Name == "Class1");
        Assert.NotNull(class1Node);
        
        // Verify the method was found in the updated file
        var methodNode = nodes.FirstOrDefault(n => n.Name == "Method" && n.Type == "Method");
        Assert.NotNull(methodNode);
    }

    [Fact]
    public async Task PerformResumableScanAsync_NewFileAdded_ProcessesOnlyNewFile()
    {
        // Arrange
        var file1 = Path.Combine(_tempDir, "File1.cs");
        var file2 = Path.Combine(_tempDir, "File2.cs");
        
        await File.WriteAllTextAsync(file1, "public class Class1 { }");
        
        // First scan
        await _service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None);

        // Add new file
        await File.WriteAllTextAsync(file2, "public class Class2 { }");

        // Act - Second scan after adding new file
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        _mockProgressReporter.Received().ReportComplete(1, Arg.Any<TimeSpan>());
        
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        Assert.Equal(2, nodes.Count());
    }

    [Fact]
    public async Task PerformResumableScanAsync_ReportsProgress()
    {
        // Arrange
        var files = new string[10];
        for (int i = 0; i < 10; i++)
        {
            files[i] = Path.Combine(_tempDir, $"File{i}.cs");
            await File.WriteAllTextAsync(files[i], $"public class Class{i} {{ }}");
        }

        // Act
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        _mockProgressReporter.Received(10).ReportProgress(Arg.Any<int>(), 10, Arg.Any<string>());
        _mockProgressReporter.Received().ReportComplete(10, Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task PerformResumableScanAsync_HandlesInvalidSyntax()
    {
        // Arrange
        var file1 = Path.Combine(_tempDir, "Good.cs");
        var file2 = Path.Combine(_tempDir, "Invalid.cs");
        
        await File.WriteAllTextAsync(file1, "public class GoodClass { }");
        await File.WriteAllTextAsync(file2, "This is not valid C# code at all!");

        // Act
        await _service.PerformResumableScanAsync(_tempDir, _mockProgressReporter, CancellationToken.None);

        // Assert
        var metadata = await _fileMetadataRepository.GetAllAsync();
        var goodFile = metadata.FirstOrDefault(m => m.FilePath == file1);
        var badFile = metadata.FirstOrDefault(m => m.FilePath == file2);
        
        Assert.NotNull(goodFile);
        Assert.NotNull(badFile);
        Assert.Equal(FileProcessingStatus.Completed, goodFile.Status);
        
        // The C# worker doesn't throw on invalid syntax, it just emits no facts
        Assert.Equal(FileProcessingStatus.Completed, badFile.Status);
        
        // Verify that no nodes were created for the invalid file
        var nodeRepo = _repositoryFactory.CreateNodeRepository();
        var nodes = await nodeRepo.GetAllAsync();
        Assert.Single(nodes); // Only the good file should have nodes
        Assert.Equal("GoodClass", nodes.First().Name);
    }

}