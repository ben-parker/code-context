using System.Text.Json;
using CodeContext.Parser.Protocol;

// Protocol-conformant fake worker for ParserProcessSupervisor tests. Speaks JSON-RPC
// over stdin/stdout, exits on stdin EOF, opens no ports, registers nowhere. The
// --behavior flag selects a misbehavior to exercise one host failure path:
//
//   normal            full protocol conformance (default)
//   protocol-too-new  answers initialize with an incompatible protocol version
//   hang-on-initialize never answers initialize
//   malformed-output  writes non-protocol garbage to stdout
//   crash-on-index    exits with code 42 when workspace/index arrives
//   crash-once        crash-on-index only while the --marker file does not exist yet
//   stderr-flood      writes 5000 stderr lines during startup, then behaves normally
//   slow-index        workspace/index takes 30s unless cancelled via $/cancel
//   native-advertised-missing advertises native trees but omits the handler

var behavior = GetOption(args, "--behavior") ?? "normal";
var markerPath = GetOption(args, "--marker");

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

if (behavior == "malformed-output")
{
    var garbage = "this is not a Content-Length framed JSON-RPC message\r\n\r\n"u8.ToArray();
    stdout.Write(garbage, 0, garbage.Length);
    stdout.Flush();
    // Stay alive; the host must detect the violation and kill us.
    await Task.Delay(Timeout.Infinite);
    return 0;
}

if (behavior == "stderr-flood")
{
    for (var i = 0; i < 5000; i++)
    {
        Console.Error.WriteLine($"fake-worker stderr flood line {i} — the host must keep draining this pipe");
    }
    Console.Error.Flush();
}

var state = new WorkerState(behavior, markerPath);
var connection = new JsonRpcConnection(stdin, stdout);

connection.AddRequestHandler(ParserProtocolMethods.Initialize, (p, ct) => state.HandleInitializeAsync(p, ct));
connection.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, (p, ct) => state.HandleOpenWorkspaceAsync(p, ct));
connection.AddRequestHandlerWithId(ParserProtocolMethods.IndexWorkspace,
    (requestId, p, ct) => state.HandleIndexAsync(requestId, p, ct, connection));
connection.AddRequestHandlerWithId(ParserProtocolMethods.ApplyChanges,
    (requestId, p, ct) => state.HandleApplyChangesAsync(requestId, p, ct, connection));
connection.AddRequestHandler(ParserProtocolMethods.Shutdown, (_, _) =>
{
    state.ShutdownRequested = true;
    return Task.FromResult<JsonElement?>(null);
});

// The read loop ends on stdin EOF — the mandatory self-cleaning exit signal.
try
{
    await connection.StartAsync();
}
catch (ParserProtocolViolationException ex)
{
    Console.Error.WriteLine($"fake-worker: protocol violation from host: {ex.Message}");
    return 1;
}
return 0;

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) return args[i + 1];
    }
    return null;
}

internal sealed class WorkerState(string behavior, string? markerPath)
{
    private const string ParserId = "fake";
    private const string ParserVersion = "1.0.0-test";

    public bool ShutdownRequested { get; set; }

    public async Task<JsonElement?> HandleInitializeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        if (behavior == "hang-on-initialize")
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        var initialize = Deserialize(paramsElement, ParserProtocolJsonContext.Default.InitializeParams);
        var version = behavior == "protocol-too-new" ? 9999 : initialize.MaxProtocolVersion;

        var result = new InitializeResult(
            ParserId: ParserId,
            ParserVersion: ParserVersion,
            DisplayName: "Fake Worker",
            ProtocolVersion: version,
            Languages: ["fake"],
            Extensions: [".fake"],
            ProjectMarkers: ["fake.proj"],
            Capabilities: new WorkerCapabilities(
                WorkspaceAnalysis: true, IncrementalUpdates: true,
                SemanticAnalysis: false,
                NativeSyntaxTree: behavior == "native-advertised-missing"),
            SpanSemantics: new SpanSemantics(LineBase: 1, ColumnBase: 1, EndIsInclusive: true));

        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.InitializeResult);
    }

    public Task<JsonElement?> HandleOpenWorkspaceAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var open = Deserialize(paramsElement, ParserProtocolJsonContext.Default.OpenWorkspaceParams);
        var result = new OpenWorkspaceResult(open.WorkspaceId, Opened: true);
        return Task.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.OpenWorkspaceResult));
    }

    public async Task<JsonElement?> HandleIndexAsync(
        long requestId, JsonElement? paramsElement, CancellationToken ct, JsonRpcConnection connection)
    {
        MaybeCrash();

        var index = Deserialize(paramsElement, ParserProtocolJsonContext.Default.IndexWorkspaceParams);

        if (behavior == "slow-index")
        {
            // Cooperative cancellation: $/cancel cancels ct, and the connection
            // answers the request with RequestCancelled on our behalf.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }

        var delta = BuildDelta(index.WorkspaceId, index.Generation, requestId,
            replacesWorkspace: true, index.Files);
        await connection.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta,
            ParserProtocolJsonContext.Default.AnalysisDelta, ct);

        var result = new IndexWorkspaceResult(index.WorkspaceId, index.Generation, DeltasEmitted: 1, Complete: true);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.IndexWorkspaceResult);
    }

    public async Task<JsonElement?> HandleApplyChangesAsync(
        long requestId, JsonElement? paramsElement, CancellationToken ct, JsonRpcConnection connection)
    {
        MaybeCrash();

        var apply = Deserialize(paramsElement, ParserProtocolJsonContext.Default.ApplyChangesParams);
        var survivingFiles = apply.Changes
            .Where(c => c.ChangeType != FileChangeKinds.Deleted)
            .Select(c => c.Path)
            .ToList();

        // The delta's scope is every touched file (deleted ones included, so their
        // facts are replaced with nothing); nodes are emitted for survivors only.
        var delta = BuildDelta(apply.WorkspaceId, apply.Generation, requestId,
            replacesWorkspace: false,
            files: survivingFiles,
            replacesFiles: apply.Changes.Select(c => c.Path).ToList());
        await connection.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta,
            ParserProtocolJsonContext.Default.AnalysisDelta, ct);

        var result = new ApplyChangesResult(apply.WorkspaceId, apply.Generation, DeltasEmitted: 1, Complete: true);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.ApplyChangesResult);
    }

    private void MaybeCrash()
    {
        if (behavior == "crash-on-index")
        {
            Environment.Exit(42);
        }
        if (behavior == "crash-once" && markerPath is not null && !File.Exists(markerPath))
        {
            File.WriteAllText(markerPath, "crashed");
            Environment.Exit(42);
        }
    }

    private static AnalysisDelta BuildDelta(
        string workspaceId, long generation, long requestId,
        bool replacesWorkspace, IReadOnlyList<string> files, IReadOnlyList<string>? replacesFiles = null)
    {
        var nodes = new List<ProtocolNode>();
        var edges = new List<ProtocolEdge>();

        foreach (var file in files)
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var classId = $"{ParserId}:{workspaceId}:{stem}.Class";
            var methodId = $"{ParserId}:{workspaceId}:{stem}.Class.Method";
            nodes.Add(new ProtocolNode(classId, $"{stem}Class", "class", "fake", file,
                StartLine: 1, EndLine: 10, StartColumn: 1, EndColumn: 1,
                Metadata: new Dictionary<string, string> { ["gen"] = generation.ToString() }));
            nodes.Add(new ProtocolNode(methodId, $"{stem}Method", "method", "fake", file,
                StartLine: 2, EndLine: 5, StartColumn: 1, EndColumn: 1));
            edges.Add(new ProtocolEdge($"{classId}->contains->{methodId}", classId, methodId, "contains"));
        }

        return new AnalysisDelta(
            ParserId: ParserId,
            ParserVersion: ParserVersion,
            WorkspaceId: workspaceId,
            Generation: generation,
            RequestId: requestId,
            ReplacesWorkspace: replacesWorkspace,
            ReplacesFiles: replacesFiles ?? files,
            Nodes: nodes,
            Edges: edges,
            IsLastForRequest: true);
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
