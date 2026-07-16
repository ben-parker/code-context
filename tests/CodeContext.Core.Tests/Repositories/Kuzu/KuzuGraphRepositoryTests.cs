using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.Kuzu;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Repositories.Kuzu;

public class KuzuGraphRepositoryTests
{
    private readonly IKuzuApi _mockKuzuApi;
    private readonly KuzuGraphRepository _repository;

    public KuzuGraphRepositoryTests()
    {
        _mockKuzuApi = Substitute.For<IKuzuApi>();
        _repository = new KuzuGraphRepository(_mockKuzuApi);
    }

    [Fact]
    public async Task SaveGraphAsync_WithEmptyGraph_DoesNotCallBatchInserts()
    {
        // Arrange
        var emptyGraph = new CodeGraph
        {
            Nodes = new List<CodeNode>(),
            Edges = new List<CodeEdge>()
        };

        // Act
        var result = await _repository.SaveGraphAsync(emptyGraph);

        // Assert
        Assert.IsType<Guid>(result);
        _mockKuzuApi.DidNotReceive().InsertNodesBatch(Arg.Any<string>());
        _mockKuzuApi.DidNotReceive().InsertEdgesBatch(Arg.Any<string>());
    }

    [Fact]
    public async Task SaveGraphAsync_WithNodesOnly_CallsInsertNodesBatch()
    {
        // Arrange
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode>
            {
                CreateTestNode("node-1", "Class1", "Class"),
                CreateTestNode("node-2", "Class2", "Class")
            },
            Edges = new List<CodeEdge>()
        };

        // Act
        var result = await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.IsType<Guid>(result);
        _mockKuzuApi.Received(1).InsertNodesBatch(Arg.Is<string>(json => 
            json.Contains("\"id\":\"node-1\"") && 
            json.Contains("\"id\":\"node-2\"") && 
            json.Contains("\"name\":\"Class1\"") && 
            json.Contains("\"name\":\"Class2\"")));
        _mockKuzuApi.DidNotReceive().InsertEdgesBatch(Arg.Any<string>());
    }

    [Fact]
    public async Task SaveGraphAsync_WithEdgesOnly_CallsInsertEdgesBatch()
    {
        // Arrange
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode>(),
            Edges = new List<CodeEdge>
            {
                CreateTestEdge("edge-1", "source-1", "target-1", "CALLS"),
                CreateTestEdge("edge-2", "source-2", "target-2", "INHERITS")
            }
        };

        // Act
        var result = await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.IsType<Guid>(result);
        _mockKuzuApi.Received(1).InsertEdgesBatch(Arg.Is<string>(json => 
            json.Contains("\"id\":\"edge-1\"") && 
            json.Contains("\"id\":\"edge-2\"") && 
            json.Contains("\"type\":\"CALLS\"") && 
            json.Contains("\"type\":\"INHERITS\"")));
        _mockKuzuApi.DidNotReceive().InsertNodesBatch(Arg.Any<string>());
    }

    [Fact]
    public async Task SaveGraphAsync_WithNodesAndEdges_CallsBothBatchInserts()
    {
        // Arrange
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode>
            {
                CreateTestNode("node-1", "Class1", "Class")
            },
            Edges = new List<CodeEdge>
            {
                CreateTestEdge("edge-1", "source-1", "target-1", "CALLS")
            }
        };

        // Act
        var result = await _repository.SaveGraphAsync(graph);

        // Assert
        Assert.IsType<Guid>(result);
        _mockKuzuApi.Received(1).InsertNodesBatch(Arg.Is<string>(json => 
            json.Contains("\"id\":\"node-1\"") && 
            json.Contains("\"name\":\"Class1\"")));
        _mockKuzuApi.Received(1).InsertEdgesBatch(Arg.Is<string>(json => 
            json.Contains("\"id\":\"edge-1\"") && 
            json.Contains("\"type\":\"CALLS\"")));
    }

    [Fact]
    public async Task SaveGraphAsync_WithComplexGraph_SerializesAllProperties()
    {
        // Arrange
        var graph = new CodeGraph
        {
            Nodes = new List<CodeNode>
            {
                CreateComplexTestNode()
            },
            Edges = new List<CodeEdge>
            {
                CreateComplexTestEdge()
            }
        };

        // Act
        await _repository.SaveGraphAsync(graph);

        // Assert
        _mockKuzuApi.Received(1).InsertNodesBatch(Arg.Is<string>(json => 
            json.Contains("\"namespace\":\"Test.Namespace\"") && 
            json.Contains("\"visibility\":\"public\"") && 
            json.Contains("\"start_line\":10") && 
            json.Contains("\"end_line\":20") && 
            json.Contains("\"start_col\":4") && 
            json.Contains("\"end_col\":8")));
        _mockKuzuApi.Received(1).InsertEdgesBatch(Arg.Is<string>(json => 
            json.Contains("\"metadata\":{") && 
            json.Contains("\"complexity\":\"high\"")));
    }

    [Fact]
    public async Task GetGraphAsync_WithNoNodes_ReturnsEmptyGraph()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        foreach (var nodeType in nodeTypes)
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
        foreach (var nodeType in nodeTypes)
        {
            _mockKuzuApi.Received(1).FindNodesByType(nodeType);
        }
    }

    [Fact]
    public async Task GetGraphAsync_WithNodes_ReturnsGraphWithNodes()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateValidNodesListJson("class-1", "TestClass", "Class"));
        _mockKuzuApi.FindNodesByType("Method").Returns(CreateValidNodesListJson("method-1", "TestMethod", "Method"));
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class" && t != "Method"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Nodes.Count);
        Assert.Contains(result.Nodes, n => n.Name == "TestClass");
        Assert.Contains(result.Nodes, n => n.Name == "TestMethod");
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GetGraphAsync_WithNodesAndDependencies_ReturnsGraphWithEdges()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateValidNodesListJson("class-1", "TestClass", "Class"));
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        _mockKuzuApi.GetDependencies("class-1").Returns(CreateValidDependenciesJson("class-1"));

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Nodes);
        Assert.Single(result.Edges);
        Assert.Equal("class-1", result.Edges.First().SourceId);
        Assert.Equal("target-1", result.Edges.First().TargetId);
        Assert.Equal("CALLS", result.Edges.First().Type);
    }

    [Fact]
    public async Task GetGraphAsync_WithDuplicateNodeProcessing_ProcessesOnlyOnce()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        var duplicateNodesJson = CreateMultipleNodesListJson();
        _mockKuzuApi.FindNodesByType("Class").Returns(duplicateNodesJson);
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        _mockKuzuApi.GetDependencies("class-1").Returns("[]");
        _mockKuzuApi.GetDependencies("class-2").Returns("[]");

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Nodes.Count);
        _mockKuzuApi.Received(1).GetDependencies("class-1");
        _mockKuzuApi.Received(1).GetDependencies("class-2");
    }

    [Fact]
    public async Task GetGraphAsync_WithNullNodeIds_SkipsNullNodes()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateNodesListWithNullIds());
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Nodes);
        Assert.Equal("valid-node", result.Nodes.First().Id);
        _mockKuzuApi.Received(1).GetDependencies("valid-node");
        _mockKuzuApi.DidNotReceive().GetDependencies(Arg.Is<string>(id => id == null));
    }

    [Fact]
    public async Task GetGraphAsync_WithNullDependenciesResponse_SkipsDependencies()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateValidNodesListJson("class-1", "TestClass", "Class"));
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        _mockKuzuApi.GetDependencies("class-1").Returns((string?)null);

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task ClearAsync_CallsClearDatabase()
    {
        // Act
        await _repository.ClearAsync();

        // Assert
        _mockKuzuApi.Received(1).ClearDatabase();
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithValidJson_CallsReconcileAndPruneGraph()
    {
        // Arrange
        var nodesJson = "[]";
        var edgesJson = "[]";
        var expectedResult = "{\"nodes_merged\": 0, \"edges_merged\": 0}";
        _mockKuzuApi.ReconcileAndPruneGraph(nodesJson, edgesJson).Returns(expectedResult);

        // Act
        var result = await _repository.ReconcileAndPruneAsync(nodesJson, edgesJson);

        // Assert
        Assert.Equal(expectedResult, result);
        _mockKuzuApi.Received(1).ReconcileAndPruneGraph(nodesJson, edgesJson);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithErrorResponse_ThrowsException()
    {
        // Arrange
        var nodesJson = "[]";
        var edgesJson = "[]";
        var errorResponse = "{\"error\": true, \"error_type\": \"query_error\", \"message\": \"Test error\"}";
        _mockKuzuApi.ReconcileAndPruneGraph(nodesJson, edgesJson).Returns(errorResponse);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _repository.ReconcileAndPruneAsync(nodesJson, edgesJson));
        Assert.Contains("Test error", exception.Message);
        _mockKuzuApi.Received(1).ReconcileAndPruneGraph(nodesJson, edgesJson);
    }

    [Fact]
    public async Task ReconcileAndPruneAsync_WithTimeoutError_ThrowsTimeoutException()
    {
        // Arrange
        var nodesJson = "[]";
        var edgesJson = "[]";
        var errorResponse = "{\"error\": true, \"error_type\": \"query_timeout\", \"message\": \"Query timeout\"}";
        _mockKuzuApi.ReconcileAndPruneGraph(nodesJson, edgesJson).Returns(errorResponse);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => 
            _repository.ReconcileAndPruneAsync(nodesJson, edgesJson));
        Assert.Contains("Query timeout", exception.Message);
        _mockKuzuApi.Received(1).ReconcileAndPruneGraph(nodesJson, edgesJson);
    }

    [Fact]
    public async Task ConvertToCodeNode_MapsAllProperties()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateComplexNodesListJson());
        
        foreach (var nodeType in nodeTypes.Where(t => t != "Class"))
        {
            _mockKuzuApi.FindNodesByType(nodeType).Returns("[]");
        }

        // Act
        var result = await _repository.GetGraphAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.Nodes);
        var node = result.Nodes.First();
        Assert.Equal("complex-1", node.Id);
        Assert.Equal("TestClass", node.Name);
        Assert.Equal("Class", node.Type);
        Assert.Equal("csharp", node.Language);
        Assert.Equal("/test/complex.cs", node.FilePath);
        Assert.Equal(10, node.StartLine);
        Assert.Equal(20, node.EndLine);
        Assert.Equal("Test.Namespace", node.Namespace);
        Assert.Equal("public", node.Visibility);
        Assert.Equal("public class TestClass", node.Signature);
    }

    [Fact]
    public async Task TaskRunWrapping_PropagatesExceptions()
    {
        // Arrange
        _mockKuzuApi.FindNodesByType("Class").Returns(x => throw new InvalidOperationException("Test error"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _repository.GetGraphAsync());
        Assert.Equal("Test error", exception.Message);
    }

    private static CodeNode CreateTestNode(string id, string name, string type)
    {
        return new CodeNode
        {
            Id = id,
            Name = name,
            Type = type,
            Language = "csharp",
            FilePath = "/test/path.cs",
            StartLine = 1,
            EndLine = 10,
            StartCol = 0,
            EndCol = 0,
            Namespace = "Test.Namespace",
            Visibility = "public",
            Signature = $"public {type.ToLower()} {name}"
        };
    }

    private static CodeNode CreateComplexTestNode()
    {
        return new CodeNode
        {
            Id = "complex-1",
            Name = "TestClass",
            Type = "Class",
            Language = "csharp",
            FilePath = "/test/complex.cs",
            StartLine = 10,
            EndLine = 20,
            StartCol = 4,
            EndCol = 8,
            Namespace = "Test.Namespace",
            Visibility = "public",
            Signature = "public class TestClass",
            ReturnType = "void",
            Parameters = "string param1, int param2",
            Modifiers = "static",
            Metrics = "complexity: 5",
            Metadata = new Dictionary<string, string> { ["test"] = "value" }
        };
    }

    private static CodeEdge CreateTestEdge(string id, string sourceId, string targetId, string type)
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

    private static string CreateValidNodesListJson(string id, string name, string type)
    {
        var nodes = new List<NodeDto>
        {
            new NodeDto(
                Id: id,
                Name: name,
                Type: type,
                Language: "csharp",
                FilePath: "/test/path.cs",
                StartLine: 1,
                EndLine: 10,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test.Namespace",
                Visibility: "public",
                Signature: $"public {type.ToLower()} {name}",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null
            )
        };
        return JsonSerializer.Serialize(nodes, CodeContextJsonContext.Default.ListNodeDto);
    }

    private static string CreateMultipleNodesListJson()
    {
        var nodes = new List<NodeDto>
        {
            new NodeDto(
                Id: "class-1",
                Name: "TestClass1",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/path1.cs",
                StartLine: 1,
                EndLine: 10,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test.Namespace",
                Visibility: "public",
                Signature: "public class TestClass1",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null
            ),
            new NodeDto(
                Id: "class-2",
                Name: "TestClass2",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/path2.cs",
                StartLine: 20,
                EndLine: 30,
                StartCol: 0,
                EndCol: 0,
                Namespace: "Test.Namespace",
                Visibility: "public",
                Signature: "public class TestClass2",
                ReturnType: null,
                Parameters: null,
                Modifiers: null,
                Metrics: null,
                Metadata: null
            )
        };
        return JsonSerializer.Serialize(nodes, CodeContextJsonContext.Default.ListNodeDto);
    }

    private static string CreateNodesListWithNullIds()
    {
        var nodes = new List<NodeDto>
        {
            new NodeDto(
                Id: "valid-node",
                Name: "ValidClass",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/valid.cs",
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
            ),
            new NodeDto(
                Id: null,
                Name: "NullIdClass",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/null.cs",
                StartLine: 20,
                EndLine: 30,
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
            )
        };
        return JsonSerializer.Serialize(nodes, CodeContextJsonContext.Default.ListNodeDto);
    }

    private static string CreateComplexNodesListJson()
    {
        var nodes = new List<NodeDto>
        {
            new NodeDto(
                Id: "complex-1",
                Name: "TestClass",
                Type: "Class",
                Language: "csharp",
                FilePath: "/test/complex.cs",
                StartLine: 10,
                EndLine: 20,
                StartCol: 4,
                EndCol: 8,
                Namespace: "Test.Namespace",
                Visibility: "public",
                Signature: "public class TestClass",
                ReturnType: "void",
                Parameters: "string param1, int param2",
                Modifiers: "static",
                Metrics: "complexity: 5",
                Metadata: new Dictionary<string, string> { ["test"] = "value" }
            )
        };
        return JsonSerializer.Serialize(nodes, CodeContextJsonContext.Default.ListNodeDto);
    }

    private static string CreateValidDependenciesJson(string sourceId)
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
            )
        };
        return JsonSerializer.Serialize(dependencies, CodeContextJsonContext.Default.IReadOnlyListNodeWithRelationshipTypeDto);
    }
}
