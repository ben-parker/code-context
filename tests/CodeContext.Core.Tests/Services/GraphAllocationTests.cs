using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// The graph queries read the shards' own arrays: a lookup over several shards allocates nothing, so what a query
/// allocates follows its answer, not the number of edges it looks at.
/// </summary>
public class GraphAllocationTests
{
    private static Dictionary<string, string> Owned(string workspace)
        => new() { ["parserId"] = "csharp", ["workspaceId"] = workspace };

    /// <summary><paramref name="targets"/> nodes in shard a, each used by <paramref name="callersPerTarget"/> callers split over both shards.</summary>
    private static InMemoryDatabase Build(int targets, int callersPerTarget)
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        for (var c = 0; c < callersPerTarget; c++)
        {
            var workspace = c % 2 == 0 ? "a" : "b";
            nodes.Add(new CodeNode { Id = $"csharp:{workspace}:caller{c}", Identifier = $"csharp:Caller{c}", Name = $"Caller{c}", Type = "Method", FilePath = $"/repo/src/C{c}.cs", Metadata = Owned(workspace) });
        }
        for (var t = 0; t < targets; t++)
        {
            var target = $"csharp:a:target{t}";
            nodes.Add(new CodeNode { Id = target, Identifier = $"csharp:Target{t}", Name = $"Target{t}", Type = "Method", FilePath = "/repo/src/T.cs", Metadata = Owned("a") });
            for (var c = 0; c < callersPerTarget; c++)
            {
                var workspace = c % 2 == 0 ? "a" : "b";
                edges.Add(new CodeEdge { Id = $"csharp:{workspace}:caller{c}>{target}", SourceId = $"csharp:{workspace}:caller{c}", TargetId = target, Type = "CALLS", Metadata = Owned(workspace) });
            }
        }
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, edges, scope: null));
        Assert.Equal(2, database.ShardKeys.Count);
        return database;
    }

    [Fact]
    public void LookupsOverSeveralShards_AllocateNothing()
    {
        var database = Build(targets: 10, callersPerTarget: 10);
        var walk = new GraphWalk(database.GetAdjacency().Shards, relations: null, includeTests: false, rootPath: "/repo");
        var shards = database.GetAdjacency().Shards;
        Assert.NotNull(walk.Node("csharp:b:caller1"));
        foreach (var shard in shards) _ = shard.GetEdgesByTarget("csharp:a:target3");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (walk.Node("csharp:b:caller1") is not null) found++;
            if (walk.IsTestNode(walk.Node("csharp:a:target3")!)) found++;
            foreach (var shard in shards) found += shard.GetEdgesByTarget("csharp:a:target3").Count;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000 * 11, found);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task Hubs_AllocateForTheirAnswer_NotPerEdge()
    {
        // 2,000 targets with 40 callers each over two shards: 80,000 edges and 4,000 multi-shard lookups. One list per
        // lookup would allocate several hundred KB on its own; a string per edge, several MB.
        var database = Build(targets: 2000, callersPerTarget: 40);
        var service = GraphTestGraph.Service(database);
        await service.GetHubsAsync(new GraphHubsRequest { Limit = 5 });

        var before = GC.GetAllocatedBytesForCurrentThread();
        var hubs = await service.GetHubsAsync(new GraphHubsRequest { Limit = 5 });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2000, hubs.TotalCount);
        Assert.Equal(40, hubs.Items[0].Dependents);
        Assert.True(allocated < 200_000, $"The hubs query allocated {allocated:N0} bytes.");
    }
}
