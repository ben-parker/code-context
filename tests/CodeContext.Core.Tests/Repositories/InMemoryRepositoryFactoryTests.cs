using System;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class InMemoryRepositoryFactoryTests
{
    private readonly ILogger<InMemoryRepositoryFactory> _mockLogger;
    private readonly InMemoryRepositoryFactory _factory;

    public InMemoryRepositoryFactoryTests()
    {
        _mockLogger = Substitute.For<ILogger<InMemoryRepositoryFactory>>();
        _factory = new InMemoryRepositoryFactory(_mockLogger);
    }

    [Fact]
    public async Task InitializeAsync_WithValidRootPath_CompletesSuccessfully()
    {
        // Arrange
        var rootPath = "/test/root/path";

        // Act
        var exception = await Record.ExceptionAsync(() => _factory.InitializeAsync(rootPath));

        // Assert
        Assert.Null(exception);
        
        // Verify logging
        _mockLogger.Received(1).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(v => v.ToString()!.Contains($"Initialized in-memory database for root path: {rootPath}")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task InitializeAsync_WithNullRootPath_CompletesSuccessfully()
    {
        // Act
        var exception = await Record.ExceptionAsync(() => _factory.InitializeAsync(null!));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task InitializeAsync_WithEmptyRootPath_CompletesSuccessfully()
    {
        // Act
        var exception = await Record.ExceptionAsync(() => _factory.InitializeAsync(""));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task InitializeAsync_MultipleCallsWithDifferentPaths_LogsEachCall()
    {
        // Arrange
        var rootPath1 = "/test/root/path1";
        var rootPath2 = "/test/root/path2";

        // Act
        await _factory.InitializeAsync(rootPath1);
        await _factory.InitializeAsync(rootPath2);

        // Assert
        _mockLogger.Received(2).Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public void CreateGraphRepository_ReturnsInMemoryCodeGraphRepository()
    {
        // Act
        var repository = _factory.CreateGraphRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<InMemoryCodeGraphRepository>(repository);
    }

    [Fact]
    public void CreateNodeRepository_ReturnsInMemoryNodeRepository()
    {
        // Act
        var repository = _factory.CreateNodeRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<InMemoryNodeRepository>(repository);
    }

    [Fact]
    public void CreateEdgeRepository_ReturnsInMemoryEdgeRepository()
    {
        // Act
        var repository = _factory.CreateEdgeRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<InMemoryEdgeRepository>(repository);
    }

    [Fact]
    public void CreateFileMetadataRepository_ReturnsInMemoryFileMetadataRepository()
    {
        // Act
        var repository = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<InMemoryFileMetadataRepository>(repository);
    }

    [Fact]
    public void CreateGraphRepository_MultipleCalls_ReturnsDifferentInstances()
    {
        // Act
        var repository1 = _factory.CreateGraphRepository();
        var repository2 = _factory.CreateGraphRepository();

        // Assert
        Assert.NotNull(repository1);
        Assert.NotNull(repository2);
        Assert.NotSame(repository1, repository2);
    }

    [Fact]
    public void CreateNodeRepository_MultipleCalls_ReturnsDifferentInstances()
    {
        // Act
        var repository1 = _factory.CreateNodeRepository();
        var repository2 = _factory.CreateNodeRepository();

        // Assert
        Assert.NotNull(repository1);
        Assert.NotNull(repository2);
        Assert.NotSame(repository1, repository2);
    }

    [Fact]
    public void CreateEdgeRepository_MultipleCalls_ReturnsDifferentInstances()
    {
        // Act
        var repository1 = _factory.CreateEdgeRepository();
        var repository2 = _factory.CreateEdgeRepository();

        // Assert
        Assert.NotNull(repository1);
        Assert.NotNull(repository2);
        Assert.NotSame(repository1, repository2);
    }

    [Fact]
    public void CreateFileMetadataRepository_MultipleCalls_ReturnsDifferentInstances()
    {
        // Act
        var repository1 = _factory.CreateFileMetadataRepository();
        var repository2 = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotNull(repository1);
        Assert.NotNull(repository2);
        Assert.NotSame(repository1, repository2);
    }

    [Fact]
    public async Task RepositoriesShareSameDatabase_GraphAndNodeRepositories()
    {
        // Act
        var graphRepository = _factory.CreateGraphRepository() as InMemoryCodeGraphRepository;
        var nodeRepository = _factory.CreateNodeRepository() as InMemoryNodeRepository;

        // Assert
        Assert.NotNull(graphRepository);
        Assert.NotNull(nodeRepository);
        
        // Both repositories should share the same database instance
        // This is verified by checking that operations on one affect the other
        var testNode = new CodeNode
        {
            Id = "test-node",
            Name = "TestClass",
            Type = "Class",
            FilePath = "/test/path.cs",
            StartLine = 1,
            EndLine = 10,
            Namespace = "Test.Namespace",
            Visibility = "public",
            Signature = "public class TestClass"
        };

        // Insert via node repository
        await nodeRepository.UpsertAsync(testNode);

        // Verify via graph repository
        var graph = await graphRepository.GetGraphAsync();
        Assert.NotNull(graph);
        Assert.Single(graph.Nodes);
        Assert.Equal("test-node", graph.Nodes[0].Id);
    }

    [Fact]
    public async Task RepositoriesShareSameDatabase_GraphAndEdgeRepositories()
    {
        // Act
        var graphRepository = _factory.CreateGraphRepository() as InMemoryCodeGraphRepository;
        var edgeRepository = _factory.CreateEdgeRepository() as InMemoryEdgeRepository;

        // Assert
        Assert.NotNull(graphRepository);
        Assert.NotNull(edgeRepository);
        
        // Both repositories should share the same database instance
        var testEdge = new CodeEdge
        {
            Id = "test-edge",
            SourceId = "source-node",
            TargetId = "target-node",
            Type = "CALLS",
            Metadata = new Dictionary<string, string>()
        };

        // Insert via edge repository
        await edgeRepository.UpsertAsync(testEdge);

        // Verify via graph repository
        var graph = await graphRepository.GetGraphAsync();
        Assert.NotNull(graph);
        Assert.Single(graph.Edges);
        Assert.Equal("test-edge", graph.Edges[0].Id);
    }

    [Fact]
    public async Task FileMetadataRepository_IsIndependent()
    {
        // Act
        var repository1 = _factory.CreateFileMetadataRepository();
        var repository2 = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotNull(repository1);
        Assert.NotNull(repository2);
        Assert.NotSame(repository1, repository2);
        
        // Each file metadata repository should be independent
        var testMetadata = new CodeContext.Core.Models.FileMetadata
        {
            FilePath = "/test/path.cs",
            LastModified = DateTime.Now,
            LastScanned = DateTime.Now,
            Status = CodeContext.Core.Models.FileProcessingStatus.Completed
        };

        // Insert in first repository
        await repository1.UpsertAsync(testMetadata);

        // Should not be present in second repository
        var result = await repository2.GetByFilePathAsync("/test/path.cs");
        Assert.Null(result);
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new InMemoryRepositoryFactory(null!));
    }

    [Fact]
    public void FactoryImplementsInterface()
    {
        // Assert
        Assert.IsAssignableFrom<IRepositoryFactory>(_factory);
    }

    [Fact]
    public void AllCreateMethods_ReturnCorrectInterfaces()
    {
        // Act & Assert
        Assert.IsAssignableFrom<ICodeGraphRepository>(_factory.CreateGraphRepository());
        Assert.IsAssignableFrom<ICodeNodeRepository>(_factory.CreateNodeRepository());
        Assert.IsAssignableFrom<ICodeEdgeRepository>(_factory.CreateEdgeRepository());
        Assert.IsAssignableFrom<IFileMetadataRepository>(_factory.CreateFileMetadataRepository());
    }

    [Fact]
    public async Task ConcurrentInitialization_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var initCount = 10;

        // Act - Concurrent initializations
        for (int i = 0; i < initCount; i++)
        {
            var rootPath = $"/test/root/path{i}";
            tasks.Add(_factory.InitializeAsync(rootPath));
        }

        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task ConcurrentRepositoryCreation_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task<ICodeNodeRepository>>();
        var createCount = 100;

        // Act - Concurrent repository creation
        for (int i = 0; i < createCount; i++)
        {
            tasks.Add(Task.Run(() => _factory.CreateNodeRepository()));
        }

        var repositories = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(createCount, repositories.Length);
        Assert.All(repositories, repo => Assert.NotNull(repo));
        
        // Verify all repositories are unique instances
        var uniqueRepositories = repositories.Distinct().ToList();
        Assert.Equal(createCount, uniqueRepositories.Count);
    }
}
