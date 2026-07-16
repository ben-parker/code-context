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

public class KuzuNodeRepositoryTests
{
    private readonly IKuzuApi _mockKuzuApi;
    private readonly KuzuNodeRepository _repository;

    public KuzuNodeRepositoryTests()
    {
        _mockKuzuApi = Substitute.For<IKuzuApi>();
        _repository = new KuzuNodeRepository(_mockKuzuApi);
    }

    [Fact]
    public async Task GetByIdAsync_WithValidId_ReturnsNode()
    {
        // Arrange
        var nodeId = "test-node-1";
        var nodeJson = CreateValidNodeJson("test-node-1", "TestClass", "Class");
        _mockKuzuApi.GetNodeById(nodeId).Returns(nodeJson);

        // Act
        var result = await _repository.GetByIdAsync(nodeId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-node-1", result.Id);
        Assert.Equal("TestClass", result.Name);
        Assert.Equal("Class", result.Type);
        _mockKuzuApi.Received(1).GetNodeById(nodeId);
    }

    [Fact]
    public async Task GetByIdAsync_WithNullResponse_ReturnsNull()
    {
        // Arrange
        var nodeId = "non-existent-node";
        _mockKuzuApi.GetNodeById(nodeId).Returns((string?)null);

        // Act
        var result = await _repository.GetByIdAsync(nodeId);

        // Assert
        Assert.Null(result);
        _mockKuzuApi.Received(1).GetNodeById(nodeId);
    }

    [Fact]
    public async Task GetByIdAsync_WithEmptyResponse_ReturnsNull()
    {
        // Arrange
        var nodeId = "empty-node";
        _mockKuzuApi.GetNodeById(nodeId).Returns("");

        // Act
        var result = await _repository.GetByIdAsync(nodeId);

        // Assert
        Assert.Null(result);
        _mockKuzuApi.Received(1).GetNodeById(nodeId);
    }

    [Fact]
    public async Task GetByIdAsync_WithNullJsonResponse_ReturnsNull()
    {
        // Arrange
        var nodeId = "null-node";
        _mockKuzuApi.GetNodeById(nodeId).Returns("null");

        // Act
        var result = await _repository.GetByIdAsync(nodeId);

        // Assert
        Assert.Null(result);
        _mockKuzuApi.Received(1).GetNodeById(nodeId);
    }

    [Fact]
    public async Task GetByIdAsync_WithMalformedResponse_PropagatesJsonFailure()
    {
        // Arrange
        var nodeId = "invalid-node";
        _mockKuzuApi.GetNodeById(nodeId).Returns("invalid json");

        // A malformed backend response is not a conclusive "not found" result.
        await Assert.ThrowsAnyAsync<JsonException>(() => _repository.GetByIdAsync(nodeId));
        _mockKuzuApi.Received(1).GetNodeById(nodeId);
    }

    [Fact]
    public async Task FindByNameAsync_WithNameOnly_CallsCorrectApi()
    {
        // Arrange
        var name = "TestClass";
        var nodesJson = CreateValidNodesListJson("test-1", "TestClass", "Class");
        _mockKuzuApi.FindNodesByName(name, false).Returns(nodesJson);

        // Act
        var result = await _repository.FindByNameAsync(name);

        // Assert
        Assert.Single(result);
        Assert.Equal("TestClass", result.First().Name);
        _mockKuzuApi.Received(1).FindNodesByName(name, false);
        _mockKuzuApi.DidNotReceive().FindNodesByNameAndType(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task FindByNameAsync_WithNameAndType_CallsOptimizedApi()
    {
        // Arrange
        var name = "TestClass";
        var type = "Class";
        var nodesJson = CreateValidNodesListJson("test-1", "TestClass", "Class");
        _mockKuzuApi.FindNodesByNameAndType(name, type, false).Returns(nodesJson);

        // Act
        var result = await _repository.FindByNameAsync(name, type);

        // Assert
        Assert.Single(result);
        Assert.Equal("TestClass", result.First().Name);
        _mockKuzuApi.Received(1).FindNodesByNameAndType(name, type, false);
        _mockKuzuApi.DidNotReceive().FindNodesByName(Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task FindByNameAsync_WithExactMatch_PassesExactFlag()
    {
        // Arrange
        var name = "TestClass";
        var nodesJson = CreateValidNodesListJson("test-1", "TestClass", "Class");
        _mockKuzuApi.FindNodesByName(name, true).Returns(nodesJson);

        // Act
        var result = await _repository.FindByNameAsync(name, exact: true);

        // Assert
        Assert.Single(result);
        _mockKuzuApi.Received(1).FindNodesByName(name, true);
    }

    [Fact]
    public async Task FindByNameAsync_WithEmptyType_UsesNameOnlyApi()
    {
        // Arrange
        var name = "TestClass";
        var nodesJson = CreateValidNodesListJson("test-1", "TestClass", "Class");
        _mockKuzuApi.FindNodesByName(name, false).Returns(nodesJson);

        // Act
        var result = await _repository.FindByNameAsync(name, "");

        // Assert
        Assert.Single(result);
        _mockKuzuApi.Received(1).FindNodesByName(name, false);
        _mockKuzuApi.DidNotReceive().FindNodesByNameAndType(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task FindByNameAsync_WithNullResponse_ReturnsEmptyList()
    {
        // Arrange
        var name = "NonExistentClass";
        _mockKuzuApi.FindNodesByName(name, false).Returns((string?)null);

        // Act
        var result = await _repository.FindByNameAsync(name);

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).FindNodesByName(name, false);
    }

    [Fact]
    public async Task FindByNameAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        // Arrange
        var name = "EmptyClass";
        _mockKuzuApi.FindNodesByName(name, false).Returns("");

        // Act
        var result = await _repository.FindByNameAsync(name);

        // Assert
        Assert.Empty(result);
        _mockKuzuApi.Received(1).FindNodesByName(name, false);
    }

    [Fact]
    public async Task FindByNameAsync_WithMultipleResults_ReturnsAllNodes()
    {
        // Arrange
        var name = "TestClass";
        var nodesJson = CreateMultipleNodesJson();
        _mockKuzuApi.FindNodesByName(name, false).Returns(nodesJson);

        // Act
        var result = await _repository.FindByNameAsync(name);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, n => n.Name == "TestClass1");
        Assert.Contains(result, n => n.Name == "TestClass2");
        _mockKuzuApi.Received(1).FindNodesByName(name, false);
    }

    [Fact]
    public async Task GetAllAsync_CallsApiForEachNodeType()
    {
        // Arrange
        var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
        foreach (var nodeType in nodeTypes)
        {
            var nodesJson = CreateValidNodesListJson($"test-{nodeType}", $"Test{nodeType}", nodeType);
            _mockKuzuApi.FindNodesByType(nodeType).Returns(nodesJson);
        }

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(7, result.Count);
        foreach (var nodeType in nodeTypes)
        {
            _mockKuzuApi.Received(1).FindNodesByType(nodeType);
            Assert.Contains(result, n => n.Type == nodeType);
        }
    }

    [Fact]
    public async Task GetAllAsync_WithSomeEmptyTypes_ReturnsAvailableNodes()
    {
        // Arrange
        _mockKuzuApi.FindNodesByType("Class").Returns(CreateValidNodesListJson("test-1", "TestClass", "Class"));
        _mockKuzuApi.FindNodesByType("Interface").Returns("");
        _mockKuzuApi.FindNodesByType("Method").Returns(CreateValidNodesListJson("test-2", "TestMethod", "Method"));
        _mockKuzuApi.FindNodesByType("Property").Returns((string?)null);
        _mockKuzuApi.FindNodesByType("Field").Returns("null");
        _mockKuzuApi.FindNodesByType("Enum").Returns(CreateValidNodesListJson("test-3", "TestEnum", "Enum"));
        _mockKuzuApi.FindNodesByType("Struct").Returns("[]");

        // Act
        var result = await _repository.GetAllAsync();

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains(result, n => n.Type == "Class");
        Assert.Contains(result, n => n.Type == "Method");
        Assert.Contains(result, n => n.Type == "Enum");
    }

    [Fact]
    public async Task UpsertAsync_WithValidNode_CallsInsertNode()
    {
        // Arrange
        var node = CreateTestNode("test-1", "TestClass", "Class");
        var expectedJson = JsonSerializer.Serialize(CreateNodeDto(node), CodeContextJsonContext.Default.NodeDto);

        // Act
        await _repository.UpsertAsync(node);

        // Assert
        _mockKuzuApi.Received(1).InsertNode(Arg.Is<string>(json => 
            json.Contains("\"id\":\"test-1\"") && 
            json.Contains("\"name\":\"TestClass\"") && 
            json.Contains("\"type\":\"Class\"")));
    }

    [Fact]
    public async Task UpsertAsync_WithNullId_SerializesCorrectly()
    {
        // Arrange
        var node = CreateTestNode(null, "TestClass", "Class");

        // Act
        await _repository.UpsertAsync(node);

        // Assert
        _mockKuzuApi.Received(1).InsertNode(Arg.Is<string>(json => 
            json.Contains("\"name\":\"TestClass\"") && 
            json.Contains("\"type\":\"Class\"")));
    }

    [Fact]
    public async Task UpsertAsync_WithComplexNode_SerializesAllProperties()
    {
        // Arrange
        var node = CreateComplexTestNode();

        // Act
        await _repository.UpsertAsync(node);

        // Assert
        _mockKuzuApi.Received(1).InsertNode(Arg.Is<string>(json => 
            json.Contains("\"namespace\":\"Test.Namespace\"") && 
            json.Contains("\"visibility\":\"public\"") && 
            json.Contains("\"signature\":\"public class TestClass\"") &&
            json.Contains("\"start_line\":10") &&
            json.Contains("\"end_line\":20")));
    }

    [Fact]
    public async Task DeleteAsync_WithValidId_CallsDeleteNode()
    {
        // Arrange
        var nodeId = "test-node-1";
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteAsync(nodeId, cts.Token);

        // Assert
        _mockKuzuApi.Received(1).DeleteNode(nodeId);
    }

    [Fact]
    public async Task DeleteAsync_WithCancellationToken_PropagatesToken()
    {
        // Arrange
        var nodeId = "test-node-1";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.DeleteAsync(nodeId, cts.Token));
    }

    [Fact]
    public async Task ConvertToCodeNode_MapsAllProperties()
    {
        // Arrange
        var nodeDto = CreateComplexNodeDto();
        var nodeJson = JsonSerializer.Serialize(nodeDto, CodeContextJsonContext.Default.NodeDto);
        _mockKuzuApi.GetNodeById("test-1").Returns(nodeJson);

        // Act
        var result = await _repository.GetByIdAsync("test-1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(nodeDto.Id, result.Id);
        Assert.Equal(nodeDto.Name, result.Name);
        Assert.Equal(nodeDto.Type, result.Type);
        Assert.Equal(nodeDto.Language, result.Language);
        Assert.Equal(nodeDto.FilePath, result.FilePath);
        Assert.Equal(nodeDto.StartLine, result.StartLine);
        Assert.Equal(nodeDto.EndLine, result.EndLine);
        Assert.Equal(nodeDto.StartCol, result.StartCol);
        Assert.Equal(nodeDto.EndCol, result.EndCol);
        Assert.Equal(nodeDto.Namespace, result.Namespace);
        Assert.Equal(nodeDto.Visibility, result.Visibility);
        Assert.Equal(nodeDto.Signature, result.Signature);
        Assert.Equal(nodeDto.ReturnType, result.ReturnType);
        Assert.Equal(nodeDto.Parameters, result.Parameters);
        Assert.Equal(nodeDto.Modifiers, result.Modifiers);
        Assert.Equal(nodeDto.Metrics, result.Metrics);
        Assert.Equal(nodeDto.Metadata, result.Metadata);
    }

    [Fact]
    public async Task TaskRunWrapping_PropagatesExceptions()
    {
        // Arrange
        var nodeId = "error-node";
        _mockKuzuApi.GetNodeById(nodeId).Returns(x => throw new InvalidOperationException("Test error"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => 
            _repository.GetByIdAsync(nodeId));
        Assert.Equal("Test error", exception.Message);
    }

    private static string CreateValidNodeJson(string id, string name, string type)
    {
        return JsonSerializer.Serialize(new NodeDto(
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
        ), CodeContextJsonContext.Default.NodeDto);
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

    private static string CreateMultipleNodesJson()
    {
        var nodes = new List<NodeDto>
        {
            new NodeDto(
                Id: "test-1",
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
                Id: "test-2",
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

    private static CodeNode CreateTestNode(string? id, string name, string type)
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

    private static NodeDto CreateNodeDto(CodeNode node)
    {
        return new NodeDto(
            Id: node.Id,
            Name: node.Name,
            Type: node.Type,
            Language: node.Language,
            FilePath: node.FilePath,
            StartLine: node.StartLine,
            EndLine: node.EndLine,
            StartCol: node.StartCol,
            EndCol: node.EndCol,
            Namespace: node.Namespace,
            Visibility: node.Visibility,
            Signature: node.Signature,
            ReturnType: node.ReturnType,
            Parameters: node.Parameters,
            Modifiers: node.Modifiers,
            Metrics: node.Metrics,
            Metadata: node.Metadata
        );
    }

    private static NodeDto CreateComplexNodeDto()
    {
        return new NodeDto(
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
        );
    }
}
