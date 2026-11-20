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

        /// <summary>The first chunk's inputs, as for <see cref="Fingerprints"/>.</summary>
        public WorkerInputs? Inputs { get; } = first.Inputs;

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

    /// <summary>
    /// For a parser, when its worker last began an operation that read all its inputs (UTC ticks, 0 when not known):
    /// recorded with the inputs a commit carries (<see cref="ShardAnnotation.InputsReadUtcTicks"/>). Set by the worker
    /// service that owns the workers.
    /// </summary>
    internal Func<string, long>? InputsReadBoundary { get; set; }

    private readonly IGenerationalGraphStore _store;
    private readonly IStoredNodeIds? _storedNodeIds;
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
        _storedNodeIds = store as IStoredNodeIds;
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
            // One pool per request: facts of the commit with equal metadata share one immutable instance,
            // and repeated strings one instance.
            var mapper = new FactMapper(pending, _storedNodeIds);
            var nodes = new List<CodeNode>(pending.Nodes.Count);
            foreach (var node in pending.Nodes) nodes.Add(mapper.ToCodeNode(node));
            var edges = new List<CodeEdge>(pending.Edges.Count);
            foreach (var edge in pending.Edges) edges.Add(mapper.ToCodeEdge(edge));

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
                    Inputs = pending.Inputs,
                    InputsReadUtcTicks = pending.Inputs is null ? 0 : InputsReadBoundary?.Invoke(pending.ParserId) ?? 0,
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

    /// <summary>
    /// Maps one request's facts to stored ones through one pool, so facts of the commit with equal metadata
    /// share one immutable instance and repeated strings one instance.
    /// </summary>
    private sealed class FactMapper
    {
        private readonly FactMetadataPool _pool;
        private readonly string _parserId;
        private readonly string _workspaceId;
        private readonly FactMetadata _ownershipOnly;
        private string[] _scratch = new string[16];

        public FactMapper(PendingDelta delta, IStoredNodeIds? stored)
        {
            // An endpoint the request carries no node for (a node of a file it does not replace) is stored
            // as the instance that node holds in the store, so editing a file does not add a copy of the
            // id of everything the file uses.
            _storedId = stored is null ? static _ => null : stored.FindStoredNodeId;
            // Node ids, paths and the other repeated text of the request: presized for about two per node.
            _pool = new FactMetadataPool(stringCapacity: 2 * delta.Nodes.Count + 64);
            _parserId = _pool.Intern(delta.ParserId);
            _workspaceId = _pool.Intern(delta.WorkspaceId);
            _ownershipOnly = _pool.Get([ParserIdKey, _parserId, WorkspaceIdKey, _workspaceId]);
            _share = piece => _pool.Intern(piece);
        }

        private readonly Func<ReadOnlySpan<char>, string> _share;
        private readonly Func<string, string?> _storedId;

        // The fact's metadata: the source's entries in their order, with the two ownership keys set to the
        // delta's values (in place where the source has them, appended in this order where it does not), so
        // the ownership keys always win over a same-named source key. A key the source yields twice keeps its
        // first position and its last value, as a dictionary assignment would. The source is only read; the
        // result is the pool's shared, immutable instance for that content, found without allocating when
        // the request already holds it.
        private FactMetadata OwnedMetadata(IReadOnlyDictionary<string, string>? source)
        {
            var count = source?.Count ?? 0;
            if (count == 0) return _ownershipOnly;

            var needed = 2 * count + 4;
            if (_scratch.Length < needed) _scratch = new string[Math.Max(needed, 2 * _scratch.Length)];
            _length = 0;
            _parserAt = _workspaceAt = -1;
            _positions = null;
            // A dictionary with an ordinal-consistent comparer cannot yield a key twice; it is also read
            // through its own enumerator rather than the interface's.
            if (source is Dictionary<string, string> dictionary
                && (ReferenceEquals(dictionary.Comparer, EqualityComparer<string>.Default)
                    || ReferenceEquals(dictionary.Comparer, StringComparer.Ordinal)
                    || ReferenceEquals(dictionary.Comparer, StringComparer.OrdinalIgnoreCase)))
            {
                foreach (var (key, value) in dictionary) Put(key, value, checkRepeats: false);
            }
            else
            {
                foreach (var (key, value) in source!) Put(key, value, checkRepeats: true);
            }
            var pairs = _scratch;
            var length = _length;
            if (_parserAt < 0)
            {
                pairs[length++] = ParserIdKey;
                pairs[length++] = _parserId;
            }
            if (_workspaceAt < 0)
            {
                pairs[length++] = WorkspaceIdKey;
                pairs[length++] = _workspaceId;
            }
            var shared = _pool.Get(new ReadOnlySpan<string>(pairs, 0, length));
            Array.Clear(pairs, 0, length);
            return shared;
        }

        private int _length;
        private int _parserAt;
        private int _workspaceAt;
        private Dictionary<string, int>? _positions;

        private void Put(string key, string value, bool checkRepeats)
        {
            if (key == ParserIdKey)
            {
                if (_parserAt >= 0) return;
                _parserAt = _length;
                value = _parserId;
            }
            else if (key == WorkspaceIdKey)
            {
                if (_workspaceAt >= 0) return;
                _workspaceAt = _length;
                value = _workspaceId;
            }
            else if (checkRepeats && Find(_scratch, _length, key, ref _positions) is var at and >= 0)
            {
                _scratch[at + 1] = value;
                return;
            }
            // Room for this pair and the two ownership pairs (a map may yield more pairs than its count).
            if (_length + 6 > _scratch.Length) Array.Resize(ref _scratch, 2 * _scratch.Length);
            _scratch[_length] = key;
            _scratch[_length + 1] = value;
            _positions?.Add(key, _length);
            _length += 2;
        }

        // The position of key among the first length pairs, or -1: in order for a few, through an index for many.
        private static int Find(string[] pairs, int length, string key, ref Dictionary<string, int>? positions)
        {
            if (positions is null && length >= 32)
            {
                positions = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < length; i += 2) positions.TryAdd(pairs[i], i);
            }
            if (positions is not null) return positions.TryGetValue(key, out var at) ? at : -1;
            for (var i = 0; i < length; i += 2)
                if (string.Equals(pairs[i], key, StringComparison.Ordinal)) return i;
            return -1;
        }

        private string? _lastSource;
        private string? _lastSourcePooled;

        // A worker sends a member's edges together, so most sources repeat the previous edge's: one compare
        // instead of a hash and a probe of the pool.
        private string? InternSource(string? source)
        {
            if (source is not null && string.Equals(source, _lastSource, StringComparison.Ordinal)) return _lastSourcePooled;
            _lastSource = source;
            return _lastSourcePooled = _pool.Intern(source, _storedId);
        }

        // Strings that repeat across the facts of a request (paths, kinds, languages, names, and node ids
        // that edges name as endpoints) arrive as one instance per occurrence from the deserializer; each is
        // stored through the request's pool, as a snapshot load shares them through the shard's string
        // table. Nodes are mapped before edges, so an endpoint is the instance its node holds: the
        // request's node, or else the stored one.
        public CodeNode ToCodeNode(ProtocolNode node) => new()
        {
            Id = _pool.Intern(node.Id),
            Identifier = node.Identifier,
            Name = _pool.Intern(node.Name),
            Type = _pool.Intern(node.Kind),
            Language = _pool.Intern(node.Language),
            FilePath = _pool.Intern(node.FilePath),
            StartLine = node.StartLine,
            EndLine = node.EndLine,
            StartCol = node.StartColumn,
            EndCol = node.EndColumn,
            Namespace = _pool.Intern(node.Namespace),
            Visibility = _pool.Intern(node.Visibility),
            Signature = _pool.Intern(node.Signature),
            ReturnType = _pool.Intern(node.ReturnType),
            Parameters = _pool.Intern(node.Parameters),
            Modifiers = _pool.Intern(node.Modifiers),
            Metadata = OwnedMetadata(node.Metadata),
        };

        // The id is kept as parts (the endpoint instances and two pooled pieces) when it is exactly its
        // source + a piece + its target + a piece, and as the string sent otherwise (EdgeId.Compose).
        public CodeEdge ToCodeEdge(ProtocolEdge edge)
        {
            var source = InternSource(edge.SourceId);
            var target = _pool.Intern(edge.TargetId, _storedId);
            var stored = new CodeEdge
            {
                SourceId = source,
                TargetId = target,
                Type = _pool.Intern(edge.Kind),
                Metadata = OwnedMetadata(edge.Metadata),
            };
            stored.UseIdKey(EdgeId.Compose(edge.Id, source, target, _share));
            return stored;
        }
    }
}
