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
        typeof(CSharpWorkerHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

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
            var deltasEmitted = await AnalyzeAndPublishAsync(
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
            var deltasEmitted = await AnalyzeAndPublishAsync(
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
    /// Runs the analysis and streams the complete workspace replacement as one or more
    /// analysis/delta notifications (all flagged <c>replacesWorkspace</c>), returning
    /// how many were emitted.
    /// </summary>
    private async Task<int> AnalyzeAndPublishAsync(
        CSharpWorkspaceAnalyzer workspace, string workspaceId, long generation, long requestId, CancellationToken ct)
    {
        var analysis = workspace.Analyze(ct);

        var nodeChunks = Chunk(analysis.Nodes, MaxItemsPerDelta);
        var edgeChunks = Chunk(analysis.Edges, MaxItemsPerDelta);
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
                ReplacesWorkspace: true,
                ReplacesFiles: [],
                Nodes: nodes,
                Edges: edges,
                IsLastForRequest: i == totalChunks - 1,
                Diagnostics: i == 0 && analysis.Diagnostics.Count > 0 ? analysis.Diagnostics : null);

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
