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
    /// C# parsing runs in the real worker process behind the protocol; graph IDs are
    /// csharp:-namespaced (see <see cref="CSharpWorkerPipeline.Id"/>).
    /// </summary>
    public class ImplementsRelationshipTests : IAsyncLifetime
    {
        private readonly string _tempDir;
        private readonly ITestOutputHelper _output;
        private CSharpWorkerPipeline _pipeline = null!;

        private IRepositoryFactory _repositoryFactory => _pipeline.RepositoryFactory;
        private GraphUpdateService _service => _pipeline.GraphUpdateService;
        private ICodeEdgeRepository _edgeRepository => _pipeline.RepositoryFactory.CreateEdgeRepository();

        private static string Id(string display) => CSharpWorkerPipeline.Id(display);

        public ImplementsRelationshipTests(ITestOutputHelper output)
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
        public async Task Parser_CreatesImplementsRelationshipCorrectly()
        {
            // Arrange - Test files with interface and implementing class
            var interfaceFile = Path.Combine(_tempDir, "IUserService.cs");
            var interfaceContent = @"
namespace MyApp.Services
{
    public interface IUserService
    {
        string GetUser(int id);
    }
}";

            var classFile = Path.Combine(_tempDir, "UserService.cs");
            var classContent = @"
namespace MyApp.Services
{
    public class UserService : IUserService
    {
        public string GetUser(int id)
        {
            return $""User {id}"";
        }
    }
}";

            await File.WriteAllTextAsync(interfaceFile, interfaceContent);
            await File.WriteAllTextAsync(classFile, classContent);

            _output.WriteLine("Created test files:");
            _output.WriteLine($"Interface: {interfaceFile}");
            _output.WriteLine($"Class: {classFile}");

            // Act - Process the files
            await _service.ProcessFileChangeAsync(interfaceFile, FileChangeType.Created, CancellationToken.None);
            await _service.ProcessFileChangeAsync(classFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that the parser created the IMPLEMENTS edge
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("MyApp.Services.UserService"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementsEdges.Count} IMPLEMENTS edges");
            foreach (var edge in implementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Single(implementsEdges);
            var implementsEdge = implementsEdges.First();
            Assert.Equal(Id("MyApp.Services.UserService"), implementsEdge.SourceId);
            Assert.Equal(Id("MyApp.Services.IUserService"), implementsEdge.TargetId);
            Assert.Equal("IMPLEMENTS", implementsEdge.Type);
        }

        [Fact]
        public async Task Parser_CreatesBothNodesAndEdges()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "CompleteExample.cs");
            var testContent = @"
                using System;
                namespace MyApp.Services
                {
                    public interface IRepository<T>
                    {
                        T GetById(int id);
                    }

                    public interface IUserRepository : IRepository<string>
                    {
                        string GetByEmail(string email);
                    }

                    public class UserRepository : IUserRepository
                    {
                        public string GetById(int id)
                        {
                            return $""User {id}"";
                        }

                        public string GetByEmail(string email)
                        {
                            return $""User with email {email}"";
                        }
                    }
                }";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check nodes were created
            var nodeRepository = _repositoryFactory.CreateNodeRepository();
            var allNodes = await nodeRepository.GetAllAsync();
            
            _output.WriteLine($"Created {allNodes.Count} nodes:");
            foreach (var node in allNodes)
            {
                _output.WriteLine($"Node: {node.Name} ({node.Type}) - {node.Id}");
            }

            var repositoryInterfaceNode = allNodes.FirstOrDefault(n => n.Name == "IRepository");
            var userRepositoryInterfaceNode = allNodes.FirstOrDefault(n => n.Name == "IUserRepository");
            var userRepositoryClassNode = allNodes.FirstOrDefault(n => n.Name == "UserRepository");

            Assert.NotNull(repositoryInterfaceNode);
            Assert.NotNull(userRepositoryInterfaceNode);
            Assert.NotNull(userRepositoryClassNode);

            // Assert - Check IMPLEMENTS edges were created
            var userRepoImplementsEdges = await _edgeRepository.GetBySourceIdAsync(userRepositoryClassNode.Id!, "IMPLEMENTS");
            
            _output.WriteLine($"Found {userRepoImplementsEdges.Count} IMPLEMENTS edges for UserRepository:");
            foreach (var edge in userRepoImplementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            // Should implement both IUserRepository and IRepository<string>
            Assert.True(userRepoImplementsEdges.Count >= 1, "UserRepository should implement at least IUserRepository");
            
            var implementsIUserRepository = userRepoImplementsEdges.Any(e => e.TargetId?.Contains("IUserRepository") == true);
            Assert.True(implementsIUserRepository, "UserRepository should implement IUserRepository");
        }

        [Fact]
        public async Task DatabaseStorage_PreservesImplementsRelationships()
        {
            // Arrange - Create a simple interface and class
            var testFile = Path.Combine(_tempDir, "StorageTest.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface ITestService
    {
        void DoSomething();
    }

    public class TestService : ITestService
    {
        public void DoSomething()
        {
            // Implementation
        }
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act - Process the file
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Verify the edge was stored by querying both directions
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestNamespace.TestService"), "IMPLEMENTS");
            var implementedByEdges = await _edgeRepository.GetByTargetIdAsync(Id("TestNamespace.ITestService"), "IMPLEMENTS");

            _output.WriteLine($"Found {implementsEdges.Count} outgoing IMPLEMENTS edges from TestService");
            _output.WriteLine($"Found {implementedByEdges.Count} incoming IMPLEMENTS edges to ITestService");

            Assert.Single(implementsEdges);
            Assert.Single(implementedByEdges);

            var outgoingEdge = implementsEdges.First();
            var incomingEdge = implementedByEdges.First();

            // Both should refer to the same relationship
            Assert.Equal(outgoingEdge.Id, incomingEdge.Id);
            Assert.Equal(Id("TestNamespace.TestService"), outgoingEdge.SourceId);
            Assert.Equal(Id("TestNamespace.ITestService"), outgoingEdge.TargetId);
            Assert.Equal("IMPLEMENTS", outgoingEdge.Type);
        }

        [Fact]
        public async Task MultipleInterfaces_CreatesMultipleImplementsEdges()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "MultipleInterfaces.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface IReadable
    {
        string Read();
    }

    public interface IWritable
    {
        void Write(string data);
    }

    public interface IDisposable
    {
        void Dispose();
    }

    public class FileHandler : IReadable, IWritable, IDisposable
    {
        public string Read() => ""data"";
        public void Write(string data) { }
        public void Dispose() { }
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert
            var implementsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestNamespace.FileHandler"), "IMPLEMENTS");
            
            _output.WriteLine($"Found {implementsEdges.Count} IMPLEMENTS edges for FileHandler:");
            foreach (var edge in implementsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            // Should implement all three interfaces
            Assert.True(implementsEdges.Count >= 3, $"FileHandler should implement 3 interfaces, found {implementsEdges.Count}");
            
            var targetIds = implementsEdges.Select(e => e.TargetId).ToList();
            Assert.Contains(Id("TestNamespace.IReadable"), targetIds);
            Assert.Contains(Id("TestNamespace.IWritable"), targetIds);
            Assert.Contains(Id("TestNamespace.IDisposable"), targetIds);
        }

        [Fact]
        public async Task Parser_TracksSimpleInterfaceInheritance()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "InterfaceInheritance.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface IBaseService
    {
        void BaseMethod();
    }

    public interface IChildService : IBaseService
    {
        void ChildMethod();
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that interface inheritance edge was created
            var inheritsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestNamespace.IChildService"), "INHERITS");
            
            _output.WriteLine($"Found {inheritsEdges.Count} INHERITS edges for IChildService:");
            foreach (var edge in inheritsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Single(inheritsEdges);
            var inheritsEdge = inheritsEdges.First();
            Assert.Equal(Id("TestNamespace.IChildService"), inheritsEdge.SourceId);
            Assert.Equal(Id("TestNamespace.IBaseService"), inheritsEdge.TargetId);
            Assert.Equal("INHERITS", inheritsEdge.Type);
        }

        [Fact]
        public async Task Parser_TracksGenericInterfaceInheritance()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "GenericInterfaceInheritance.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface IRepository<T>
    {
        T GetById(int id);
    }

    public interface IUserRepository : IRepository<string>
    {
        string GetByEmail(string email);
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that generic interface inheritance edge was created
            var inheritsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestNamespace.IUserRepository"), "INHERITS");
            
            _output.WriteLine($"Found {inheritsEdges.Count} INHERITS edges for IUserRepository:");
            foreach (var edge in inheritsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Single(inheritsEdges);
            var inheritsEdge = inheritsEdges.First();
            Assert.Equal(Id("TestNamespace.IUserRepository"), inheritsEdge.SourceId);
            Assert.Equal(Id("TestNamespace.IRepository<string>"), inheritsEdge.TargetId);
            Assert.Equal("INHERITS", inheritsEdge.Type);
        }

        [Fact]
        public async Task Parser_TracksMultipleInterfaceInheritance()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "MultipleInterfaceInheritance.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface IDisposable
    {
        void Dispose();
    }

    public interface ICloneable
    {
        object Clone();
    }

    public interface ISerializable
    {
        void Serialize();
    }

    public interface IComplexService : IDisposable, ICloneable, ISerializable
    {
        void DoWork();
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);

            // Assert - Check that all interface inheritance edges were created
            var inheritsEdges = await _edgeRepository.GetBySourceIdAsync(Id("TestNamespace.IComplexService"), "INHERITS");
            
            _output.WriteLine($"Found {inheritsEdges.Count} INHERITS edges for IComplexService:");
            foreach (var edge in inheritsEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.Equal(3, inheritsEdges.Count);
            
            var targetIds = inheritsEdges.Select(e => e.TargetId).ToList();
            Assert.Contains(Id("TestNamespace.IDisposable"), targetIds);
            Assert.Contains(Id("TestNamespace.ICloneable"), targetIds);
            Assert.Contains(Id("TestNamespace.ISerializable"), targetIds);
        }

        [Fact]
        public async Task EdgeRepository_GetAllAsync_ReturnsAllEdges()
        {
            // Arrange
            var testFile = Path.Combine(_tempDir, "GetAllAsyncTest.cs");
            var testContent = @"
namespace TestNamespace
{
    public interface IService
    {
        void DoWork();
    }

    public class Service : IService
    {
        public void DoWork() { }
    }

    public class ServiceHelper
    {
        private readonly Service _service;

        public ServiceHelper()
        {
            _service = new Service();
        }

        public void Execute()
        {
            _service.DoWork();
        }
    }
}";

            await File.WriteAllTextAsync(testFile, testContent);

            // Act
            await _service.ProcessFileChangeAsync(testFile, FileChangeType.Created, CancellationToken.None);
            var allEdges = await _edgeRepository.GetAllAsync();

            // Assert
            _output.WriteLine($"Found {allEdges.Count} total edges:");
            foreach (var edge in allEdges)
            {
                _output.WriteLine($"Edge: {edge.SourceId} -{edge.Type}-> {edge.TargetId}");
            }

            Assert.NotEmpty(allEdges);
            
            // Should have at least an IMPLEMENTS edge
            Assert.Contains(allEdges, e => e.Type == "IMPLEMENTS" && e.SourceId == Id("TestNamespace.Service") && e.TargetId == Id("TestNamespace.IService"));
            
            // Should have CALLS edges from ServiceHelper.Execute to Service.DoWork
            Assert.Contains(allEdges, e => e.Type == "CALLS");
        }
    }
}
