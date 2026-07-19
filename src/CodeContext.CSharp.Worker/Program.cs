using System.Reflection;
using System.Text.Json;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

// CodeContext C# language worker. A private child of the repository host: speaks
// JSON-RPC 2.0 with Content-Length framing over stdin/stdout, opens no ports,
// registers no global instance, and exits when stdin reaches EOF (the mandatory
// self-cleaning signal if the host dies). Roslyn lives here, not in the host.

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

var connection = new JsonRpcConnection(stdin, stdout);
var host = new CSharpWorkerHost(connection);

connection.AddRequestHandler(ParserProtocolMethods.Initialize, host.HandleInitializeAsync);
connection.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, host.HandleOpenWorkspaceAsync);
connection.AddRequestHandlerWithId(ParserProtocolMethods.IndexWorkspace, host.HandleIndexWorkspaceAsync);
connection.AddRequestHandlerWithId(ParserProtocolMethods.ApplyChanges, host.HandleApplyChangesAsync);
connection.AddRequestHandler(ParserProtocolMethods.GetNativeSyntaxTree, host.HandleNativeSyntaxTreeAsync);
connection.AddRequestHandler(ParserProtocolMethods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));

try
{
    // The read loop completes on stdin EOF: exit then, whether or not a shutdown
    // request was ever received.
    await connection.StartAsync();
}
catch (ParserProtocolViolationException ex)
{
    Console.Error.WriteLine($"csharp-worker: protocol violation from host: {ex.Message}");
    return 1;
}
return 0;

internal sealed class CSharpWorkerHost(JsonRpcConnection connection)
{
    private const string ParserId = "csharp";
    private static readonly string ParserVersion =
        GetInformationalVersion(typeof(CSharpWorkerHost).Assembly);

    // Mirror of CodeContext.Core.AssemblyVersionInfo.GetInformationalVersion. The worker
    // deliberately references only Parser.Protocol + Roslyn and must not take a dependency
    // on Core (which drags in Microsoft.Extensions.Hosting/Logging), so this tiny reader is
    // duplicated here rather than shared.
    private static string GetInformationalVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>Upper bound on nodes/edges per analysis/delta message so a huge
    /// workspace streams in bounded frames instead of one giant payload.</summary>
    private const int MaxItemsPerDelta = 2000;

    private readonly Dictionary<string, CSharpWorkspaceAnalyzer> _workspaces = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public Task<JsonElement?> HandleInitializeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        _ = Deserialize(paramsElement, ParserProtocolJsonContext.Default.InitializeParams);

        var result = new InitializeResult(
            ParserId: ParserId,
            ParserVersion: ParserVersion,
            DisplayName: "CSharp",
            ProtocolVersion: ParserProtocol.Version,
            Languages: ["csharp"],
            Extensions: [".cs"],
            ProjectMarkers: [".csproj", ".sln"],
            Capabilities: new WorkerCapabilities(
                WorkspaceAnalysis: true,
                IncrementalUpdates: true,
                SemanticAnalysis: true,
                NativeSyntaxTree: true),
            // Roslyn line/column positions are zero-based; the end position is the
            // location immediately after the declaration's last character (exclusive).
            SpanSemantics: new SpanSemantics(LineBase: 0, ColumnBase: 0, EndIsInclusive: false));

        return Task.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.InitializeResult));
    }

    public async Task<JsonElement?> HandleOpenWorkspaceAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var open = Deserialize(paramsElement, ParserProtocolJsonContext.Default.OpenWorkspaceParams);

        await _mutationLock.WaitAsync(ct);
        try
        {
            if (!_workspaces.TryGetValue(open.WorkspaceId, out var workspace))
            {
                workspace = new CSharpWorkspaceAnalyzer(open.WorkspaceId);
                _workspaces[open.WorkspaceId] = workspace;
            }
            // Non-destructive sync: the host re-opens before every mutation with the
            // current approved file list, which lets a freshly (re)started worker
            // rebuild its tree cache without a full reindex round-trip.
            workspace.SyncApprovedFiles(open.ApprovedFiles, ct);
        }
        finally
        {
            _mutationLock.Release();
        }

        var result = new OpenWorkspaceResult(open.WorkspaceId, Opened: true);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.OpenWorkspaceResult);
    }

    public async Task<JsonElement?> HandleIndexWorkspaceAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        var index = Deserialize(paramsElement, ParserProtocolJsonContext.Default.IndexWorkspaceParams);

        await _mutationLock.WaitAsync(ct);
        try
        {
            var workspace = GetWorkspace(index.WorkspaceId);
            workspace.ReplaceFiles(index.Files, ct);
            var deltasEmitted = await PublishFullAsync(
                workspace, index.WorkspaceId, index.Generation, requestId, ct);

            var result = new IndexWorkspaceResult(index.WorkspaceId, index.Generation, deltasEmitted, Complete: true);
            return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.IndexWorkspaceResult);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<JsonElement?> HandleApplyChangesAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        var apply = Deserialize(paramsElement, ParserProtocolJsonContext.Default.ApplyChangesParams);

        await _mutationLock.WaitAsync(ct);
        try
        {
            var workspace = GetWorkspace(apply.WorkspaceId);
            workspace.ApplyChanges(apply.Changes, ct);
            var deltasEmitted = await PublishIncrementalAsync(
                workspace, apply.WorkspaceId, apply.Generation, requestId, ct);

            var result = new ApplyChangesResult(apply.WorkspaceId, apply.Generation, deltasEmitted, Complete: true);
            return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.ApplyChangesResult);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<JsonElement?> HandleNativeSyntaxTreeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var request = Deserialize(paramsElement, ParserProtocolJsonContext.Default.NativeSyntaxTreeParams);
        await _mutationLock.WaitAsync(ct);
        try
        {
            var workspace = GetWorkspace(request.WorkspaceId);
            var native = workspace.GetNativeSyntaxTree(
                request.FilePath, request.Start, request.Length, request.MaxDepth, ct);
            var result = new NativeSyntaxTreeResult(
                ParserId, ParserVersion, request.WorkspaceId, Path.GetFullPath(request.FilePath),
                "roslyn-csharp-syntax-v1", native.Tree, native.Truncated);
            return JsonSerializer.SerializeToElement(
                result, ParserProtocolJsonContext.Default.NativeSyntaxTreeResult);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    private CSharpWorkspaceAnalyzer GetWorkspace(string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var workspace))
        {
            // Tolerate a mutation without a prior open (e.g. a host that skips the
            // re-open after restart): start from an empty workspace.
            workspace = new CSharpWorkspaceAnalyzer(workspaceId);
            _workspaces[workspaceId] = workspace;
        }
        return workspace;
    }

    /// <summary>
    /// Whole-workspace (index/reset) path: walks everything, streams the complete
    /// workspace replacement as one or more analysis/delta notifications (all flagged
    /// <c>replacesWorkspace</c>), then seeds the per-file hash baseline.
    /// </summary>
    private async Task<int> PublishFullAsync(
        CSharpWorkspaceAnalyzer workspace, string workspaceId, long generation, long requestId, CancellationToken ct)
    {
        var analysis = workspace.Analyze(ct);
        var emitted = await PublishAsync(
            workspaceId, generation, requestId,
            analysis.Nodes, analysis.Edges,
            replacesWorkspace: true, replacesFiles: [],
            analysis.Diagnostics, ct);
        // Seed only after every chunk was sent, mirroring the incremental path's
        // commit-after-send rule: a cancelled mid-index emission must not leave the
        // hash baseline claiming facts the host never received.
        workspace.SeedFactHashes(analysis);
        return emitted;
    }

    /// <summary>
    /// Incremental (applyChanges) path: re-walks the whole workspace for cross-file
    /// binding correctness, then emits only the dirty buckets' facts scoped to the
    /// dirty ∪ removed file set (every chunk flagged <c>replacesWorkspace:false</c> and
    /// carrying the full <c>replacesFiles</c> list). The per-file hash map is committed
    /// only after every chunk has been sent, so a failed emission cannot desync it.
    /// </summary>
    private async Task<int> PublishIncrementalAsync(
        CSharpWorkspaceAnalyzer workspace, string workspaceId, long generation, long requestId, CancellationToken ct)
    {
        var analysis = workspace.Analyze(ct);
        var emission = workspace.BuildIncrementalEmission(analysis);
        var emitted = await PublishAsync(
            workspaceId, generation, requestId,
            emission.Nodes, emission.Edges,
            replacesWorkspace: false, replacesFiles: emission.ReplacesFiles,
            analysis.Diagnostics, ct);
        workspace.CommitIncremental(emission);
        return emitted;
    }

    /// <summary>
    /// Streams a node/edge set as one or more <c>analysis/delta</c> notifications, chunked
    /// at <see cref="MaxItemsPerDelta"/>. Always emits at least one delta (the supervisor
    /// requires ≥1 per mutation), so an empty incremental batch still sends a single
    /// terminal delta with no facts. Diagnostics ride on the first chunk only.
    /// </summary>
    private async Task<int> PublishAsync(
        string workspaceId, long generation, long requestId,
        List<ProtocolNode> allNodes, List<ProtocolEdge> allEdges,
        bool replacesWorkspace, IReadOnlyList<string> replacesFiles,
        List<ProtocolDiagnostic> diagnostics, CancellationToken ct)
    {
        var nodeChunks = Chunk(allNodes, MaxItemsPerDelta);
        var edgeChunks = Chunk(allEdges, MaxItemsPerDelta);
        var totalChunks = Math.Max(1, nodeChunks.Count + edgeChunks.Count);

        var emitted = 0;
        for (var i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ProtocolNode> nodes = i < nodeChunks.Count ? nodeChunks[i] : [];
            var edgeIndex = i - nodeChunks.Count;
            IReadOnlyList<ProtocolEdge> edges =
                edgeIndex >= 0 && edgeIndex < edgeChunks.Count ? edgeChunks[edgeIndex] : [];

            var delta = new AnalysisDelta(
                ParserId: ParserId,
                ParserVersion: ParserVersion,
                WorkspaceId: workspaceId,
                Generation: generation,
                RequestId: requestId,
                ReplacesWorkspace: replacesWorkspace,
                ReplacesFiles: replacesFiles,
                Nodes: nodes,
                Edges: edges,
                IsLastForRequest: i == totalChunks - 1,
                Diagnostics: i == 0 && diagnostics.Count > 0 ? diagnostics : null);

            await connection.NotifyAsync(
                ParserProtocolMethods.AnalysisDeltaNotification, delta,
                ParserProtocolJsonContext.Default.AnalysisDelta, ct);
            emitted++;
        }
        return emitted;
    }

    private static List<IReadOnlyList<T>> Chunk<T>(List<T> items, int size)
    {
        var chunks = new List<IReadOnlyList<T>>();
        for (var offset = 0; offset < items.Count; offset += size)
        {
            chunks.Add(items.GetRange(offset, Math.Min(size, items.Count - offset)));
        }
        return chunks;
    }

    private static T Deserialize<T>(JsonElement? paramsElement, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        if (paramsElement is not { } element)
        {
            throw new JsonRpcRemoteException(ParserProtocolErrorCodes.InvalidParams, "Missing params.");
        }
        return element.Deserialize(typeInfo)
            ?? throw new JsonRpcRemoteException(ParserProtocolErrorCodes.InvalidParams, "Params deserialized to null.");
    }
}
