using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class InMemoryEdgeRepositoryTests
{
    private readonly InMemoryDatabase _database;
    private readonly InMemoryEdgeRepository _repository;

    public InMemoryEdgeRepositoryTests()
    {
        _database = new InMemoryDatabase();
        _repository = new InMemoryEdgeRepository(_database);
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
    public async Task GetAllAsync_WithPopulatedRepository_ReturnsAllEdges()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source2", "target2", "INHERITS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Id == "edge1");
        Assert.Contains(result, e => e.Id == "edge2");
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithValidSourceId_ReturnsMatchingEdges()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source1", "target2", "INHERITS");
        var edge3 = CreateTestEdge("edge3", "source2", "target3", "CALLS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);
        _database.UpsertEdge(edge3);

        // Act
        var result = await _repository.GetBySourceIdAsync("source1");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("source1", e.SourceId));
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithTypeFilter_ReturnsFilteredResults()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source1", "target2", "INHERITS");
        var edge3 = CreateTestEdge("edge3", "source1", "target3", "CALLS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);
        _database.UpsertEdge(edge3);

        // Act
        var result = await _repository.GetBySourceIdAsync("source1", type: "CALLS");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("CALLS", e.Type));
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithNonExistentSourceId_ReturnsEmptyList()
    {
        // Arrange
        var edge = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        _database.UpsertEdge(edge);

        // Act
        var result = await _repository.GetBySourceIdAsync("non-existent-source");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithValidTargetId_ReturnsMatchingEdges()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source2", "target1", "INHERITS");
        var edge3 = CreateTestEdge("edge3", "source3", "target2", "CALLS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);
        _database.UpsertEdge(edge3);

        // Act
        var result = await _repository.GetByTargetIdAsync("target1");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("target1", e.TargetId));
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithTypeFilter_ReturnsFilteredResults()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source2", "target1", "INHERITS");
        var edge3 = CreateTestEdge("edge3", "source3", "target1", "CALLS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);
        _database.UpsertEdge(edge3);

        // Act
        var result = await _repository.GetByTargetIdAsync("target1", type: "CALLS");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("CALLS", e.Type));
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithNonExistentTargetId_ReturnsEmptyList()
    {
        // Arrange
        var edge = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        _database.UpsertEdge(edge);

        // Act
        var result = await _repository.GetByTargetIdAsync("non-existent-target");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task UpsertAsync_WithNewEdge_InsertsEdge()
    {
        // Arrange
        var edge = CreateTestEdge("new-edge", "source1", "target1", "CALLS");

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        Assert.True(_database.ContainsEdge("new-edge"));
        Assert.Equal("CALLS", _database.GetEdge("new-edge")!.Type);
    }

    [Fact]
    public async Task UpsertAsync_WithExistingEdge_UpdatesEdge()
    {
        // Arrange
        var originalEdge = CreateTestEdge("existing-edge", "source1", "target1", "CALLS");
        _database.UpsertEdge(originalEdge);

        var updatedEdge = CreateTestEdge("existing-edge", "source1", "target1", "INHERITS");

        // Act
        await _repository.UpsertAsync(updatedEdge);

        // Assert
        Assert.Equal("INHERITS", _database.GetEdge("existing-edge")!.Type);
    }

    [Fact]
    public async Task UpsertAsync_WithNullId_GeneratesNewId()
    {
        // Arrange
        var edge = CreateTestEdge(null, "source1", "target1", "CALLS");

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        Assert.NotNull(edge.Id);
        Assert.True(_database.ContainsEdge(edge.Id));
    }

    [Fact]
    public async Task UpsertAsync_WithEmptyId_GeneratesNewId()
    {
        // Arrange
        var edge = CreateTestEdge("", "source1", "target1", "CALLS");

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        Assert.NotNull(edge.Id);
        Assert.NotEmpty(edge.Id);
        Assert.True(_database.ContainsEdge(edge.Id));
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithValidNodeId_RemovesRelatedEdges()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "node1", "target1", "CALLS");
        var edge2 = CreateTestEdge("edge2", "source1", "node1", "INHERITS");
        var edge3 = CreateTestEdge("edge3", "source2", "target2", "CALLS");
        
        _database.UpsertEdge(edge1);
        _database.UpsertEdge(edge2);
        _database.UpsertEdge(edge3);

        // Act
        await _repository.DeleteByNodeIdAsync("node1", CancellationToken.None);

        // Assert
        Assert.False(_database.ContainsEdge("edge1"));
        Assert.False(_database.ContainsEdge("edge2"));
        Assert.True(_database.ContainsEdge("edge3"));
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithNonExistentNodeId_DoesNotThrow()
    {
        // Arrange
        var edge = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        _database.UpsertEdge(edge);

        // Act & Assert
        var exception = await Record.ExceptionAsync(() => 
            _repository.DeleteByNodeIdAsync("non-existent-node", CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithCancellationToken_CompletesSuccessfully()
    {
        // Arrange
        var edge = CreateTestEdge("edge1", "node1", "target1", "CALLS");
        _database.UpsertEdge(edge);
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteByNodeIdAsync("node1", cts.Token);

        // Assert
        Assert.False(_database.ContainsEdge("edge1"));
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithNullId_HandlesGracefully()
    {
        // Arrange
        var edge1 = CreateTestEdge("edge1", "source1", "target1", "CALLS");
        var edge2 = CreateTestEdge(null, "source2", "target2", "INHERITS");
        
        _database.UpsertEdge(edge1);
        // Note: edge2 with null ID won't be added to database

        // Act & Assert
        var exception = await Record.ExceptionAsync(() => 
            _repository.DeleteByNodeIdAsync("source1", CancellationToken.None));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ConcurrentOperations_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var edgeCount = 100;

        // Act - Concurrent upserts
        for (int i = 0; i < edgeCount; i++)
        {
            var edge = CreateTestEdge($"edge-{i}", $"source-{i}", $"target-{i}", "CALLS");
            tasks.Add(_repository.UpsertAsync(edge));
        }

        await Task.WhenAll(tasks);

        // Assert
        var allEdges = await _repository.GetAllAsync();
        Assert.Equal(edgeCount, allEdges.Count);
    }

    [Fact]
    public async Task ConcurrentDeleteOperations_ThreadSafe()
    {
        // Arrange
        var nodeCount = 50;
        var edgesPerNode = 3;
        
        // Create edges for each node
        for (int i = 0; i < nodeCount; i++)
        {
            for (int j = 0; j < edgesPerNode; j++)
            {
                var edge = CreateTestEdge($"edge-{i}-{j}", $"node-{i}", $"target-{j}", "CALLS");
                _database.UpsertEdge(edge);
            }
        }

        var tasks = new List<Task>();

        // Act - Concurrent deletes
        for (int i = 0; i < nodeCount; i++)
        {
            var nodeId = $"node-{i}";
            tasks.Add(_repository.DeleteByNodeIdAsync(nodeId, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        // Assert
        var remainingEdges = await _repository.GetAllAsync();
        Assert.Empty(remainingEdges);
    }

    [Fact]
    public async Task GetBySourceIdAsync_NoTypeFilter_ReturnedInstance_IsNotDowncastableToRawBuffer()
    {
        _database.UpsertEdge(CreateTestEdge("edge1", "source1", "target1", "CALLS"));
        _database.UpsertEdge(CreateTestEdge("edge2", "source1", "target2", "INHERITS"));

        var result = await _repository.GetBySourceIdAsync("source1");

        // The keyed lookup's backing bucket is a raw CodeEdge[] shared by every reader (or, multi-shard,
        // a per-call List). Neither may escape downcastable — a stray .Sort()/index-write would corrupt
        // the frozen snapshot other readers hold.
        Assert.Equal(2, result.Count);
        Assert.Null(result as CodeEdge[]);
        Assert.Null(result as List<CodeEdge>);
    }

    [Fact]
    public async Task GetByTargetIdAsync_NoTypeFilter_ReturnedInstance_IsNotDowncastableToRawBuffer()
    {
        _database.UpsertEdge(CreateTestEdge("edge1", "source1", "target1", "CALLS"));
        _database.UpsertEdge(CreateTestEdge("edge2", "source2", "target1", "INHERITS"));

        var result = await _repository.GetByTargetIdAsync("target1");

        Assert.Equal(2, result.Count);
        Assert.Null(result as CodeEdge[]);
        Assert.Null(result as List<CodeEdge>);
    }

    [Fact]
    public async Task GetAllAsync_ReturnedInstance_IsNotDowncastableToMutableBackingStore()
    {
        _database.UpsertEdge(CreateTestEdge("edge1", "source1", "target1", "CALLS"));

        var result = await _repository.GetAllAsync();

        Assert.Null(result as CodeEdge[]);
        Assert.Null(result as List<CodeEdge>);
        Assert.IsType<ReadOnlyCollection<CodeEdge>>(result);
    }

    private static CodeEdge CreateTestEdge(string? id, string sourceId, string targetId, string type)
    {
        return new CodeEdge
        {
            Id = id,
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Metadata = new Dictionary<string, string>
            {
                ["test"] = "value"
            }
        };
    }
}