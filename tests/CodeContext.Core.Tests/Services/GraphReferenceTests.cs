using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// The graph queries against a slow reference written straight from their definitions (fixed points over a flat edge
/// list, no shards, no pruning), on random graphs with every edge kind, statically bound calls, library symbols, test
/// files, containment and two shards. Each seed is checked with and without filters.
/// </summary>
public class GraphReferenceTests
{
    private const string Root = "/repo";
    private static readonly string[] UsageKinds = ["CALLS", "REFERENCES", "IMPLEMENTS", "INHERITS", "IMPORTS"];

    private sealed record Graph(List<CodeNode> Nodes, List<CodeEdge> Edges)
    {
        public CodeNode? Node(string id) => Nodes.FirstOrDefault(node => node.Id == id);
    }

    private static Graph Generate(int seed, int nodeCount, int edgeCount)
    {
        var random = new Random(seed);
        var nodes = new List<CodeNode>();
        for (var i = 0; i < nodeCount; i++)
        {
            var workspace = random.Next(2) == 0 ? "a" : "b";
            var external = random.Next(12) == 0;
            var metadata = new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = workspace };
            if (external) metadata["external"] = "true";
            var file = random.Next(6) == 0 ? $"tests/T{i % 4}Tests.cs" : $"src/F{i % 7}.cs";
            nodes.Add(new CodeNode
            {
                Id = $"csharp:{workspace}:n{i}",
                Identifier = $"csharp:Demo.N{i}",
                Name = $"N{i}",
                Type = random.Next(5) == 0 ? "Class" : "Method",
                FilePath = external ? null : $"{Root}/{file}",
                StartLine = i,
                Metadata = metadata,
            });
        }

        var edges = new List<CodeEdge>();
        for (var i = 0; i < edgeCount; i++)
        {
            var source = nodes[random.Next(nodeCount)];
            var target = nodes[random.Next(nodeCount)];
            var roll = random.Next(20);
            var type = roll switch
            {
                < 3 => "IMPLEMENTS_MEMBER",
                < 5 => "OVERRIDES_MEMBER",
                < 7 => "HAS_METHOD",
                < 8 => null,
                _ => UsageKinds[random.Next(UsageKinds.Length)],
            };
            var metadata = new Dictionary<string, string>(source.Metadata!.Where(pair => pair.Key != "external"));
            if (type == "CALLS" && random.Next(4) == 0) metadata[EdgeMetadataKeys.Dispatch] = EdgeDispatch.Static;
            if (random.Next(3) == 0) metadata["line"] = random.Next(100).ToString();
            edges.Add(new CodeEdge { Id = $"{source.Id}>{type}>{target.Id}>{i}", SourceId = source.Id, TargetId = target.Id, Type = type, Metadata = metadata });
        }
        return new Graph(nodes, edges);
    }

    private static ContextService Serve(Graph graph)
    {
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, graph.Nodes, graph.Edges, scope: null));
        Assert.Equal(2, database.ShardKeys.Count);
        return new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = Root }));
    }

    // ---- The reference ----

    private static bool IsUsage(CodeEdge edge) => edge.Type is null or "CALLS" or "REFERENCES" or "IMPLEMENTS" or "INHERITS" or "IMPORTS";
    private static bool IsFamily(CodeEdge edge) => edge.Type is "IMPLEMENTS_MEMBER" or "OVERRIDES_MEMBER";
    private static bool IsStatic(CodeEdge edge) => edge.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Dispatch) == EdgeDispatch.Static;
    private static bool IsExternal(CodeNode node) => node.Metadata?.GetValueOrDefault("external") == "true";
    private static bool IsTest(CodeNode node) => node.FilePath?.Contains("/tests/") == true;
    private static bool Admits(CodeEdge edge, string[]? relations) => relations is null || relations.Contains(edge.Type ?? "USES");

    private static HashSet<string> Members(Graph graph, string root)
    {
        var members = new HashSet<string> { root };
        bool grew;
        do
        {
            grew = false;
            foreach (var edge in graph.Edges.Where(edge => edge.Type == "HAS_METHOD" && members.Contains(edge.SourceId!)))
                grew |= graph.Node(edge.TargetId!) is not null && members.Add(edge.TargetId!);
        } while (grew);
        return members;
    }

    /// <summary>Distances of (node, may dispatch) states by relaxation until nothing changes.</summary>
    private static Dictionary<(string, bool), int> PathDistances(
        Graph graph, HashSet<string> starts, HashSet<string> goals, string[]? relations, bool includeTests)
    {
        var distance = starts.ToDictionary(start => (start, false), _ => 0);
        bool Allowed(string id) => graph.Node(id) is { } node && (includeTests || goals.Contains(id) || !IsTest(node));
        bool changed;
        do
        {
            changed = false;
            foreach (var ((id, dispatch), d) in distance.ToList())
            {
                foreach (var edge in graph.Edges)
                {
                    (string, bool)? reached = null;
                    var cost = 0;
                    if (edge.SourceId == id && IsUsage(edge) && Admits(edge, relations) && Allowed(edge.TargetId!))
                    {
                        reached = (edge.TargetId!, !IsStatic(edge));
                        cost = 1;
                    }
                    else if (dispatch && edge.TargetId == id && IsFamily(edge) && !IsExternal(graph.Node(id)!) && Allowed(edge.SourceId!))
                    {
                        reached = (edge.SourceId!, true);
                    }
                    if (reached is { } state && (!distance.TryGetValue(state, out var known) || known > d + cost))
                    {
                        distance[state] = d + cost;
                        changed = true;
                    }
                }
            }
        } while (changed);
        return distance;
    }

    private static int? ReferencePathLength(Graph graph, HashSet<string> starts, HashSet<string> goals, string[]? relations, bool includeTests, int maxDepth, out bool depthLimited)
    {
        var distances = PathDistances(graph, starts, goals, relations, includeTests);
        var best = distances.Where(pair => goals.Contains(pair.Key.Item1)).Select(pair => (int?)pair.Value).Min();
        var within = distances.Where(pair => pair.Value <= maxDepth).Select(pair => pair.Key.Item1).ToHashSet();
        depthLimited = !(best <= maxDepth) && distances.Keys.Any(state => !within.Contains(state.Item1));
        return best <= maxDepth ? best : null;
    }

    /// <summary>Affected distances by relaxation, straight from the definition.</summary>
    private static Dictionary<string, int> ReferenceAffected(Graph graph, HashSet<string> seeds, string[]? relations)
    {
        var distance = seeds.ToDictionary(seed => seed, _ => 0);
        // Declarations a node implements or overrides, at any height, library ones and what is above them excluded.
        HashSet<string> Declarations(string id)
        {
            var found = new HashSet<string>();
            var pending = new Stack<string>([id]);
            while (pending.TryPop(out var current))
            {
                foreach (var edge in graph.Edges.Where(edge => edge.SourceId == current && IsFamily(edge) && edge.TargetId != current))
                {
                    if (graph.Node(edge.TargetId!) is not { } declared || IsExternal(declared) || !found.Add(edge.TargetId!)) continue;
                    pending.Push(edge.TargetId!);
                }
            }
            return found;
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var (id, d) in distance.ToList())
            {
                var users = new List<string>();
                users.AddRange(graph.Edges.Where(edge => edge.TargetId == id
                    && ((IsUsage(edge) && Admits(edge, relations)) || (IsFamily(edge) && relations is null))).Select(edge => edge.SourceId!));
                foreach (var declaration in Declarations(id))
                {
                    users.AddRange(graph.Edges.Where(edge => edge.TargetId == declaration && IsUsage(edge) && Admits(edge, relations) && !IsStatic(edge))
                        .Select(edge => edge.SourceId!));
                }
                foreach (var user in users.Where(user => graph.Node(user) is not null))
                {
                    if (!distance.TryGetValue(user, out var known) || known > d + 1)
                    {
                        distance[user] = d + 1;
                        changed = true;
                    }
                }
            }
        } while (changed);
        foreach (var seed in seeds) distance.Remove(seed);
        return distance;
    }

    private static Dictionary<string, (int Dependents, int Files)> ReferenceHubs(Graph graph, string[]? relations, bool includeTests)
        => graph.Nodes
            .Where(node => !IsExternal(node) && (includeTests || !IsTest(node)))
            .Select(node => (node, users: graph.Edges
                .Where(edge => edge.TargetId == node.Id && edge.SourceId != node.Id && IsUsage(edge) && Admits(edge, relations))
                .Select(edge => graph.Node(edge.SourceId!)).OfType<CodeNode>()
                .Where(user => includeTests || !IsTest(user))
                .DistinctBy(user => user.Id).ToList()))
            .Where(item => item.users.Count > 0)
            .ToDictionary(item => item.node.Identifier, item => (item.users.Count, item.users.Select(user => user.FilePath).Distinct().Count()));

    // ---- The comparison ----

    public static TheoryData<int> Seeds() => new(Enumerable.Range(1, 12));

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Path_AgreesWithTheReference_AndEveryHopIsARecordedStep(int seed)
    {
        var graph = Generate(seed, nodeCount: 40, edgeCount: 90);
        var service = Serve(graph);
        var random = new Random(seed * 31);
        foreach (var (relations, includeTests, maxDepth) in new (string[]?, bool, int)[] { (null, false, 10), (null, true, 3), (["CALLS"], false, 10), (["CALLS", "REFERENCES"], true, 2) })
        {
            for (var trial = 0; trial < 15; trial++)
            {
                var from = graph.Nodes[random.Next(graph.Nodes.Count)];
                var to = graph.Nodes[random.Next(graph.Nodes.Count)];
                var response = await service.FindPathAsync(new GraphPathRequest
                {
                    From = from.Identifier, To = to.Identifier, MaxDepth = maxDepth,
                    Relation = relations is null ? null : string.Join(",", relations), IncludeTests = includeTests,
                });
                var starts = Members(graph, from.Id!);
                var goals = Members(graph, to.Id!);
                var expected = ReferencePathLength(graph, starts, goals, relations, includeTests, maxDepth, out var expectedLimited);
                var context = $"seed {seed}, {from.Name} -> {to.Name}, relation {relations?.Length}, tests {includeTests}, depth {maxDepth}";
                Assert.True(expected == response.Length, $"{context}: expected {expected}, got {response.Length}: "
                    + (response.Path is null ? "-" : string.Join(" ", response.Path.Select(hop => $"{hop.Hop}:{hop.Relation}>{hop.Name}")))
                    + " | " + string.Join(" ", PathDistances(graph, starts, goals, relations, includeTests).OrderBy(p => p.Value).Select(p => $"{graph.Node(p.Key.Item1)!.Name}{(p.Key.Item2 ? "*" : "")}={p.Value}")));
                if (response.Found) AssertPathIsValid(graph, response, relations, includeTests, starts, goals, context);
                else Assert.True(expectedLimited == response.DepthLimited, $"{context}: depth limited {expectedLimited} expected.");
            }
        }
    }

    private static void AssertPathIsValid(Graph graph, GraphPathResponse response, string[]? relations, bool includeTests,
        HashSet<string> starts, HashSet<string> goals, string context)
    {
        string IdOf(string identifier) => graph.Nodes.Single(node => node.Identifier == identifier).Id!;
        var hops = response.Path!;
        var ids = hops.Select(hop => IdOf(hop.Identifier)).ToList();
        var mayDispatch = false;
        var traversed = hops.Where(hop => hop.Hop is not ("member" or "memberOf")).ToList();
        for (var i = 1; i < hops.Count; i++)
        {
            var (previous, current, hop) = (ids[i - 1], ids[i], hops[i]);
            var edges = graph.Edges.Where(edge => edge.Type == hop.Relation || (hop.Relation == "USES" && edge.Type is null)).ToList();
            switch (hop.Hop)
            {
                case "member":
                case "memberOf":
                    Assert.Contains(edges, edge => hop.Hop == "member" ? edge.SourceId == previous && edge.TargetId == current : edge.SourceId == current && edge.TargetId == previous);
                    break;
                case "uses":
                    var used = edges.Where(edge => edge.SourceId == previous && edge.TargetId == current && IsUsage(edge) && Admits(edge, relations)).ToList();
                    Assert.True(used.Count > 0, $"{context}: hop {i} is no recorded use.");
                    mayDispatch = used.Any(edge => !IsStatic(edge));
                    break;
                case "dispatch":
                    Assert.True(mayDispatch, $"{context}: hop {i} dispatches after a statically bound call or a start.");
                    Assert.Contains(edges, edge => edge.SourceId == current && edge.TargetId == previous && IsFamily(edge));
                    Assert.False(IsExternal(graph.Node(previous)!));
                    break;
                default:
                    Assert.Fail($"{context}: unexpected hop {hop.Hop}.");
                    break;
            }
            if (hop.Hop is "uses" or "dispatch" && i < hops.Count - 1 && !includeTests && !goals.Contains(current))
                Assert.False(IsTest(graph.Node(current)!), $"{context}: passes through a test file.");
        }
        Assert.Contains(IdOf(traversed[0].Identifier), starts);
        // The goal is the last node before the climb to the target type (a member hop can reach it first).
        Assert.Contains(IdOf(hops.Last(hop => hop.Hop != "memberOf").Identifier), goals);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Affected_AgreesWithTheReference(int seed)
    {
        var graph = Generate(seed, nodeCount: 50, edgeCount: 110);
        var service = Serve(graph);
        foreach (var relations in new[] { null, new[] { "CALLS" }, new[] { "CALLS", "INHERITS" } })
        {
            foreach (var node in graph.Nodes.Where((_, index) => index % 3 == seed % 3))
            {
                foreach (var maxDepth in new[] { 1, 3, 20 })
                {
                    var expected = ReferenceAffected(graph, Members(graph, node.Id!), relations);
                    var response = await service.GetAffectedAsync(new GraphAffectedRequest
                    {
                        Identifier = node.Identifier, MaxDepth = maxDepth, IncludeTests = true, Limit = 100,
                        Relation = relations is null ? null : string.Join(",", relations),
                    });
                    var within = expected.Where(pair => pair.Value <= maxDepth).ToDictionary(pair => graph.Node(pair.Key)!.Identifier, pair => pair.Value);
                    var context = $"seed {seed}, {node.Name}, relation {relations?.Length}, depth {maxDepth}";
                    Assert.True(within.Count == response.TotalCount, $"{context}: expected {within.Count}, got {response.TotalCount}.");
                    Assert.Equal(within.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                        response.Items.ToDictionary(item => item.Identifier, item => item.Distance!.Value).OrderBy(pair => pair.Key, StringComparer.Ordinal));
                    Assert.True((expected.Count > within.Count) == response.DepthLimited, $"{context}: depth limited.");
                    Assert.Equal(response.Items.Select(item => item.Distance), response.Items.Select(item => item.Distance).Order());
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Hubs_AgreeWithTheReference(int seed)
    {
        var graph = Generate(seed, nodeCount: 60, edgeCount: 200);
        var service = Serve(graph);
        foreach (var (relations, includeTests) in new (string[]?, bool)[] { (null, false), (null, true), (["REFERENCES", "IMPORTS"], false) })
        {
            var expected = ReferenceHubs(graph, relations, includeTests);
            var response = await service.GetHubsAsync(new GraphHubsRequest
            {
                Limit = 100, IncludeTests = includeTests, Relation = relations is null ? null : string.Join(",", relations),
            });
            Assert.Equal(expected.Count, response.TotalCount);
            Assert.Equal(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal),
                response.Items.ToDictionary(item => item.Identifier, item => (item.Dependents, item.DependentFiles)).OrderBy(pair => pair.Key, StringComparer.Ordinal));
            Assert.Equal(response.Items.Select(item => -item.Dependents), response.Items.Select(item => -item.Dependents).Order());
        }
    }
}
