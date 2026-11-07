using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using Xunit;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Context responses over the real in-memory store depend on the graph only: not on the order its
/// facts were committed in, nor on the capacity of the dictionaries that hold them. The graph is
/// built with ties on every ranking key (same name, file, line and type), more callers than the
/// relationship cap, more name-matched test files than the naming-heuristic cap, and facts in three
/// shards, so any read that still leaks dictionary order changes the bytes.
/// </summary>
public class CanonicalOrderContextTests
{
    private const string Widget = "csharp:App.Widget";
    private const string WidgetRun = "csharp:App.Widget.Run()";

    [Fact]
    public async Task CompactResponses_AreIndependentOfCommitOrderAndCapacity()
    {
        var forward = await ResponsesAsync(BuildInOneCommit());
        var shuffled = await ResponsesAsync(BuildFileByFileInReverse());
        var replaced = await ResponsesAsync(BuildByReplaceAllAfterJunk());

        Assert.Equal(forward.Count, shuffled.Count);
        for (var i = 0; i < forward.Count; i++)
        {
            Assert.Equal(forward[i], shuffled[i]);
            Assert.Equal(forward[i], replaced[i]);
        }
    }

    [Fact]
    public async Task NamingHeuristic_TakesTheFirstTenFilesByPath()
    {
        var database = BuildInOneCommit();
        var service = Service(database);

        var result = await service.GetCompleteContextAsync(Widget, includeTests: true, includeRelated: false,
            includeMetrics: false, maxTestFiles: 100, maxTestMethods: 100);

        var testing = Assert.Single(result.Matches).Testing;
        var heuristic = testing.TestFiles
            .Where(file => file.Evidence.Contains("namingHeuristic"))
            .Select(file => file.FilePath)
            .ToList();

        // Twelve files are named for Widget; the spec file sorts first but cannot test it. The ten
        // taken are the first ten by path, OrdinalIgnoreCase then Ordinal ("A" before "a"). Test
        // evidence is grouped by file case-insensitively, so "A" and "a" share one entry.
        var expected = TestFilePaths()
            .Order(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        Assert.Equal(12, TestFilePaths().Count());
        Assert.Equal(
            expected.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
            heuristic.Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(heuristic, path => path.Contains("Spec", StringComparison.Ordinal));
        Assert.DoesNotContain(heuristic, path => path.Contains("/f7/", StringComparison.Ordinal) || path.Contains("/f8/", StringComparison.Ordinal));
    }

    // ---- The query matrix ----

    private static async Task<List<string>> ResponsesAsync(InMemoryDatabase database)
    {
        var service = Service(database);
        var responses = new List<string>();

        async Task Compact(string identifier, bool tests = false, bool expand = false, int maxMatches = 5,
            int maxRelationships = 10, bool related = false, int depth = 1, bool? exact = null)
        {
            var response = await service.GetCompactContextAsync(identifier, depth: depth, includeTests: tests, exact: exact,
                includeRelated: related, maxMatches: maxMatches, maxRelationships: maxRelationships, expandAmbiguous: expand);
            responses.Add($"{identifier}: " + JsonSerializer.Serialize(response, CodeContextJsonContext.Default.CompactContextResponse));
        }

        await Compact("Run");
        await Compact("Run", expand: true, maxMatches: 3, maxRelationships: 3);
        await Compact("Run", tests: true, expand: true, maxMatches: 50);
        await Compact("Ru", exact: false, expand: true, maxMatches: 2);
        await Compact(WidgetRun, tests: true, maxRelationships: 4, depth: 2);
        await Compact(WidgetRun, tests: true, related: true, maxRelationships: 100, depth: 3);
        await Compact(Widget, tests: true);
        await Compact(Widget, tests: true, related: true, maxRelationships: 5, depth: 2);
        await Compact("Widget.Run");
        await Compact("/repo/src/Things.cs", expand: true);
        await Compact("Things.cs");
        await Compact("WidgetTests.cs", tests: true);

        var complete = await service.GetCompleteContextAsync("Run", includeTests: true, includeRelated: true,
            maxTestFiles: 3, maxTestMethods: 2);
        responses.Add("complete Run: " + JsonSerializer.Serialize(complete, CodeContextJsonContext.Default.CompleteContextResponse));
        complete = await service.GetCompleteContextAsync(Widget, includeTests: true, includeRelated: true);
        responses.Add("complete Widget: " + JsonSerializer.Serialize(complete, CodeContextJsonContext.Default.CompleteContextResponse));

        var statistics = new InMemoryCodeGraphRepository(database).GetStatistics();
        responses.Add("stats: " + string.Join(",", statistics.NodesByType.Select(pair => $"{pair.Key}={pair.Value}"))
            + " / " + string.Join(",", statistics.EdgesByType.Select(pair => $"{pair.Key}={pair.Value}")));
        return responses;
    }

    private static ContextService Service(InMemoryDatabase database)
        => new(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new InMemoryFileMetadataRepository());

    // ---- Three ways to build the same graph ----

    private static InMemoryDatabase BuildInOneCommit()
    {
        var (nodes, edges) = Graph();
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, edges, scope: null));
        return database;
    }

    /// <summary>
    /// Each shard is first filled with junk (so its dictionaries grow to another capacity), then
    /// replaced whole with its facts in reverse, then re-committed file by file in reverse path order.
    /// </summary>
    private static InMemoryDatabase BuildFileByFileInReverse()
    {
        var database = new InMemoryDatabase();
        var generation = 0L;
        foreach (var (parser, workspace) in Shards().Reverse())
        {
            var junk = Enumerable.Range(0, 300).Select(i => new CodeNode
            {
                Id = $"{parser}:{workspace}:junk{i}", Identifier = $"junk:{parser}:{workspace}:{i}", Name = "Junk",
                Type = "Class", FilePath = "/repo/junk.cs", Metadata = Owner(parser, workspace),
            }).ToList();
            Assert.True(database.TryCommitGeneration(++generation, junk, [], new CommitScope(parser, workspace, null)));

            var (nodes, edges) = Graph();
            var mine = nodes.Where(node => Belongs(node.Metadata, parser, workspace)).Reverse().ToList();
            var myEdges = edges.Where(edge => Belongs(edge.Metadata, parser, workspace)).Reverse().ToList();
            Assert.True(database.TryCommitGeneration(++generation, mine, myEdges, new CommitScope(parser, workspace, null)));

            var (again, againEdges) = Graph();
            // Paths that differ only in case are one file to a case-insensitive file system, so they
            // are replaced together, as a worker on such a system would.
            var files = again.Where(node => Belongs(node.Metadata, parser, workspace) && node.FilePath is not null)
                .Select(node => node.FilePath!)
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Distinct(StringComparer.Ordinal).ToList())
                .OrderByDescending(group => group[0], StringComparer.Ordinal)
                .ToList();
            foreach (var group in files)
            {
                var fileNodes = again.Where(node => Belongs(node.Metadata, parser, workspace)
                    && node.FilePath is not null && group.Contains(node.FilePath)).ToList();
                var ids = fileNodes.Select(node => node.Id).ToHashSet();
                var fileEdges = againEdges.Where(edge => Belongs(edge.Metadata, parser, workspace) && ids.Contains(edge.SourceId)).ToList();
                fileNodes.Reverse();
                Assert.True(database.TryCommitGeneration(++generation, fileNodes, fileEdges, new CommitScope(parser, workspace, group)));
            }
        }
        return database;
    }

    private static InMemoryDatabase BuildByReplaceAllAfterJunk()
    {
        var database = new InMemoryDatabase();
        database.ReplaceAll(Enumerable.Range(0, 1000).Select(i => new CodeNode { Id = $"junk{i}", Name = "Junk" }), []);
        var (nodes, edges) = Graph();
        var random = new Random(4);
        database.ReplaceAll(nodes.OrderBy(_ => random.Next()).ToList(), edges.OrderBy(_ => random.Next()).ToList());
        return database;
    }

    private static IEnumerable<(string Parser, string Workspace)> Shards() => [("", ""), ("csharp", "w"), ("typescript", "w")];

    private static IEnumerable<string> TestFilePaths()
        => new[] { "/repo/tests/A/WidgetTests.cs", "/repo/tests/a/WidgetTests.cs", "/repo/tests/B/WidgetTests.cs" }
            .Concat(Enumerable.Range(0, 9).Select(i => $"/repo/tests/f{i}/WidgetTests.cs"));

    /// <summary>The graph, as fresh objects on every call (context assembly may write to nodes).</summary>
    private static (List<CodeNode> Nodes, List<CodeEdge> Edges) Graph()
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        CodeNode Add(string id, string identifier, string name, string type, string? file, int line,
            string parser = "csharp", string workspace = "w", string? signature = null)
        {
            var node = new CodeNode
            {
                Id = id, Identifier = identifier, Name = name, Type = type, FilePath = file, StartLine = line, EndLine = line + 3,
                Namespace = "App", Signature = signature ?? $"public void {name}()", Metadata = Owner(parser, workspace),
            };
            nodes.Add(node);
            return node;
        }
        void Link(string id, string source, string target, string type, int line, string parser = "csharp", string workspace = "w")
        {
            var metadata = Owner(parser, workspace) ?? new Dictionary<string, string>();
            metadata["line"] = line.ToString();
            edges.Add(new CodeEdge { Id = id, SourceId = source, TargetId = target, Type = type, Metadata = metadata });
        }

        Add("csharp:w:widget", Widget, "Widget", "Class", "/repo/src/Widget.cs", 1);
        Add("csharp:w:widget.run", WidgetRun, "Run", "Method", "/repo/src/Widget.cs", 10);
        Add("csharp:w:irunner.run", "csharp:App.IRunner.Run()", "Run", "Method", "/repo/src/IRunner.cs", 3);
        Link("csharp:w:e:impl", "csharp:w:widget.run", "csharp:w:irunner.run", "IMPLEMENTS_MEMBER", 10);
        Link("csharp:w:e:has", "csharp:w:widget", "csharp:w:widget.run", "HAS_METHOD", 1);

        // Ties on every ranking key: same name, file, line and type.
        foreach (var thing in new[] { "Gadget", "Gizmo", "Doohickey", "Contraption" })
            Add($"csharp:w:{thing.ToLowerInvariant()}.run", $"csharp:App.{thing}.Run()", "Run", "Method", "/repo/src/Things.cs", 5);
        Add("typescript:w:run", "typescript:app#run", "Run", "Method", "/repo/web/run.ts", 5, "typescript", "w");
        Add("plain:run", "plain:run", "Run", "Method", "/repo/src/Things.cs", 5, "", "");

        // More callers than the relationship cap, all tied in rank, file and line, in three shards.
        for (var i = 0; i < 14; i++)
        {
            Add($"csharp:w:caller{i:D2}", $"csharp:App.Callers.Call{i:D2}()", $"Call{i:D2}", "Method", "/repo/src/Callers.cs", 1);
            Link($"csharp:w:e:call{i:D2}", $"csharp:w:caller{i:D2}", "csharp:w:widget.run", "CALLS", 20 + i);
            Link($"csharp:w:e:callsiface{i:D2}", $"csharp:w:caller{i:D2}", "csharp:w:irunner.run", "CALLS", 40 + i);
        }
        Add("typescript:w:caller", "typescript:app#caller", "caller", "Method", "/repo/web/caller.ts", 1, "typescript", "w");
        Link("typescript:w:e:call", "typescript:w:caller", "csharp:w:widget.run", "CALLS", 2, "typescript", "w");
        Link("plain:e:call", "plain:run", "csharp:w:widget.run", "REFERENCES", 3, "", "");

        // Test files named for Widget (more than the heuristic's ten), one spec file that sorts first
        // but does not test Widget, and test methods that call Widget.Run.
        Add("csharp:w:spec", "csharp:Specs.Other", "OtherSpec", "Class", "/repo/specs/AaaSpec.cs", 1);
        var index = 0;
        foreach (var path in TestFilePaths())
        {
            var id = $"csharp:w:test{index:D2}";
            Add(id, $"csharp:Tests.T{index:D2}.Widget_Runs()", "Widget_Runs", "Method", path, 7,
                signature: "[Fact] public void Widget_Runs()");
            if (index % 3 == 0) Link($"csharp:w:e:test{index:D2}", id, "csharp:w:widget.run", "CALLS", 8);
            index++;
        }
        return (nodes, edges);
    }

    private static Dictionary<string, string>? Owner(string parser, string workspace)
        => parser.Length == 0 ? null : new Dictionary<string, string> { ["parserId"] = parser, ["workspaceId"] = workspace };

    private static bool Belongs(IReadOnlyDictionary<string, string>? metadata, string parser, string workspace)
        => parser.Length == 0
            ? metadata is null || !metadata.ContainsKey("parserId")
            : metadata?.GetValueOrDefault("parserId") == parser && metadata.GetValueOrDefault("workspaceId") == workspace;
}
