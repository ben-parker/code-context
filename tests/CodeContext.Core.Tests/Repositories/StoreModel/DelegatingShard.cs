using System.Collections.Generic;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

/// <summary>
/// A shard that is not a heap shard but answers exactly as one: every member delegates to a private
/// heap copy, and every commit member wraps its result again, so the shard stays non-heap across
/// commits. It stands in for the mapped shard of a later phase wherever the store must treat a
/// shard of another kind as it treats the heap shard (the legacy in-place mutations materialize it,
/// <c>TryReplaceShard</c> and <c>PublishLoadedShards</c> install it).
/// </summary>
internal sealed class DelegatingShard : IGraphShard
{
    private readonly IGraphShard _inner;

    private DelegatingShard(IGraphShard inner) => _inner = inner;

    /// <summary>An equivalent non-heap shard over a private copy of <paramref name="shard"/>.</summary>
    public static IGraphShard Of(IGraphShard shard) => new DelegatingShard(InMemoryDatabase.GraphState.CopyOf(shard));

    private static IGraphShard? Wrap(IGraphShard? shard) => shard is null ? null : new DelegatingShard(shard);

    public int NodeCount => _inner.NodeCount;

    public int EdgeCount => _inner.EdgeCount;

    public bool TryGetNode(string id, out CodeNode? node) => _inner.TryGetNode(id, out node);

    public bool TryGetNodeIdByIdentifier(string identifier, out string? id) => _inner.TryGetNodeIdByIdentifier(identifier, out id);

    public bool TryGetEdge(string id, out CodeEdge? edge) => _inner.TryGetEdge(id, out edge);

    public IReadOnlyList<CodeNode> FindNodesByName(string name, bool exact) => _inner.FindNodesByName(name, exact);

    public IReadOnlyList<CodeNode> FindNodesByPath(string requestedPath) => _inner.FindNodesByPath(requestedPath);

    public IReadOnlyList<CodeEdge> GetEdgesBySource(string id) => _inner.GetEdgesBySource(id);

    public IReadOnlyList<CodeEdge> GetEdgesByTarget(string id) => _inner.GetEdgesByTarget(id);

    public IReadOnlyList<CodeNode> GetAllNodes() => _inner.GetAllNodes();

    public IReadOnlyList<CodeEdge> GetAllEdges() => _inner.GetAllEdges();

    public IReadOnlyList<string> GetFilePaths() => _inner.GetFilePaths();

    public void AddStatistics(Dictionary<string, int> nodesByType, Dictionary<string, int> edgesByType)
        => _inner.AddStatistics(nodesByType, edgesByType);

    public IReadOnlyCollection<string> GetCrossShardEdgeIds() => _inner.GetCrossShardEdgeIds();

    public IGraphShard? ApplyScoped(
        IReadOnlyList<CodeNode> nodes,
        IReadOnlyList<CodeEdge> edges,
        IReadOnlySet<string>? replaceFiles,
        IReadOnlyList<IGraphShard> foreignShards,
        out HashSet<string> removedIds,
        out HashSet<string> removedNotReestablished)
        => Wrap(_inner.ApplyScoped(nodes, edges, replaceFiles, foreignShards, out removedIds, out removedNotReestablished));

    public IGraphShard? WithoutForeignConflicts(
        IReadOnlySet<string> removedIds,
        IReadOnlySet<string> removedNotReestablished,
        IReadOnlySet<string> deltaNodeIds,
        IReadOnlySet<string> deltaEdgeIds)
        => Wrap(_inner.WithoutForeignConflicts(removedIds, removedNotReestablished, deltaNodeIds, deltaEdgeIds));

    public void CollectNodesOutside(IReadOnlySet<string> keepPaths, HashSet<string> into)
        => _inner.CollectNodesOutside(keepPaths, into);

    public IGraphShard? WithoutNodes(IReadOnlySet<string> removedIds) => Wrap(_inner.WithoutNodes(removedIds));
}
