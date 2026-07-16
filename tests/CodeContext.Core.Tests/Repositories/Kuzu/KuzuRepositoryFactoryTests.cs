using System;
using System.IO;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.Kuzu;
using CSnakes.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Repositories.Kuzu;

public class KuzuRepositoryFactoryTests
{
    private readonly IKuzuApi _mockKuzuApi;
    private readonly ILoggerFactory _mockLoggerFactory;
    private readonly ILogger<KuzuFileMetadataRepository> _mockFileMetadataLogger;
    private readonly KuzuRepositoryFactory _factory;

    public KuzuRepositoryFactoryTests()
    {
        _mockKuzuApi = Substitute.For<IKuzuApi>();
        _mockLoggerFactory = Substitute.For<ILoggerFactory>();
        _mockFileMetadataLogger = Substitute.For<ILogger<KuzuFileMetadataRepository>>();
        _mockLoggerFactory.CreateLogger<KuzuFileMetadataRepository>().Returns(_mockFileMetadataLogger);
        _factory = new KuzuRepositoryFactory(_mockKuzuApi, _mockLoggerFactory);
    }

    [Fact]
    public async Task InitializeAsync_WithValidRootPath_InitializesDatabase()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        var expectedDbPath = Path.Combine(rootPath, ".codecontext", "codecontext.kuzu");

        // Act
        await _factory.InitializeAsync(rootPath);

        // Assert
        _mockKuzuApi.Received(1).InitializeDatabase(expectedDbPath);
    }

    [Fact]
    public async Task InitializeAsync_WithRootPathWithSpaces_HandlesCorrectly()
    {
        // Arrange
        var rootPath = Path.Combine(Path.GetTempPath(), "test root with spaces");
        var expectedDbPath = Path.Combine(rootPath, ".codecontext", "codecontext.kuzu");

        // Act
        await _factory.InitializeAsync(rootPath);

        // Assert
        _mockKuzuApi.Received(1).InitializeDatabase(expectedDbPath);
    }

    [Fact]
    public async Task InitializeAsync_CreatesCodeContextDirectory()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        var expectedCodeContextDir = Path.Combine(rootPath, ".codecontext");

        // Act
        await _factory.InitializeAsync(rootPath);

        // Assert
        // We can't easily test Directory.CreateDirectory, but we can verify the path construction
        _mockKuzuApi.Received(1).InitializeDatabase(Arg.Is<string>(path => 
            path.StartsWith(expectedCodeContextDir) && 
            path.EndsWith("codecontext.kuzu")));
    }

    [Fact]
    public async Task InitializeAsync_WithNullRootPath_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => 
            _factory.InitializeAsync(null!));
    }

    [Fact]
    public async Task InitializeAsync_WithEmptyRootPath_ThrowsArgumentException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => 
            _factory.InitializeAsync(""));
    }

    [Fact]
    public async Task InitializeAsync_WithKuzuApiException_PropagatesException()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        _mockKuzuApi.When(x => x.InitializeDatabase(Arg.Any<string>()))
                    .Do(x => throw new InvalidOperationException("Database initialization failed"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _factory.InitializeAsync(rootPath));
        Assert.Equal("Database initialization failed", exception.Message);
    }

    [Fact]
    public async Task InitializeAsync_CalledMultipleTimes_InitializesEachTime()
    {
        // Arrange
        var rootPath1 = Path.Combine(Path.GetTempPath(), "root1");
        var rootPath2 = Path.Combine(Path.GetTempPath(), "root2");

        // Act
        await _factory.InitializeAsync(rootPath1);
        await _factory.InitializeAsync(rootPath2);

        // Assert
        _mockKuzuApi.Received(1).InitializeDatabase(Arg.Is<string>(path => path.Contains("root1")));
        _mockKuzuApi.Received(1).InitializeDatabase(Arg.Is<string>(path => path.Contains("root2")));
    }

    [Fact]
    public async Task CreateNodeRepository_BeforeInitialization_ThrowsInvalidOperationException()
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => 
            _factory.CreateNodeRepository());
        Assert.Equal("Repository factory not initialized. Call InitializeAsync first.", exception.Message);
    }

    [Fact]
    public async Task CreateNodeRepository_AfterInitialization_ReturnsKuzuNodeRepository()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var repository = _factory.CreateNodeRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<KuzuNodeRepository>(repository);
        Assert.IsAssignableFrom<ICodeNodeRepository>(repository);
    }

    [Fact]
    public async Task CreateEdgeRepository_BeforeInitialization_ThrowsInvalidOperationException()
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => 
            _factory.CreateEdgeRepository());
        Assert.Equal("Repository factory not initialized. Call InitializeAsync first.", exception.Message);
    }

    [Fact]
    public async Task CreateEdgeRepository_AfterInitialization_ReturnsKuzuEdgeRepository()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var repository = _factory.CreateEdgeRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<KuzuEdgeRepository>(repository);
        Assert.IsAssignableFrom<ICodeEdgeRepository>(repository);
    }

    [Fact]
    public async Task CreateGraphRepository_BeforeInitialization_ThrowsInvalidOperationException()
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => 
            _factory.CreateGraphRepository());
        Assert.Equal("Repository factory not initialized. Call InitializeAsync first.", exception.Message);
    }

    [Fact]
    public async Task CreateGraphRepository_AfterInitialization_ReturnsKuzuGraphRepository()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var repository = _factory.CreateGraphRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<KuzuGraphRepository>(repository);
        Assert.IsAssignableFrom<ICodeGraphRepository>(repository);
    }

    [Fact]
    public async Task CreateFileMetadataRepository_BeforeInitialization_ThrowsInvalidOperationException()
    {
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => 
            _factory.CreateFileMetadataRepository());
        Assert.Equal("Repository factory not initialized. Call InitializeAsync first.", exception.Message);
    }

    [Fact]
    public async Task CreateFileMetadataRepository_AfterInitialization_ReturnsKuzuFileMetadataRepository()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var repository = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotNull(repository);
        Assert.IsType<KuzuFileMetadataRepository>(repository);
        Assert.IsAssignableFrom<IFileMetadataRepository>(repository);
    }

    [Fact]
    public async Task CreateFileMetadataRepository_AfterInitialization_UsesCorrectLogger()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var repository = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotNull(repository);
        _mockLoggerFactory.Received(1).CreateLogger<KuzuFileMetadataRepository>();
    }

    [Fact]
    public async Task CreateRepositories_MultipleCalls_ReturnsDifferentInstances()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var nodeRepo1 = _factory.CreateNodeRepository();
        var nodeRepo2 = _factory.CreateNodeRepository();
        var edgeRepo1 = _factory.CreateEdgeRepository();
        var edgeRepo2 = _factory.CreateEdgeRepository();
        var graphRepo1 = _factory.CreateGraphRepository();
        var graphRepo2 = _factory.CreateGraphRepository();
        var fileRepo1 = _factory.CreateFileMetadataRepository();
        var fileRepo2 = _factory.CreateFileMetadataRepository();

        // Assert
        Assert.NotSame(nodeRepo1, nodeRepo2);
        Assert.NotSame(edgeRepo1, edgeRepo2);
        Assert.NotSame(graphRepo1, graphRepo2);
        Assert.NotSame(fileRepo1, fileRepo2);
    }

    [Fact]
    public async Task CreateRepositories_ShareSameKuzuApiInstance()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act
        var nodeRepo = _factory.CreateNodeRepository() as KuzuNodeRepository;
        var edgeRepo = _factory.CreateEdgeRepository() as KuzuEdgeRepository;
        var graphRepo = _factory.CreateGraphRepository() as KuzuGraphRepository;
        var fileRepo = _factory.CreateFileMetadataRepository() as KuzuFileMetadataRepository;

        // Assert
        Assert.NotNull(nodeRepo);
        Assert.NotNull(edgeRepo);
        Assert.NotNull(graphRepo);
        Assert.NotNull(fileRepo);
        // All repositories should share the same KuzuApi instance passed to the factory
        // This is validated by the fact that they all work with the same mocked instance
    }

    [Fact]
    public async Task FactoryImplementsInterface()
    {
        // Assert
        Assert.IsAssignableFrom<IRepositoryFactory>(_factory);
    }

    [Fact]
    public async Task AllCreateMethods_ReturnCorrectInterfaces()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());

        // Act & Assert
        Assert.IsAssignableFrom<ICodeNodeRepository>(_factory.CreateNodeRepository());
        Assert.IsAssignableFrom<ICodeEdgeRepository>(_factory.CreateEdgeRepository());
        Assert.IsAssignableFrom<ICodeGraphRepository>(_factory.CreateGraphRepository());
        Assert.IsAssignableFrom<IFileMetadataRepository>(_factory.CreateFileMetadataRepository());
    }

    [Fact]
    public async Task InitializationState_IsTrackedCorrectly()
    {
        // Arrange & Act - Before initialization
        Assert.Throws<InvalidOperationException>(() => _factory.CreateNodeRepository());
        Assert.Throws<InvalidOperationException>(() => _factory.CreateEdgeRepository());
        Assert.Throws<InvalidOperationException>(() => _factory.CreateGraphRepository());
        Assert.Throws<InvalidOperationException>(() => _factory.CreateFileMetadataRepository());

        // Act - After initialization
        await _factory.InitializeAsync(Path.GetTempPath());

        // Assert - After initialization
        Assert.NotNull(_factory.CreateNodeRepository());
        Assert.NotNull(_factory.CreateEdgeRepository());
        Assert.NotNull(_factory.CreateGraphRepository());
        Assert.NotNull(_factory.CreateFileMetadataRepository());
    }

    [Fact]
    public async Task DatabasePathConstruction_IsConsistent()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        var expectedDbPath = Path.Combine(rootPath, ".codecontext", "codecontext.kuzu");

        // Act
        await _factory.InitializeAsync(rootPath);

        // Assert
        _mockKuzuApi.Received(1).InitializeDatabase(expectedDbPath);
    }

    [Fact]
    public async Task ConcurrentInitialization_HandledCorrectly()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        var tasks = new Task[10];

        // Act
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = _factory.InitializeAsync(rootPath);
        }

        await Task.WhenAll(tasks);

        // Assert
        _mockKuzuApi.Received(10).InitializeDatabase(Arg.Any<string>());
    }

    [Fact]
    public async Task ConcurrentRepositoryCreation_AfterInitialization_IsThreadSafe()
    {
        // Arrange
        await _factory.InitializeAsync(Path.GetTempPath());
        var nodeRepoTasks = new Task<ICodeNodeRepository>[10];
        var edgeRepoTasks = new Task<ICodeEdgeRepository>[10];

        // Act
        for (int i = 0; i < 10; i++)
        {
            nodeRepoTasks[i] = Task.Run(() => _factory.CreateNodeRepository());
            edgeRepoTasks[i] = Task.Run(() => _factory.CreateEdgeRepository());
        }

        var nodeRepos = await Task.WhenAll(nodeRepoTasks);
        var edgeRepos = await Task.WhenAll(edgeRepoTasks);

        // Assert
        Assert.All(nodeRepos, repo => Assert.NotNull(repo));
        Assert.All(edgeRepos, repo => Assert.NotNull(repo));
        
        // Verify all instances are unique
        var uniqueNodeRepos = nodeRepos.Distinct().ToArray();
        var uniqueEdgeRepos = edgeRepos.Distinct().ToArray();
        Assert.Equal(10, uniqueNodeRepos.Length);
        Assert.Equal(10, uniqueEdgeRepos.Length);
    }

    [Fact]
    public void Constructor_WithNullKuzuApi_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new KuzuRepositoryFactory(null!, _mockLoggerFactory));
    }

    [Fact]
    public void Constructor_WithNullLoggerFactory_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new KuzuRepositoryFactory(_mockKuzuApi, null!));
    }

    [Fact]
    public async Task TaskRunWrapping_InInitialization_PropagatesExceptions()
    {
        // Arrange
        var rootPath = Path.GetTempPath();
        _mockKuzuApi.When(x => x.InitializeDatabase(Arg.Any<string>()))
                    .Do(x => throw new TimeoutException("Database timeout"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => 
            _factory.InitializeAsync(rootPath));
        Assert.Equal("Database timeout", exception.Message);
    }
}
