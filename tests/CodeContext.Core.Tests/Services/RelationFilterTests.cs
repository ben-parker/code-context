using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

public class RelationFilterTests
{
    private readonly ICodeNodeRepository _nodes = Substitute.For<ICodeNodeRepository>();
    private readonly ICodeEdgeRepository _edges = Substitute.For<ICodeEdgeRepository>();
    private readonly IFileMetadataRepository _files = Substitute.For<IFileMetadataRepository>();

    private ContextService CreateService() => new(_nodes, _edges, _files);

    private static CodeNode Node(string id, string name, string type, string? file = null)
        => new() { Id = id, Name = name, Type = type, FilePath = file, Identifier = id };

    private static CodeEdge Edge(string source, string target, string? type)
        => new() { Id = $"{source}->{target}:{type}", SourceId = source, TargetId = target, Type = type };

    /// <summary>Registers a target reachable via the identity fast-path with the given inbound edges.</summary>
    private CodeNode ArrangeInbound(params (CodeNode Source, string? Type)[] callers)
    {
        var target = Node("widget", "Widget", "Class");
        _nodes.GetByIdentifierAsync("Widget").Returns(target);
        _nodes.GetByIdAsync("widget").Returns(target);
        var inbound = new List<CodeEdge>();
        foreach (var (source, type) in callers)
        {
            _nodes.GetByIdAsync(source.Id!).Returns(source);
            inbound.Add(Edge(source.Id!, "widget", type));
        }
        _edges.GetByTargetIdAsync("widget").Returns(inbound);
        _edges.GetBySourceIdAsync("widget").Returns(new List<CodeEdge>());
        return target;
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationCalls_FiltersUsedByToCallEdges()
    {
        ArrangeInbound(
            (Node("a", "CallerA", "Method"), "CALLS"),
            (Node("b", "CallerB", "Method"), "REFERENCES"));

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "CALLS");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(new[] { "CallerA" }, rel.UsedBy!.Select(n => n.Name));
        Assert.Equal(1, rel.UsedByCount);
        Assert.Equal(1, rel.UsedByReturnedCount);
        Assert.False(rel.UsedByTruncated);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationFilter_IsCaseInsensitive()
    {
        ArrangeInbound(
            (Node("a", "CallerA", "Method"), "CALLS"),
            (Node("b", "CallerB", "Method"), "REFERENCES"));

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "calls");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(new[] { "CallerA" }, rel.UsedBy!.Select(n => n.Name));
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationCsv_MatchesAnyListedKind()
    {
        ArrangeInbound(
            (Node("a", "CallerA", "Method"), "CALLS"),
            (Node("b", "CallerB", "Method"), "REFERENCES"));

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "CALLS,REFERENCES");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(new[] { "CallerA", "CallerB" }, rel.UsedBy!.Select(n => n.Name).OrderBy(n => n));
        Assert.Equal(2, rel.UsedByCount);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationUses_FiltersOutgoingEdges()
    {
        var target = Node("widget", "Widget", "Class");
        var x = Node("x", "UsedX", "Method");
        var y = Node("y", "UsedY", "Method");
        _nodes.GetByIdentifierAsync("Widget").Returns(target);
        _nodes.GetByIdAsync("widget").Returns(target);
        _nodes.GetByIdAsync("x").Returns(x);
        _nodes.GetByIdAsync("y").Returns(y);
        _edges.GetBySourceIdAsync("widget").Returns(new List<CodeEdge>
        {
            Edge("widget", "x", "CALLS"),
            Edge("widget", "y", "IMPORTS"),
        });
        _edges.GetByTargetIdAsync("widget").Returns(new List<CodeEdge>());

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "CALLS");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(new[] { "UsedX" }, rel.Uses!.Select(n => n.Name));
        Assert.Equal(1, rel.UsesCount);
        Assert.Equal(1, rel.UsesReturnedCount);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationUsesToken_MatchesNullTypedEdges()
    {
        ArrangeInbound((Node("a", "CallerA", "Method"), null));

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "USES");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(new[] { "CallerA" }, rel.UsedBy!.Select(n => n.Name));
        Assert.Equal(1, rel.UsedByCount);
    }

    [Fact]
    public async Task GetCompactContextAsync_UnknownRelation_ThrowsListingValidKinds()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => CreateService().GetCompactContextAsync("Widget", relation: "HAS_METHOD"));

        Assert.Contains("Unknown relation", ex.Message);
        Assert.Contains("HAS_METHOD", ex.Message);
        Assert.Contains("CALLS", ex.Message);
        Assert.Equal("relation", ex.ParamName);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationFiltersToZero_EmitsZeroCountsNotOmission()
    {
        ArrangeInbound((Node("a", "CallerA", "Method"), "REFERENCES"));

        var result = await CreateService().GetCompactContextAsync("Widget", relation: "CALLS");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.NotNull(rel.UsedBy);
        Assert.Empty(rel.UsedBy!);
        Assert.Equal(0, rel.UsedByCount);
        Assert.Equal(0, rel.UsedByReturnedCount);
        Assert.False(rel.UsedByTruncated);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationFilter_CountsAndTruncationReflectFilteredTotals()
    {
        var callers = Enumerable.Range(1, 5).Select(i => Node($"c{i}", $"C{i}", "Method")).ToList();
        var refs = Enumerable.Range(1, 5).Select(i => Node($"r{i}", $"R{i}", "Method")).ToList();
        ArrangeInbound(callers.Select(c => (c, (string?)"CALLS"))
            .Concat(refs.Select(r => (r, (string?)"REFERENCES"))).ToArray());

        var result = await CreateService().GetCompactContextAsync(
            "Widget", maxRelationships: 3, relation: "CALLS");

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(5, rel.UsedByCount);
        Assert.Equal(3, rel.UsedByReturnedCount);
        Assert.True(rel.UsedByTruncated);
        Assert.Equal(3, rel.UsedBy!.Count);
        Assert.All(rel.UsedBy!, n => Assert.StartsWith("C", n.Name!));
    }

    [Fact]
    public async Task GetCompactContextAsync_NoRelationFilter_BehaviorUnchanged()
    {
        var callers = Enumerable.Range(1, 5).Select(i => Node($"c{i}", $"C{i}", "Method")).ToList();
        var refs = Enumerable.Range(1, 5).Select(i => Node($"r{i}", $"R{i}", "Method")).ToList();
        ArrangeInbound(callers.Select(c => (c, (string?)"CALLS"))
            .Concat(refs.Select(r => (r, (string?)"REFERENCES"))).ToArray());

        var result = await CreateService().GetCompactContextAsync("Widget", maxRelationships: 3);

        var rel = Assert.Single(result.Matches).Relationships!;
        Assert.Equal(10, rel.UsedByCount);
        Assert.Equal(3, rel.UsedByReturnedCount);
        Assert.True(rel.UsedByTruncated);
    }

    [Fact]
    public async Task GetMultipleCompactContextAsync_RelationshipTypes_AppliedToEachIdentifier()
    {
        ArrangeInbound(
            (Node("a", "CallerA", "Method"), "CALLS"),
            (Node("b", "CallerB", "Method"), "REFERENCES"));
        var request = new MultiContextRequest
        {
            Identifiers = new List<string> { "Widget" },
            Depth = 1,
            RelationshipTypes = new List<string> { "CALLS" },
        };

        var results = await CreateService().GetMultipleCompactContextAsync(request);

        var rel = Assert.Single(Assert.Single(results).Matches).Relationships!;
        Assert.Equal(new[] { "CallerA" }, rel.UsedBy!.Select(n => n.Name));
        Assert.Equal(1, rel.UsedByCount);
    }

    [Fact]
    public async Task GetCompactContextAsync_RelationFilter_AppliesToUnifiedMethodFamilyCallers()
    {
        // Method target with a family: interface member `i` implemented by `p1` (the target).
        // X calls the interface member (an interface-binding caller of the unified family);
        // Y references the target itself. Under relation=CALLS, only X survives.
        var target = new CodeNode { Id = "p1", Identifier = "csharp:Example.Service.Run()", Name = "Run", Type = "Method", Signature = "Run()" };
        var iface = new CodeNode { Id = "i", Identifier = "csharp:Example.IService.Run()", Name = "Run", Type = "Method", Signature = "Run()" };
        var callerX = new CodeNode { Id = "x", Identifier = "csharp:Example.CallInterface()", Name = "CallInterface", Type = "Method", Signature = "CallInterface()" };
        var callerY = new CodeNode { Id = "y", Identifier = "csharp:Example.ReferenceRun()", Name = "ReferenceRun", Type = "Method", Signature = "ReferenceRun()" };
        var allNodes = new[] { target, iface, callerX, callerY };
        var allEdges = new[]
        {
            Edge("p1", "i", "IMPLEMENTS_MEMBER"),
            Edge("x", "i", "CALLS"),
            Edge("y", "p1", "REFERENCES"),
        };
        _nodes.GetByIdentifierAsync(Arg.Any<string>()).Returns(call =>
            allNodes.SingleOrDefault(n => n.Identifier == call.Arg<string>()));
        _nodes.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            allNodes.SingleOrDefault(n => n.Id == call.Arg<string>()));
        _edges.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            allEdges.Where(e => e.SourceId == call.ArgAt<string>(0)).ToList());
        _edges.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            allEdges.Where(e => e.TargetId == call.ArgAt<string>(0)).ToList());

        var result = await CreateService().GetCompactContextAsync(
            "csharp:Example.Service.Run()", relation: "CALLS");

        var rel = Assert.Single(result.Matches).Relationships!;
        var used = Assert.Single(rel.UsedBy!);
        Assert.Equal("CallInterface", used.Name);
        Assert.Equal(1, rel.UsedByCount);
        Assert.Equal(1, rel.UsedByReturnedCount);
        Assert.Equal(new[] { "interface" }, used.Bindings);
        Assert.DoesNotContain(rel.UsedBy!, n => n.Name == "ReferenceRun");
    }

    [Fact]
    public async Task GetMultipleContextAsync_FullViewWithRelationshipTypes_Throws()
    {
        var request = new MultiContextRequest
        {
            Identifiers = new List<string> { "Widget" },
            RelationshipTypes = new List<string> { "CALLS" },
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateService().GetMultipleContextAsync(request));
    }
}
