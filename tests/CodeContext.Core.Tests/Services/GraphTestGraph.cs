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
    private readonly string _root;
    private long _generation;

    /// <param name="root">The root files live under; <see cref="Root"/> unless a test needs real files.</param>
    public GraphTestGraph(string? root = null) => _root = root ?? Root;

    /// <summary>The index's freshness given to the service (diff impact); null leaves the time check out.</summary>
    public IIndexFreshness? Freshness { get; set; }

    /// <summary>The file table given to the service; a new, empty one when null.</summary>
    public CodeContext.Core.Repositories.IFileMetadataRepository? Files { get; set; }

    /// <summary>The workers and the selection policy given to the service (diff impact asks them which files feed the index).</summary>
    public CodeContext.Core.Workers.ILanguageWorkerService? Workers { get; set; }
    public IRepositoryFileSelector? Selector { get; set; }

    public InMemoryDatabase Database { get; } = new();

    /// <summary>
    /// Records, for each of the two shards, what its worker reported reading besides its files (null: it reported
    /// nothing), as a commit that carries the report and changes no fact.
    /// </summary>
    public void ReportInputs(WorkerInputs? a, WorkerInputs? b)
    {
        foreach (var (workspace, inputs) in new[] { ("a", a), ("b", b) })
        {
            Assert.True(Database.TryCommitGeneration(++_generation, [], [],
                new CodeContext.Core.Repositories.CommitScope("csharp", workspace, [])
                {
                    Annotation = new CodeContext.Core.Repositories.ShardAnnotation(null, null) { Inputs = inputs },
                }));
        }
    }

    public static string Id(string name) => $"csharp:{Workspace(name)}:{name}";

    /// <summary>The canonical identifier of a node added by name.</summary>
    public static string Identifier(string name) => $"csharp:Demo.{name}";

    private static string Workspace(string name) => (name.Length % 2 == 0) ? "a" : "b";

    private static Dictionary<string, string> Owner(string name)
        => new() { ["parserId"] = "csharp", ["workspaceId"] = Workspace(name) };

    /// <param name="line">The 0-based start line; <paramref name="endLine"/> the 0-based end line (the start line when null).</param>
    /// <param name="parts">
    /// Other declarations of the node (a partial type's other parts): each part's file and 0-based lines, from column 0
    /// to column 200 of its last line.
    /// </param>
    /// <param name="col">The 0-based start column; <paramref name="endCol"/> the end column (one past the start when null).</param>
    public GraphTestGraph Node(string name, string type = "Method", string? file = null, int line = 0, bool external = false,
        int? endLine = null, string? id = null, (string File, int Line, int EndLine)[]? parts = null, int col = 0, int? endCol = null)
    {
        var metadata = Owner(name);
        if (external)
        {
            metadata["external"] = "true";
            metadata["assembly"] = "Lib";
        }
        _nodes.Add(new CodeNode
        {
            Id = id ?? Id(name),
            Identifier = Identifier(name),
            Name = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name,
            Type = type,
            FilePath = external ? null : Path.Combine(_root, (file ?? "src/" + name.Split('.')[0] + ".cs").Replace('/', Path.DirectorySeparatorChar)),
            StartLine = line,
            EndLine = endLine ?? line,
            StartCol = col,
            EndCol = endCol ?? col + 1,
            Metadata = metadata,
            Parts = parts?.Select(part => new CodeNodePart(
                Path.Combine(_root, part.File.Replace('/', Path.DirectorySeparatorChar)), part.Line, part.EndLine, 0, 200)).ToList(),
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
        return Service(Database, _root, Freshness, Files, Workers, Selector);
    }

    /// <summary>Commits the graph as it is now as a new generation.</summary>
    public void Commit() => Assert.True(Database.TryCommitGeneration(++_generation, _nodes.ToList(), _edges.ToList(), scope: null));

    public static ContextService Service(InMemoryDatabase database, string root = Root, IIndexFreshness? freshness = null,
        CodeContext.Core.Repositories.IFileMetadataRepository? files = null,
        CodeContext.Core.Workers.ILanguageWorkerService? workers = null, IRepositoryFileSelector? selector = null)
        => new(
            new InMemoryNodeRepository(database),
            new InMemoryEdgeRepository(database),
            files ?? new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(),
            Options.Create(new CodeContextOptions { RootPath = root }),
            workerService: workers,
            freshness: freshness,
            fileSelector: selector);
}
