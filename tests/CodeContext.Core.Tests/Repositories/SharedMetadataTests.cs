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
