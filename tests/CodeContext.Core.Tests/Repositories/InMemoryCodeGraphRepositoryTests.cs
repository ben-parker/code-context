using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class InMemoryCodeGraphRepositoryTests
{
    private readonly InMemoryDatabase _database;
    private readonly InMemoryCodeGraphRepository _repository;

    public InMemoryCodeGraphRepositoryTests()
    {
        _database = new InMemoryDatabase();
        _repository = new InMemoryCodeGraphRepository(_database);
    }

    [Fact]
    public async Task SaveGraphAsync_WithEmptyGraph_ClearsExistingData()
    {
        // Arrange
        var existingNode = CreateTestNode("existing-node", "ExistingClass");
        var existingEdge = CreateTestEdge("existing-edge", "source", "target", "CALLS");
        
        _database.UpsertNode(existingNode);
        _database.UpsertEdge(existingEdge);

        var emptyGraph = new CodeGraph
        {
            Nodes = new List<CodeNode>(),
            Edges = new List<CodeEdge>()
        };

        // Act
        var result = await _repository.SaveGraphAsync(emptyGraph);

        // Assert
        Assert.IsType<Guid>(result);
        Assert.Equal(0, _database.NodeCount);
        Assert.Equal(0, _database.EdgeCount);
        Assert.Same(emptyGraph, _database.CurrentGraph);
    }

    [Fact]
    public async Task SaveGraphAsync_WithNewGraph_SavesNodesAndEdges()
    {
        // Arrange
        var node1 = CreateTestNode("node1", "Class1");
        var node2 = CreateTestNode("node2", "Class2");
        var edge1 = CreateTestEdge("edge1", "node1", "node2", "CALLS");
        
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode> { node1, node2 },
            Edges = new List<CodeEdge> { edge1 }
        };

        // Act
        var result = await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.IsType<Guid>(result);
        Assert.Equal(2, _database.NodeCount);
        Assert.Equal(1, _database.EdgeCount);
        Assert.Same(graph, _database.CurrentGraph);
    }

    [Fact]
    public async Task SaveGraphAsync_WithNodesWithNullIds_SkipsNullNodes()
    {
        // Arrange
        var validNode = CreateTestNode("valid-node", "ValidClass");
        var nullIdNode = CreateTestNode(null, "NullIdClass");
        
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode> { validNode, nullIdNode },
            Edges = new List<CodeEdge>()
        };

        // Act
        await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.Equal(1, _database.NodeCount);
        Assert.Equal("valid-node", _database.EnumerateNodes().First().Id);
    }

    [Fact]
    public async Task SaveGraphAsync_WithEdgesWithNullIds_SkipsNullEdges()
    {
        // Arrange
        var validEdge = CreateTestEdge("valid-edge", "source", "target", "CALLS");
        var nullIdEdge = CreateTestEdge(null, "source", "target", "INHERITS");
        
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode>(),
            Edges = new List<CodeEdge> { validEdge, nullIdEdge }
        };

        // Act
        await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.Equal(1, _database.EdgeCount);
        Assert.Equal("valid-edge", _database.EnumerateEdges().First().Id);
    }

    [Fact]
    public async Task SaveGraphAsync_ReplacesExistingGraph_ClearsOldData()
    {
        // Arrange
        var oldNode = CreateTestNode("old-node", "OldClass");
        var oldEdge = CreateTestEdge("old-edge", "old-source", "old-target", "CALLS");
        
        var oldGraph = new CodeGraph
        {
            Nodes = new List<CodeNode> { oldNode },
            Edges = new List<CodeEdge> { oldEdge }
        };

        await _repository.SaveGraphAsync(oldGraph);

        var newNode = CreateTestNode("new-node", "NewClass");
        var newGraph = new CodeGraph
        {
            Nodes = new List<CodeNode> { newNode },
            Edges = new List<CodeEdge>()
        };

        // Act
        await _repository.SaveGraphAsync(newGraph);

        // Assert
        Assert.Equal(1, _database.NodeCount);
        Assert.Equal("new-node", _database.EnumerateNodes().First().Id);
        Assert.Equal(0, _database.EdgeCount);
        Assert.Same(newGraph, _database.CurrentGraph);
    }

    [Fact]
    public async Task GetGraphAsync_WithEmptyDatabase_ReturnsEmptyGraph()
    {
        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GetGraphAsync_WithPopulatedDatabase_ReturnsGraphWithAllData()
    {
        // Arrange
        var node1 = CreateTestNode("node1", "Class1");
        var node2 = CreateTestNode("node2", "Class2");
        var edge1 = CreateTestEdge("edge1", "node1", "node2", "CALLS");
        
        _database.UpsertNode(node1);
        _database.UpsertNode(node2);
        _database.UpsertEdge(edge1);

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Single(result.Edges);
        Assert.Contains(result.Nodes, n => n.Id == "node1");
        Assert.Contains(result.Nodes, n => n.Id == "node2");
        Assert.Contains(result.Edges, e => e.Id == "edge1");
    }

    [Fact]
    public async Task ClearAsync_WithPopulatedDatabase_RemovesAllData()
    {
        // Arrange
        var node = CreateTestNode("node1", "Class1");
        var edge = CreateTestEdge("edge1", "source", "target", "CALLS");
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode> { node },
            Edges = new List<CodeEdge> { edge }
        };

        _database.UpsertNode(node);
        _database.UpsertEdge(edge);
        _database.CurrentGraph = graph;

        // Act
        await _repository.ClearAsync();

        // Assert
        Assert.Equal(0, _database.NodeCount);
        Assert.Equal(0, _database.EdgeCount);
        Assert.Null(_database.CurrentGraph);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithValidJson_UpdatesDatabase()
    {
        // Arrange
        var existingNode = CreateTestNode("existing-node", "ExistingClass");
        _database.UpsertNode(existingNode);

        var nodeDto = new NodeDto(
            Id: "new-node",
            Name: "NewClass",
            Type: "Class",
            Language: "csharp",
            FilePath: "/test/path.cs",
            StartLine: 1,
            EndLine: 10,
            StartCol: 0,
            EndCol: 0,
            Namespace: "Test.Namespace",
            Visibility: "public",
            Signature: "public class NewClass",
            ReturnType: null,
            Parameters: null,
            Modifiers: null,
            Metrics: null,
            Metadata: null
        );

        var edgeDto = new EdgeDto(
            Id: "new-edge",
            SourceId: "source",
            TargetId: "target",
            Type: "CALLS",
            Metadata: new Dictionary<string, string> { ["test"] = "value" }
        );

        var nodesJson = JsonSerializer.Serialize(new List<NodeDto> { nodeDto }, CodeContextJsonContext.Default.ListNodeDto);
        var edgesJson = JsonSerializer.Serialize(new List<EdgeDto> { edgeDto }, CodeContextJsonContext.Default.ListEdgeDto);

        // Act
        var result = await _repository.ReconcileAndPruneAsync(nodesJson, edgesJson);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, _database.NodeCount);
        Assert.Equal(1, _database.EdgeCount);
        Assert.Equal("new-node", _database.EnumerateNodes().First().Id);
        Assert.Equal("new-edge", _database.EnumerateEdges().First().Id);

        var stats = JsonSerializer.Deserialize(result, CodeContextJsonContext.Default.ReconcileStatsDto);
        Assert.NotNull(stats);
        Assert.Equal(1, stats.NodesMerged);
        Assert.Equal(1, stats.EdgesMerged);
        Assert.Equal(1, stats.NodesDeleted);
        Assert.Equal("reconcile_and_prune", stats.Operation);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithEmptyJson_ClearsDatabase()
    {
        // Arrange
        var existingNode = CreateTestNode("existing-node", "ExistingClass");
        var existingEdge = CreateTestEdge("existing-edge", "source", "target", "CALLS");
        
        _database.UpsertNode(existingNode);
        _database.UpsertEdge(existingEdge);

        var nodesJson = JsonSerializer.Serialize(new List<NodeDto>(), CodeContextJsonContext.Default.ListNodeDto);
        var edgesJson = JsonSerializer.Serialize(new List<EdgeDto>(), CodeContextJsonContext.Default.ListEdgeDto);

        // Act
        var result = await _repository.ReconcileAndPruneAsync(nodesJson, edgesJson);

        // Assert
        Assert.Equal(0, _database.NodeCount);
        Assert.Equal(0, _database.EdgeCount);

        var stats = JsonSerializer.Deserialize(result, CodeContextJsonContext.Default.ReconcileStatsDto);
        Assert.NotNull(stats);
        Assert.Equal(0, stats.NodesMerged);
        Assert.Equal(0, stats.EdgesMerged);
        Assert.Equal(1, stats.NodesDeleted);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithNullNodeIds_SkipsNullNodes()
    {
        // Arrange
        var validNodeDto = new NodeDto(
            Id: "valid-node",
            Name: "ValidClass",
            Type: "Class",
            Language: "csharp",
            FilePath: "/test/path.cs",
            StartLine: 1,
            EndLine: 10,
            StartCol: 0,
            EndCol: 0,
            Namespace: "Test.Namespace",
            Visibility: "public",
            Signature: "public class ValidClass",
            ReturnType: null,
            Parameters: null,
            Modifiers: null,
            Metrics: null,
            Metadata: null
        );

        var nullIdNodeDto = new NodeDto(
            Id: null,
            Name: "NullIdClass",
            Type: "Class",
            Language: "csharp",
            FilePath: "/test/path.cs",
            StartLine: 1,
            EndLine: 10,
            StartCol: 0,
            EndCol: 0,
            Namespace: "Test.Namespace",
            Visibility: "public",
            Signature: "public class NullIdClass",
            ReturnType: null,
            Parameters: null,
            Modifiers: null,
            Metrics: null,
            Metadata: null
        );

        var nodesJson = JsonSerializer.Serialize(new List<NodeDto> { validNodeDto, nullIdNodeDto }, CodeContextJsonContext.Default.ListNodeDto);
        var edgesJson = JsonSerializer.Serialize(new List<EdgeDto>(), CodeContextJsonContext.Default.ListEdgeDto);

        // Act
        await _repository.ReconcileAndPruneAsync(nodesJson, edgesJson);

        // Assert
        Assert.Equal(1, _database.NodeCount);
        Assert.Equal("valid-node", _database.EnumerateNodes().First().Id);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithNullEdgeIds_SkipsNullEdges()
    {
        // Arrange
        var validEdgeDto = new EdgeDto(
            Id: "valid-edge",
            SourceId: "source",
            TargetId: "target",
            Type: "CALLS",
            Metadata: new Dictionary<string, string>()
        );

        var nullIdEdgeDto = new EdgeDto(
            Id: null,
            SourceId: "source",
            TargetId: "target",
            Type: "INHERITS",
            Metadata: new Dictionary<string, string>()
        );

        var nodesJson = JsonSerializer.Serialize(new List<NodeDto>(), CodeContextJsonContext.Default.ListNodeDto);
        var edgesJson = JsonSerializer.Serialize(new List<EdgeDto> { validEdgeDto, nullIdEdgeDto }, CodeContextJsonContext.Default.ListEdgeDto);

        // Act
        await _repository.ReconcileAndPruneAsync(nodesJson, edgesJson);

        // Assert
        Assert.Equal(1, _database.EdgeCount);
        Assert.Equal("valid-edge", _database.EnumerateEdges().First().Id);
    }

    [Fact]
    public async Task ConcurrentOperations_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        var operationCount = 10;

        // Act - Concurrent save operations
        for (int i = 0; i < operationCount; i++)
        {
            var node = CreateTestNode($"node-{i}", $"Class{i}");
            var graph = new CodeGraph
            {
                Nodes = new List<CodeNode> { node },
                Edges = new List<CodeEdge>()
            };
            tasks.Add(_repository.SaveGraphAsync(graph));
        }

        await Task.WhenAll(tasks);

        // Assert - Last saved graph should be in database
        var result = await _repository.GetGraphAsync();
        Assert.NotNull(result);
        Assert.Single(result.Nodes);
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
