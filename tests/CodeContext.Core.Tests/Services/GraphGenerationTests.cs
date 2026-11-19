using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Each graph answer comes from one generation: a commit that lands while a query reads makes it read again, and a
/// graph that never holds still is reported, never answered from a mix.
/// </summary>
public class GraphGenerationTests
{
    private static string I(string name) => GraphTestGraph.Identifier(name);

    /// <summary>Generation 1: A calls B. Generation 2: A no longer calls B, and C does.</summary>
    private static (GraphTestGraph Graph, ContextService Service, InMemoryDatabase Next) TwoGenerations()
    {
        var graph = new GraphTestGraph().Node("A").Node("B").Node("C").Calls("A", "B");
        var service = graph.Build();
        var next = new GraphTestGraph().Node("A").Node("B").Node("C").Calls("C", "B");
        next.Build();
        return (graph, service, next.Database);
    }

    private static void Replace(InMemoryDatabase target, InMemoryDatabase from)
    {
        var graph = from.CaptureGraph();
        Assert.True(target.TryCommitGeneration(target.LastCommittedGeneration + 1, graph.Nodes, graph.Edges, scope: null));
    }

    [Fact]
    public async Task ACommitDuringTheRead_IsReadAgain_AndTheAnswerIsTheNewGeneration()
    {
        var (graph, service, next) = TwoGenerations();
        var attempts = new List<int>();
        service.AfterGraphReadForTests = attempt =>
        {
            attempts.Add(attempt);
            if (attempt == 1) Replace(graph.Database, next);
            return Task.CompletedTask;
        };

        var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = I("B") });

        Assert.Equal([1, 2], attempts);
        Assert.Equal([I("C")], affected.Items.Select(item => item.Identifier));
    }

    [Fact]
    public async Task UnderConcurrentCommits_EveryAnswerIsOneGenerations()
    {
        // The symbol keeps its identifier but changes its id between generations, so an answer that resolved it in one
        // generation and traversed another would find no edges at all: an answer neither generation gives.
        var database = new InMemoryDatabase();
        var service = GraphTestGraph.Service(database);
        CodeNode Node(string id, string identifier) => new()
        {
            Id = id, Identifier = identifier, Name = identifier.Split('.')[^1], Type = "Method", FilePath = "/repo/src/X.cs",
            Metadata = new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = "a" },
        };
        CodeEdge Edge(string source, string target) => new()
        {
            Id = $"{source}>{target}", SourceId = source, TargetId = target, Type = "CALLS",
            Metadata = new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = "a" },
        };
        (CodeNode[] Nodes, CodeEdge[] Edges) Generation(int which) => which == 0
            ? ([Node("csharp:a:b1", "csharp:Demo.B"), Node("csharp:a:A", "csharp:Demo.A")], [Edge("csharp:a:A", "csharp:a:b1")])
            : ([Node("csharp:a:b2", "csharp:Demo.B"), Node("csharp:a:C", "csharp:Demo.C")], [Edge("csharp:a:C", "csharp:a:b2")]);
        var first = Generation(0);
        Assert.True(database.TryCommitGeneration(1, first.Nodes, first.Edges, scope: null));

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var generation = 2L; !stop.IsCancellationRequested; generation++)
            {
                var facts = Generation((int)(generation % 2));
                database.TryCommitGeneration(generation, facts.Nodes, facts.Edges, scope: null);
            }
        });

        var answered = 0;
        var refused = 0;
        try
        {
            for (var i = 0; i < 20000 && answered < 2000; i++)
            {
                try
                {
                    var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "csharp:Demo.B" });
                    var users = string.Join(",", affected.Items.Select(item => item.Identifier));
                    Assert.True(users is "csharp:Demo.A" or "csharp:Demo.C", $"An answer mixed generations: [{users}].");
                    answered++;
                }
                catch (GraphChangingException)
                {
                    refused++;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
        Assert.True(answered > 0, $"No query answered ({refused} refused).");
    }

    [Fact]
    public async Task AGraphThatChangesUnderEveryAttempt_IsReported_NotAnsweredFromAMix()
    {
        var (graph, service, next) = TwoGenerations();
        service.AfterGraphReadForTests = _ =>
        {
            Replace(graph.Database, next);
            return Task.CompletedTask;
        };

        var error = await Assert.ThrowsAsync<GraphChangingException>(
            () => service.FindPathAsync(new GraphPathRequest { From = I("A"), To = I("B") }));
        Assert.Contains($"each of {ContextService.GraphReadAttempts} attempts", error.Message);
    }

    [Fact]
    public async Task ALegacyInPlaceWrite_CountsAsAChange()
    {
        var (graph, service, _) = TwoGenerations();
        var attempts = 0;
        service.AfterGraphReadForTests = attempt =>
        {
            attempts = attempt;
            if (attempt == 1)
            {
                graph.Database.UpsertEdge(new CodeEdge
                {
                    Id = "late", SourceId = GraphTestGraph.Id("C"), TargetId = GraphTestGraph.Id("B"), Type = "CALLS",
                    Metadata = new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = "b" },
                });
            }
            return Task.CompletedTask;
        };

        var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = I("B") });

        Assert.Equal(2, attempts);
        Assert.Equal([I("A"), I("C")], affected.Items.Select(item => item.Identifier).Order());
    }
}
