using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

public class GenerationalGraphStoreTests
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryCodeGraphRepository _repository;
    private IGenerationalGraphStore Store => _repository;

    public GenerationalGraphStoreTests()
    {
        _repository = new InMemoryCodeGraphRepository(_database);
    }

    private static CodeNode Node(string id, string filePath = "/repo/a.cs", string type = "Class", string? language = "csharp") => new()
    {
        Id = id,
        Name = id,
        Type = type,
        FilePath = filePath,
        Language = language,
        StartLine = 1,
        EndLine = 5,
        StartCol = 2,
        EndCol = 3,
        Namespace = "N",
        Visibility = "public",
        Signature = $"public class {id}",
        ReturnType = "void",
        Parameters = "()",
        Modifiers = "static",
        Metrics = "{}",
        Metadata = new Dictionary<string, string> { ["k"] = "v" },
    };

    private static CodeEdge Edge(string id, string source, string target, string type = "CALLS") => new()
    {
        Id = id,
        SourceId = source,
        TargetId = target,
        Type = type,
    };

    [Fact]
    public async Task Commit_PreservesAllNodeFields()
    {
        // The old JSON round-trip dropped Language, columns, ReturnType, Parameters,
        // Modifiers, Metrics and Metadata; the typed path must not.
        var node = Node("n1");
        await Store.TryCommitGenerationAsync(1, [node], []);

        var stored = _database.Nodes["n1"];
        Assert.Equal("csharp", stored.Language);
        Assert.Equal(2, stored.StartCol);
        Assert.Equal(3, stored.EndCol);
        Assert.Equal("void", stored.ReturnType);
        Assert.Equal("()", stored.Parameters);
        Assert.Equal("static", stored.Modifiers);
        Assert.Equal("{}", stored.Metrics);
        Assert.NotNull(stored.Metadata);
        Assert.Equal("v", stored.Metadata!["k"]);
    }

    [Fact]
    public async Task Commit_WithNullScope_ReplacesEntireGraph()
    {
        await Store.TryCommitGenerationAsync(1, [Node("old")], [Edge("e1", "old", "x")]);

        var committed = await Store.TryCommitGenerationAsync(2, [Node("new")], []);

        Assert.True(committed);
        Assert.Single(_database.Nodes);
        Assert.True(_database.Nodes.ContainsKey("new"));
        Assert.Empty(_database.Edges);
    }

    [Fact]
    public async Task Commit_WithScope_PreservesNodesOutsideScope()
    {
        // A C# reparse must not wipe TypeScript facts.
        var tsNode = Node("ts1", filePath: "/repo/app.ts", language: "typescript");
        var csNode = Node("cs1", filePath: "/repo/a.cs");
        var tsEdge = Edge("e-ts", "ts1", "ts2");
        await Store.TryCommitGenerationAsync(1, [tsNode, csNode], [tsEdge]);

        static bool IsCSharp(CodeNode n) => n.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;
        var committed = await Store.TryCommitGenerationAsync(2, [Node("cs2")], [], IsCSharp);

        Assert.True(committed);
        Assert.True(_database.Nodes.ContainsKey("ts1"), "TypeScript node must survive a C# commit");
        Assert.True(_database.Nodes.ContainsKey("cs2"));
        Assert.False(_database.Nodes.ContainsKey("cs1"), "old C# node must be replaced");
        Assert.True(_database.Edges.ContainsKey("e-ts"), "edge from an out-of-scope node must survive");
    }

    [Fact]
    public async Task Commit_WithScope_DropsCarriedEdgesWhoseTargetWasNotReestablished()
    {
        // A TS -> C# edge survives only while the C# target still exists after the commit.
        var tsNode = Node("ts1", filePath: "/repo/app.ts", language: "typescript");
        var keptTarget = Node("cs-kept", filePath: "/repo/kept.cs");
        var goneTarget = Node("cs-gone", filePath: "/repo/gone.cs");
        await Store.TryCommitGenerationAsync(1,
            [tsNode, keptTarget, goneTarget],
            [Edge("e-kept", "ts1", "cs-kept"), Edge("e-gone", "ts1", "cs-gone")]);

        static bool IsCSharp(CodeNode n) => n.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true;
        // The new C# generation re-establishes cs-kept but not cs-gone.
        await Store.TryCommitGenerationAsync(2, [Node("cs-kept", filePath: "/repo/kept.cs")], [], IsCSharp);

        Assert.True(_database.Edges.ContainsKey("e-kept"), "edge to a re-established target must survive");
        Assert.False(_database.Edges.ContainsKey("e-gone"), "edge to a vanished target must not dangle");
    }

    [Fact]
    public async Task Commit_StaleGeneration_IsRejected()
    {
        Assert.True(await Store.TryCommitGenerationAsync(5, [Node("newer")], []));

        var committed = await Store.TryCommitGenerationAsync(4, [Node("older")], []);

        Assert.False(committed);
        Assert.True(_database.Nodes.ContainsKey("newer"));
        Assert.False(_database.Nodes.ContainsKey("older"));
        Assert.Equal(5, Store.LastCommittedGeneration);
    }

    [Fact]
    public async Task PruneFilesNotPresent_RemovesNodesAndTouchingEdges()
    {
        var kept = Node("kept", filePath: "/repo/kept.cs");
        var gone = Node("gone", filePath: "/repo/gone.cs");
        await Store.TryCommitGenerationAsync(1,
            [kept, gone],
            [Edge("e1", "kept", "gone"), Edge("e2", "kept", "kept")]);

        var pruned = await Store.PruneFilesNotPresentAsync(["/repo/kept.cs"]);

        Assert.Equal(1, pruned);
        Assert.True(_database.Nodes.ContainsKey("kept"));
        Assert.False(_database.Nodes.ContainsKey("gone"));
        Assert.False(_database.Edges.ContainsKey("e1"), "edges touching pruned nodes must go");
        Assert.True(_database.Edges.ContainsKey("e2"));
    }

    [Fact]
    public async Task GetStatistics_TracksCommitsWithoutMaterializingGraph()
    {
        await Store.TryCommitGenerationAsync(1,
            [Node("a", type: "Class"), Node("b", type: "Class"), Node("c", type: "Method")],
            [Edge("e1", "a", "b", "CALLS"), Edge("e2", "b", "c", "INHERITS")]);

        var stats = Store.GetStatistics();

        Assert.Equal(3, stats.NodeCount);
        Assert.Equal(2, stats.EdgeCount);
        Assert.Equal(2, stats.NodesByType["Class"]);
        Assert.Equal(1, stats.NodesByType["Method"]);
        Assert.Equal(1, stats.EdgesByType["CALLS"]);

        // A later commit must be reflected (cache invalidation).
        await Store.TryCommitGenerationAsync(2, [Node("only", type: "Interface")], []);
        var updated = Store.GetStatistics();
        Assert.Equal(1, updated.NodeCount);
        Assert.Equal(1, updated.NodesByType["Interface"]);
    }

    [Fact]
    public async Task ReadersDuringCommit_SeeCompleteSnapshots()
    {
        // Readers must never observe an empty graph while a replacement commit is in
        // flight — the swap is atomic.
        await Store.TryCommitGenerationAsync(1,
            Enumerable.Range(0, 500).Select(i => Node($"n{i}")).ToList(), []);

        var stop = false;
        var sawIncompleteSnapshot = false;
        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                var count = _database.Nodes.Count;
                if (count != 500) Volatile.Write(ref sawIncompleteSnapshot, true);
            }
        });

        for (long gen = 2; gen < 40; gen++)
        {
            await Store.TryCommitGenerationAsync(gen,
                Enumerable.Range(0, 500).Select(i => Node($"n{i}")).ToList(), []);
        }

        Volatile.Write(ref stop, true);
        await reader;
        Assert.False(sawIncompleteSnapshot, "a reader observed a partially built or cleared graph");
    }

    [Fact]
    public async Task GetGraph_CapturesNodesAndEdgesFromSameCommittedGeneration()
    {
        await Store.TryCommitGenerationAsync(1, [Node("a")], [Edge("e-a", "a", "a")]);

        var stop = false;
        var sawMixedGeneration = false;
        var reader = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                var graph = (await _repository.GetGraphAsync())!;
                var nodeId = Assert.Single(graph.Nodes).Id;
                var edge = Assert.Single(graph.Edges);
                if (edge.SourceId != nodeId || edge.TargetId != nodeId)
                {
                    Volatile.Write(ref sawMixedGeneration, true);
                }
            }
        });

        for (long generation = 2; generation < 100; generation++)
        {
            var id = generation % 2 == 0 ? "b" : "a";
            await Store.TryCommitGenerationAsync(
                generation, [Node(id)], [Edge($"e-{id}", id, id)]);
        }

        Volatile.Write(ref stop, true);
        await reader;
        Assert.False(sawMixedGeneration, "GetGraph combined nodes and edges from different generations");
    }

    [Fact]
    public async Task Reconciliation_StagesScopedCommitsUntilOnePublishOrRollback()
    {
        await Store.TryCommitGenerationAsync(1, [Node("old")], []);

        Store.BeginReconciliation();
        Assert.True(await Store.TryCommitGenerationAsync(2, [Node("new")], []));
        Assert.Equal("old", Assert.Single((await _repository.GetGraphAsync())!.Nodes).Id);
        Store.CommitReconciliation();
        Assert.Equal("new", Assert.Single((await _repository.GetGraphAsync())!.Nodes).Id);

        Store.BeginReconciliation();
        Assert.True(await Store.TryCommitGenerationAsync(3, [Node("discarded")], []));
        Store.RollbackReconciliation();
        Assert.Equal("new", Assert.Single((await _repository.GetGraphAsync())!.Nodes).Id);
        Assert.Equal(2, Store.LastCommittedGeneration);
    }
}
