using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.Kuzu;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Repositories.Kuzu;

public class KuzuEdgeRepositoryTests
{
    private readonly IKuzuApi _mockKuzuApi;
    private readonly KuzuEdgeRepository _repository;

    public KuzuEdgeRepositoryTests()
    {
        _mockKuzuApi = Substitute.For<IKuzuApi>();
        _repository = new KuzuEdgeRepository(_mockKuzuApi);
    }

    [Fact]
    public async Task GetAllAsync_WithValidResponse_ReturnsAllEdges()
    {
        // Arrange
        var edgesJson = CreateValidEdgesListJson();
        _mockKuzuApi.GetAllEdges().Returns(edgesJson);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Id == "edge-1");
        Assert.Contains(result, e => e.Id == "edge-2");
        _mockKuzuApi.Received(1).GetAllEdges();
    }

    [Fact]
    public async Task GetAllAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        // Arrange
        _mockKuzuApi.GetAllEdges().Returns("[]");

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).GetAllEdges();
    }

    [Fact]
    public async Task GetAllAsync_WithNullResponse_ReturnsEmptyList()
    {
        // Arrange
        _mockKuzuApi.GetAllEdges().Returns((string?)null);

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).GetAllEdges();
    }

    [Fact]
    public async Task GetAllAsync_WithMalformedResponse_PropagatesJsonFailure()
    {
        // Arrange
        _mockKuzuApi.GetAllEdges().Returns("invalid json");

        // A malformed backend response is not a conclusive empty graph.
        await Assert.ThrowsAnyAsync<JsonException>(() => _repository.GetAllAsync());
        _mockKuzuApi.Received(1).GetAllEdges();
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithValidSourceId_ReturnsEdges()
    {
        // Arrange
        var sourceId = "source-node-1";
        var dependenciesJson = CreateValidDependenciesJson();
        _mockKuzuApi.GetDependencies(sourceId).Returns(dependenciesJson);

        // Act
        var result = await _repository.GetBySourceIdAsync(sourceId);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, edge => Assert.Equal(sourceId, edge.SourceId));
        Assert.Contains(result, e => e.TargetId == "target-1");
        Assert.Contains(result, e => e.TargetId == "target-2");
        _mockKuzuApi.Received(1).GetDependencies(sourceId);
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithTypeFilter_ReturnsFilteredEdges()
    {
        // Arrange
        var sourceId = "source-node-1";
        var dependenciesJson = CreateValidDependenciesJson();
        _mockKuzuApi.GetDependencies(sourceId).Returns(dependenciesJson);

        // Act
        var result = await _repository.GetBySourceIdAsync(sourceId, type: "CALLS");

        // Assert
        Assert.Single(result);
        Assert.Equal("CALLS", result.First().Type);
        _mockKuzuApi.Received(1).GetDependencies(sourceId);
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithEmptyTypeFilter_ReturnsAllEdges()
    {
        // Arrange
        var sourceId = "source-node-1";
        var dependenciesJson = CreateValidDependenciesJson();
        _mockKuzuApi.GetDependencies(sourceId).Returns(dependenciesJson);

        // Act
        var result = await _repository.GetBySourceIdAsync(sourceId, type: "");

        // Assert
        Assert.Equal(2, result.Count);
        _mockKuzuApi.Received(1).GetDependencies(sourceId);
    }

    [Fact]
    public async Task GetBySourceIdAsync_WithNullResponse_ReturnsEmptyList()
    {
        // Arrange
        var sourceId = "non-existent-source";
        _mockKuzuApi.GetDependencies(sourceId).Returns((string?)null);

        // Act
        var result = await _repository.GetBySourceIdAsync(sourceId);

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).GetDependencies(sourceId);
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithValidTargetId_ReturnsEdges()
    {
        // Arrange
        var targetId = "target-node-1";
        var callersJson = CreateValidCallersJson();
        _mockKuzuApi.GetCallers(targetId).Returns(callersJson);

        // Act
        var result = await _repository.GetByTargetIdAsync(targetId);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, edge => Assert.Equal(targetId, edge.TargetId));
        Assert.Contains(result, e => e.SourceId == "caller-1");
        Assert.Contains(result, e => e.SourceId == "caller-2");
        _mockKuzuApi.Received(1).GetCallers(targetId);
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithTypeFilter_ReturnsFilteredEdges()
    {
        // Arrange
        var targetId = "target-node-1";
        var callersJson = CreateValidCallersJson();
        _mockKuzuApi.GetCallers(targetId).Returns(callersJson);

        // Act
        var result = await _repository.GetByTargetIdAsync(targetId, type: "CALLS");

        // Assert
        Assert.Single(result);
        Assert.Equal("CALLS", result.First().Type);
        _mockKuzuApi.Received(1).GetCallers(targetId);
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithNullEdgeInfo_SkipsEdge()
    {
        // Arrange
        var targetId = "target-node-1";
        var callersJson = CreateCallersJsonWithNullEdgeInfo();
        _mockKuzuApi.GetCallers(targetId).Returns(callersJson);

        // Act
        var result = await _repository.GetByTargetIdAsync(targetId);

        // Assert
        Assert.Single(result);
        Assert.Equal("caller-1", result.First().SourceId);
        _mockKuzuApi.Received(1).GetCallers(targetId);
    }

    [Fact]
    public async Task GetByTargetIdAsync_WithNullResponse_ReturnsEmptyList()
    {
        // Arrange
        var targetId = "non-existent-target";
        _mockKuzuApi.GetCallers(targetId).Returns((string?)null);

        // Act
        var result = await _repository.GetByTargetIdAsync(targetId);

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).GetCallers(targetId);
    }

    [Fact]
    public async Task UpsertAsync_WithValidEdge_CallsInsertEdge()
    {
        // Arrange
        var edge = CreateTestEdge("edge-1", "source-1", "target-1", "CALLS");

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        _mockKuzuApi.Received(1).InsertEdge(Arg.Is<string>(json => 
            json.Contains("\"id\":\"edge-1\"") && 
            json.Contains("\"source_id\":\"source-1\"") && 
            json.Contains("\"target_id\":\"target-1\"") && 
            json.Contains("\"type\":\"CALLS\"")));
    }

    [Fact]
    public async Task UpsertAsync_WithNullId_SerializesCorrectly()
    {
        // Arrange
        var edge = CreateTestEdge(null, "source-1", "target-1", "CALLS");

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        _mockKuzuApi.Received(1).InsertEdge(Arg.Is<string>(json => 
            json.Contains("\"source_id\":\"source-1\"") && 
            json.Contains("\"target_id\":\"target-1\"") && 
            json.Contains("\"type\":\"CALLS\"")));
    }

    [Fact]
    public async Task UpsertAsync_WithMetadata_SerializesMetadata()
    {
        // Arrange
        var edge = CreateTestEdge("edge-1", "source-1", "target-1", "CALLS");
        edge.Metadata = new Dictionary<string, string> { ["test"] = "value", ["key"] = "data" };

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        _mockKuzuApi.Received(1).InsertEdge(Arg.Is<string>(json => 
            json.Contains("\"metadata\":{") && 
            json.Contains("\"test\":\"value\"") && 
            json.Contains("\"key\":\"data\"")));
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithValidNodeId_CallsDeleteEdgesByNode()
    {
        // Arrange
        var nodeId = "node-1";
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteByNodeIdAsync(nodeId, cts.Token);

        // Assert
        _mockKuzuApi.Received(1).DeleteEdgesByNode(nodeId);
    }

    [Fact]
    public async Task DeleteByNodeIdAsync_WithCancellationToken_PropagatesToken()
    {
        // Arrange
        var nodeId = "node-1";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.DeleteByNodeIdAsync(nodeId, cts.Token));
    }

    [Fact]
    public async Task ConvertToEdgeDto_MapsAllProperties()
    {
        // Arrange
        var edge = CreateComplexTestEdge();

        // Act
        await _repository.UpsertAsync(edge);

        // Assert
        _mockKuzuApi.Received(1).InsertEdge(Arg.Is<string>(json => 
            json.Contains("\"id\":\"complex-edge\"") && 
            json.Contains("\"source_id\":\"complex-source\"") && 
            json.Contains("\"target_id\":\"complex-target\"") && 
            json.Contains("\"type\":\"INHERITS\"") && 
            json.Contains("\"metadata\":{\"complexity\":\"high\"")));
    }

    [Fact]
    public async Task TaskRunWrapping_PropagatesExceptions()
    {
        // Arrange
        var sourceId = "error-source";
        _mockKuzuApi.GetDependencies(sourceId).Returns(x => throw new InvalidOperationException("Test error"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _repository.GetBySourceIdAsync(sourceId));
        Assert.Equal("Test error", exception.Message);
    }

    [Fact]
    public async Task EdgeConstruction_FromDependencies_SetsCorrectProperties()
    {
        // Arrange
        var sourceId = "source-1";
        var dependenciesJson = CreateValidDependenciesJson();
        _mockKuzuApi.GetDependencies(sourceId).Returns(dependenciesJson);

        // Act
        var result = await _repository.GetBySourceIdAsync(sourceId);

        // Assert
        var edge = result.First();
        Assert.Equal(sourceId, edge.SourceId);
        Assert.Equal("target-1", edge.TargetId);
        Assert.Equal("CALLS", edge.Type);
        Assert.Null(edge.Id); // Dependencies don't have edge IDs
        Assert.Null(edge.Metadata); // Dependencies don't have metadata
    }

    [Fact]
    public async Task EdgeConstruction_FromCallers_SetsCorrectProperties()
    {
        // Arrange
        var targetId = "target-1";
        var callersJson = CreateValidCallersJson();
        _mockKuzuApi.GetCallers(targetId).Returns(callersJson);

        // Act
        var result = await _repository.GetByTargetIdAsync(targetId);

        // Assert
        var edge = result.First();
        Assert.Equal("caller-1", edge.SourceId);
        Assert.Equal(targetId, edge.TargetId);
        Assert.Equal("CALLS", edge.Type);
        Assert.Equal("edge-info-1", edge.Id);
        Assert.NotNull(edge.Metadata);
        Assert.Equal("value1", edge.Metadata["key1"]);
    }

    private static string CreateValidEdgesListJson()
    {
        var edges = new List<EdgeDto>
        {
            new EdgeDto(
                Id: "edge-1",
                SourceId: "source-1",
                TargetId: "target-1",
                Type: "CALLS",
                Metadata: new Dictionary<string, string> { ["key1"] = "value1" }
            ),
            new EdgeDto(
                Id: "edge-2",
                SourceId: "source-2",
                TargetId: "target-2",
                Type: "INHERITS",
                Metadata: new Dictionary<string, string> { ["key2"] = "value2" }
            )
        };
        return JsonSerializer.Serialize(edges, CodeContextJsonContext.Default.ListEdgeDto);
    }

    private static string CreateValidDependenciesJson()
    {
        var dependencies = new List<NodeWithRelationshipTypeDto>
        {
            new NodeWithRelationshipTypeDto(
                Id: "target-1",
                Name: "Target1",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/target1.cs",
                StartLine: 1,
                EndLine: 10,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public class Target1",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                RelationshipType: "CALLS"
            ),
            new NodeWithRelationshipTypeDto(
                Id: "target-2",
                Name: "Target2",
                Type: "Method",
                Language: "csharp",
                FilePath: "/test/target2.cs",
                StartLine: 20,
                EndLine: 30,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public void Target2()",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                RelationshipType: "INHERITS"
            )
        };
        return JsonSerializer.Serialize(dependencies, CodeContextJsonContext.Default.IReadOnlyListNodeWithRelationshipTypeDto);
    }

    private static string CreateValidCallersJson()
    {
        var edgeInfo1 = new EdgeDto(
            Id: "edge-info-1",
            SourceId: null,
            TargetId: null,
            Type: "CALLS",
            Metadata: new Dictionary<string, string> { ["key1"] = "value1" }
        );

        var edgeInfo2 = new EdgeDto(
            Id: "edge-info-2",
            SourceId: null,
            TargetId: null,
            Type: "INHERITS",
            Metadata: new Dictionary<string, string> { ["key2"] = "value2" }
        );

        var callers = new List<NodeWithEdgeInfoDto>
        {
            new NodeWithEdgeInfoDto(
                Id: "caller-1",
                Name: "Caller1",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/caller1.cs",
                StartLine: 1,
                EndLine: 10,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public class Caller1",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                EdgeInfo: edgeInfo1
            ),
            new NodeWithEdgeInfoDto(
                Id: "caller-2",
                Name: "Caller2",
                Type: "Method",
                Language: "csharp",
                FilePath: "/test/caller2.cs",
                StartLine: 20,
                EndLine: 30,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public void Caller2()",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                EdgeInfo: edgeInfo2
            )
        };
        return JsonSerializer.Serialize(callers, CodeContextJsonContext.Default.IReadOnlyListNodeWithEdgeInfoDto);
    }

    private static string CreateCallersJsonWithNullEdgeInfo()
    {
        var edgeInfo1 = new EdgeDto(
            Id: "edge-info-1",
            SourceId: null,
            TargetId: null,
            Type: "CALLS",
            Metadata: new Dictionary<string, string> { ["key1"] = "value1" }
        );

        var callers = new List<NodeWithEdgeInfoDto>
        {
            new NodeWithEdgeInfoDto(
                Id: "caller-1",
                Name: "Caller1",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/caller1.cs",
                StartLine: 1,
                EndLine: 10,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public class Caller1",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                EdgeInfo: edgeInfo1
            ),
            new NodeWithEdgeInfoDto(
                Id: "caller-2",
                Name: "Caller2",
                Type: "Method",
                Language: "csharp",
                FilePath: "/test/caller2.cs",
                StartLine: 20,
                EndLine: 30,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test",
                Visibility: "public",
                Signature: "public void Caller2()",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null,
                EdgeInfo: null
            )
        };
        return JsonSerializer.Serialize(callers, CodeContextJsonContext.Default.IReadOnlyListNodeWithEdgeInfoDto);
    }

    private static CodeEdge CreateTestEdge(string? id, string sourceId, string targetId, string type)
    {
        return new CodeEdge
        {
            Id = id,
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Metadata = new Dictionary<string, string>()
        };
    }

    private static CodeEdge CreateComplexTestEdge()
    {
        return new CodeEdge
        {
            Id = "complex-edge",
            SourceId = "complex-source",
            TargetId = "complex-target",
            Type = "INHERITS",
            Metadata = new Dictionary<string, string>
            {
                ["complexity"] = "high",
                ["confidence"] = "0.95"
            }
        };
    }
}
