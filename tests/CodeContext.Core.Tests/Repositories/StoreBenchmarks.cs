using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Repositories;

/// <summary>
/// Opt-in timing of the store's edit path on a jellyfin-sized shard (26,000 nodes, about
/// 296,000 edges, ids of 80-120 characters with long shared prefixes). Excluded from the
/// ordinary suite by the project's default filter; run with
/// <c>dotnet test -c Release --filter "Category=Benchmark" --logger "console;verbosity=detailed"</c>.
/// Asserts nothing about time. Reports the median of 5 runs of (a) building the shard's
/// adjacency, (b) one single-file scoped commit followed by one by-target edge query, and (c) one
/// such commit followed by one compact context query (depth 2, tests included).
/// Set <c>CODECONTEXT_BENCH_OUT</c> to a file path to append the numbers there as well.
/// </summary>
[Trait("Category", "Benchmark")]
public class StoreBenchmarks
{
    private const string Parser = "csharp";
    private const string Workspace = "default";
    private const int Files = 1300;
    private const int NodesPerFile = 20;
    private const int Runs = 5;

    private readonly ITestOutputHelper _output;

    public StoreBenchmarks(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AdjacencyBuild_AndSingleFileEdit_OnAJellyfinSizedShard()
    {
        var shard = GeneratedShard.Create(seed: 20261105);
        Report($"shard: {shard.Nodes.Count} nodes, {shard.Edges.Count} edges, "
            + $"mean id length {shard.Nodes.Average(node => node.Id!.Length):F0}");

        // Warm up the JIT on a full pass of both measurements before timing anything.
        _ = MeasureAdjacencyBuild(shard);
        _ = await MeasureEditAsync(shard, runs: 1);

        var builds = Enumerable.Range(0, Runs).Select(_ => MeasureAdjacencyBuild(shard)).ToList();
        var edits = await MeasureEditAsync(shard, Runs);

        Report($"adjacency build: median {Median(builds):F1} ms [{string.Join(", ", builds.Select(ms => ms.ToString("F1")))}]");
        Report($"commit: median {Median(edits.Select(e => e.Commit)):F1} ms "
            + $"[{string.Join(", ", edits.Select(e => e.Commit.ToString("F1")))}]");
        Report($"query after commit: median {Median(edits.Select(e => e.Query)):F1} ms "
            + $"[{string.Join(", ", edits.Select(e => e.Query.ToString("F1")))}]");
        Report($"commit + query: median {Median(edits.Select(e => e.Commit + e.Query)):F1} ms");

        _ = await MeasureEditWithContextAsync(shard, runs: 1);
        var contexts = await MeasureEditWithContextAsync(shard, Runs);
        Report($"context query after commit: median {Median(contexts.Select(e => e.Query)):F1} ms "
            + $"[{string.Join(", ", contexts.Select(e => e.Query.ToString("F1")))}]");
        Report($"commit + context query: median {Median(contexts.Select(e => e.Commit + e.Query)):F1} ms "
            + $"[{string.Join(", ", contexts.Select(e => (e.Commit + e.Query).ToString("F1")))}]");
    }

    /// <summary>
    /// One single-file scoped commit followed by what an agent asks next: a compact context query
    /// (depth 2, tests included) for a method of the edited file, through <see cref="ContextService"/>.
    /// </summary>
    private static async Task<List<(double Commit, double Query)>> MeasureEditWithContextAsync(GeneratedShard shard, int runs)
    {
        var database = new InMemoryDatabase();
        var service = new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new InMemoryFileMetadataRepository());
        Assert.True(database.TryCommitGeneration(1, shard.Nodes, shard.Edges, new CommitScope(Parser, Workspace, null)));
        _ = database.GetAdjacency();

        var results = new List<(double, double)>();
        var generation = 1L;
        for (var run = 0; run < runs; run++)
        {
            var file = shard.FilePaths[(run * 89 + 7) % shard.FilePaths.Count];
            var fileNodes = shard.NodesByFile[file];
            var fileEdges = shard.EdgesByFile[file];
            var queried = fileNodes[1].Identifier!;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var stopwatch = Stopwatch.StartNew();
            Assert.True(database.TryCommitGeneration(
                ++generation, fileNodes, fileEdges, new CommitScope(Parser, Workspace, [file])));
            var commit = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            var response = await service.GetCompactContextAsync(queried, depth: 2, includeTests: true);
            var query = stopwatch.Elapsed.TotalMilliseconds;
            Assert.NotEmpty(response.Matches);
            results.Add((commit, query));
        }
        return results;
    }

    private static double MeasureAdjacencyBuild(GeneratedShard shard)
    {
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, shard.Nodes, shard.Edges, new CommitScope(Parser, Workspace, null)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        // The first keyed read builds the shard's adjacency, whether the store builds it eagerly
        // with the composite or on first use.
        var stopwatch = Stopwatch.StartNew();
        var hits = database.GetAdjacency().GetEdgesByTarget(shard.Nodes[0].Id!);
        stopwatch.Stop();
        Assert.NotNull(hits);
        Assert.Equal(shard.Edges.Count, database.GetAdjacency().Edges.Count);
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static async Task<List<(double Commit, double Query)>> MeasureEditAsync(GeneratedShard shard, int runs)
    {
        var database = new InMemoryDatabase();
        var edges = new InMemoryEdgeRepository(database);
        Assert.True(database.TryCommitGeneration(1, shard.Nodes, shard.Edges, new CommitScope(Parser, Workspace, null)));
        _ = database.GetAdjacency();

        var results = new List<(double, double)>();
        var generation = 1L;
        for (var run = 0; run < runs; run++)
        {
            var file = shard.FilePaths[(run * 97 + 13) % shard.FilePaths.Count];
            var fileNodes = shard.NodesByFile[file];
            var fileEdges = shard.EdgesByFile[file];
            var queried = fileNodes[0].Id!;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var stopwatch = Stopwatch.StartNew();
            Assert.True(database.TryCommitGeneration(
                ++generation, fileNodes, fileEdges, new CommitScope(Parser, Workspace, [file])));
            var commit = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            var hits = await edges.GetByTargetIdAsync(queried);
            var query = stopwatch.Elapsed.TotalMilliseconds;
            Assert.NotNull(hits);
            results.Add((commit, query));
        }
        return results;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[sorted.Count / 2];
    }

    private void Report(string line)
    {
        _output.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_OUT");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>A deterministic jellyfin-shaped shard: 1,300 files of 20 nodes, about 11 edges a node.</summary>
    private sealed class GeneratedShard
    {
        public required List<CodeNode> Nodes { get; init; }
        public required List<CodeEdge> Edges { get; init; }
        public required List<string> FilePaths { get; init; }
        public required Dictionary<string, List<CodeNode>> NodesByFile { get; init; }
        public required Dictionary<string, List<CodeEdge>> EdgesByFile { get; init; }

        public static GeneratedShard Create(int seed)
        {
            var random = new Random(seed);
            var nodes = new List<CodeNode>(Files * NodesPerFile);
            var filePaths = new List<string>(Files);
            var nodesByFile = new Dictionary<string, List<CodeNode>>(StringComparer.Ordinal);
            var meta = new Dictionary<string, string> { ["parserId"] = Parser, ["workspaceId"] = Workspace };
            string[] areas = ["MediaBrowser.Controller.Entities", "Jellyfin.Server.Implementations.Users",
                "MediaBrowser.Model.Configuration", "Emby.Server.Implementations.Library"];

            for (var f = 0; f < Files; f++)
            {
                var ns = $"{areas[f % areas.Length]}.Area{f / 40:D2}";
                var type = $"Type{f:D4}Service";
                var path = $"C:/src/jellyfin/{ns.Replace('.', '/')}/{type}.cs";
                filePaths.Add(path);
                var hash = ((uint)random.Next()).ToString("x8");
                var bucket = new List<CodeNode>(NodesPerFile);
                for (var n = 0; n < NodesPerFile; n++)
                {
                    var member = n == 0 ? type : $"{type}.Method{n:D2}Async(CancellationToken,String)";
                    var display = $"{ns}.{member}";
                    var node = new CodeNode
                    {
                        Id = $"{Parser}:{Workspace}:{hash}:{display}",
                        Identifier = $"{Parser}:{display}",
                        Name = n == 0 ? type : $"Method{n:D2}Async",
                        Type = n == 0 ? "Class" : "Method",
                        FilePath = path,
                        StartLine = n * 10,
                        EndLine = n * 10 + 8,
                        Namespace = ns,
                        Signature = $"public Task Method{n:D2}Async(CancellationToken cancellationToken, string value)",
                        Language = "csharp",
                        Metadata = meta,
                    };
                    nodes.Add(node);
                    bucket.Add(node);
                }
                nodesByFile[path] = bucket;
            }

            // About 11.4 edges a node, sourced in the node's file; targets skewed toward a
            // popular few so some by-target buckets are large, as in a real code base.
            var edges = new List<CodeEdge>(296_000);
            var edgesByFile = new Dictionary<string, List<CodeEdge>>(StringComparer.Ordinal);
            string[] kinds = ["CALLS", "REFERENCES", "HAS_METHOD", "IMPLEMENTS"];
            foreach (var path in filePaths)
            {
                var bucket = new List<CodeEdge>(240);
                foreach (var source in nodesByFile[path])
                {
                    var count = 9 + random.Next(6);
                    for (var k = 0; k < count; k++)
                    {
                        var targetIndex = random.Next(4) == 0
                            ? random.Next(200)
                            : random.Next(nodes.Count);
                        var target = nodes[targetIndex];
                        var kind = kinds[random.Next(kinds.Length)];
                        var line = (source.StartLine + k).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var edge = new CodeEdge
                        {
                            Id = $"{source.Id}|{kind}|{target.Id}#{line}",
                            SourceId = source.Id,
                            TargetId = target.Id,
                            Type = kind,
                            Metadata = new Dictionary<string, string>
                            {
                                ["parserId"] = Parser, ["workspaceId"] = Workspace, ["line"] = line,
                            },
                        };
                        edges.Add(edge);
                        bucket.Add(edge);
                    }
                }
                edgesByFile[path] = bucket;
            }

            return new GeneratedShard
            {
                Nodes = nodes, Edges = edges, FilePaths = filePaths,
                NodesByFile = nodesByFile, EdgesByFile = edgesByFile,
            };
        }
    }
}
