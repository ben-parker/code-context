using System.Text.Json;
using CodeContext.Core.Repositories;
using CodeContext.Core.Serialization;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>
/// Delta sink for backends without <see cref="IGenerationalGraphStore"/> support
/// (the opt-in Kuzu backend): buffers a request's chunks and pushes the completed
/// result through the legacy JSON reconcile boundary. That reconcile replaces the
/// whole graph, so on Kuzu a C# generation still clobbers other parsers' facts —
/// the same pre-existing limitation the in-process parser had. JSON is legitimate
/// here: it is the process boundary to kuzu_api.py, not an in-memory format.
/// </summary>
public sealed class JsonReconcileDeltaSink : IAnalysisDeltaSink
{
    private readonly ICodeGraphRepository _graphRepository;
    private readonly ILogger<JsonReconcileDeltaSink> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _lastCommittedGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId), long> _highestSeenGeneration = new();
    private readonly Dictionary<(string ParserId, string WorkspaceId, long Generation, long RequestId), Buffered> _pending = new();

    private sealed class Buffered
    {
        public List<ProtocolNode> Nodes { get; } = [];
        public List<ProtocolEdge> Edges { get; } = [];
    }

    public JsonReconcileDeltaSink(ICodeGraphRepository graphRepository, ILogger<JsonReconcileDeltaSink> logger)
    {
        _graphRepository = graphRepository;
        _logger = logger;
    }

    public async Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sessionKey = (delta.ParserId, delta.WorkspaceId);
            if (_lastCommittedGeneration.TryGetValue(sessionKey, out var committed) && delta.Generation <= committed)
            {
                _logger.LogWarning(
                    "[{ParserId}] discarded stale delta generation {Generation} (<= {Committed}) for workspace {WorkspaceId}.",
                    delta.ParserId, delta.Generation, committed, delta.WorkspaceId);
                return false;
            }

            // A newer generation supersedes any partially buffered older one (e.g. a
            // worker that crashed mid-stream and was restarted): evict those buffers
            // so they cannot accumulate for the lifetime of the host.
            if (!_highestSeenGeneration.TryGetValue(sessionKey, out var highestSeen)
                || delta.Generation > highestSeen)
            {
                _highestSeenGeneration[sessionKey] = delta.Generation;
                EvictPending(sessionKey, generation => generation < delta.Generation);
            }
            else if (delta.Generation < highestSeen)
            {
                _logger.LogWarning(
                    "[{ParserId}] discarded stale delta generation {Generation} (< newest {Newest}) for workspace {WorkspaceId}.",
                    delta.ParserId, delta.Generation, highestSeen, delta.WorkspaceId);
                return false;
            }

            var key = (delta.ParserId, delta.WorkspaceId, delta.Generation, delta.RequestId);
            if (!_pending.TryGetValue(key, out var buffered))
            {
                buffered = new Buffered();
                _pending[key] = buffered;
            }
            buffered.Nodes.AddRange(delta.Nodes);
            buffered.Edges.AddRange(delta.Edges);

            if (!delta.IsLastForRequest)
            {
                return true;
            }

            _pending.Remove(key);

            var nodeDtos = buffered.Nodes.Select(n => new NodeDto(
                n.Id, n.Name, n.Kind, n.Language, n.FilePath,
                n.StartLine, n.EndLine, n.StartColumn, n.EndColumn,
                n.Namespace, n.Visibility, n.Signature, n.ReturnType,
                n.Parameters, n.Modifiers, null, n.Metadata)).ToList();
            var edgeDtos = buffered.Edges.Select(e => new EdgeDto(
                e.Id, e.SourceId, e.TargetId, e.Kind, e.Metadata)).ToList();

            var nodesJson = JsonSerializer.Serialize(nodeDtos, typeof(List<NodeDto>), CodeContextJsonContext.Default);
            var edgesJson = JsonSerializer.Serialize(edgeDtos, typeof(List<EdgeDto>), CodeContextJsonContext.Default);
            await _graphRepository.ReconcileAndPruneAsync(nodesJson, edgesJson).ConfigureAwait(false);

            _lastCommittedGeneration[sessionKey] = delta.Generation;
            EvictPending(sessionKey, generation => generation <= delta.Generation);
            _logger.LogDebug(
                "[{ParserId}] reconciled generation {Generation} for workspace {WorkspaceId} through the JSON boundary: {Nodes} nodes, {Edges} edges.",
                delta.ParserId, delta.Generation, delta.WorkspaceId, nodeDtos.Count, edgeDtos.Count);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EvictPending((string ParserId, string WorkspaceId) sessionKey, Func<long, bool> predicate)
    {
        foreach (var key in _pending.Keys.Where(key =>
                     key.ParserId == sessionKey.ParserId
                     && key.WorkspaceId == sessionKey.WorkspaceId
                     && predicate(key.Generation)).ToList())
        {
            _pending.Remove(key);
        }
    }
}
