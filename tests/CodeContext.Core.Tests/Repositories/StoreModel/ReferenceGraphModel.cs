using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// An obviously-correct model of the graph store's commit and read rules: one dictionary of nodes
/// and one of edges, each fact tagged with the shard that owns it. It is written from the rules
/// (plan section 3.4 and the store's documented behavior), never by calling the store, so a store
/// that disagrees with it has broken a rule. Reads return canonical order: shards in
/// <see cref="ShardKey"/> order, ordinal id order inside a shard.
///
/// Inputs are cloned on the way in, so the model and a store under test never share a mutable
/// <see cref="CodeNode"/> (both derive missing identifiers in place).
/// </summary>
internal sealed class ReferenceGraphModel
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal sealed record Owned<T>(ShardKey Shard, T Fact);

    internal sealed class State
    {
        public Dictionary<string, Owned<CodeNode>> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Owned<CodeEdge>> Edges { get; } = new(StringComparer.Ordinal);

        public State Copy()
        {
            var copy = new State();
            foreach (var (id, node) in Nodes) copy.Nodes[id] = node;
            foreach (var (id, edge) in Edges) copy.Edges[id] = edge;
            return copy;
        }
    }

    private State _published = new();
    private State? _staged;
    private long _generation;
    private long _stagedGeneration;

    /// <summary>The state commits and prunes act on: the staged reconciliation, else the published graph.</summary>
    private State Working => _staged ?? _published;

    /// <summary>What readers see.</summary>
    public State Published => _published;

    /// <summary>The state the next commit acts on (for generators choosing inputs).</summary>
    public State Current => Working;

    public bool ReconciliationActive => _staged is not null;

    public long LastCommittedGeneration => _staged is null ? _generation : _stagedGeneration;

    // ---- Writes ----

    public bool TryCommitGeneration(long generation, IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges, CommitScope? scope)
    {
        if (generation <= LastCommittedGeneration) return false;
        var nodeList = nodes.Select(Clone).ToList();
        var edgeList = edges.Select(Clone).ToList();
        var next = scope is null ? Replace(nodeList, edgeList) : Scoped(Working, nodeList, edgeList, scope);
        if (_staged is not null)
        {
            _staged = next;
            _stagedGeneration = generation;
        }
        else
        {
            _published = next;
            _generation = generation;
        }
        return true;
    }

    /// <summary>Replaces the published graph (no generation check); a staged reconciliation is untouched.</summary>
    public void ReplaceAll(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
        => _published = Replace(nodes.Select(Clone).ToList(), edges.Select(Clone).ToList());

    /// <summary>Empties the published graph; the generation and a staged reconciliation are untouched.</summary>
    public void Reset() => _published = new State();

    /// <summary>
    /// Whether swapping the published shard <paramref name="shard"/> for an equivalent one succeeds:
    /// only outside a reconciliation and only for a shard that exists. A swap changes no read.
    /// </summary>
    public bool TryReplaceShard(ShardKey shard)
        => _staged is null
            && (_published.Nodes.Values.Any(owned => owned.Shard == shard) || _published.Edges.Values.Any(owned => owned.Shard == shard));

    // ---- Legacy in-place mutations (published graph; not modelled during a reconciliation) ----

    /// <summary>
    /// Upserts a node into the shard its ownership metadata names. A missing identifier is derived;
    /// an identifier held by any other id throws; a copy of the id in another shard is dropped. Its
    /// old identifier stops resolving. Edges are untouched.
    /// </summary>
    public void UpsertNode(CodeNode input)
    {
        if (input.Id is null) throw new ArgumentException("Node must have an Id", "node");
        var node = Clone(input);
        DeriveIdentifier(node);
        RequireNotStaged();
        if (HasIdentifier(node) && _published.Nodes.Values.Any(owned =>
                owned.Fact.Id != node.Id && HasIdentifier(owned.Fact) && owned.Fact.Identifier == node.Identifier))
        {
            throw Duplicate(node.Identifier);
        }
        var next = _published.Copy();
        next.Nodes[node.Id!] = new Owned<CodeNode>(ShardKey.ForNode(node), node);
        _published = next;
    }

    /// <summary>Removes a node from whichever shard holds it, edges untouched; returns whether it existed.</summary>
    public bool RemoveNode(string id)
    {
        RequireNotStaged();
        if (!_published.Nodes.ContainsKey(id)) return false;
        var next = _published.Copy();
        next.Nodes.Remove(id);
        _published = next;
        return true;
    }

    /// <summary>Upserts an edge into the shard its ownership metadata names; a copy of the id elsewhere is dropped.</summary>
    public void UpsertEdge(CodeEdge input)
    {
        if (input.Id is null) throw new ArgumentException("Edge must have an Id", "edge");
        RequireNotStaged();
        var edge = Clone(input);
        var next = _published.Copy();
        next.Edges[edge.Id!] = new Owned<CodeEdge>(ShardKey.ForEdge(edge), edge);
        _published = next;
    }

    /// <summary>Removes every edge whose source or target equals <paramref name="nodeId"/> (null matches a null endpoint).</summary>
    public int RemoveEdgesTouchingNode(string? nodeId)
    {
        RequireNotStaged();
        var gone = _published.Edges
            .Where(pair => pair.Value.Fact.SourceId == nodeId || pair.Value.Fact.TargetId == nodeId)
            .Select(pair => pair.Key)
            .ToList();
        if (gone.Count == 0) return 0;
        var next = _published.Copy();
        foreach (var id in gone) next.Edges.Remove(id);
        _published = next;
        return gone.Count;
    }

    // A legacy mutation during a reconciliation writes the live map, and whether it survives the
    // reconciliation's commit depends on whether the staged map still shares that shard object. That
    // is not a rule worth modelling; the store's named tests pin it instead, and generators do not
    // emit these operations while a reconciliation is staged.
    private void RequireNotStaged()
    {
        if (_staged is not null)
            throw new NotSupportedException("The model does not define legacy mutations during a staged reconciliation.");
    }

    public int PruneFilesNotPresent(IReadOnlyCollection<string> presentFilePaths)
    {
        var keep = new HashSet<string>(presentFilePaths, PathComparer);
        var state = Working;
        var removed = state.Nodes.Values
            .Where(owned => owned.Fact.Metadata?.GetValueOrDefault("external") != "true"
                && !string.IsNullOrEmpty(owned.Fact.FilePath)
                && !keep.Contains(owned.Fact.FilePath))
            .Select(owned => owned.Fact.Id!)
            .ToHashSet(StringComparer.Ordinal);
        if (removed.Count == 0) return 0;

        var next = state.Copy();
        foreach (var id in removed) next.Nodes.Remove(id);
        foreach (var (id, edge) in state.Edges)
        {
            if ((edge.Fact.SourceId is not null && removed.Contains(edge.Fact.SourceId))
                || (edge.Fact.TargetId is not null && removed.Contains(edge.Fact.TargetId)))
            {
                next.Edges.Remove(id);
            }
        }
        if (_staged is not null) _staged = next;
        else _published = next;
        return removed.Count;
    }

    public void BeginReconciliation()
    {
        if (_staged is not null) throw new InvalidOperationException("A graph reconciliation is already active.");
        _staged = _published;
        _stagedGeneration = _generation;
    }

    public void CommitReconciliation()
    {
        if (_staged is null) throw new InvalidOperationException("No graph reconciliation is active.");
        _published = _staged;
        _generation = _stagedGeneration;
        _staged = null;
    }

    public void RollbackReconciliation()
    {
        _staged = null;
        _stagedGeneration = _generation;
    }

    /// <summary>
    /// Whole-graph replacement. Duplicate ids collapse to the last copy (keeping the first copy's
    /// position); each fact's shard comes from its ownership metadata; a public identifier held by
    /// two ids anywhere throws. The duplicate reported is the first one met walking shards in order
    /// of first appearance, nodes in that order inside a shard.
    /// </summary>
    private static State Replace(List<CodeNode> nodes, List<CodeEdge> edges)
    {
        var nodeOrder = LastWins(nodes, node => node.Id);
        var shardOrder = new List<ShardKey>();
        foreach (var node in nodeOrder)
            if (!shardOrder.Contains(ShardKey.ForNode(node))) shardOrder.Add(ShardKey.ForNode(node));

        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        var next = new State();
        foreach (var shard in shardOrder)
        {
            foreach (var node in nodeOrder.Where(node => ShardKey.ForNode(node) == shard))
            {
                DeriveIdentifier(node);
                if (HasIdentifier(node))
                {
                    if (identifiers.TryGetValue(node.Identifier, out var holder) && holder != node.Id)
                        throw Duplicate(node.Identifier);
                    identifiers[node.Identifier] = node.Id!;
                }
                next.Nodes[node.Id!] = new Owned<CodeNode>(shard, node);
            }
        }
        foreach (var edge in LastWins(edges, edge => edge.Id))
            next.Edges[edge.Id!] = new Owned<CodeEdge>(ShardKey.ForEdge(edge), edge);
        return next;
    }

    /// <summary>
    /// Scoped commit into the scope's shard K, following the rules one by one:
    /// 1. removed = K's nodes whose raw path is in the file set (never a null or empty path), or all
    ///    of K's nodes when the set is null;
    /// 2. each delta node in order takes its id into K; its identifier must not be held by a
    ///    different id in K as it stands so far, nor in any other shard as it stood before the commit;
    /// 3-4. K's edges sourced at a removed id go; those targeting a removed id that K no longer holds go;
    /// 5. delta edges join K, replacing any copy of the same id;
    /// 6. every other shard loses edges sourced at a removed id, edges targeting a removed id K no
    ///    longer holds, facts whose id the delta supplied, and every edge touching such a node.
    /// Nothing removed and nothing supplied leaves the state as it was.
    /// </summary>
    private static State Scoped(State state, List<CodeNode> nodes, List<CodeEdge> edges, CommitScope scope)
    {
        var key = ShardKey.From(scope.ParserId, scope.WorkspaceId);
        var files = scope.ReplacesFiles is null ? null : new HashSet<string>(scope.ReplacesFiles, PathComparer);
        var own = state.Nodes.Values.Where(owned => owned.Shard == key).Select(owned => owned.Fact).ToList();

        // Rule 1.
        var removed = own
            .Where(node => files is null || (!string.IsNullOrEmpty(node.FilePath) && files.Contains(node.FilePath)))
            .Select(node => node.Id!)
            .ToHashSet(StringComparer.Ordinal);
        var deltaNodes = nodes.Where(node => node.Id is not null).ToList();
        var deltaEdges = edges.Where(edge => edge.Id is not null).ToList();
        if (removed.Count == 0 && deltaNodes.Count == 0 && deltaEdges.Count == 0) return state;

        // Rule 2: K's nodes after the commit, built in order.
        var shardNodes = new Dictionary<string, CodeNode>(StringComparer.Ordinal);
        foreach (var node in own.Where(node => !removed.Contains(node.Id!)))
            shardNodes[node.Id!] = node;
        foreach (var node in deltaNodes)
        {
            DeriveIdentifier(node);
            if (HasIdentifier(node))
            {
                var heldInShard = shardNodes.Values.Any(other => other.Id != node.Id && other.Identifier == node.Identifier);
                var heldElsewhere = state.Nodes.Values.Any(other => other.Shard != key
                    && other.Fact.Id != node.Id && HasIdentifier(other.Fact) && other.Fact.Identifier == node.Identifier);
                if (heldInShard || heldElsewhere) throw Duplicate(node.Identifier);
            }
            shardNodes[node.Id!] = node;
        }

        var next = new State();
        var deltaNodeIds = deltaNodes.Select(node => node.Id!).ToHashSet(StringComparer.Ordinal);
        var deltaEdgeIds = deltaEdges.Select(edge => edge.Id!).ToHashSet(StringComparer.Ordinal);
        var goneForGood = removed.Where(id => !shardNodes.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);

        // Other shards' nodes, less those the delta took (rule 6).
        var evicted = new Dictionary<ShardKey, HashSet<string>>();
        foreach (var (id, owned) in state.Nodes)
        {
            if (owned.Shard == key) continue;
            if (deltaNodeIds.Contains(id))
            {
                if (!evicted.TryGetValue(owned.Shard, out var set)) evicted[owned.Shard] = set = new(StringComparer.Ordinal);
                set.Add(id);
                continue;
            }
            next.Nodes[id] = owned;
        }
        foreach (var node in shardNodes.Values) next.Nodes[node.Id!] = new Owned<CodeNode>(key, node);

        foreach (var (id, owned) in state.Edges)
        {
            var edge = owned.Fact;
            var sourceRemoved = edge.SourceId is not null && removed.Contains(edge.SourceId);
            var targetGone = edge.TargetId is not null && goneForGood.Contains(edge.TargetId);
            if (owned.Shard == key)
            {
                // Rules 3 and 4.
                if (sourceRemoved || targetGone) continue;
            }
            else
            {
                // Rule 6.
                if (sourceRemoved || targetGone || deltaEdgeIds.Contains(id)) continue;
                if (evicted.TryGetValue(owned.Shard, out var lost)
                    && ((edge.SourceId is not null && lost.Contains(edge.SourceId))
                        || (edge.TargetId is not null && lost.Contains(edge.TargetId))))
                {
                    continue;
                }
            }
            next.Edges[id] = owned;
        }

        // Rule 5.
        foreach (var edge in deltaEdges) next.Edges[edge.Id!] = new Owned<CodeEdge>(key, edge);
        return next;
    }

    // ---- Reads (published state, canonical order) ----

    private IEnumerable<ShardKey> Shards()
        => _published.Nodes.Values.Select(owned => owned.Shard)
            .Concat(_published.Edges.Values.Select(owned => owned.Shard))
            .Distinct()
            .Order(ShardKey.Comparer);

    private IEnumerable<CodeNode> CanonicalNodes(Func<CodeNode, bool> predicate)
        => Shards().SelectMany(shard => _published.Nodes.Values
            .Where(owned => owned.Shard == shard && predicate(owned.Fact))
            .Select(owned => owned.Fact)
            .OrderBy(node => node.Id, StringComparer.Ordinal));

    private IEnumerable<CodeEdge> CanonicalEdges(Func<CodeEdge, bool> predicate)
        => Shards().SelectMany(shard => _published.Edges.Values
            .Where(owned => owned.Shard == shard && predicate(owned.Fact))
            .Select(owned => owned.Fact)
            .OrderBy(edge => edge.Id, StringComparer.Ordinal));

    public int NodeCount => _published.Nodes.Count;

    public int EdgeCount => _published.Edges.Count;

    public CodeNode? GetNode(string id) => _published.Nodes.TryGetValue(id, out var owned) ? owned.Fact : null;

    public CodeEdge? GetEdge(string id) => _published.Edges.TryGetValue(id, out var owned) ? owned.Fact : null;

    /// <summary>A shard's identifier map is exactly { node.Identifier -> node.Id } over its nodes.</summary>
    public CodeNode? GetByIdentifier(string identifier)
        => CanonicalNodes(node => HasIdentifier(node) && node.Identifier == identifier).FirstOrDefault();

    public IReadOnlyList<CodeNode> AllNodes() => CanonicalNodes(_ => true).ToList();

    public IReadOnlyList<CodeEdge> AllEdges() => CanonicalEdges(_ => true).ToList();

    public IReadOnlyList<CodeEdge> EdgesBySource(string id, string? type = null)
        => CanonicalEdges(edge => edge.SourceId == id && (type is null || edge.Type == type)).ToList();

    public IReadOnlyList<CodeEdge> EdgesByTarget(string id, string? type = null)
        => CanonicalEdges(edge => edge.TargetId == id && (type is null || edge.Type == type)).ToList();

    public IReadOnlyList<CodeNode> FindByName(string name, string? type, bool exact)
        => CanonicalNodes(node => (exact
                ? string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)
                : node.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
            && (type is null || string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public IReadOnlyList<CodeNode> FindByFilePath(string path, string? type)
        => CanonicalNodes(node => FilePathMatcher.Matches(node.FilePath, path)
            && (string.IsNullOrEmpty(type) || string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public IReadOnlyList<string> FilePaths()
        => _published.Nodes.Values.Select(owned => owned.Fact.FilePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();

    public (IReadOnlyList<KeyValuePair<string, int>> NodesByType, IReadOnlyList<KeyValuePair<string, int>> EdgesByType) Statistics()
        => (Histogram(_published.Nodes.Values.Select(owned => owned.Fact.Type)),
            Histogram(_published.Edges.Values.Select(owned => owned.Fact.Type)));

    private static IReadOnlyList<KeyValuePair<string, int>> Histogram(IEnumerable<string?> types)
        => types.Where(type => !string.IsNullOrEmpty(type))
            .GroupBy(type => type!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => KeyValuePair.Create(group.Key, group.Count()))
            .ToList();

    // ---- Helpers ----

    private static List<T> LastWins<T>(List<T> items, Func<T, string?> idOf)
    {
        var order = new List<string>();
        var last = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = idOf(item);
            if (id is null) continue;
            if (!last.ContainsKey(id)) order.Add(id);
            last[id] = item;
        }
        return order.Select(id => last[id]).ToList();
    }

    private static bool HasIdentifier(CodeNode node)
        => !string.IsNullOrWhiteSpace(node.Identifier) && !string.IsNullOrEmpty(node.Id);

    /// <summary>A node without an identifier gets the legacy one: its id after the second colon.</summary>
    private static void DeriveIdentifier(CodeNode node)
    {
        if (!string.IsNullOrEmpty(node.Identifier) || string.IsNullOrEmpty(node.Id)) return;
        var parts = node.Id.Split(':', 3);
        node.Identifier = parts.Length == 3 && parts[2].Length > 0 ? parts[2] : node.Id;
    }

    private static InvalidDataException Duplicate(string identifier)
        => new($"Duplicate public symbol identifier '{identifier}'.");

    internal static CodeNode Clone(CodeNode node) => new()
    {
        Id = node.Id, Identifier = node.Identifier, Name = node.Name, Type = node.Type, FilePath = node.FilePath,
        StartLine = node.StartLine, EndLine = node.EndLine, StartCol = node.StartCol, EndCol = node.EndCol,
        Namespace = node.Namespace, Visibility = node.Visibility, Signature = node.Signature, Language = node.Language,
        ReturnType = node.ReturnType, Parameters = node.Parameters, Modifiers = node.Modifiers, Metrics = node.Metrics,
        Metadata = node.Metadata is null ? null : new Dictionary<string, string>(node.Metadata),
    };

    internal static CodeEdge Clone(CodeEdge edge) => new()
    {
        Id = edge.Id, SourceId = edge.SourceId, TargetId = edge.TargetId, Type = edge.Type,
        Metadata = edge.Metadata is null ? null : new Dictionary<string, string>(edge.Metadata),
    };
}
