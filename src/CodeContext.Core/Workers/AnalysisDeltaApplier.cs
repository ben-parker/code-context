using CodeContext.Core.Repositories;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>
/// Buffers streamed <see cref="AnalysisDelta"/> chunks and atomically commits the
/// complete request through <see cref="IGenerationalGraphStore"/>. Readers therefore
/// never observe a partially streamed generation. Older generations and chunks for a
/// superseded request are discarded per parser/workspace.
/// </summary>
public sealed class AnalysisDeltaApplier : IAnalysisDeltaSink
{
    private const string ParserIdKey = "parserId";
    private const string WorkspaceIdKey = "workspaceId";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class PendingDelta(AnalysisDelta first)
    {
        public string ParserId { get; } = first.ParserId;
        public string ParserVersion { get; } = first.ParserVersion;
        public string WorkspaceId { get; } = first.WorkspaceId;
        public long Generation { get; } = first.Generation;
        public long RequestId { get; } = first.RequestId;
        public bool ReplacesWorkspace { get; private set; } = first.ReplacesWorkspace;
        public HashSet<string> ReplacesFiles { get; } = new(first.ReplacesFiles, PathComparer);
        public List<ProtocolNode> Nodes { get; } = [];
        public List<ProtocolEdge> Edges { get; } = [];
        public IReadOnlyList<ProjectCoverage>? Coverage { get; private set; }

        /// <summary>The first chunk's fingerprints, which is where a worker sends them; later chunks' are ignored.</summary>
        public IReadOnlyList<UnitFingerprint>? Fingerprints { get; } = first.Fingerprints;

        public void Append(AnalysisDelta delta)
        {
            if (!string.Equals(ParserVersion, delta.ParserVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Parser version changed within request {RequestId}: '{ParserVersion}' to '{delta.ParserVersion}'.");
            }

            ReplacesWorkspace |= delta.ReplacesWorkspace;
            ReplacesFiles.UnionWith(delta.ReplacesFiles);
            Coverage = delta.Coverage ?? Coverage;
            Nodes.AddRange(delta.Nodes);
            Edges.AddRange(delta.Edges);
        }
    }

    private readonly IGenerationalGraphStore _store;
    private readonly ILogger<AnalysisDeltaApplier> _logger;
    private readonly IAnalysisCoverageRegistry? _coverage;
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _lastCommittedGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _highestSeenGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId, long Generation, long RequestId), PendingDelta> _pending = new();

    public AnalysisDeltaApplier(
        IGenerationalGraphStore store,
        ILogger<AnalysisDeltaApplier> logger,
        IAnalysisCoverageRegistry? coverage = null)
    {
        _store = store;
        _logger = logger;
        _coverage = coverage;
    }

    /// <summary>
    /// Accepts one chunk. Returns false only when it is stale or belongs to a different
    /// request that reused the same workspace generation. A true result means the chunk
    /// was buffered, or (for the final chunk) that the complete generation committed.
    /// </summary>
    public Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default) => ApplyAsync(delta, null, ct);

    Task<bool> IAnalysisDeltaSink.ApplyAsync(AnalysisDelta delta, DeltaOrigin? origin, CancellationToken ct) => ApplyAsync(delta, origin, ct);

    /// <summary>
    /// <see cref="ApplyAsync(AnalysisDelta, CancellationToken)"/>, recording <paramref name="origin"/> (the final
    /// chunk's) as the commit's lineage: an establishing one when the request replaced the workspace or carried
    /// a baseline, otherwise one that only extends the shard's recorded lineage (see the store's rules).
    /// </summary>
    internal async Task<bool> ApplyAsync(AnalysisDelta delta, DeltaOrigin? origin, CancellationToken ct)
    {
        await _commitLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionKey = (delta.ParserId, delta.WorkspaceId);
            if (_lastCommittedGeneration.TryGetValue(sessionKey, out var committedGeneration)
                && delta.Generation <= committedGeneration)
            {
                LogStale(delta, committedGeneration);
                return false;
            }

            if (_highestSeenGeneration.TryGetValue(sessionKey, out var highestSeen)
                && delta.Generation < highestSeen)
            {
                LogStale(delta, highestSeen);
                return false;
            }

            if (!_highestSeenGeneration.TryGetValue(sessionKey, out highestSeen)
                || delta.Generation > highestSeen)
            {
                _highestSeenGeneration[sessionKey] = delta.Generation;
                RemovePending(sessionKey, generation => generation < delta.Generation);
            }

            var conflictingRequest = _pending.Keys.Any(key =>
                key.ParserId == delta.ParserId
                && key.WorkspaceId == delta.WorkspaceId
                && key.Generation == delta.Generation
                && key.RequestId != delta.RequestId);
            if (conflictingRequest)
            {
                _logger.LogWarning(
                    "[{ParserId}] discarded delta for request {RequestId}: generation {Generation} is already owned by another request in workspace {WorkspaceId}.",
                    delta.ParserId, delta.RequestId, delta.Generation, delta.WorkspaceId);
                return false;
            }

            var pendingKey = (delta.ParserId, delta.WorkspaceId, delta.Generation, delta.RequestId);
            if (!_pending.TryGetValue(pendingKey, out var pending))
            {
                pending = new PendingDelta(delta);
                _pending.Add(pendingKey, pending);
            }
            pending.Append(delta);

            if (!delta.IsLastForRequest)
            {
                return true;
            }

            _pending.Remove(pendingKey);
            // One pool per request: facts of the commit with equal metadata share one immutable instance.
            var pool = new FactMetadataPool();
            var nodes = pending.Nodes.Select(n => ToCodeNode(n, pending, pool)).ToList();
            var edges = pending.Edges.Select(e => ToCodeEdge(e, pending, pool)).ToList();

            // The scope names the (parser, workspace) shard this delta replaces (optionally narrowed
            // to the listed files). The store routes and partitions by the ownership metadata that
            // BuildOwnedMetadata stamped onto every node/edge — no scope predicate needed.
            // The scope also carries what produced the shard (parser version, coverage, fingerprints, the
            // worker process and request), which the store records in the same commit, staged and rolled back
            // with it.
            var scope = new CommitScope(
                pending.ParserId,
                pending.WorkspaceId,
                pending.ReplacesWorkspace ? null : pending.ReplacesFiles.ToList())
            {
                Annotation = new ShardAnnotation(pending.ParserVersion, pending.Coverage)
                {
                    Fingerprints = pending.Fingerprints,
                    Lineage = origin is null
                        ? null
                        : new ShardLineage(origin.Incarnation, origin.Request,
                            Establishes: pending.ReplacesWorkspace || origin.BaselineOffered, BaselineStateId: origin.OfferedStateId),
                },
            };

            var committed = false;
            for (var attempt = 0; attempt < 5 && !committed; attempt++)
            {
                var storeGeneration = _store.LastCommittedGeneration + 1;
                committed = await _store.TryCommitGenerationAsync(
                    storeGeneration, nodes, edges, scope, ct).ConfigureAwait(false);
            }
            if (!committed)
            {
                _logger.LogWarning(
                    "[{ParserId}] could not commit generation {Generation} for workspace {WorkspaceId}: another writer kept winning the store-generation race.",
                    pending.ParserId, pending.Generation, pending.WorkspaceId);
                return false;
            }

            _lastCommittedGeneration[sessionKey] = pending.Generation;
            if (pending.Coverage is { } coverage)
            {
                _coverage?.Report(pending.ParserId, pending.WorkspaceId, coverage);
            }
            RemovePending(sessionKey, generation => generation <= pending.Generation);
            _logger.LogDebug(
                "[{ParserId}] atomically committed generation {Generation} for workspace {WorkspaceId}: {Nodes} nodes, {Edges} edges.",
                pending.ParserId, pending.Generation, pending.WorkspaceId, nodes.Count, edges.Count);
            return true;
        }
        finally
        {
            _commitLock.Release();
        }
    }

    private void RemovePending((string ParserId, string WorkspaceId) sessionKey, Func<long, bool> predicate)
    {
        foreach (var key in _pending.Keys.Where(key =>
                     key.ParserId == sessionKey.ParserId
                     && key.WorkspaceId == sessionKey.WorkspaceId
                     && predicate(key.Generation)).ToList())
        {
            _pending.Remove(key);
        }
    }

    private void LogStale(AnalysisDelta delta, long newestGeneration)
        => _logger.LogWarning(
            "[{ParserId}] discarded stale delta: generation {Stale} <= newest {Newest} for workspace {WorkspaceId}.",
            delta.ParserId, delta.Generation, newestGeneration, delta.WorkspaceId);

    // The fact's metadata: the source's entries in their order, with the two ownership keys set to the
    // delta's values (in place where the source has them, appended in this order where it does not), so
    // the ownership keys always win over a same-named source key. The source is only read; the result is
    // the pool's shared, immutable instance for that content.
    private static FactMetadata BuildOwnedMetadata(
        IReadOnlyDictionary<string, string>? source, PendingDelta delta, FactMetadataPool pool)
    {
        var count = source?.Count ?? 0;
        var pairs = new List<string>(2 * count + 4);
        bool hasParser = false, hasWorkspace = false;
        if (source is not null)
        {
            foreach (var (key, value) in source)
            {
                pairs.Add(key);
                if (key == ParserIdKey)
                {
                    hasParser = true;
                    pairs.Add(delta.ParserId);
                }
                else if (key == WorkspaceIdKey)
                {
                    hasWorkspace = true;
                    pairs.Add(delta.WorkspaceId);
                }
                else
                {
                    pairs.Add(value);
                }
            }
        }
        if (!hasParser)
        {
            pairs.Add(ParserIdKey);
            pairs.Add(delta.ParserId);
        }
        if (!hasWorkspace)
        {
            pairs.Add(WorkspaceIdKey);
            pairs.Add(delta.WorkspaceId);
        }
        return pool.Get(pairs.ToArray());
    }

    private static CodeNode ToCodeNode(ProtocolNode node, PendingDelta delta, FactMetadataPool pool)
    {
        var metadata = BuildOwnedMetadata(node.Metadata, delta, pool);

        return new CodeNode
        {
            Id = node.Id,
            Identifier = node.Identifier,
            Name = node.Name,
            Type = node.Kind,
            Language = node.Language,
            FilePath = node.FilePath,
            StartLine = node.StartLine,
            EndLine = node.EndLine,
            StartCol = node.StartColumn,
            EndCol = node.EndColumn,
            Namespace = node.Namespace,
            Visibility = node.Visibility,
            Signature = node.Signature,
            ReturnType = node.ReturnType,
            Parameters = node.Parameters,
            Modifiers = node.Modifiers,
            Metadata = metadata,
        };
    }

    private static CodeEdge ToCodeEdge(ProtocolEdge edge, PendingDelta delta, FactMetadataPool pool)
    {
        var metadata = BuildOwnedMetadata(edge.Metadata, delta, pool);

        return new CodeEdge
        {
            Id = edge.Id,
            SourceId = edge.SourceId,
            TargetId = edge.TargetId,
            Type = edge.Kind,
            Metadata = metadata,
        };
    }
}
