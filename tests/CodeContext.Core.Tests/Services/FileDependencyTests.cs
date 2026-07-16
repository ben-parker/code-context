using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

public class FileDependencyTests
{
    private readonly ICodeNodeRepository _nodes = Substitute.For<ICodeNodeRepository>();
    private readonly ICodeEdgeRepository _edges = Substitute.For<ICodeEdgeRepository>();
    private readonly IFileMetadataRepository _files = Substitute.For<IFileMetadataRepository>();

    [Theory]
    [InlineData("CALLS")]
    [InlineData("MOCK_CALLS")]
    [InlineData("REFERENCES")]
    [InlineData("IMPLEMENTS")]
    [InlineData("INHERITS")]
    [InlineData("EXTENDS")]
    [InlineData("IMPORTS")]
    public async Task SemanticEdgeKindsProduceOutboundAndInboundFileRelationships(string edgeKind)
    {
        var root = NewRoot();
        var source = Node("source", "Source", Path.Combine(root, "src", "Source.cs"));
        var target = Node("target", "Target", Path.Combine(root, "src", "Target.cs"));
        var edge = Edge("source", "target", edgeKind);
        ConfigureNodes(source, target);
        _nodes.FindByNameAsync("Source", null, false).Returns([source]);
        _nodes.FindByNameAsync("Target", null, false).Returns([target]);
        _edges.GetBySourceIdAsync("source").Returns([edge]);
        _edges.GetBySourceIdAsync("target").Returns([]);
        _edges.GetByTargetIdAsync("source").Returns([]);
        _edges.GetByTargetIdAsync("target").Returns([edge]);

        var service = CreateService(root);
        var outbound = await service.GetCompleteContextAsync(
            "Source", depth: 1, includeTests: false, includeRelated: false, includeMetrics: false);
        var inbound = await service.GetCompleteContextAsync(
            "Target", depth: 1, includeTests: false, includeRelated: false, includeMetrics: false);

        Assert.Equal(["src/Target.cs"], Assert.Single(outbound.Matches).Relationships.FileDependencies);
        Assert.Equal(["src/Source.cs"], Assert.Single(inbound.Matches).Relationships.FileDependents);
    }

    [Fact]
    public async Task NonSemanticUnresolvedSameFileAndNamespaceOnlyRelationshipsAreExcluded()
    {
        var root = NewRoot();
        var sourcePath = Path.Combine(root, "src", "Source.cs");
        var source = Node("source", "Source", sourcePath, "Example");
        var sameFile = Node("same-file", "Member", sourcePath, "Example");
        var namespaceOnly = Node("namespace-only", "NamespaceSibling", Path.Combine(root, "src", "NamespaceSibling.cs"), "Example");
        var unknown = Node("unknown", "Unknown", Path.Combine(root, "src", "Unknown.cs"));
        var containment = Node("containment", "Child", Path.Combine(root, "src", "Containment.cs"));
        var nullKind = Node("null-kind", "NullKind", Path.Combine(root, "src", "NullKind.cs"));
        ConfigureNodes(source, sameFile, namespaceOnly, unknown, containment, nullKind);
        _nodes.FindByNameAsync("Source", null, false).Returns([source]);
        _edges.GetBySourceIdAsync("source").Returns([
            Edge("source", "unknown", "FUTURE_KIND"),
            Edge("source", "containment", "HAS_METHOD"),
            Edge("source", "null-kind", null),
            Edge("source", "missing", "IMPORTS"),
            Edge("source", "same-file", "CALLS")]);
        _edges.GetBySourceIdAsync("same-file").Returns([]);
        _edges.GetByTargetIdAsync("source").Returns([]);
        _edges.GetByTargetIdAsync("same-file").Returns([]);

        var result = await CreateService(root).GetCompleteContextAsync(
            "Source", depth: 1, includeTests: false, includeRelated: false, includeMetrics: false);

        Assert.Empty(Assert.Single(result.Matches).Relationships.FileDependencies);
    }

    [Fact]
    public async Task RelationshipsAggregateEveryNodeDeduplicateCaseInsensitivelyAndSortRelativePaths()
    {
        var root = NewRoot();
        var targetPath = Path.Combine(root, "src", "Target.cs");
        var target = Node("target", "Target", targetPath);
        var targetMember = Node("target-member", "TargetMember", targetPath);
        var a = Node("a", "A", Path.Combine(root, "src", "A.cs"));
        var upperB = Node("b-upper", "B1", Path.Combine(root, "src", "B.cs"));
        var lowerB = Node("b-lower", "B2", Path.Combine(root, "src", "b.cs"));
        var c = Node("c", "C", Path.Combine(root, "src", "C.cs"));
        var x = Node("x", "X", Path.Combine(root, "src", "X.cs"));
        var upperZ = Node("z-upper", "Z1", Path.Combine(root, "src", "Z.cs"));
        var lowerZ = Node("z-lower", "Z2", Path.Combine(root, "src", "z.cs"));
        ConfigureNodes(target, targetMember, a, upperB, lowerB, c, x, upperZ, lowerZ);
        _nodes.FindByNameAsync("Target", null, false).Returns([target]);

        _edges.GetBySourceIdAsync("target").Returns([
            Edge("target", "b-lower", "CALLS"), Edge("target", "a", "REFERENCES")]);
        _edges.GetBySourceIdAsync("target-member").Returns([
            Edge("target-member", "b-upper", "IMPORTS"), Edge("target-member", "c", "EXTENDS")]);
        _edges.GetByTargetIdAsync("target").Returns([
            Edge("z-lower", "target", "CALLS"), Edge("x", "target", "IMPLEMENTS")]);
        _edges.GetByTargetIdAsync("target-member").Returns([
            Edge("z-upper", "target-member", "REFERENCES")]);

        var relationships = Assert.Single((await CreateService(root).GetCompleteContextAsync(
            "Target", depth: 1, includeTests: false, includeRelated: false, includeMetrics: false)).Matches).Relationships;

        Assert.Equal(["src/A.cs", "src/B.cs", "src/C.cs"], relationships.FileDependencies);
        Assert.Equal(["src/X.cs", "src/Z.cs"], relationships.FileDependents);
        Assert.DoesNotContain(relationships.Uses, node => node.Name == "C");
    }

    [Fact]
    public async Task CompactFileRelationshipCapsReportTotalsReturnedCountsAndCategoryTruncation()
    {
        var root = NewRoot();
        var target = Node("target", "Target", Path.Combine(root, "src", "Target.cs"));
        var outbound = Enumerable.Range(0, 3)
            .Select(index => Node($"out-{index}", $"Out{index}", Path.Combine(root, "src", $"Out{index}.cs")))
            .ToArray();
        var inbound = Enumerable.Range(0, 4)
            .Select(index => Node($"in-{index}", $"In{index}", Path.Combine(root, "src", $"In{index}.cs")))
            .ToArray();
        ConfigureNodes([target, .. outbound, .. inbound]);
        _nodes.FindByNameAsync("Target", null, true).Returns([target]);
        _edges.GetBySourceIdAsync("target").Returns(outbound.Select(node => Edge("target", node.Id!, "CALLS")).ToList());
        _edges.GetByTargetIdAsync("target").Returns(inbound.Select(node => Edge(node.Id!, "target", "REFERENCES")).ToList());

        var relationships = Assert.Single((await CreateService(root).GetCompactContextAsync(
            "Target", maxRelationships: 2)).Matches).Relationships!;

        Assert.Equal(3, relationships.FileDependenciesCount);
        Assert.Equal(2, relationships.FileDependenciesReturnedCount);
        Assert.Equal(2, relationships.FileDependencies!.Count);
        Assert.True(relationships.FileDependenciesTruncated);
        Assert.Equal(4, relationships.FileDependentsCount);
        Assert.Equal(2, relationships.FileDependentsReturnedCount);
        Assert.Equal(2, relationships.FileDependents!.Count);
        Assert.True(relationships.FileDependentsTruncated);
        Assert.True(relationships.Truncated);
    }

    private ContextService CreateService(string root) => new(
        _nodes, _edges, _files, Options.Create(new CodeContextOptions { RootPath = root }));

    private void ConfigureNodes(params CodeNode[] nodes)
    {
        _nodes.GetAllAsync().Returns(nodes.ToList());
        _nodes.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => string.Equals(node.Id, call.Arg<string>(), StringComparison.Ordinal)));
        foreach (var node in nodes)
        {
            _edges.GetBySourceIdAsync(node.Id!).Returns([]);
            _edges.GetByTargetIdAsync(node.Id!).Returns([]);
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"codecontext-{Guid.NewGuid():N}");

    private static CodeNode Node(string id, string name, string path, string? @namespace = null) => new()
    {
        Id = id,
        Name = name,
        Type = "Class",
        FilePath = path,
        Namespace = @namespace,
    };

    private static CodeEdge Edge(string source, string target, string? type) => new()
    {
        Id = $"{source}-{type}-{target}",
        SourceId = source,
        TargetId = target,
        Type = type,
    };
}
