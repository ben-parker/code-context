using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// A graph query whose caller has gone away stops inside its walk, not only between attempts: on a large graph a walk
/// is the whole cost of the query.
/// </summary>
public class GraphCancellationTests
{
    private const int Links = GraphWalk.VisitsPerCancellationCheck * 3;

    /// <summary>A call chain n0 -> n1 -> ... long enough for a walk to look at the token more than once.</summary>
    private static (GraphTestGraph Graph, ContextService Service) Chain()
    {
        var graph = new GraphTestGraph();
        for (var i = 0; i <= Links; i++) graph.Node($"n{i}");
        for (var i = 0; i < Links; i++) graph.Calls($"n{i}", $"n{i + 1}");
        return (graph, graph.Build());
    }

    private static GraphWalk Walk(GraphTestGraph graph, CancellationToken token)
        => new(graph.Database.GetAdjacency().Shards, relations: null, includeTests: true, GraphTestGraph.Root, token);

    [Fact]
    public void EachWalk_StopsWhenTheCallerIsGone()
    {
        var (graph, _) = Chain();
        using var gone = new CancellationTokenSource();
        gone.Cancel();
        var start = new[] { GraphTestGraph.Id("n0") };
        var end = new HashSet<string> { GraphTestGraph.Id($"n{Links}") };

        // The depth limit is the service's to enforce; unbounded here, each walk would visit the whole chain.
        Assert.Throws<OperationCanceledException>(() => Walk(graph, gone.Token).ShortestPath(start, end, int.MaxValue - 1, out _));
        Assert.Throws<OperationCanceledException>(() => Walk(graph, gone.Token).AffectedBy([.. end], int.MaxValue - 1, out _));
        Assert.Throws<OperationCanceledException>(() => Walk(graph, gone.Token).Hubs(_ => true));
        Assert.Throws<OperationCanceledException>(() => Walk(graph, gone.Token).WithoutRelationFilter().Hubs(_ => true));
    }

    [Fact]
    public void AWalkNobodyCancels_Finishes()
    {
        var (graph, _) = Chain();
        var walk = Walk(graph, CancellationToken.None);
        var path = walk.ShortestPath([GraphTestGraph.Id("n0")], new HashSet<string> { GraphTestGraph.Id($"n{Links}") }, Links, out _);
        Assert.Equal(Links + 1, path!.Count);
        Assert.Equal(Links, walk.AffectedBy([GraphTestGraph.Id($"n{Links}")], Links, out _).Count);
        Assert.Equal(Links, walk.Hubs(_ => true).Count);
    }
}
