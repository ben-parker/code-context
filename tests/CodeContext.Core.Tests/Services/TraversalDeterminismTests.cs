using System.Text.Json;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Guards that compact-context traversal is independent of the order the repository
/// enumerates edges. The InMemory store returns edges in ConcurrentDictionary
/// enumeration order, which shifts with bucket geometry (capacity presizing, insertion
/// order). Before the fix, <see cref="ContextService"/>'s inbound BFS recorded a
/// first-parent-wins <c>RelationPath</c> for each transitive node, so the same logical
/// graph produced different serialized bytes depending only on edge order. These tests
/// build one logical graph, feed it to two services whose only difference is edge
/// enumeration order (forward vs reversed), and assert the compact output is identical.
/// </summary>
public class TraversalDeterminismTests
{
    [Fact]
    public async Task TransitiveUsedByPath_IsIndependentOfEdgeEnumerationOrder()
    {
        // T is the target. A and B both CALL T (distance 1). X reaches T at distance 2
        // through BOTH A (CALLS) and B (MOCK_CALLS): whichever parent the BFS visits first
        // records X's RelationPath. Edge Ids give A's edge to T a lower ordinal than B's,
        // so a deterministic ordering must always resolve X via A -> ["CALLS","CALLS"],
        // regardless of the order the store hands edges back.
        var nodes = new[]
        {
            Node("t", "csharp:Ex.T.Run()", "Run", "Method"),
            Node("a", "csharp:Ex.A.CallA()", "CallA", "Method"),
            Node("b", "csharp:Ex.B.CallB()", "CallB", "Method"),
            Node("x", "csharp:Ex.X.Outer()", "Outer", "Method"),
        };
        var edges = new[]
        {
            Edge("e1-a-t", "a", "t", "CALLS", 1),
            Edge("e2-b-t", "b", "t", "CALLS", 2),
            Edge("e3-x-a", "x", "a", "CALLS", 3),
            Edge("e4-x-b", "x", "b", "MOCK_CALLS", 4),
        };

        var forward = await CompactRelationshipsJsonAsync(nodes, edges, reverse: false);
        var reversed = await CompactRelationshipsJsonAsync(nodes, edges, reverse: true);

        Assert.Equal(forward, reversed);
    }

    private static async Task<string> CompactRelationshipsJsonAsync(
        CodeNode[] nodes, CodeEdge[] edges, bool reverse)
    {
        var service = CreateService(nodes, edges, reverse);
        var result = await service.GetCompactContextAsync(
            "csharp:Ex.T.Run()", depth: 2, includeTests: true, maxRelationships: 10);
        var relationships = Assert.Single(result.Matches).Relationships;
        return JsonSerializer.Serialize(relationships);
    }

    private static ContextService CreateService(CodeNode[] nodes, CodeEdge[] edges, bool reverse)
    {
        var nodeRepository = Substitute.For<ICodeNodeRepository>();
        var edgeRepository = Substitute.For<ICodeEdgeRepository>();
        var files = Substitute.For<IFileMetadataRepository>();

        nodeRepository.GetAllAsync().Returns(nodes.ToList());
        nodeRepository.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Id == call.Arg<string>()));
        nodeRepository.GetByIdentifierAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Identifier == call.Arg<string>()));
        nodeRepository.FindByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(call =>
            nodes.Where(node => string.Equals(node.Name, call.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)).ToList());

        // Simulate different store enumeration orders: one service sees edges in insertion
        // order, the other reversed. This is the only difference between the two services.
        List<CodeEdge> Ordered(IEnumerable<CodeEdge> matches)
        {
            var list = matches.ToList();
            if (reverse) list.Reverse();
            return list;
        }

        edgeRepository.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            Ordered(edges.Where(edge => edge.SourceId == call.ArgAt<string>(0))));
        edgeRepository.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            Ordered(edges.Where(edge => edge.TargetId == call.ArgAt<string>(0))));

        return new ContextService(nodeRepository, edgeRepository, files);
    }

    private static CodeNode Node(string id, string identifier, string name, string type) => new()
    {
        Id = id, Identifier = identifier, Name = name, Type = type, Signature = name + "()"
    };

    private static CodeEdge Edge(string id, string source, string target, string type, int line) => new()
    {
        Id = id, SourceId = source, TargetId = target, Type = type,
        Metadata = new Dictionary<string, string> { ["line"] = line.ToString() }
    };
}
