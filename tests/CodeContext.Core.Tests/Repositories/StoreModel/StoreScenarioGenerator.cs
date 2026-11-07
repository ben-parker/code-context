using System;
using System.Collections.Generic;
using System.Linq;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>One store operation, applied identically to a store under test and to the model.</summary>
internal abstract record StoreOperation
{
    public abstract string Describe();

    public sealed record Commit(long Generation, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges, CommitScope? Scope)
        : StoreOperation
    {
        public override string Describe()
            => $"commit g{Generation} scope={(Scope is null ? "null" : $"{Scope.ParserId}/{Scope.WorkspaceId} files=" + (Scope.ReplacesFiles is null ? "all" : $"[{string.Join(",", Scope.ReplacesFiles.Select(path => path ?? "<null>"))}]"))} "
                + $"nodes=[{DescribeNodes(Nodes)}] edges=[{DescribeEdges(Edges)}]";
    }

    public sealed record ReplaceAll(IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges) : StoreOperation
    {
        public override string Describe() => $"replace-all nodes=[{DescribeNodes(Nodes)}] edges=[{DescribeEdges(Edges)}]";
    }

    public sealed record Reset : StoreOperation
    {
        public override string Describe() => "reset";
    }

    public sealed record Prune(IReadOnlyList<string> PresentFiles) : StoreOperation
    {
        public override string Describe() => $"prune keep=[{string.Join(",", PresentFiles)}]";
    }

    public sealed record BeginReconciliation : StoreOperation
    {
        public override string Describe() => "begin";
    }

    public sealed record CommitReconciliation : StoreOperation
    {
        public override string Describe() => "commit-reconciliation";
    }

    public sealed record RollbackReconciliation : StoreOperation
    {
        public override string Describe() => "rollback";
    }

    public sealed record UpsertNode(CodeNode Node) : StoreOperation
    {
        public override string Describe() => $"upsert-node {DescribeNodes([Node])}";
    }

    public sealed record RemoveNode(string Id) : StoreOperation
    {
        public override string Describe() => $"remove-node {Id}";
    }

    public sealed record UpsertEdge(CodeEdge Edge) : StoreOperation
    {
        public override string Describe() => $"upsert-edge {DescribeEdges([Edge])}";
    }

    public sealed record RemoveEdgesTouchingNode(string? NodeId) : StoreOperation
    {
        public override string Describe() => $"remove-edges-touching {NodeId ?? "<null>"}";
    }

    /// <summary>Swaps the live shard for an equivalent one made by the runner's shard factory.</summary>
    public sealed record SwapShard(ShardKey Shard) : StoreOperation
    {
        public override string Describe() => $"swap-shard {Shard.ParserId}/{Shard.WorkspaceId}";
    }

    /// <summary>
    /// Applies to a store; returns the result as text, or the exception's type and message.
    /// <paramref name="equivalentShard"/> makes the replacement for <see cref="SwapShard"/>: a shard
    /// that must answer every read and commit exactly as the one it replaces.
    /// </summary>
    public string ApplyTo(InMemoryDatabase database, Func<IGraphShard, IGraphShard> equivalentShard) => Capture(() => this switch
    {
        Commit c => database.TryCommitGeneration(c.Generation, c.Nodes.Select(ReferenceGraphModel.Clone).ToList(),
            c.Edges.Select(ReferenceGraphModel.Clone).ToList(), c.Scope).ToString(),
        ReplaceAll r => Run(() => database.ReplaceAll(r.Nodes.Select(ReferenceGraphModel.Clone).ToList(),
            r.Edges.Select(ReferenceGraphModel.Clone).ToList())),
        Reset => Run(database.Reset),
        Prune p => database.PruneFilesNotPresent(p.PresentFiles).ToString(),
        BeginReconciliation => Run(database.BeginReconciliation),
        CommitReconciliation => Run(database.CommitReconciliation),
        RollbackReconciliation => Run(database.RollbackReconciliation),
        UpsertNode u => Run(() => database.UpsertNode(ReferenceGraphModel.Clone(u.Node))),
        RemoveNode r => database.RemoveNode(r.Id).ToString(),
        UpsertEdge u => Run(() => database.UpsertEdge(ReferenceGraphModel.Clone(u.Edge))),
        RemoveEdgesTouchingNode r => database.RemoveEdgesTouchingNode(r.NodeId!).ToString(),
        SwapShard s => database.GetShardState(s.Shard) is IGraphShard current
            ? database.TryReplaceShard(s.Shard, current, equivalentShard(current)).ToString()
            : false.ToString(),
        _ => throw new NotSupportedException(GetType().Name),
    });

    public string ApplyTo(ReferenceGraphModel model) => Capture(() => this switch
    {
        Commit c => model.TryCommitGeneration(c.Generation, c.Nodes, c.Edges, c.Scope).ToString(),
        ReplaceAll r => Run(() => model.ReplaceAll(r.Nodes, r.Edges)),
        Reset => Run(model.Reset),
        Prune p => model.PruneFilesNotPresent(p.PresentFiles).ToString(),
        BeginReconciliation => Run(model.BeginReconciliation),
        CommitReconciliation => Run(model.CommitReconciliation),
        RollbackReconciliation => Run(model.RollbackReconciliation),
        UpsertNode u => Run(() => model.UpsertNode(u.Node)),
        RemoveNode r => model.RemoveNode(r.Id).ToString(),
        UpsertEdge u => Run(() => model.UpsertEdge(u.Edge)),
        RemoveEdgesTouchingNode r => model.RemoveEdgesTouchingNode(r.NodeId).ToString(),
        SwapShard s => model.TryReplaceShard(s.Shard).ToString(),
        _ => throw new NotSupportedException(GetType().Name),
    });

    private static string DescribeNodes(IEnumerable<CodeNode> nodes)
        => string.Join(",", nodes.Select(node => $"{node.Id ?? "<null>"}@{node.FilePath ?? "<null>"}#{node.Identifier}"
            + $"[{node.Metadata?.GetValueOrDefault("parserId")}/{node.Metadata?.GetValueOrDefault("workspaceId")}]"));

    private static string DescribeEdges(IEnumerable<CodeEdge> edges)
        => string.Join(",", edges.Select(edge => edge.Id ?? $"<null>:{edge.SourceId}>{edge.TargetId}"));

    private static string Run(Action action)
    {
        action();
        return "ok";
    }

    private static string Capture(Func<string> apply)
    {
        try
        {
            return apply();
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.XunitException)
        {
            return $"{exception.GetType().FullName}: {exception.Message}";
        }
    }
}

/// <summary>
/// The fixed vocabulary of a scenario: shards, ids, identifiers, files, names and types. Every
/// read the matrix performs is drawn from it, so a fact that exists can always be asked about.
/// </summary>
internal sealed class StoreUniverse
{
    public static readonly ShardKey[] OwnedShards =
    [
        new("csharp", "w1"), new("csharp", "w2"), new("typescript", "w1"),
    ];

    public static readonly string[] Names = ["Alpha", "AlphaBeta", "alpha", "Gamma", "Delta", "Widget", "WidgetTests"];

    public static readonly string[] NodeTypes = ["Class", "Method", "Property", "class"];

    public static readonly string[] EdgeTypes = ["CALLS", "REFERENCES", "IMPLEMENTS", "HAS_METHOD"];

    /// <summary>Virtual files of library symbols: never on disk, never a rooted path.</summary>
    public static readonly string[] ExternalFiles = ["external:System.Runtime/System.String", "external:Newtonsoft.Json/Linq.JObject"];

    /// <summary>A path that is not empty but blank: listed as a file path, never matched by a path query.</summary>
    public const string BlankPath = "   ";

    public const int LocalIdsPerShard = 8;
    public const int SharedIds = 3;
    public const int FilesPerShard = 4;

    /// <summary>Node ids local to a shard, carrying its prefix.</summary>
    public static string LocalId(ShardKey shard, int index) => $"{shard.ParserId}:{shard.WorkspaceId}:n{index}";

    /// <summary>Ids any shard may emit, so a delta can reclaim an id another shard holds.</summary>
    public static string SharedId(int index) => $"shared:{index}";

    public static string FilePath(ShardKey shard, int index)
        => $"/repo/{shard.ParserId}/{shard.WorkspaceId}/{(index % 2 == 0 ? "src" : "tests")}/File{index}.cs";

    public static IEnumerable<string> AllNodeIds()
        => OwnedShards.SelectMany(shard => Enumerable.Range(0, LocalIdsPerShard).Select(i => LocalId(shard, i)))
            .Concat(Enumerable.Range(0, SharedIds).Select(SharedId))
            .Append("default:only")
            .Append("missing:node");

    public static IEnumerable<string> AllFiles()
        => OwnedShards.SelectMany(shard => Enumerable.Range(0, FilesPerShard).Select(i => FilePath(shard, i)))
            .Append("/repo/shared/Common.cs")
            .Append("/repo/shared/common.cs")
            .Concat(ExternalFiles)
            .Append(BlankPath);

    /// <summary>Requests the path matrix asks: every file, case variants, relative suffixes, a miss.</summary>
    public static IEnumerable<string> PathQueries()
        => AllFiles()
            .Concat(AllFiles().Select(path => path.ToUpperInvariant()))
            .Concat(AllFiles().Select(path => path.Replace('/', '\\')))
            .Concat(["File0.cs", "src/File0.cs", "tests/File1.cs", "w1/src/File2.cs", "Common.cs", "COMMON.CS", "Nope.cs", "  ",
                "System.String", "Linq.JObject", "external:", "external"])
            .Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> NameQueries() => Names.Concat(["ALPHA", "lph", "Test", "", "zzz"]);
}

/// <summary>
/// A seeded generator of store operations: scoped and whole-shard commits, whole-graph
/// replacements (by commit and by <c>ReplaceAll</c>), <c>Reset</c>, shadowing ids (a delta
/// re-supplying a carried id with a new identifier), dangling and cross-shard edges, ids reclaimed
/// from another shard, duplicate identifiers, empty commits, stale generations, null ids and null
/// endpoints, facts whose ownership metadata names another shard than the commit, blank and
/// external file paths, null, empty and case-variant file-set entries, prune (with case-variant
/// keep sets), reconciliation begin, commit and rollback (also out of turn), the legacy in-place
/// mutations, and swaps of a live shard for an equivalent one.
///
/// The legacy mutations are generated only while no reconciliation is staged: during one they write
/// the live map, and whether that survives the reconciliation's commit depends on whether the staged
/// map still shares the shard object. The store's named tests cover that case instead.
///
/// Deterministic for a seed: the same seed and the same model state give the same operation.
///
/// With <c>persistableFacts</c>, every fact is one a snapshot can persist: no fact is made for
/// <see cref="ShardKey.Default"/> (its operations go to an owned shard), ids carry the shard's prefix,
/// edges have a prefixed source, and ownership metadata names the shard. Cross-shard and dangling
/// targets, shadowing, stale generations, prunes and reconciliations are generated as before. The
/// default mode draws exactly what it drew before this option existed (the persistable mode applies
/// its overrides to what was drawn), so its sequences do not change.
/// </summary>
internal sealed class StoreScenarioGenerator
{
    private readonly Random _random;
    private readonly bool _persistableFacts;
    private readonly SortedSet<string> _issuedIdentifiers = new(StringComparer.Ordinal);
    private int _version;

    public StoreScenarioGenerator(int seed, bool persistableFacts = false)
    {
        _random = new Random(seed);
        _persistableFacts = persistableFacts;
    }

    /// <summary>Every identifier any generated node has carried so far.</summary>
    public IReadOnlyCollection<string> IssuedIdentifiers => _issuedIdentifiers;

    /// <summary>The next operation, chosen against the model's current (staged or published) state.</summary>
    public StoreOperation Next(ReferenceGraphModel model)
    {
        var roll = _random.Next(100);
        if (roll < 4) return new StoreOperation.BeginReconciliation();
        if (roll < 7) return new StoreOperation.CommitReconciliation();
        if (roll < 9) return new StoreOperation.RollbackReconciliation();
        if (roll < 14) return new StoreOperation.Prune(PickKeepFiles());
        if (roll < 18) return FullReplacement(model, byCommit: true);
        if (roll < 20) return FullReplacement(model, byCommit: false);
        if (roll < 21) return new StoreOperation.Reset();
        if (roll < 24 && Swap(model) is { } swap) return swap;
        if (roll < 34 && !model.ReconciliationActive) return LegacyMutation(model);
        return ScopedCommit(model, _random.Next(34, 100));
    }

    private StoreOperation ScopedCommit(ReferenceGraphModel model, int roll)
    {
        var shard = _random.Next(10) == 0 ? DefaultOrOwned() : Pick(StoreUniverse.OwnedShards);
        var generation = NextGeneration(model);

        // A commit with an empty file set and no facts: must leave the shard reference alone.
        if (roll < 39)
            return new StoreOperation.Commit(generation, [], [], new CommitScope(shard.ParserId, shard.WorkspaceId, []));

        IReadOnlyCollection<string>? files = roll < 49 ? null : ShardFiles(shard);
        var nodes = new List<CodeNode>();
        var count = _random.Next(0, 6);
        for (var i = 0; i < count; i++)
        {
            var real = files?.Where(path => !string.IsNullOrEmpty(path)).ToList();
            var path = real is { Count: > 0 } && _random.Next(4) != 0
                ? CaseVariant(real[_random.Next(real.Count)])
                : PickNodePath(shard);
            var id = _random.Next(25) == 0 ? null : PickNodeId(shard, model);
            nodes.Add(MakeNode(id, path, OwnerFor(shard)));
        }

        if (nodes.Count > 0 && _random.Next(8) == 0) ReuseIdentifier(model, nodes[_random.Next(nodes.Count)]);

        var edges = MakeEdges(shard, _random.Next(0, 7));
        return new StoreOperation.Commit(generation, nodes, edges, new CommitScope(shard.ParserId, shard.WorkspaceId, files));
    }

    private StoreOperation FullReplacement(ReferenceGraphModel model, bool byCommit)
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        foreach (var shard in _persistableFacts ? StoreUniverse.OwnedShards : StoreUniverse.OwnedShards.Append(ShardKey.Default))
        {
            for (var i = _random.Next(0, 5); i > 0; i--)
                nodes.Add(MakeNode(_random.Next(25) == 0 ? null : PickNodeId(shard, model), PickNodePath(shard), shard));
            edges.AddRange(MakeEdges(shard, _random.Next(0, 5)));
        }
        if (nodes.Count > 1 && _random.Next(6) == 0)
            nodes[_random.Next(nodes.Count)].Identifier = nodes[_random.Next(nodes.Count)].Identifier;
        Shuffle(nodes);
        Shuffle(edges);
        return byCommit
            ? new StoreOperation.Commit(NextGeneration(model), nodes, edges, Scope: null)
            : new StoreOperation.ReplaceAll(nodes, edges);
    }

    /// <summary>
    /// A swap of a shard that exists for an equivalent one (refused while a reconciliation is staged),
    /// or null when no published shard holds a fact (the store may keep an empty shard the model does
    /// not track, so an empty key is never asked about).
    /// </summary>
    private StoreOperation? Swap(ReferenceGraphModel model)
    {
        if (model.ReconciliationActive) return new StoreOperation.SwapShard(Pick(StoreUniverse.OwnedShards));
        var live = model.Published.Nodes.Values.Select(owned => owned.Shard)
            .Concat(model.Published.Edges.Values.Select(owned => owned.Shard))
            .Distinct()
            .Order(ShardKey.Comparer)
            .ToList();
        return live.Count == 0 ? null : new StoreOperation.SwapShard(live[_random.Next(live.Count)]);
    }

    private StoreOperation LegacyMutation(ReferenceGraphModel model)
    {
        var shard = _random.Next(6) == 0 ? DefaultOrOwned() : Pick(StoreUniverse.OwnedShards);
        var roll = _random.Next(20);
        if (roll < 8)
        {
            var id = _random.Next(12) == 0 ? null : PickNodeId(shard, model);
            var node = MakeNode(id, PickNodePath(shard), OwnerFor(shard));
            if (_random.Next(8) == 0) ReuseIdentifier(model, node);
            return new StoreOperation.UpsertNode(node);
        }
        if (roll < 12)
        {
            var held = model.Published.Nodes.Keys.Order(StringComparer.Ordinal).ToList();
            return new StoreOperation.RemoveNode(held.Count > 0 && _random.Next(4) != 0 ? Pick(held) : Pick(StoreUniverse.AllNodeIds().ToList()));
        }
        if (roll < 17)
        {
            var edge = MakeEdges(shard, 1)[0];
            if (_random.Next(10) == 0) edge.Id = null;
            return new StoreOperation.UpsertEdge(edge);
        }
        if (_random.Next(8) == 0) return new StoreOperation.RemoveEdgesTouchingNode(null);
        var endpoints = model.Published.Edges.Values
            .SelectMany(owned => new[] { owned.Fact.SourceId, owned.Fact.TargetId })
            .Where(id => id is not null).Select(id => id!).Distinct().Order(StringComparer.Ordinal).ToList();
        return new StoreOperation.RemoveEdgesTouchingNode(
            endpoints.Count > 0 && _random.Next(4) != 0 ? Pick(endpoints) : Pick(StoreUniverse.AllNodeIds().ToList()));
    }

    // Duplicate identifier: reuse an identifier some other node currently holds.
    private void ReuseIdentifier(ReferenceGraphModel model, CodeNode node)
    {
        var holders = model.Current.Nodes.Values.Select(owned => owned.Fact)
            .Where(other => !string.IsNullOrWhiteSpace(other.Identifier)).OrderBy(other => other.Id, StringComparer.Ordinal).ToList();
        if (holders.Count > 0) node.Identifier = holders[_random.Next(holders.Count)].Identifier;
    }

    private long NextGeneration(ReferenceGraphModel model)
    {
        var last = model.LastCommittedGeneration;
        return _random.Next(12) switch
        {
            0 => last,                       // stale: equal
            1 => Math.Max(0, last - 1),      // stale: older
            2 => last + 3,                   // a gap is allowed
            _ => last + 1,
        };
    }

    // The ownership a node of a commit to this shard carries: mostly the shard's own, sometimes
    // another shard's or none (a scoped commit takes the node into its scope shard regardless; a
    // whole-graph replacement and the legacy upsert route it by this metadata).
    private ShardKey OwnerFor(ShardKey shard)
    {
        var owner = _random.Next(10) switch
        {
            0 => Pick(StoreUniverse.OwnedShards),
            1 => ShardKey.Default,
            _ => shard,
        };
        return Persistable(shard) ? shard : owner;
    }

    private bool Persistable(ShardKey shard) => _persistableFacts && shard != ShardKey.Default;

    // In the persistable mode no fact is made for the un-owned shard, which is never persisted.
    private ShardKey DefaultOrOwned() => _persistableFacts ? Pick(StoreUniverse.OwnedShards) : ShardKey.Default;

    private CodeNode MakeNode(string? id, string? path, ShardKey owner)
    {
        var version = ++_version;
        var identifierRoll = _random.Next(20);
        var name = Pick(StoreUniverse.Names);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (owner != ShardKey.Default)
        {
            metadata["parserId"] = owner.ParserId;
            metadata["workspaceId"] = owner.WorkspaceId;
        }
        var external = path?.StartsWith("external:", StringComparison.Ordinal) == true ? _random.Next(4) != 0 : _random.Next(12) == 0;
        if (external) metadata["external"] = "true";
        var node = new CodeNode
        {
            Id = id,
            // Mostly a stable identifier per id; sometimes a new one for the same id (the shadowing
            // case), none (legacy derivation) or blank (never registered).
            Identifier = identifierRoll switch
            {
                0 => string.Empty,
                1 => "   ",
                < 6 => $"I:{id}#{version}",
                _ => $"I:{id}",
            },
            Name = name,
            Type = _random.Next(10) == 0 ? null : Pick(StoreUniverse.NodeTypes),
            FilePath = path,
            StartLine = _random.Next(100),
            Signature = $"v{version}",
            Metadata = metadata,
        };
        _issuedIdentifiers.Add(node.Identifier);
        return node;
    }

    private List<CodeEdge> MakeEdges(ShardKey shard, int count)
    {
        var all = StoreUniverse.AllNodeIds().ToList();
        var edges = new List<CodeEdge>();
        for (var i = 0; i < count; i++)
        {
            var source = _random.Next(15) == 0 ? null : PickWeighted(shard, all);
            var target = _random.Next(15) == 0 ? null : PickWeighted(shard, all);
            var type = Pick(StoreUniverse.EdgeTypes);
            var idRoll = _random.Next(25);
            var id = idRoll switch
            {
                0 => null,
                < 3 => $"shared-edge:{_random.Next(3)}",
                _ => $"{shard.ParserId}:{shard.WorkspaceId}:e:{source}>{target}:{type}",
            };
            if (Persistable(shard))
            {
                if (source is null || !source.StartsWith($"{shard.ParserId}:{shard.WorkspaceId}:", StringComparison.Ordinal))
                    source = StoreUniverse.LocalId(shard, (source?.Length ?? 0) % StoreUniverse.LocalIdsPerShard);
                id = $"{shard.ParserId}:{shard.WorkspaceId}:e:{source}>{target}:{type}";
            }
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["line"] = _random.Next(50).ToString() };
            var owner = OwnerFor(shard);
            if (owner != ShardKey.Default)
            {
                metadata["parserId"] = owner.ParserId;
                metadata["workspaceId"] = owner.WorkspaceId;
            }
            edges.Add(new CodeEdge { Id = id, SourceId = source, TargetId = target, Type = type, Metadata = metadata });
        }
        return edges;
    }

    // Mostly the shard's own ids (so edges usually resolve), sometimes any id (cross-shard or dangling).
    private string PickWeighted(ShardKey shard, List<string> all)
        => _random.Next(3) == 0 ? Pick(all) : StoreUniverse.LocalId(shard, _random.Next(StoreUniverse.LocalIdsPerShard));

    private string PickNodeId(ShardKey shard, ReferenceGraphModel model)
    {
        var roll = _random.Next(10);
        if (roll == 0)
        {
            var shared = _random.Next(StoreUniverse.SharedIds);
            return Persistable(shard) ? StoreUniverse.LocalId(shard, shared) : StoreUniverse.SharedId(shared);
        }
        if (shard == ShardKey.Default) return roll < 5 ? "default:only" : Pick(StoreUniverse.AllNodeIds().ToList());

        // Prefer an id this shard holds now, so deltas often shadow carried nodes.
        var held = model.Current.Nodes.Values.Where(owned => owned.Shard == shard)
            .Select(owned => owned.Fact.Id!).Order(StringComparer.Ordinal).ToList();
        return roll < 5 && held.Count > 0
            ? held[_random.Next(held.Count)]
            : StoreUniverse.LocalId(shard, _random.Next(StoreUniverse.LocalIdsPerShard));
    }

    private string? PickNodePath(ShardKey shard)
    {
        var roll = _random.Next(24);
        if (roll == 0) return null;
        if (roll == 1) return string.Empty;
        if (roll == 2) return "/repo/shared/Common.cs";
        if (roll == 3) return "/repo/shared/common.cs";
        if (roll == 4) return StoreUniverse.BlankPath;
        if (roll == 5) return Pick(StoreUniverse.ExternalFiles);
        var owner = shard == ShardKey.Default ? Pick(StoreUniverse.OwnedShards) : shard;
        return StoreUniverse.FilePath(owner, _random.Next(StoreUniverse.FilesPerShard));
    }

    private List<string> ShardFiles(ShardKey shard)
    {
        var owner = shard == ShardKey.Default ? Pick(StoreUniverse.OwnedShards) : shard;
        var files = Enumerable.Range(0, StoreUniverse.FilesPerShard)
            .Where(_ => _random.Next(3) == 0)
            .Select(i => CaseVariant(StoreUniverse.FilePath(owner, i)))
            .ToList();
        if (_random.Next(6) == 0) files.Add("/repo/shared/Common.cs");
        if (_random.Next(10) == 0) files.Add("/repo/never/Indexed.cs");
        if (_random.Next(10) == 0) files.Add(StoreUniverse.BlankPath);
        if (_random.Next(10) == 0) files.Add(Pick(StoreUniverse.ExternalFiles));
        // A file set may carry null and empty entries; neither ever names a node's file.
        if (_random.Next(10) == 0) files.Add(null!);
        if (_random.Next(10) == 0) files.Add(string.Empty);
        return files;
    }

    // An occasional case variant exercises the platform path comparer of the file set.
    private string CaseVariant(string path) => _random.Next(8) == 0 ? path.ToUpperInvariant() : path;

    // A keep set for prune: most files, some in another case than any node uses.
    private List<string> PickKeepFiles()
        => StoreUniverse.AllFiles()
            .Where(_ => _random.Next(4) != 0)
            .Select(path => _random.Next(5) == 0 ? path.ToUpperInvariant() : path)
            .ToList();

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    private void Shuffle<T>(List<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
