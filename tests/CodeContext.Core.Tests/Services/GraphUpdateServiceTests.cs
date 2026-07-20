using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Workers;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    /// <summary>
    /// C# traffic runs through the real out-of-process worker (protocol fixtures),
    /// exactly like production; node/edge IDs therefore carry the csharp: namespace.
    /// </summary>
    public class GraphUpdateServiceTests : IAsyncLifetime
    {
        private readonly string _tempDir;
        private CSharpWorkerPipeline _pipeline = null!;

        private IRepositoryFactory _repositoryFactory => _pipeline.RepositoryFactory;
        private GraphUpdateService _service => _pipeline.GraphUpdateService;
        private IFileMetadataRepository _fileMetadataRepository => _pipeline.FileMetadataRepository;

        public GraphUpdateServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);
        }

        public Task InitializeAsync()
        {
            _pipeline = new CSharpWorkerPipeline(_tempDir);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _pipeline.DisposeAsync();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [Fact]
        public async Task ProcessFileChangeAsync_Created_ParsesAndStoresFile()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "TestClass.cs");
            var testContent = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod() { }
    }
}";
            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert
            var nodeRepo = _repositoryFactory.CreateNodeRepository();
            var nodes = await nodeRepo.GetAllAsync();
            
            Assert.Equal(2, nodes.Count); // Class and Method
            Assert.Contains(nodes, n => n.Name == "TestClass" && n.Type == "Class");
            Assert.Contains(nodes, n => n.Name == "TestMethod" && n.Type == "Method");
        }

        [Fact]
        public async Task ProcessFileChangeAsync_Deleted_RemovesNodesAndEdges()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "TestClass.cs");
            var testContent = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod() { }
    }
}";
            await File.WriteAllTextAsync(testFile, testContent);
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Deleted, CancellationToken.None);

            // Assert
            var nodeRepo = _repositoryFactory.CreateNodeRepository();
            var nodes = await nodeRepo.GetAllAsync();
            
            Assert.Empty(nodes);
        }

        [Fact]
        public async Task ProcessFileChangeAsync_Changed_UpdatesNodes()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "TestClass.cs");
            var originalContent = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void MethodOne() { }
    }
}";
            await File.WriteAllTextAsync(testFile, originalContent);
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            var updatedContent = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void MethodOne() { }
        public void MethodTwo() { }
    }
}";
            await File.WriteAllTextAsync(testFile, updatedContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Changed, CancellationToken.None);

            // Assert
            var nodeRepo = _repositoryFactory.CreateNodeRepository();
            var nodes = await nodeRepo.GetAllAsync();
            
            Assert.Equal(3, nodes.Count); // Class and 2 Methods
            Assert.Contains(nodes, n => n.Name == "MethodOne" && n.Type == "Method");
            Assert.Contains(nodes, n => n.Name == "MethodTwo" && n.Type == "Method");
        }

        [Fact]
        public async Task PerformInitialScanAsync_ScansAllCSharpFiles()
        {
            // Arrange
            var file1 = Path.Combine(_tempDir, "Class1.cs");
            var file2 = Path.Combine(_tempDir, "Class2.cs");
            var subDir = Path.Combine(_tempDir, "SubDir");
            Directory.CreateDirectory(subDir);
            var file3 = Path.Combine(subDir, "Class3.cs");

            await File.WriteAllTextAsync(file1, "public class Class1 { }");
            await File.WriteAllTextAsync(file2, "public class Class2 { }");
            await File.WriteAllTextAsync(file3, "public class Class3 { }");

            // Act
            await _service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

            // Assert
            var nodeRepo = _repositoryFactory.CreateNodeRepository();
            var nodes = await nodeRepo.GetAllAsync();
            
            Assert.Equal(3, nodes.Count);
            Assert.Contains(nodes, n => n.Name == "Class1");
            Assert.Contains(nodes, n => n.Name == "Class2");
            Assert.Contains(nodes, n => n.Name == "Class3");
        }

        [Fact]
        public async Task ProcessFileChangeAsync_WithInheritance_CreatesEdges()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "TestClasses.cs");
            var testContent = @"
namespace TestNamespace
{
    public class BaseClass { }
    
    public class DerivedClass : BaseClass { }
}";
            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert
            var edgeRepo = _repositoryFactory.CreateEdgeRepository();
            var edges = await edgeRepo.GetBySourceIdAsync(CSharpWorkerPipeline.Id("TestNamespace.DerivedClass"));

            Assert.Single(edges);
            Assert.Equal("INHERITS", edges[0].Type);
            Assert.Equal(CSharpWorkerPipeline.Id("TestNamespace.BaseClass"), edges[0].TargetId);
        }

        [Fact]
        public async Task ProcessFileChangeAsync_IgnoresNonCSharpFiles()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "test.txt");
            await File.WriteAllTextAsync(testFile, "This is not C# code");

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert
            var nodeRepo = _repositoryFactory.CreateNodeRepository();
            var nodes = await nodeRepo.GetAllAsync();
            
            Assert.Empty(nodes);
        }

        [Fact]
        public async Task ProcessFileChangesAsync_Batch_AppliesAllChangesInOnePass()
        {
            var file1 = Path.Combine(_tempDir, "A.cs");
            var file2 = Path.Combine(_tempDir, "B.cs");
            await File.WriteAllTextAsync(file1, "public class A { }");
            await File.WriteAllTextAsync(file2, "public class B { }");

            await _service.ProcessFileChangesAsync(
            [
                new FileChange(file1, FileChangeType.Created),
                new FileChange(file2, FileChangeType.Created),
            ], CancellationToken.None);

            var nodes = await _repositoryFactory.CreateNodeRepository().GetAllAsync();
            Assert.Contains(nodes, n => n.Name == "A");
            Assert.Contains(nodes, n => n.Name == "B");
        }

        [Fact]
        public async Task PerformInitialScanAsync_ReportsProgress()
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.cs"), "public class A { }");
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "B.cs"), "public class B { }");
            var reporter = new RecordingProgressReporter();

            await _service.PerformInitialScanAsync(_tempDir, reporter, CancellationToken.None);

            Assert.True(reporter.Completed);
            Assert.Equal((0, 2), reporter.Progress[0]);
            Assert.Equal(2, reporter.LastTotal);
            Assert.Equal(2, reporter.LastProcessed);
        }

        [Fact]
        public async Task PerformResumableScanAsync_ReportsWorkloadBeforeCompletedFiles()
        {
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.cs"), "public class A { }");
            await File.WriteAllTextAsync(Path.Combine(_tempDir, "B.cs"), "public class B { }");
            var reporter = new RecordingProgressReporter();

            await _service.PerformResumableScanAsync(_tempDir, reporter, CancellationToken.None);

            Assert.True(reporter.Completed);
            Assert.Equal((0, 2), reporter.Progress[0]);
            Assert.Contains((1, 2), reporter.Progress);
            Assert.Equal((2, 2), reporter.Progress[^1]);
        }

        [Fact]
        public async Task PerformInitialScanAsync_Rescan_PrunesDeletedFiles()
        {
            var keptFile = Path.Combine(_tempDir, "Kept.cs");
            var deletedFile = Path.Combine(_tempDir, "Deleted.cs");
            await File.WriteAllTextAsync(keptFile, "public class Kept { }");
            await File.WriteAllTextAsync(deletedFile, "public class Doomed { }");
            await _service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

            File.Delete(deletedFile);
            await _service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

            var nodes = await _repositoryFactory.CreateNodeRepository().GetAllAsync();
            Assert.Contains(nodes, n => n.Name == "Kept");
            Assert.DoesNotContain(nodes, n => n.Name == "Doomed");

            var metadata = await _fileMetadataRepository.GetAllAsync();
            Assert.DoesNotContain(metadata, m => m.FilePath == deletedFile);
        }

        [Fact]
        public async Task PerformResumableScanAsync_PrunesFilesDeletedWhileDown()
        {
            // Files deleted while the instance was stopped never appear in the
            // files-to-process list; the resumable scan must still prune their facts.
            var keptFile = Path.Combine(_tempDir, "Kept.cs");
            var doomedFile = Path.Combine(_tempDir, "Doomed.cs");
            await File.WriteAllTextAsync(keptFile, "public class Kept { }");
            await File.WriteAllTextAsync(doomedFile, "public class Doomed { }");
            await _service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None);

            File.Delete(doomedFile);
            await _service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None);

            var nodes = await _repositoryFactory.CreateNodeRepository().GetAllAsync();
            Assert.Contains(nodes, n => n.Name == "Kept");
            Assert.DoesNotContain(nodes, n => n.Name == "Doomed");

            var metadata = await _fileMetadataRepository.GetAllAsync();
            Assert.DoesNotContain(metadata, m => m.FilePath == doomedFile);
        }

        private sealed class RecordingProgressReporter : IScanProgressReporter
        {
            public int LastProcessed;
            public int LastTotal;
            public bool Completed;
            public List<(int Processed, int Total)> Progress { get; } = [];

            public void ReportProgress(int processed, int total, string currentFile)
            {
                Progress.Add((processed, total));
                LastProcessed = processed;
                LastTotal = total;
            }

            public void ReportComplete(int totalProcessed, TimeSpan elapsed)
            {
                LastProcessed = totalProcessed;
                Completed = true;
            }

            public void ReportError(string filePath, string error) { }
        }
    }
}
