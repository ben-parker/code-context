using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Every read member of one shard, asked over a vocabulary drawn from the shard's own facts and
/// written out as lines: every field of every fact (metadata as pairs in key order; the store reads
/// metadata only by key), node, identifier and edge lookups, both adjacency directions, exact and
/// substring names, paths, file paths, histogram and cross-shard edge ids. Two shards (or a shard and
/// a snapshot reader) agree exactly when their lines are equal.
/// </summary>
internal sealed class ShardReadMatrix
{
    private readonly Func<int> _nodeCount;
    private readonly Func<int> _edgeCount;
    private readonly Func<string, CodeNode?> _node;
    private readonly Func<string, string?> _identifier;
    private readonly Func<string, CodeEdge?> _edge;
    private readonly Func<string, IReadOnlyList<CodeEdge>> _bySource;
    private readonly Func<string, IReadOnlyList<CodeEdge>> _byTarget;
    private readonly Func<string, bool, IReadOnlyList<CodeNode>> _byName;
    private readonly Func<string, IReadOnlyList<CodeNode>> _byPath;
    private readonly Func<IReadOnlyList<string>> _filePaths;
    private readonly Action<Dictionary<string, int>, Dictionary<string, int>> _statistics;
    private readonly Func<IEnumerable<string>> _crossShard;
    private readonly Func<IReadOnlyList<CodeNode>> _allNodes;
    private readonly Func<IReadOnlyList<CodeEdge>> _allEdges;

    private ShardReadMatrix(
        Func<int> nodeCount, Func<int> edgeCount, Func<string, CodeNode?> node, Func<string, string?> identifier,
        Func<string, CodeEdge?> edge, Func<string, IReadOnlyList<CodeEdge>> bySource, Func<string, IReadOnlyList<CodeEdge>> byTarget,
        Func<string, bool, IReadOnlyList<CodeNode>> byName, Func<string, IReadOnlyList<CodeNode>> byPath,
        Func<IReadOnlyList<string>> filePaths, Action<Dictionary<string, int>, Dictionary<string, int>> statistics,
        Func<IEnumerable<string>> crossShard, Func<IReadOnlyList<CodeNode>> allNodes, Func<IReadOnlyList<CodeEdge>> allEdges)
    {
        _nodeCount = nodeCount; _edgeCount = edgeCount; _node = node; _identifier = identifier; _edge = edge;
        _bySource = bySource; _byTarget = byTarget; _byName = byName; _byPath = byPath; _filePaths = filePaths;
        _statistics = statistics; _crossShard = crossShard; _allNodes = allNodes; _allEdges = allEdges;
    }

    public static ShardReadMatrix Of(IGraphShard shard) => new(
        () => shard.NodeCount, () => shard.EdgeCount,
        id => shard.TryGetNode(id, out var node) ? node : null,
        identifier => shard.TryGetNodeIdByIdentifier(identifier, out var id) ? id : null,
        id => shard.TryGetEdge(id, out var edge) ? edge : null,
        shard.GetEdgesBySource, shard.GetEdgesByTarget, shard.FindNodesByName, shard.FindNodesByPath,
        shard.GetFilePaths, shard.AddStatistics, shard.GetCrossShardEdgeIds, shard.GetAllNodes, shard.GetAllEdges);

    /// <summary>The snapshot reader's in-place accessors, each asked directly (no materialized shard).</summary>
    public static ShardReadMatrix Of(SnapshotShardReader reader) => new(
        () => reader.NodeCount, () => reader.EdgeCount,
        id => reader.TryGetNode(id, out var node) ? node : null,
        identifier => reader.TryGetNodeIdByIdentifier(identifier, out var id) ? id : null,
        id => reader.TryGetEdge(id, out var edge) ? edge : null,
        reader.GetEdgesBySource, reader.GetEdgesByTarget, reader.FindNodesByName, reader.FindNodesByPath,
        reader.GetFilePaths, reader.AddStatistics, reader.GetCrossShardEdgeIds, reader.GetAllNodes, reader.GetAllEdges);

    /// <summary>The vocabulary a shard's matrix asks about: its own facts, variants of them, and misses.</summary>
    public sealed record Vocabulary(
        IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds, IReadOnlyList<string> Identifiers,
        IReadOnlyList<string> Names, IReadOnlyList<string> Paths)
    {
        public static Vocabulary From(IGraphShard shard)
        {
            var nodes = shard.GetAllNodes();
            var edges = shard.GetAllEdges();
            var nodeIds = nodes.Select(node => node.Id!)
                .Concat(edges.SelectMany(edge => new[] { edge.SourceId, edge.TargetId }).Where(id => id is not null).Select(id => id!))
                .Concat(["missing:node", "", "\uD800"])
                .Concat(nodes.Take(3).Select(node => node.Id!.ToUpperInvariant()))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var edgeIds = edges.Select(edge => edge.Id!)
                .Concat(edges.Take(5).Select(edge => edge.Id! + "x"))
                .Concat(edges.Take(5).Select(edge => edge.Id![..^1]))
                .Concat(["missing:edge", "", "\uDC00"])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var identifiers = nodes.Select(node => node.Identifier)
                .Concat(nodes.Select(node => InMemoryDatabase.DeriveLegacyIdentifier(node)!))
                .Concat(nodes.Take(5).Select(node => node.Identifier + "?"))
                .Concat(["missing:identifier", "", "   ", "\uD800x"])
                .Where(identifier => identifier is not null)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var names = nodes.Select(node => node.Name).Where(name => name is not null).Select(name => name!)
                .SelectMany(name => new[]
                {
                    name, name.ToUpperInvariant(), name.ToLowerInvariant(),
                    name.Length > 2 ? name[1..^1] : name, name.Length > 0 ? name[..1] : name,
                })
                .Concat(["", "zzz-none", "\uD800", "a\uDC00"])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var paths = nodes.Select(node => node.FilePath).Where(path => path is not null).Select(path => path!)
                .SelectMany(path => new[]
                {
                    path, path.ToUpperInvariant(), path.Replace('/', '\\'), path + "/",
                    path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
                    string.Join('/', path.Split('/').TakeLast(2)),
                })
                .Concat(["Nope.cs", "/rooted/Nope.cs", "  ", "", "C:\\elsewhere\\x.cs"])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return new Vocabulary(nodeIds, edgeIds, identifiers, names, paths);
        }
    }

    public List<string> Capture(Vocabulary vocabulary)
    {
        var lines = new List<string>
        {
            $"nodeCount {_nodeCount()}",
            $"edgeCount {_edgeCount()}",
            $"allNodes {Nodes(_allNodes())}",
            $"allEdges {Edges(_allEdges())}",
        };
        foreach (var id in vocabulary.NodeIds)
        {
            lines.Add($"node {id} {Node(_node(id))}");
            lines.Add($"bySource {id} {Edges(_bySource(id))}");
            lines.Add($"byTarget {id} {Edges(_byTarget(id))}");
        }
        foreach (var id in vocabulary.EdgeIds) lines.Add($"edge {id} {Edge(_edge(id))}");
        foreach (var identifier in vocabulary.Identifiers) lines.Add($"identifier {identifier} {_identifier(identifier) ?? "-"}");
        foreach (var name in vocabulary.Names)
        {
            lines.Add($"nameExact {name} {Nodes(_byName(name, true))}");
            lines.Add($"nameSubstring {name} {Nodes(_byName(name, false))}");
        }
        foreach (var path in vocabulary.Paths) lines.Add($"path {path} {Nodes(_byPath(path))}");
        // A null argument, answered or thrown exactly as the heap shard does (never a NullReferenceException).
        lines.Add($"null node {Probe(() => Node(_node(null!)))}");
        lines.Add($"null identifier {Probe(() => _identifier(null!) ?? "-")}");
        lines.Add($"null edge {Probe(() => Edge(_edge(null!)))}");
        lines.Add($"null bySource {Probe(() => Edges(_bySource(null!)))}");
        lines.Add($"null byTarget {Probe(() => Edges(_byTarget(null!)))}");
        lines.Add($"null nameExact {Probe(() => Nodes(_byName(null!, true)))}");
        lines.Add($"null nameSubstring {Probe(() => Nodes(_byName(null!, false)))}");
        lines.Add($"null path {Probe(() => Nodes(_byPath(null!)))}");
        lines.Add($"filePaths {string.Join(" | ", _filePaths())}");
        var nodeTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var edgeTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        _statistics(nodeTypes, edgeTypes);
        lines.Add($"stats nodes[{Histogram(nodeTypes)}] edges[{Histogram(edgeTypes)}]");
        lines.Add($"crossShard {string.Join(", ", _crossShard().Order(StringComparer.Ordinal))}");
        return lines;
    }

    public static void AssertEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string context)
        => Repositories.StoreModel.StoreReadMatrix.AssertEqual(expected, actual, context);

    internal static string Node(CodeNode? node)
        => node is null
            ? "-"
            : string.Join("|", node.Id, node.Identifier ?? "<null>", Str(node.Name), Str(node.Type), Str(node.Language), Str(node.FilePath),
                Int(node.StartLine), Int(node.EndLine), Int(node.StartCol), Int(node.EndCol), Str(node.Namespace), Str(node.Visibility),
                Str(node.Signature), Str(node.ReturnType), Str(node.Parameters), Str(node.Modifiers), Str(node.Metrics), Meta(node.Metadata));

    internal static string Edge(CodeEdge? edge)
        => edge is null ? "-" : string.Join("|", edge.Id, Str(edge.SourceId), Str(edge.TargetId), Str(edge.Type), Meta(edge.Metadata));

    private static string Probe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not NullReferenceException)
        {
            return $"throws {exception.GetType().Name}";
        }
    }

    private static string Nodes(IEnumerable<CodeNode> nodes) => "[" + string.Join(", ", nodes.Select(Node)) + "]";

    private static string Edges(IEnumerable<CodeEdge> edges) => "[" + string.Join(", ", edges.Select(Edge)) + "]";

    private static string Str(string? value) => value is null ? "<null>" : $"'{value}'";

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Meta(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null
            ? "<null>"
            : "{" + string.Join(";", metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value ?? "<null>"}")) + "}";

    private static string Histogram(Dictionary<string, int> counts)
        => string.Join(",", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
}
