using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

/// <summary>
/// Every read path of the store, run over the whole <see cref="StoreUniverse"/> and written out as
/// lines of text, in order. Two stores (or a store and the model) agree exactly when their matrices
/// are equal line for line: same facts, same values, same order. Facts are written with every field
/// a commit can change, so two versions of one id never look alike.
/// </summary>
internal static class StoreReadMatrix
{
    private static readonly string[] EdgeTypeFilters = ["CALLS", "calls"];
    private static readonly string?[] NodeTypeFilters = [null, "Class", "method"];

    /// <param name="issuedIdentifiers">
    /// Every identifier the scenario ever handed the store, so an identifier that no node holds any
    /// more is still asked about (a stale alias resolves only for identifiers nobody holds).
    /// </param>
    public static async Task<List<string>> CaptureAsync(InMemoryDatabase database, IEnumerable<string>? issuedIdentifiers = null)
    {
        var nodes = new InMemoryNodeRepository(database);
        var edges = new InMemoryEdgeRepository(database);
        var graph = new InMemoryCodeGraphRepository(database);
        var lines = new List<string>
        {
            $"generation {database.LastCommittedGeneration}",
            $"nodeCount {database.NodeCount}",
            $"edgeCount {database.EdgeCount}",
            $"allNodes {Nodes(await nodes.GetAllAsync())}",
            $"allEdges {Edges(await edges.GetAllAsync())}",
            $"enumerateNodes {Nodes(database.EnumerateNodes())}",
            $"enumerateEdges {Edges(database.EnumerateEdges())}",
        };
        var captured = (await graph.GetGraphAsync())!;
        lines.Add($"captureNodes {Nodes(captured.Nodes)}");
        lines.Add($"captureEdges {Edges(captured.Edges)}");

        foreach (var id in NodeIdsToProbe(captured.Edges))
        {
            lines.Add($"node {id} {Node(await nodes.GetByIdAsync(id))}");
            lines.Add($"bySource {id} {Edges(await edges.GetBySourceIdAsync(id))}");
            lines.Add($"byTarget {id} {Edges(await edges.GetByTargetIdAsync(id))}");
            foreach (var type in EdgeTypeFilters)
            {
                lines.Add($"bySource {id} {type} {Edges(await edges.GetBySourceIdAsync(id, type))}");
                lines.Add($"byTarget {id} {type} {Edges(await edges.GetByTargetIdAsync(id, type))}");
            }
        }

        foreach (var id in EdgeIdsToProbe(captured.Edges.Select(edge => edge.Id!)))
            lines.Add($"edge {id} {Edge(database.GetEdge(id))}");

        // The raw identifier map first: the repository read below hides an entry whose node is gone.
        foreach (var identifier in IdentifiersToProbe(captured.Nodes, issuedIdentifiers))
        {
            lines.Add($"rawIdentifier {identifier} {(database.TryGetNodeIdByIdentifier(identifier, out var id) ? id : "-")}");
            lines.Add($"identifier {identifier} {Node(await nodes.GetByIdentifierAsync(identifier))}");
        }

        foreach (var name in StoreUniverse.NameQueries())
            foreach (var type in NodeTypeFilters)
            {
                lines.Add($"nameExact {name} {type} {Nodes(await nodes.FindByNameAsync(name, type, exact: true))}");
                lines.Add($"nameSubstring {name} {type} {Nodes(await nodes.FindByNameAsync(name, type, exact: false))}");
            }

        foreach (var path in StoreUniverse.PathQueries())
            foreach (var type in NodeTypeFilters)
                lines.Add($"path {path} {type} {Nodes(await nodes.FindByFilePathAsync(path, type))}");

        lines.Add($"filePaths {string.Join(" | ", await nodes.GetFilePathsAsync())}");
        var statistics = graph.GetStatistics();
        lines.Add($"stats {statistics.NodeCount} {statistics.EdgeCount} "
            + $"nodes[{Histogram(statistics.NodesByType)}] edges[{Histogram(statistics.EdgesByType)}]");
        return lines;
    }

    public static List<string> Capture(ReferenceGraphModel model, IEnumerable<string>? issuedIdentifiers = null)
    {
        var all = model.AllNodes();
        var allEdges = model.AllEdges();
        var lines = new List<string>
        {
            $"generation {model.LastCommittedGeneration}",
            $"nodeCount {model.NodeCount}",
            $"edgeCount {model.EdgeCount}",
            $"allNodes {Nodes(all)}",
            $"allEdges {Edges(allEdges)}",
            $"enumerateNodes {Nodes(all)}",
            $"enumerateEdges {Edges(allEdges)}",
            $"captureNodes {Nodes(all)}",
            $"captureEdges {Edges(allEdges)}",
        };

        foreach (var id in NodeIdsToProbe(allEdges))
        {
            lines.Add($"node {id} {Node(model.GetNode(id))}");
            lines.Add($"bySource {id} {Edges(model.EdgesBySource(id))}");
            lines.Add($"byTarget {id} {Edges(model.EdgesByTarget(id))}");
            foreach (var type in EdgeTypeFilters)
            {
                lines.Add($"bySource {id} {type} {Edges(model.EdgesBySource(id, type))}");
                lines.Add($"byTarget {id} {type} {Edges(model.EdgesByTarget(id, type))}");
            }
        }

        foreach (var id in EdgeIdsToProbe(allEdges.Select(edge => edge.Id!)))
            lines.Add($"edge {id} {Edge(model.GetEdge(id))}");

        foreach (var identifier in IdentifiersToProbe(all, issuedIdentifiers))
        {
            var holder = model.GetByIdentifier(identifier);
            lines.Add($"rawIdentifier {identifier} {holder?.Id ?? "-"}");
            lines.Add($"identifier {identifier} {Node(holder)}");
        }

        foreach (var name in StoreUniverse.NameQueries())
            foreach (var type in NodeTypeFilters)
            {
                lines.Add($"nameExact {name} {type} {Nodes(model.FindByName(name, type, exact: true))}");
                lines.Add($"nameSubstring {name} {type} {Nodes(model.FindByName(name, type, exact: false))}");
            }

        foreach (var path in StoreUniverse.PathQueries())
            foreach (var type in NodeTypeFilters)
                lines.Add($"path {path} {type} {Nodes(model.FindByFilePath(path, type))}");

        lines.Add($"filePaths {string.Join(" | ", model.FilePaths())}");
        var (nodesByType, edgesByType) = model.Statistics();
        lines.Add($"stats {model.NodeCount} {model.EdgeCount} nodes[{Histogram(nodesByType)}] edges[{Histogram(edgesByType)}]");
        return lines;
    }

    /// <summary>Asserts two matrices are equal, naming the first line that differs.</summary>
    public static void AssertEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string context)
    {
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{context}\nfirst difference at line {i}:\nexpected: {expected[i]}\nactual:   {actual[i]}");
        }
        Assert.True(expected.Count == actual.Count, $"{context}\nline count {actual.Count}, expected {expected.Count}");
    }

    // The edge ids worth probing: every edge that exists plus the generator's shared ids and a miss.
    // Both sides list the same set, since the matrices compare the "allEdges" line first anyway.
    private static IEnumerable<string> EdgeIdsToProbe(IEnumerable<string> present)
        => present.Concat(["shared-edge:0", "shared-edge:1", "shared-edge:2", "missing:edge"]).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    // Every id of the universe plus every id an edge names as an endpoint, so an edge whose
    // endpoint lies outside the universe is still found by both of its adjacency lookups.
    private static IEnumerable<string> NodeIdsToProbe(IEnumerable<CodeEdge> present)
        => StoreUniverse.AllNodeIds()
            .Concat(present.SelectMany(edge => new[] { edge.SourceId, edge.TargetId })
                .Where(id => id is not null)
                .Select(id => id!)
                .Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal);

    // Every identifier any present node holds, every identifier an id could derive, the stale ones
    // the generator issues (I:<id>) and every identifier the scenario ever issued, so a stale alias
    // is caught whichever version of an identifier it keeps.
    private static IEnumerable<string> IdentifiersToProbe(IEnumerable<CodeNode> present, IEnumerable<string>? issued)
        => present.Select(node => node.Identifier)
            .Concat(issued ?? [])
            .Where(identifier => !string.IsNullOrWhiteSpace(identifier))
            .Concat(StoreUniverse.AllNodeIds().SelectMany(id => new[]
            {
                $"I:{id}", InMemoryDatabase.DeriveLegacyIdentifier(new CodeNode { Id = id })!,
            }))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static string Node(CodeNode? node)
        => node is null
            ? "-"
            : $"{node.Id}|{node.Identifier}|{node.Name}|{node.Type}|{node.FilePath ?? "<null>"}|{node.StartLine}|{node.Signature}|{Meta(node.Metadata)}";

    private static string Edge(CodeEdge? edge)
        => edge is null ? "-" : $"{edge.Id}|{edge.SourceId}|{edge.TargetId}|{edge.Type}|{Meta(edge.Metadata)}";

    private static string Nodes(IEnumerable<CodeNode> nodes) => "[" + string.Join(", ", nodes.Select(Node)) + "]";

    private static string Edges(IEnumerable<CodeEdge> edges) => "[" + string.Join(", ", edges.Select(Edge)) + "]";

    private static string Meta(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null ? "" : string.Join(";", metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

    private static string Histogram(IEnumerable<KeyValuePair<string, int>> counts)
        => string.Join(",", counts.Select(pair => $"{pair.Key}={pair.Value}"));
}
