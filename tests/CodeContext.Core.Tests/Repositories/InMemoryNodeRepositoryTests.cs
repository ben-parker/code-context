using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class InMemoryNodeRepositoryTests
{
    private readonly InMemoryDatabase _database;
    private readonly InMemoryNodeRepository _repository;

    public InMemoryNodeRepositoryTests()
    {
        _database = new InMemoryDatabase();
        _repository = new InMemoryNodeRepository(_database);
    }

    [Fact]
    public async Task GetByIdAsync_WithValidId_ReturnsNode()
    {
        // Arrange
        var node = CreateTestNode("test-id", "TestClass");
        _database.Nodes.TryAdd(node.Id!, node);

        // Act
        var result = await _repository.GetByIdAsync("test-id");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-id", result.Id);
        Assert.Equal("TestClass", result.Name);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync("non-existent-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindByNameAsync_WithExactMatchTrue_ReturnsExactMatches()
    {
        // Arrange
        var node1 = CreateTestNode("id1", "TestClass");
        var node2 = CreateTestNode("id2", "TestClassHelper");
        var node3 = CreateTestNode("id3", "testclass");
        
        _database.Nodes.TryAdd(node1.Id!, node1);
        _database.Nodes.TryAdd(node2.Id!, node2);
        _database.Nodes.TryAdd(node3.Id!, node3);

        // Act
        var result = await _repository.FindByNameAsync("TestClass", exact: true);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Name == "TestClass");
        Assert.Contains(result, n => n.Name == "testclass");
    }

    [Fact]
    public async Task FindByNameAsync_WithExactMatchFalse_ReturnsContainsMatches()
    {
        // Arrange
        var node1 = CreateTestNode("id1", "TestClass");
        var node2 = CreateTestNode("id2", "TestClassHelper");
        var node3 = CreateTestNode("id3", "AnotherClass");
        
        _database.Nodes.TryAdd(node1.Id!, node1);
        _database.Nodes.TryAdd(node2.Id!, node2);
        _database.Nodes.TryAdd(node3.Id!, node3);

        // Act
        var result = await _repository.FindByNameAsync("TestClass", exact: false);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Name == "TestClass");
        Assert.Contains(result, n => n.Name == "TestClassHelper");
    }

    [Fact]
    public async Task FindByNameAsync_WithTypeFilter_ReturnsFilteredResults()
    {
        // Arrange
        var classNode = CreateTestNode("id1", "TestClass", "Class");
        var methodNode = CreateTestNode("id2", "TestClass", "Method");
        
        _database.Nodes.TryAdd(classNode.Id!, classNode);
        _database.Nodes.TryAdd(methodNode.Id!, methodNode);

        // Act
        var result = await _repository.FindByNameAsync("TestClass", type: "Class", exact: true);

        // Assert
        Assert.Single(result);
        Assert.Equal("Class", result.First().Type);
    }

    [Fact]
    public async Task FindByNameAsync_WithLowercaseTypeFilter_MatchesCaseInsensitively()
    {
        // Arrange
        var methodNode = CreateTestNode("id1", "Parse", "Method");
        _database.Nodes.TryAdd(methodNode.Id!, methodNode);

        // Act
        var result = await _repository.FindByNameAsync("Parse", type: "method", exact: true);

        // Assert
        Assert.Single(result);
        Assert.Equal("Method", result.First().Type);
    }

    [Fact]
    public async Task FindByNameAsync_WithNullName_ReturnsEmptyList()
    {
        // Arrange
        var node = CreateTestNode("id1", "TestClass");
        _database.Nodes.TryAdd(node.Id!, node);

        // Act
        var result = await _repository.FindByNameAsync(null!, exact: false);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindByNameAsync_CaseInsensitive_ReturnsMatches()
    {
        // Arrange
        var node1 = CreateTestNode("id1", "TestClass");
        var node2 = CreateTestNode("id2", "testclass");
        var node3 = CreateTestNode("id3", "TESTCLASS");
        
        _database.Nodes.TryAdd(node1.Id!, node1);
        _database.Nodes.TryAdd(node2.Id!, node2);
        _database.Nodes.TryAdd(node3.Id!, node3);

        // Act
        var result = await _repository.FindByNameAsync("testclass", exact: true);

        // Assert
        Assert.Equal(3, result.Count);
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
    public async Task GetAllAsync_WithPopulatedRepository_ReturnsAllNodes()
    {
        // Arrange
        var node1 = CreateTestNode("id1", "TestClass1");
        var node2 = CreateTestNode("id2", "TestClass2");
        
        _database.Nodes.TryAdd(node1.Id!, node1);
        _database.Nodes.TryAdd(node2.Id!, node2);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Name == "TestClass1");
        Assert.Contains(result, n => n.Name == "TestClass2");
    }

    [Fact]
    public async Task UpsertAsync_WithNewNode_InsertsNode()
    {
        // Arrange
        var node = CreateTestNode("new-id", "NewClass");

        // Act
        await _repository.UpsertAsync(node);

        // Assert
        Assert.True(_database.Nodes.ContainsKey("new-id"));
        Assert.Equal("NewClass", _database.Nodes["new-id"].Name);
    }

    [Fact]
    public async Task PublicIdentifier_IsIndexedSeparatelyAndMustBeUnique()
    {
        var first = CreateTestNode("internal-1", "Run", "Method");
        first.Identifier = "csharp:Example.Service.Run(int)";
        await _repository.UpsertAsync(first);

        Assert.Same(first, await _repository.GetByIdentifierAsync(first.Identifier));
        Assert.Null(await _repository.GetByIdentifierAsync("internal-1"));

        var duplicate = CreateTestNode("internal-2", "Run", "Method");
        duplicate.Identifier = first.Identifier;
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.UpsertAsync(duplicate));
    }

    [Fact]
    public async Task UpsertAsync_WithExistingNode_UpdatesNode()
    {
        // Arrange
        var originalNode = CreateTestNode("existing-id", "OriginalClass");
        _database.Nodes.TryAdd(originalNode.Id!, originalNode);

        var updatedNode = CreateTestNode("existing-id", "UpdatedClass");

        // Act
        await _repository.UpsertAsync(updatedNode);

        // Assert
        Assert.Equal("UpdatedClass", _database.Nodes["existing-id"].Name);
    }

    [Fact]
    public async Task UpsertAsync_WithNullId_ThrowsArgumentException()
    {
        // Arrange
        var node = CreateTestNode(null, "TestClass");

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertAsync(node));
        Assert.Equal("Node must have an Id (Parameter 'node')", exception.Message);
    }

    [Fact]
    public async Task DeleteAsync_WithExistingId_RemovesNode()
    {
        // Arrange
        var node = CreateTestNode("delete-id", "DeleteClass");
        _database.Nodes.TryAdd(node.Id!, node);

        // Act
        await _repository.DeleteAsync("delete-id", CancellationToken.None);

        // Assert
        Assert.False(_database.Nodes.ContainsKey("delete-id"));
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentId_DoesNotThrow()
    {
        // Act & Assert
        var exception = await Record.ExceptionAsync(() => _repository.DeleteAsync("non-existent-id", CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var node = CreateTestNode("delete-id", "DeleteClass");
        _database.Nodes.TryAdd(node.Id!, node);
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteAsync("delete-id", cts.Token);

        // Assert
        Assert.False(_database.Nodes.ContainsKey("delete-id"));
    }

    [Fact]
    public async Task ConcurrentOperations_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var nodeCount = 100;

        // Act - Concurrent upserts
        for (int i = 0; i < nodeCount; i++)
        {
            var nodeId = $"node-{i}";
            var node = CreateTestNode(nodeId, $"Class{i}");
            tasks.Add(_repository.UpsertAsync(node));
        }

        await Task.WhenAll(tasks);

        // Assert
        var allNodes = await _repository.GetAllAsync();
        Assert.Equal(nodeCount, allNodes.Count);
    }

    private static CodeNode CreateTestNode(string? id, string name, string type = "Class")
    {
        return new CodeNode
        {
            Id = id,
            Name = name,
            Type = type,
            FilePath = "/test/path.cs",
            StartLine = 1,
            EndLine = 10,
            Namespace = "Test.Namespace",
            Visibility = "public",
            Signature = $"public {type.ToLower()} {name}"
        };
    }
}
