using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Xunit;

namespace CodeContext.Core.Tests.Repositories;

/// <summary>
/// Covers the version-stamped adjacency index (<see cref="InMemoryDatabase.GetAdjacency"/> +
/// <see cref="GraphAdjacency"/>): invalidation on every mutation path, generational/reconciliation
/// consistency, and set-equivalence with a brute-force scan (the pre-index behaviour). The path
/// index deliberately mirrors <see cref="FilePathMatcher"/> — the same matcher <c>ContextService</c>
/// uses — so its case/suffix semantics are validated against the real matcher, not a copy.
/// </summary>
public class GraphAdjacencyTests
{
    private readonly InMemoryDatabase _database = new();
    private readonly InMemoryNodeRepository _nodes;
    private readonly InMemoryEdgeRepository _edges;

    public GraphAdjacencyTests()
    {
        _nodes = new InMemoryNodeRepository(_database);
        _edges = new InMemoryEdgeRepository(_database);
    }

    private static CodeNode Node(string id, string filePath, string? name = null, string type = "Class",
        string? parserId = null, string? workspaceId = null)
        => new() { Id = id, Name = name ?? id, Type = type, FilePath = filePath, StartLine = 1, EndLine = 2,
            Metadata = Meta(parserId, workspaceId) };

    private static CodeEdge Edge(string id, string source, string target, string type = "CALLS",
        string? parserId = null, string? workspaceId = null)
        => new() { Id = id, SourceId = source, TargetId = target, Type = type,
            Metadata = Meta(parserId, workspaceId) };

    private static Dictionary<string, string>? Meta(string? parserId, string? workspaceId)
    {
        if (parserId is null && workspaceId is null) return null;
        var metadata = new Dictionary<string, string>();
        if (parserId is not null) metadata["parserId"] = parserId;
        if (workspaceId is not null) metadata["workspaceId"] = workspaceId;
        return metadata;
    }

    // ---- Invalidation ------------------------------------------------------

    [Fact]
    public void GetAdjacency_CachedWhenVersionUnchanged_ReturnsSameInstance()
    {
        _database.UpsertNode(Node("n1", "/a/A.cs"));

        var first = _database.GetAdjacency();
        var second = _database.GetAdjacency();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAdjacency_ReflectsUpsertedNodeAndEdge()
    {
        await _nodes.UpsertAsync(Node("n1", "/a/A.cs"));
        await _nodes.UpsertAsync(Node("n2", "/a/B.cs"));
        await _edges.UpsertAsync(Edge("e1", "n1", "n2"));

        var before = _database.GetAdjacency();
        Assert.Single(before.GetEdgesBySource("n1"));

        await _edges.UpsertAsync(Edge("e2", "n1", "n2", "REFERENCES"));

        var after = _database.GetAdjacency();
        Assert.NotSame(before, after);
        Assert.Equal(2, after.GetEdgesBySource("n1").Count);
        Assert.Equal(2, after.GetEdgesByTarget("n2").Count);
    }

    [Fact]
    public async Task GetAdjacency_ReflectsNodeDeletion()
    {
        await _nodes.UpsertAsync(Node("n1", "/a/A.cs"));
        Assert.Single(_database.GetAdjacency().Nodes);

        await _nodes.DeleteAsync("n1", CancellationToken.None);

        Assert.Empty(_database.GetAdjacency().Nodes);
    }

    [Fact]
    public async Task GetAdjacency_ReflectsEdgeDeletionByNodeId()
    {
        await _edges.UpsertAsync(Edge("e1", "n1", "n2"));
        await _edges.UpsertAsync(Edge("e2", "n3", "n1"));
        Assert.Single(_database.GetAdjacency().GetEdgesBySource("n1"));
        Assert.Single(_database.GetAdjacency().GetEdgesByTarget("n1"));

        await _edges.DeleteByNodeIdAsync("n1", CancellationToken.None);

        var adj = _database.GetAdjacency();
        Assert.Empty(adj.GetEdgesBySource("n1"));
        Assert.Empty(adj.GetEdgesByTarget("n1"));
        Assert.Empty(adj.Edges);
    }

    // ---- Generational commit / prune / reconciliation ----------------------

    [Fact]
    public void GetAdjacency_ReflectsGenerationCommit_ScopeReplacement()
    {
        // Two parsers own disjoint scopes via ownership metadata shards.
        Assert.True(_database.TryCommitGeneration(1,
            new[] { Node("p1:a", "/p1/A.cs", parserId: "p1", workspaceId: "w"),
                    Node("p2:b", "/p2/B.cs", parserId: "p2", workspaceId: "w") },
            new[] { Edge("p1:e", "p1:a", "p2:b", parserId: "p1", workspaceId: "w") },
            scope: null));

        var gen1 = _database.GetAdjacency();
        Assert.Equal(2, gen1.Nodes.Count);
        Assert.Single(gen1.GetEdgesBySource("p1:a"));

        // Replace only p1's scope with a new node + edge; p2 must survive.
        Assert.True(_database.TryCommitGeneration(2,
            new[] { Node("p1:a2", "/p1/A.cs", parserId: "p1", workspaceId: "w") },
            new[] { Edge("p1:e2", "p1:a2", "p2:b", parserId: "p1", workspaceId: "w") },
            scope: new CommitScope("p1", "w", null)));

        var gen2 = _database.GetAdjacency();
        Assert.Contains(gen2.Nodes, n => n.Id == "p1:a2");
        Assert.DoesNotContain(gen2.Nodes, n => n.Id == "p1:a");
        Assert.Contains(gen2.Nodes, n => n.Id == "p2:b");
        Assert.Empty(gen2.GetEdgesBySource("p1:a"));
        Assert.Single(gen2.GetEdgesBySource("p1:a2"));
    }

    [Fact]
    public void GetAdjacency_ReflectsPruneFilesNotPresent()
    {
        _database.TryCommitGeneration(1,
            new[] { Node("n1", "/a/A.cs"), Node("n2", "/a/B.cs") },
            new[] { Edge("e1", "n1", "n2") },
            scope: null);
        Assert.Equal(2, _database.GetAdjacency().Nodes.Count);

        var removed = _database.PruneFilesNotPresent(new[] { "/a/A.cs" });

        Assert.Equal(1, removed);
        var adj = _database.GetAdjacency();
        Assert.Single(adj.Nodes);
        Assert.Contains(adj.Nodes, n => n.Id == "n1");
        // Edge into the pruned node is gone.
        Assert.Empty(adj.GetEdgesBySource("n1"));
        Assert.Empty(adj.Edges);
    }

    [Fact]
    public void GetAdjacency_DuringReconciliation_SeesCommittedStateUntilCommit()
    {
        _database.TryCommitGeneration(1,
            new[] { Node("n1", "/a/A.cs") }, Array.Empty<CodeEdge>(), scope: null);

        _database.BeginReconciliation();
        // A null-scope generation commit is a whole-graph replacement; while it is staged in the
        // reconciliation buffer, readers must still observe the previous committed generation.
        _database.TryCommitGeneration(2,
            new[] { Node("n2", "/a/B.cs") }, Array.Empty<CodeEdge>(), scope: null);

        Assert.Single(_database.GetAdjacency().Nodes);
        Assert.Contains(_database.GetAdjacency().Nodes, n => n.Id == "n1");

        _database.CommitReconciliation();

        var committed = _database.GetAdjacency();
        Assert.Single(committed.Nodes);
        Assert.Contains(committed.Nodes, n => n.Id == "n2");
    }

    [Fact]
    public void GetAdjacency_ReconciliationRollback_LeavesCommittedStateIntact()
    {
        _database.TryCommitGeneration(1,
            new[] { Node("n1", "/a/A.cs") }, Array.Empty<CodeEdge>(), scope: null);

        _database.BeginReconciliation();
        _database.TryCommitGeneration(2,
            new[] { Node("n2", "/a/B.cs") }, Array.Empty<CodeEdge>(), scope: null);
        _database.RollbackReconciliation();

        var adj = _database.GetAdjacency();
        Assert.Single(adj.Nodes);
        Assert.Contains(adj.Nodes, n => n.Id == "n1");
    }

    // ---- File-path index semantics -----------------------------------------

    [Fact]
    public void NodesByFilePath_ExactCaseInsensitiveAndSuffixMatch_MirrorsFilePathMatcher()
    {
        _database.TryCommitGeneration(1, new[]
        {
            Node("n1", "/repo/src/Foo.cs"),
            Node("n2", "/repo/src/Bar.cs"),
            Node("n3", "/repo/tests/Foo.cs"),
        }, Array.Empty<CodeEdge>(), scope: null);
        var adj = _database.GetAdjacency();

        // Rooted request -> exact normalized match (case-insensitive, backslash-normalized).
        AssertSameNodeSet(new[] { "n1" }, adj.FindNodesByPath("/repo/src/Foo.cs"));
        AssertSameNodeSet(new[] { "n1" }, adj.FindNodesByPath("/REPO/SRC/FOO.CS"));
        AssertSameNodeSet(new[] { "n1" }, adj.FindNodesByPath(@"\repo\src\Foo.cs"));

        // Relative request -> exact OR "/"+suffix match; matches both Foo.cs files.
        AssertSameNodeSet(new[] { "n1", "n3" }, adj.FindNodesByPath("Foo.cs"));
        AssertSameNodeSet(new[] { "n1" }, adj.FindNodesByPath("src/Foo.cs"));

        // No spurious substring match (Bar.cs vs ar.cs).
        Assert.Empty(adj.FindNodesByPath("ar.cs"));
    }

    // ---- Scan-vs-index equivalence on a random (seeded) graph ---------------

    [Fact]
    public void Adjacency_IsSetEqualToBruteForceScan_OnRandomGraph()
    {
        var rng = new Random(20260717);
        var filePaths = new[]
        {
            "/repo/src/Alpha.cs", "/repo/src/Beta.cs", "/repo/src/sub/Alpha.cs",
            "/repo/tests/AlphaTests.cs", "/repo/src/GAMMA.cs", "/repo/src/gamma.cs",
        };
        var nodes = new List<CodeNode>();
        for (int i = 0; i < 200; i++)
            nodes.Add(Node($"n{i}", filePaths[rng.Next(filePaths.Length)], name: $"Sym{i}"));

        var nodeIds = nodes.Select(n => n.Id!).ToArray();
        var edges = new List<CodeEdge>();
        for (int i = 0; i < 600; i++)
            edges.Add(Edge($"e{i}", nodeIds[rng.Next(nodeIds.Length)], nodeIds[rng.Next(nodeIds.Length)],
                rng.Next(2) == 0 ? "CALLS" : "REFERENCES"));

        _database.TryCommitGeneration(1, nodes, edges, scope: null);
        var adj = _database.GetAdjacency();

        // Edge adjacency set-equal to brute-force scan for every node id.
        foreach (var id in nodeIds)
        {
            var expectSrc = edges.Where(e => e.SourceId == id).Select(e => e.Id!).ToHashSet();
            var expectTgt = edges.Where(e => e.TargetId == id).Select(e => e.Id!).ToHashSet();
            Assert.Equal(expectSrc, adj.GetEdgesBySource(id).Select(e => e.Id!).ToHashSet());
            Assert.Equal(expectTgt, adj.GetEdgesByTarget(id).Select(e => e.Id!).ToHashSet());
        }

        // File-path index set-equal to a brute-force FilePathMatcher filter for a mix of
        // rooted, relative, and case-variant query paths.
        var queries = new[]
        {
            "/repo/src/Alpha.cs", "/REPO/SRC/ALPHA.CS", @"\repo\src\Beta.cs",
            "Alpha.cs", "src/Alpha.cs", "sub/Alpha.cs", "gamma.cs", "GAMMA.CS",
            "AlphaTests.cs", "Missing.cs",
        };
        foreach (var q in queries)
        {
            var expected = nodes.Where(n => FilePathMatcher.Matches(n.FilePath, q)).Select(n => n.Id!).ToHashSet();
            var actual = adj.FindNodesByPath(q).Select(n => n.Id!).ToHashSet();
            Assert.Equal(expected, actual);
        }
    }

    // ---- Concurrency smoke -------------------------------------------------

    [Fact]
    public async Task GetAdjacency_ParallelReadsDuringGenerationSwaps_NeverPartial()
    {
        _database.TryCommitGeneration(1,
            new[] { Node("a", "/a/A.cs"), Node("b", "/a/B.cs") },
            new[] { Edge("e0", "a", "b") }, scope: null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var gen = 1L;

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                var g = Interlocked.Increment(ref gen);
                // Each committed generation is internally consistent: node x{i} + a self-edge.
                var id = $"x{i++}";
                _database.TryCommitGeneration(g,
                    new[] { Node(id, "/a/A.cs") },
                    new[] { Edge($"se{id}", id, id) },
                    scope: null);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var adj = _database.GetAdjacency();
                // Snapshot must be complete: every edge's endpoints resolve within the same snapshot.
                var nodeIds = adj.Nodes.Select(n => n.Id!).ToHashSet();
                foreach (var e in adj.Edges)
                {
                    Assert.Contains(e.SourceId!, nodeIds);
                    Assert.Contains(e.TargetId!, nodeIds);
                }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer));
    }

    private static void AssertSameNodeSet(IEnumerable<string> expectedIds, IReadOnlyList<CodeNode> actual)
        => Assert.Equal(expectedIds.ToHashSet(), actual.Select(n => n.Id!).ToHashSet());
}
