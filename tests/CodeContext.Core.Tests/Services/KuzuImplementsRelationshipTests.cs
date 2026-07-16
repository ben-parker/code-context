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
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Services
{
    /// <summary>
    /// Storage-shape regression tests kept from the historical relationships incident;
    /// C# facts now arrive through the real worker protocol, IDs csharp:-namespaced.
    /// </summary>
    public class KuzuImplementsRelationshipTests : IAsyncLifetime
    {
        private readonly string _tempDir;
        private readonly ITestOutputHelper _output;
        private CSharpWorkerPipeline _pipeline = null!;

        private IRepositoryFactory _repositoryFactory => _pipeline.RepositoryFactory;
        private GraphUpdateService _service => _pipeline.GraphUpdateService;
        private ICodeEdgeRepository _edgeRepository => _pipeline.RepositoryFactory.CreateEdgeRepository();

        private static string Id(string display) => CSharpWorkerPipeline.Id(display);

        public KuzuImplementsRelationshipTests(ITestOutputHelper output)
        {
            _output = output;
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
        public async Task KuzuDatabase_StoresImplementsRelationshipsCorrectly()
        {
            // Arrange - Test files with interface and implementing class
            var interfaceFile = Path.Combine(_tempDir, "ITestService.cs");
            var interfaceContent = @"
namespace TestApp.Services
{
    public interface ITestService
    {
        string DoWork(int id);
    }
}";

            var classFile = Path.Combine(_tempDir, "TestService.cs");
            var classContent = @"
namespace TestApp.Services
{
    public class TestService : ITestService
    {
        public string DoWork(int id)
        {
            return $""Work done for {id}"";
        }
    }
}";

            await File.WriteAllTextAsync(interfaceFile, interfaceContent);
            await File.WriteAllTextAsync(classFile, classContent);

            _output.WriteLine("Created test files for Kuzu testing:");
            _output.WriteLine($"Interface: {interfaceFile}");
            _output.WriteLine($"Class: {classFile}");

            // Act - Process the files
            await _service.ProcessFileChangeAsync(interfaceFile, FileChangeType.Created, CancellationToken.None);
            await _service.ProcessFileChangeAsync(classFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that the IMPLEMENTS edge was stored in Kuzu
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestApp.Services.TestService"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementsEdges.Count} IMPLEMENTS edges in Kuzu database");
            foreach (var edge in implementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Single(implementsEdges);
            var implementsEdge = implementsEdges.First();
            Assert.Equal(Id("TestApp.Services.TestService"), implementsEdge.SourceId);
            Assert.Equal(Id("TestApp.Services.ITestService"), implementsEdge.TargetId);
            Assert.Equal("IMPLEMENTS", implementsEdge.Type);

            // Also check the reverse direction
            var implementedByEdges = await _edgeRepository.GetByTargetIdAsync(Id("TestApp.Services.ITestService"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementedByEdges.Count} incoming IMPLEMENTS edges in Kuzu database");
            foreach (var edge in implementedByEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Single(implementedByEdges);
            Assert.Equal(implementsEdge.Id, implementedByEdges.First().Id);
        }

        [Fact]
        public async Task KuzuDatabase_HandlesMultipleInterfacesCorrectly()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "MultiInterface.cs");
            var testContent = @"
namespace TestApp
{
    public interface IReady
    {
        bool IsReady();
    }

    public interface IRunnable
    {
        void Run();
    }

    public class Worker : IReady, IRunnable
    {
        public bool IsReady() => true;
        public void Run() { }
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that all IMPLEMENTS edges were stored
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestApp.Worker"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementsEdges.Count} IMPLEMENTS edges for Worker in Kuzu database");
            foreach (var edge in implementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.True(implementsEdges.Count >= 2, $"Worker should implement 2 interfaces, found {implementsEdges.Count}");
            
            var targetIds = implementsEdges.Select(e => e.TargetId).ToList();
            Assert.Contains(Id("TestApp.IReady"), targetIds);
            Assert.Contains(Id("TestApp.IRunnable"), targetIds);
        }

        [Fact]
        public async Task KuzuDatabase_NodeExistenceBeforeEdgeInsertion()
        {
            // This test specifically checks if nodes exist before trying to insert edges
            
            // Arrange
            var testFile = Path.Combine(_tempDir, "NodeOrder.cs");
            var testContent = @"
namespace TestApp
{
    public interface IProcessor
    {
        void Process();
    }

    public class DataProcessor : IProcessor
    {
        public void Process() { }
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - First check that both nodes exist
            var nodeRepository = _repositoryFactory.CreateNodeRepository();
            var allNodes = await nodeRepository.GetAllAsync();
            
            _output.WriteLine($"Found {allNodes.Count} nodes in Kuzu database:");
            foreach (var node in allNodes)
            {
                _output.WriteLine($"Node: {node.Name} ({node.Type}) - {node.Id}");
            }

            var interfaceNode = allNodes.FirstOrDefault(n => n.Name == "IProcessor");
            var classNode = allNodes.FirstOrDefault(n => n.Name == "DataProcessor");

            Assert.NotNull(interfaceNode);
            Assert.NotNull(classNode);
            Assert.Equal(Id("TestApp.IProcessor"), interfaceNode.Id);
            Assert.Equal(Id("TestApp.DataProcessor"), classNode.Id);

            // Then check the edge exists
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestApp.DataProcessor"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementsEdges.Count} IMPLEMENTS edges for DataProcessor");
            foreach (var edge in implementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId} (ID: {edge.Id})");
            }

            Assert.Single(implementsEdges);
            var implementsEdge = implementsEdges.First();
            Assert.Equal(Id("TestApp.DataProcessor"), implementsEdge.SourceId);
            Assert.Equal(Id("TestApp.IProcessor"), implementsEdge.TargetId);
            Assert.Equal("IMPLEMENTS", implementsEdge.Type);
            Assert.NotNull(implementsEdge.Id);
        }
    }
}