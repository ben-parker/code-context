using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// A small graph written by hand for the graph-query tests: nodes by short name, owned by one of two shards
/// (<c>a</c> or <c>b</c>) so every traversal crosses shards, under the root <c>/repo</c>.
/// </summary>
internal sealed class GraphTestGraph
{
    public const string Root = "/repo";

    private readonly List<CodeNode> _nodes = new();
    private readonly List<CodeEdge> _edges = new();
    private long _generation;

    public InMemoryDatabase Database { get; } = new();

    public static string Id(string name) => $"csharp:{Workspace(name)}:{name}";

    /// <summary>The canonical identifier of a node added by name.</summary>
    public static string Identifier(string name) => $"csharp:Demo.{name}";

    private static string Workspace(string name) => (name.Length % 2 == 0) ? "a" : "b";

    private static Dictionary<string, string> Owner(string name)
        => new() { ["parserId"] = "csharp", ["workspaceId"] = Workspace(name) };

    public GraphTestGraph Node(string name, string type = "Method", string? file = null, int line = 0, bool external = false)
    {
        var metadata = Owner(name);
        if (external)
        {
            metadata["external"] = "true";
            metadata["assembly"] = "Lib";
        }
        _nodes.Add(new CodeNode
        {
            Id = Id(name),
            Identifier = Identifier(name),
            Name = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name,
            Type = type,
            FilePath = external ? null : $"{Root}/{file ?? "src/" + name.Split('.')[0] + ".cs"}",
            StartLine = line,
            Metadata = metadata,
        });
        return this;
    }

    public GraphTestGraph Edge(string source, string target, string type = "CALLS", int? line = null, bool staticCall = false)
    {
        var metadata = Owner(source);
        if (line is not null) metadata["line"] = line.Value.ToString();
        if (staticCall) metadata[EdgeMetadataKeys.Dispatch] = EdgeDispatch.Static;
        _edges.Add(new CodeEdge
        {
            Id = $"{Id(source)}|{type}|{Id(target)}|{line}|{_edges.Count}",
            SourceId = Id(source),
            TargetId = Id(target),
            Type = type,
            Metadata = metadata,
        });
        return this;
    }

    public GraphTestGraph Calls(string source, params string[] targets)
    {
        foreach (var target in targets) Edge(source, target);
        return this;
    }

    public ContextService Build()
    {
        Assert.True(Database.TryCommitGeneration(++_generation, _nodes, _edges, scope: null));
        return Service(Database);
    }

    /// <summary>Commits the graph as it is now as a new generation.</summary>
    public void Commit() => Assert.True(Database.TryCommitGeneration(++_generation, _nodes.ToList(), _edges.ToList(), scope: null));

    public static ContextService Service(InMemoryDatabase database)
        => new(
            new InMemoryNodeRepository(database),
            new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(),
            Options.Create(new CodeContextOptions { RootPath = Root }));
}
