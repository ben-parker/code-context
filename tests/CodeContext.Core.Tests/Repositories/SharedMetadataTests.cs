using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Repositories;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Plan M1 step 1: facts of a shard with equal metadata share one immutable instance, after a snapshot
/// load and after a worker commit, and sharing changes no answer (content and enumeration order kept).
/// </summary>
public class SharedMetadataTests
{
    private static readonly ShardKey Key = ShardKey.From("csharp", "default");

    [Fact]
    public void Load_FactsWithEqualMetadataShareOneImmutableInstance()
    {
        using var temp = new SnapshotTempDirectory();
        var nodes = Enumerable.Range(0, 4).Select(i => new CodeNode
        {
            Id = $"csharp:default:N{i}", Identifier = $"csharp:N{i}", Name = $"N{i}", Type = "Class", FilePath = "a.cs",
            Metadata = i == 3 ? SnapshotTestSupport.Owned(Key, ("isTest", "true")) : SnapshotTestSupport.Owned(Key),
        }).ToList();
        var edges = new List<CodeEdge>
        {
            Edge("e0", "N0", "N1", ("line", "4"), ("column", "9")),
            Edge("e1", "N2", "N1", ("line", "4"), ("column", "9")),
            Edge("e2", "N0", "N2", ("line", "5"), ("column", "9")),
            Edge("e3", "N1", "N2"),
            Edge("e4", "N3", "N2", ("isTest", "true")),
        };
        var database = SnapshotTestSupport.Commit((Key, nodes, edges));
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));

        var shard = Assert.Single(SnapshotTestSupport.LoadOrFail(path).Shards).Shard;
        var loadedNodes = shard.Nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList();
        var loadedEdges = shard.Edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList();

        Assert.Same(loadedNodes[0].Metadata, loadedNodes[1].Metadata);
        Assert.Same(loadedNodes[0].Metadata, loadedNodes[2].Metadata);
        Assert.NotSame(loadedNodes[0].Metadata, loadedNodes[3].Metadata);
        Assert.Same(loadedEdges[0].Metadata, loadedEdges[1].Metadata);
        Assert.NotSame(loadedEdges[0].Metadata, loadedEdges[2].Metadata);
        // An edge without a position has the nodes' content, and so the nodes' instance.
        Assert.Same(loadedNodes[0].Metadata, loadedEdges[3].Metadata);
        Assert.Same(loadedNodes[3].Metadata, loadedEdges[4].Metadata);

        AssertImmutable(loadedNodes[0].Metadata);
        AssertImmutable(loadedEdges[0].Metadata);
        Assert.Equal(["column", "line", "parserId", "workspaceId"], loadedEdges[0].Metadata!.Keys);
        Assert.Equal(["9", "4", "csharp", "default"], loadedEdges[0].Metadata!.Values);
        Assert.Equal(["isTest", "parserId", "workspaceId"], loadedNodes[3].Metadata!.Keys);
    }

    [Fact]
    public async Task Commit_FactsWithEqualMetadataShareOneImmutableInstance_InTheSourceOrder()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);
        var source = new Dictionary<string, string> { ["zeta"] = "1", ["parserId"] = "SPOOFED", ["alpha"] = "2" };
        var delta = new AnalysisDelta(
            ParserId: "fake", ParserVersion: "1.0", WorkspaceId: "ws-1", Generation: 1, RequestId: 1,
            ReplacesWorkspace: true, ReplacesFiles: [],
            Nodes:
            [
                Node("fake:ws-1:A", null),
                Node("fake:ws-1:B", null),
                Node("fake:ws-1:C", new Dictionary<string, string> { ["zeta"] = "1", ["parserId"] = "x", ["alpha"] = "2" }),
                Node("fake:ws-1:D", source),
                Node("fake:ws-1:E", new Dictionary<string, string> { ["alpha"] = "2", ["zeta"] = "1" }),
            ],
            Edges:
            [
                new ProtocolEdge("fake:ws-1:A->B", "fake:ws-1:A", "fake:ws-1:B", "calls", new Dictionary<string, string> { ["line"] = "3" }),
                new ProtocolEdge("fake:ws-1:C->B", "fake:ws-1:C", "fake:ws-1:B", "calls", new Dictionary<string, string> { ["line"] = "3" }),
                new ProtocolEdge("fake:ws-1:D->B", "fake:ws-1:D", "fake:ws-1:B", "calls"),
            ],
            IsLastForRequest: true);

        Assert.True(await applier.ApplyAsync(delta));

        var graph = (await ((ICodeGraphRepository)store).GetGraphAsync())!;
        var nodes = graph.Nodes.ToDictionary(node => node.Id!.Split(':')[2], StringComparer.Ordinal);
        var edges = graph.Edges.ToDictionary(edge => edge.Id!, StringComparer.Ordinal);
        Assert.Same(nodes["A"].Metadata, nodes["B"].Metadata);
        Assert.Same(nodes["C"].Metadata, nodes["D"].Metadata);
        Assert.NotSame(nodes["D"].Metadata, nodes["E"].Metadata); // same pairs, other order: not shared
        Assert.Same(nodes["A"].Metadata, edges["fake:ws-1:D->B"].Metadata);
        Assert.Same(edges["fake:ws-1:A->B"].Metadata, edges["fake:ws-1:C->B"].Metadata);

        // The source order is kept, the ownership keys win in place or are appended, the source is untouched.
        Assert.Equal(["zeta", "parserId", "alpha", "workspaceId"], nodes["D"].Metadata!.Keys);
        Assert.Equal(["1", "fake", "2", "ws-1"], nodes["D"].Metadata!.Values);
        Assert.Equal(["alpha", "zeta", "parserId", "workspaceId"], nodes["E"].Metadata!.Keys);
        Assert.Equal("SPOOFED", source["parserId"]);
        Assert.Equal(3, source.Count);
        AssertImmutable(nodes["A"].Metadata);
        AssertImmutable(edges["fake:ws-1:A->B"].Metadata);
    }

    /// <summary>Plan M1 step 2: a request's repeated strings are stored as one instance each.</summary>
    [Fact]
    public async Task Commit_StoresRepeatedStringsOfARequestAsOneInstance()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);
        // Distinct instances of equal text, as a deserializer produces them.
        static string Fresh(string text) => new(text.AsSpan());
        ProtocolNode NodeIn(string name) => new(Fresh($"fake:ws-1:{name}"), Fresh($"fake:{name}"), Fresh("Run"), Fresh("method"),
            Fresh("fake"), Fresh("src/a.fake"), Namespace: Fresh("App"), Visibility: Fresh("public"), ReturnType: Fresh("void"),
            Parameters: Fresh("()"), Modifiers: Fresh("static"), Signature: Fresh("void Run()"));
        var delta = new AnalysisDelta(
            ParserId: "fake", ParserVersion: "1.0", WorkspaceId: "ws-1", Generation: 1, RequestId: 1,
            ReplacesWorkspace: true, ReplacesFiles: [],
            Nodes: [NodeIn("A"), NodeIn("B")],
            Edges:
            [
                new ProtocolEdge(Fresh("fake:ws-1:A->B"), Fresh("fake:ws-1:A"), Fresh("fake:ws-1:B"), Fresh("calls")),
                new ProtocolEdge(Fresh("fake:ws-1:B->A"), Fresh("fake:ws-1:B"), Fresh("fake:ws-1:A"), Fresh("calls")),
            ],
            IsLastForRequest: true);

        Assert.True(await applier.ApplyAsync(delta));

        var graph = (await ((ICodeGraphRepository)store).GetGraphAsync())!;
        var a = graph.Nodes.Single(node => node.Id == "fake:ws-1:A");
        var b = graph.Nodes.Single(node => node.Id == "fake:ws-1:B");
        foreach (var field in new Func<CodeNode, string?>[]
                 {
                     n => n.Name, n => n.Type, n => n.Language, n => n.FilePath, n => n.Namespace, n => n.Visibility,
                     n => n.Signature, n => n.ReturnType, n => n.Parameters, n => n.Modifiers,
                 })
        {
            Assert.Same(field(a), field(b));
        }
        var ab = graph.Edges.Single(edge => edge.Id == "fake:ws-1:A->B");
        var ba = graph.Edges.Single(edge => edge.Id == "fake:ws-1:B->A");
        Assert.Same(a.Id, ab.SourceId);
        Assert.Same(b.Id, ab.TargetId);
        Assert.Same(b.Id, ba.SourceId);
        Assert.Same(a.Id, ba.TargetId);
        Assert.Same(ab.Type, ba.Type);
    }

    /// <summary>
    /// A file-scoped request names nodes of other files as edge endpoints. Those ids are stored as the
    /// instance the stored node holds, not one more copy per request: after every file had been edited once,
    /// this repository's store held three instances of each target id on average.
    /// </summary>
    [Fact]
    public async Task Commit_OfOneFile_StoresEndpointsInOtherFilesAsTheStoredNodesIds()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);
        static string Fresh(string text) => new(text.AsSpan());
        static ProtocolNode NodeIn(string name, string file) => new(Fresh($"fake:ws-1:{name}"), Fresh($"fake:{name}"), name, "method", "fake", Fresh(file));
        static ProtocolEdge Calls(string source, string target)
            => new(Fresh($"fake:ws-1:{source}->fake:ws-1:{target}"), Fresh($"fake:ws-1:{source}"), Fresh($"fake:ws-1:{target}"), "calls");
        AnalysisDelta Delta(long generation, bool all, string[] files, ProtocolNode[] nodes, ProtocolEdge[] edges)
            => new("fake", "1.0", "ws-1", generation, generation, all, files, nodes, edges, IsLastForRequest: true);

        Assert.True(await applier.ApplyAsync(Delta(1, true, [],
            [NodeIn("A", "src/a.fake"), NodeIn("B", "src/b.fake")], [Calls("A", "B"), Calls("B", "A")])));
        // File b again, alone: its edge names A, a node of a file the request does not carry, and Missing, no node at all.
        Assert.True(await applier.ApplyAsync(Delta(2, false, ["src/b.fake"],
            [NodeIn("B", "src/b.fake")], [Calls("B", "A"), Calls("B", "Missing")])));
        // And inside a reconciliation, where the shard the request extends is the staged one.
        store.BeginReconciliation();
        Assert.True(await applier.ApplyAsync(Delta(3, false, ["src/c.fake"], [NodeIn("C", "src/c.fake")], [Calls("C", "B")])));
        Assert.True(await applier.ApplyAsync(Delta(4, false, ["src/d.fake"], [NodeIn("D", "src/d.fake")], [Calls("D", "C")])));
        store.CommitReconciliation();

        var graph = (await ((ICodeGraphRepository)store).GetGraphAsync())!;
        CodeNode Stored(string name) => graph.Nodes.Single(node => node.Id == $"fake:ws-1:{name}");
        CodeEdge StoredEdge(string source, string target) => graph.Edges.Single(edge => edge.Id == $"fake:ws-1:{source}->fake:ws-1:{target}");
        Assert.Same(Stored("A").Id, StoredEdge("B", "A").TargetId);
        Assert.Same(Stored("B").Id, StoredEdge("B", "A").SourceId);
        Assert.Equal("fake:ws-1:Missing", StoredEdge("B", "Missing").TargetId);
        Assert.Same(Stored("B").Id, StoredEdge("C", "B").TargetId);
        Assert.Same(Stored("C").Id, StoredEdge("D", "C").TargetId);
        // The id is still kept as parts over those instances.
        var key = Assert.IsType<EdgeId>(StoredEdge("B", "A").IdKey);
        Assert.Same(Stored("A").Id, key.Target);
    }

    /// <summary>
    /// A source map that yields a key twice (any IReadOnlyDictionary a worker host hands over, not only a
    /// Dictionary) keeps the last value at the key's first position, as the store did before M1, and the
    /// request still commits.
    /// </summary>
    [Fact]
    public async Task Commit_ASourceKeyRepeated_KeepsTheLastValue_AndCommitsTheRequest()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var store = (IGenerationalGraphStore)factory.CreateGraphRepository();
        var applier = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);
        var repeated = new RepeatingMap(("a", "1"), ("parserId", "x"), ("b", "3"), ("a", "2"), ("parserId", "y"));
        var many = new RepeatingMap(Enumerable.Range(0, 20).Select(i => ($"k{i % 12}", $"v{i}")).ToArray());
        var delta = new AnalysisDelta(
            ParserId: "fake", ParserVersion: "1.0", WorkspaceId: "ws-1", Generation: 1, RequestId: 1,
            ReplacesWorkspace: true, ReplacesFiles: [],
            Nodes: [Node("fake:ws-1:A", repeated), Node("fake:ws-1:B", many)],
            Edges: [new ProtocolEdge("fake:ws-1:A->B", "fake:ws-1:A", "fake:ws-1:B", "calls", repeated)],
            IsLastForRequest: true);

        Assert.True(await applier.ApplyAsync(delta));

        var graph = (await ((ICodeGraphRepository)store).GetGraphAsync())!;
        var a = graph.Nodes.Single(node => node.Id == "fake:ws-1:A");
        Assert.Equal(["a", "parserId", "b", "workspaceId"], a.Metadata!.Keys);
        Assert.Equal(["2", "fake", "3", "ws-1"], a.Metadata!.Values);
        Assert.Same(a.Metadata, graph.Edges.Single().Metadata);
        var b = graph.Nodes.Single(node => node.Id == "fake:ws-1:B");
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"k{i}").Concat(["parserId", "workspaceId"]), b.Metadata!.Keys);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i < 8 ? $"v{i + 12}" : $"v{i}").Concat(["fake", "ws-1"]), b.Metadata!.Values);
    }

    /// <summary>A read-only map that enumerates its pairs as given, repeats included.</summary>
    private sealed class RepeatingMap(params (string Key, string Value)[] pairs) : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => pairs.Last(pair => pair.Key == key).Value;
        public IEnumerable<string> Keys => pairs.Select(pair => pair.Key);
        public IEnumerable<string> Values => pairs.Select(pair => pair.Value);
        public int Count => pairs.Length;
        public bool ContainsKey(string key) => pairs.Any(pair => pair.Key == key);
        public bool TryGetValue(string key, out string value)
        {
            value = ContainsKey(key) ? this[key] : null!;
            return ContainsKey(key);
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => pairs.Select(pair => KeyValuePair.Create(pair.Key, pair.Value)).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void FactMetadata_ReadsAsADictionaryWouldAndRefusesARepeatedKey()
    {
        var small = new FactMetadata(["b", "1", "a", null!]);
        Assert.Equal(2, small.Count);
        Assert.True(small.TryGetValue("a", out var nullValue));
        Assert.Null(nullValue);
        Assert.False(small.ContainsKey("B"));
        Assert.Throws<KeyNotFoundException>(() => small["c"]);
        Assert.Equal(["b", "a"], small.Keys);

        var pairs = Enumerable.Range(0, 20).SelectMany(i => new[] { $"k{19 - i}", $"v{i}" }).ToArray();
        var large = new FactMetadata(pairs);
        Assert.Equal("v19", large["k0"]);
        Assert.Equal(pairs.Where((_, i) => i % 2 == 0), large.Keys);
        Assert.False(large.TryGetValue("k20", out _));

        Assert.Throws<ArgumentException>(() => new FactMetadata(["a", "1", "a", "2"]));
        Assert.Throws<ArgumentException>(() => new FactMetadata(pairs.Concat(["k3", "x"]).ToArray()));
    }

    private static void AssertImmutable(IReadOnlyDictionary<string, string>? metadata)
    {
        Assert.IsType<FactMetadata>(metadata);
        Assert.False(metadata is IDictionary<string, string>);
        Assert.False(metadata is System.Collections.IDictionary);
        Assert.False(metadata is ICollection<KeyValuePair<string, string>>);
    }

    private static ProtocolNode Node(string id, IReadOnlyDictionary<string, string>? metadata)
        => new(id, id, id, "class", "fake", "a.fake") { Metadata = metadata };

    private static CodeEdge Edge(string id, string source, string target, params (string Key, string Value)[] pairs) => new()
    {
        Id = $"csharp:default:{id}",
        SourceId = $"csharp:default:{source}",
        TargetId = $"csharp:default:{target}",
        Type = "CALLS",
        Metadata = SnapshotTestSupport.Owned(Key, pairs),
    };
}
