using System.Collections;
using System.Reflection;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Guards the trackPath-skip optimization in ContextService's traversal methods
/// (TraverseRelationshipsAsync / TraverseInboundFromFamilyAsync). BuildTestingInfoAsync
/// reads only Node/Distance, so it traverses with trackPath:false and must produce the
/// exact same test-evidence classification while allocating no per-node path lists.
/// BuildRelationshipsAsync keeps trackPath:true and its RelationPath stays load-bearing.
/// </summary>
public class PathTrackingSkipTests
{
    // (a) Equivalence — Class target: reverse traversal (TraverseRelationshipsAsync,
    // trackPath:false) classifies a depth-1 test as testReference and a depth-2 test as
    // indirectReference. Values are captured whole (a fixture), not weakened.
    [Fact]
    public async Task ClassTarget_TestingClassification_MatchesFixtureUnderTrackPathFalse()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class", filePath: "/src/Widget.cs");
        var intermediate = Node("agg", "csharp:Ns.Aggregator", "Aggregator", "Class", filePath: "/src/Aggregator.cs");
        var directTest = TestMethod("t1", "csharp:Tests.ExercisesDirectly()", "ExercisesDirectly", "/tests/AlphaTests.cs");
        var indirectTest = TestMethod("t2", "csharp:Tests.ExercisesIndirectly()", "ExercisesIndirectly", "/tests/BetaTests.cs");
        var edges = new[]
        {
            Edge("t1", "cls", "REFERENCES", 5),   // direct: test -> target (distance 1)
            Edge("agg", "cls", "REFERENCES", 3),  // intermediate -> target
            Edge("t2", "agg", "CALLS", 7),        // indirect: test -> intermediate -> target (distance 2)
        };
        var service = CreateService([cls, intermediate, directTest, indirectTest], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.True(testing.IsTested);
        Assert.Equal(2, testing.TestReferenceCount);

        var directFile = Assert.Single(testing.TestFiles, f => f.FilePath == "/tests/AlphaTests.cs");
        Assert.Equal(["testReference"], directFile.Evidence);
        var indirectFile = Assert.Single(testing.TestFiles, f => f.FilePath == "/tests/BetaTests.cs");
        Assert.Equal(["indirectReference"], indirectFile.Evidence);
        Assert.Equal(2, testing.TestFiles.Count);
    }

    // (a') Equivalence — Method target: reverse family traversal
    // (TraverseInboundFromFamilyAsync, trackPath:false) drives the same classification.
    [Fact]
    public async Task MethodTarget_TestingClassification_MatchesFixtureUnderTrackPathFalse()
    {
        var method = Node("m", "csharp:Ns.Svc.Process()", "Process", "Method", filePath: "/src/Svc.cs");
        var mid = Node("mid", "csharp:Ns.Svc.Helper()", "Helper", "Method", filePath: "/src/Svc.cs");
        var directTest = TestMethod("t1", "csharp:Tests.CoversDirectly()", "CoversDirectly", "/tests/AlphaTests.cs");
        var indirectTest = TestMethod("t2", "csharp:Tests.CoversIndirectly()", "CoversIndirectly", "/tests/BetaTests.cs");
        var edges = new[]
        {
            Edge("t1", "m", "CALLS", 5),     // direct call from test (distance 1)
            Edge("mid", "m", "CALLS", 3),    // intermediate calls target
            Edge("t2", "mid", "CALLS", 7),   // indirect: test -> mid -> target (distance 2)
        };
        var service = CreateService([method, mid, directTest, indirectTest], edges);

        var result = await service.GetCompleteContextAsync(
            method.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.True(testing.DirectlyTested);
        Assert.Equal(2, testing.TestReferenceCount);

        var directFile = Assert.Single(testing.TestFiles, f => f.FilePath == "/tests/AlphaTests.cs");
        Assert.Equal(["directCall", "testReference"], directFile.Evidence);
        var indirectFile = Assert.Single(testing.TestFiles, f => f.FilePath == "/tests/BetaTests.cs");
        Assert.Equal(["indirectReference"], indirectFile.Evidence);
        Assert.Equal(2, testing.TestFiles.Count);
    }

    // (b) Relationships at depth >= 2 still carry the full RelationPath sequence
    // (BuildRelationshipsAsync keeps trackPath:true; the .ToList() projection is intact).
    [Fact]
    public async Task TransitiveRelationships_StillCarryFullRelationPath()
    {
        var root = Node("root", "csharp:Ns.Root", "Root", "Class");
        var one = Node("one", "csharp:Ns.One", "One", "Class");
        var two = Node("two", "csharp:Ns.Two", "Two", "Class");
        var edges = new[]
        {
            Edge("root", "one", "CALLS", 1),
            Edge("one", "two", "REFERENCES", 2),
        };
        var service = CreateService([root, one, two], edges);

        var result = await service.GetCompactContextAsync("Root", depth: 2, maxRelationships: 5);

        var transitive = Assert.Single(Assert.Single(result.Matches).Relationships!.TransitiveUses!);
        Assert.Equal(2, transitive.Distance);
        Assert.Equal(["CALLS", "REFERENCES"], transitive.RelationPath);
    }

    // (c) The no-track traversal hands every result the same shared immutable empty
    // instance (reference equality) instead of freshly allocated path lists.
    [Fact]
    public async Task TrackPathFalse_SharesSingleEmptyPathInstance()
    {
        var root = Node("root", "csharp:Ns.Root", "Root", "Class");
        var a = Node("a", "csharp:Ns.A", "A", "Class");
        var b = Node("b", "csharp:Ns.B", "B", "Class");
        var edges = new[]
        {
            Edge("root", "a", "CALLS", 1),
            Edge("a", "b", "CALLS", 2),
        };
        var service = CreateService([root, a, b], edges);

        var noTrack = await InvokeTraverseAsync(service, "root", depth: 3, outgoing: true, trackPath: false);
        Assert.Equal(2, noTrack.Count);
        var first = GetRelationPath(noTrack[0]);
        Assert.Empty((IEnumerable)first);
        Assert.All(noTrack, item => Assert.Same(first, GetRelationPath(item)));

        // Sanity: with tracking on, each node gets its own distinct, populated path.
        var tracked = await InvokeTraverseAsync(service, "root", depth: 3, outgoing: true, trackPath: true);
        Assert.All(tracked, item => Assert.NotSame(first, GetRelationPath(item)));
    }

    private static async Task<IReadOnlyList<object>> InvokeTraverseAsync(
        ContextService service, string rootNodeId, int depth, bool outgoing, bool trackPath)
    {
        var method = typeof(ContextService).GetMethod(
            "TraverseRelationshipsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(service, [rootNodeId, depth, outgoing, trackPath])!;
        await task;
        var resultList = (IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        return resultList.Cast<object>().ToList();
    }

    private static object GetRelationPath(object traversedRelationship) =>
        traversedRelationship.GetType().GetProperty("RelationPath")!.GetValue(traversedRelationship)!;

    private static ContextService CreateService(CodeNode[] nodes, CodeEdge[] edges)
    {
        var nodeRepository = Substitute.For<ICodeNodeRepository>();
        var edgeRepository = Substitute.For<ICodeEdgeRepository>();
        var files = Substitute.For<IFileMetadataRepository>();
        nodeRepository.GetAllAsync().Returns(nodes.ToList());
        nodeRepository.StubFindByFilePathFromGetAll();
        nodeRepository.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Id == call.Arg<string>()));
        nodeRepository.GetByIdentifierAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Identifier == call.Arg<string>()));
        nodeRepository.FindByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(call =>
            nodes.Where(node => string.Equals(node.Name, call.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)).ToList());
        edgeRepository.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.SourceId == call.ArgAt<string>(0)).ToList());
        edgeRepository.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.TargetId == call.ArgAt<string>(0)).ToList());
        return new ContextService(nodeRepository, edgeRepository, files);
    }

    private static CodeNode Node(
        string id, string identifier, string name, string type,
        string? filePath = null, string? signature = null) => new()
    {
        Id = id,
        Identifier = identifier,
        Name = name,
        Type = type,
        FilePath = filePath,
        Signature = signature ?? name + "()"
    };

    private static CodeNode TestMethod(string id, string identifier, string name, string filePath) => new()
    {
        Id = id,
        Identifier = identifier,
        Name = name,
        Type = "Method",
        FilePath = filePath,
        Signature = $"[Fact] public void {name}()"
    };

    private static CodeEdge Edge(string source, string target, string type, int? line = null) => new()
    {
        Id = $"{source}-{type}-{target}-{line}",
        SourceId = source,
        TargetId = target,
        Type = type,
        Metadata = line is null ? null : new Dictionary<string, string> { ["line"] = line.Value.ToString() }
    };
}
