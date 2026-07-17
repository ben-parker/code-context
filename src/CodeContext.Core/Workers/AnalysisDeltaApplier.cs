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

        public void Append(AnalysisDelta delta)
        {
            if (!string.Equals(ParserVersion, delta.ParserVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Parser version changed within request {RequestId}: '{ParserVersion}' to '{delta.ParserVersion}'.");
            }

            ReplacesWorkspace |= delta.ReplacesWorkspace;
            ReplacesFiles.UnionWith(delta.ReplacesFiles);
            Nodes.AddRange(delta.Nodes);
            Edges.AddRange(delta.Edges);
        }
    }

    private readonly IGenerationalGraphStore _store;
    private readonly ILogger<AnalysisDeltaApplier> _logger;
    private readonly SemaphoreSlim _commitLock = new(1, 1);
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _lastCommittedGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _highestSeenGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId, long Generation, long RequestId), PendingDelta> _pending = new();

    public AnalysisDeltaApplier(IGenerationalGraphStore store, ILogger<AnalysisDeltaApplier> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Accepts one chunk. Returns false only when it is stale or belongs to a different
    /// request that reused the same workspace generation. A true result means the chunk
    /// was buffered, or (for the final chunk) that the complete generation committed.
    /// </summary>
    public async Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default)
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
            var nodes = pending.Nodes.Select(n => ToCodeNode(n, pending)).ToList();
            var edges = pending.Edges.Select(e => ToCodeEdge(e, pending)).ToList();
            var replacesFiles = pending.ReplacesWorkspace ? null : pending.ReplacesFiles;

            bool InScope(CodeNode node)
            {
                if (node.Metadata is null
                    || !node.Metadata.TryGetValue(ParserIdKey, out var owner)
                    || !string.Equals(owner, pending.ParserId, StringComparison.Ordinal)
                    || !node.Metadata.TryGetValue(WorkspaceIdKey, out var workspace)
                    || !string.Equals(workspace, pending.WorkspaceId, StringComparison.Ordinal))
                {
                    return false;
                }
                return replacesFiles is null
                    || (node.FilePath is { Length: > 0 } path && replacesFiles.Contains(path));
            }

            var committed = false;
            for (var attempt = 0; attempt < 5 && !committed; attempt++)
            {
                var storeGeneration = _store.LastCommittedGeneration + 1;
                committed = await _store.TryCommitGenerationAsync(
                    storeGeneration, nodes, edges, InScope, ct).ConfigureAwait(false);
            }
            if (!committed)
            {
                _logger.LogWarning(
                    "[{ParserId}] could not commit generation {Generation} for workspace {WorkspaceId}: another writer kept winning the store-generation race.",
                    pending.ParserId, pending.Generation, pending.WorkspaceId);
                return false;
            }

            _lastCommittedGeneration[sessionKey] = pending.Generation;
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

    // The node/edge metadata dictionaries arrive freshly deserialized on this delta and are
    // never read again after this mapping (pending.Nodes/Edges are each projected exactly once
    // and then discarded), so we take ownership of the dictionary in place and stamp the two
    // ownership keys onto it instead of allocating a fresh dictionary and copying every entry.
    // The ownership keys are assigned last so they always win over any same-named source key
    // (matching the previous ownership-first + TryAdd precedence).
    private static Dictionary<string, string> BuildOwnedMetadata(
        IReadOnlyDictionary<string, string>? source, PendingDelta delta)
    {
        if (source is null)
        {
            return new Dictionary<string, string>(2)
            {
                [ParserIdKey] = delta.ParserId,
                [WorkspaceIdKey] = delta.WorkspaceId,
            };
        }

        if (source is not Dictionary<string, string> owned)
        {
            // Defensive: an IReadOnlyDictionary that is not a mutable Dictionary must be copied.
            owned = new Dictionary<string, string>(source.Count + 2);
            foreach (var (key, value) in source)
            {
                owned[key] = value;
            }
        }

        owned[ParserIdKey] = delta.ParserId;
        owned[WorkspaceIdKey] = delta.WorkspaceId;
        return owned;
    }

    private static CodeNode ToCodeNode(ProtocolNode node, PendingDelta delta)
    {
        var metadata = BuildOwnedMetadata(node.Metadata, delta);

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

    private static CodeEdge ToCodeEdge(ProtocolEdge edge, PendingDelta delta)
    {
        var metadata = BuildOwnedMetadata(edge.Metadata, delta);

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
