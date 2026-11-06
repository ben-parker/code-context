using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Repositories.StoreModel;
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
    private const string Parser = GeneratedShard.Parser;
    private const string Workspace = GeneratedShard.Workspace;
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
}
