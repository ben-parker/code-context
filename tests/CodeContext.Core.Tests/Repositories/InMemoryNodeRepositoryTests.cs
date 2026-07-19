using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
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
        _database.UpsertNode(node);

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
        
        _database.UpsertNode(node1);
        _database.UpsertNode(node2);
        _database.UpsertNode(node3);

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
        
        _database.UpsertNode(node1);
        _database.UpsertNode(node2);
        _database.UpsertNode(node3);

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
        
        _database.UpsertNode(classNode);
        _database.UpsertNode(methodNode);

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
        _database.UpsertNode(methodNode);

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
        _database.UpsertNode(node);

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
        
        _database.UpsertNode(node1);
        _database.UpsertNode(node2);
        _database.UpsertNode(node3);

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
        
        _database.UpsertNode(node1);
        _database.UpsertNode(node2);

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
        Assert.True(_database.ContainsNode("new-id"));
        Assert.Equal("NewClass", _database.GetNode("new-id")!.Name);
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
        _database.UpsertNode(originalNode);

        var updatedNode = CreateTestNode("existing-id", "UpdatedClass");

        // Act
        await _repository.UpsertAsync(updatedNode);

        // Assert
        Assert.Equal("UpdatedClass", _database.GetNode("existing-id")!.Name);
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
        _database.UpsertNode(node);

        // Act
        await _repository.DeleteAsync("delete-id", CancellationToken.None);

        // Assert
        Assert.False(_database.ContainsNode("delete-id"));
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
        _database.UpsertNode(node);
        using var cts = new CancellationTokenSource();

        // Act
        await _repository.DeleteAsync("delete-id", cts.Token);

        // Assert
        Assert.False(_database.ContainsNode("delete-id"));
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

    [Fact]
    public async Task FindByFilePathAsync_MatchesBruteForceFilePathMatcherScan_AcrossPathShapes()
    {
        // A file-path index keyed on normalized paths, exercised against every match shape.
        var featureClass = FileNode("f-class", "TestClass", "Class", "/repo/src/Feature/TestClass.cs");
        var featureMethod = FileNode("f-method", "DoWork", "Method", "/repo/src/Feature/TestClass.cs");
        var otherClass = FileNode("o-class", "TestClass", "Class", "/repo/src/Other/TestClass.cs");
        var helper = FileNode("helper", "Helper", "Class", "/repo/src/Feature/Helper.cs");
        // Stored with backslashes to prove the index normalizes exactly like FilePathMatcher.
        var winStyle = FileNode("win", "Widget", "Class", "/repo/src/Feature/Widget.cs".Replace('/', '\\'));
        foreach (var node in new[] { featureClass, featureMethod, otherClass, helper, winStyle })
            _database.UpsertNode(node);

        var all = await _repository.GetAllAsync();

        (string Path, string? Type)[] queries =
        [
            ("/repo/src/Feature/TestClass.cs", null),  // rooted: exact key only (excludes Other/TestClass.cs)
            ("Feature/TestClass.cs", null),            // relative multi-segment: exact-or-suffix
            ("TestClass.cs", null),                    // bare filename suffix: both TestClass.cs files
            ("/REPO/SRC/FEATURE/testclass.cs", null),  // rooted, case-insensitive
            ("feature/widget.cs", null),               // relative suffix against a backslash-stored path
            ("TestClass.cs", "Method"),                // type filter narrows to the method
            ("Nonexistent.cs", null),                  // no match
        ];

        foreach (var (path, type) in queries)
        {
            var actual = await _repository.FindByFilePathAsync(path, type);
            var expected = BruteForceMatches(all, path, type);
            Assert.Equal(
                expected.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal),
                actual.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal));
        }

        // Guard against a vacuous brute force: confirm the interesting shapes are non-trivial.
        // Bare "TestClass.cs" suffix-matches all three TestClass.cs nodes (both in Feature + the one in Other).
        Assert.Equal(3, (await _repository.FindByFilePathAsync("TestClass.cs")).Count);
        Assert.Equal("f-method", Assert.Single(await _repository.FindByFilePathAsync("TestClass.cs", "Method")).Id);
        Assert.Empty(await _repository.FindByFilePathAsync("Nonexistent.cs"));
    }

    [Fact]
    public async Task GetAllAsync_ReturnedInstance_IsNotDowncastableToMutableBackingStore()
    {
        _database.UpsertNode(CreateTestNode("id1", "One"));
        _database.UpsertNode(CreateTestNode("id2", "Two"));

        var result = await _repository.GetAllAsync();

        // The whole-graph node list is a cached, shared snapshot: it must not be downcastable to
        // the mutable List/array backing the adjacency, or a caller could corrupt every other reader.
        Assert.Null(result as CodeNode[]);
        Assert.Null(result as List<CodeNode>);
        Assert.IsType<ReadOnlyCollection<CodeNode>>(result);
    }

    private static IReadOnlyList<CodeNode> BruteForceMatches(
        IReadOnlyList<CodeNode> all, string path, string? type)
    {
        IEnumerable<CodeNode> matches = all.Where(n => FilePathMatcher.Matches(n.FilePath, path));
        if (!string.IsNullOrEmpty(type))
            matches = matches.Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));
        return matches.ToList();
    }

    private static CodeNode FileNode(string id, string name, string type, string filePath) => new()
    {
        Id = id,
        Name = name,
        Type = type,
        FilePath = filePath,
        StartLine = 1,
        EndLine = 5,
    };

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
