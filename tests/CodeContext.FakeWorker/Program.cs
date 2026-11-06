using System.Security.Cryptography;
using System.Text;
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
//   missing-progress  workspace/index omits its required terminal progress notification
//   invalid-progress-total reports a total different from the approved file count
//   regressing-progress reports a smaller processed count after a larger one
//   invalid-progress-file reports a current file outside the approved set
//   invalid-progress-request reports progress for an unknown request id
//   native-advertised-missing advertises native trees but omits the handler
//
// Persistent state (plan phase 5). Every behaviour that does not declare persistentState fails a request
// that carries a baseline, and workspace/saveState, with InvalidRequest: the host must never send them.
//
//   strict            normal, named for the tests of that rule
//   persistent-state  declares persistentState; saves its file list and fingerprints on workspace/saveState
//                     (temp file, flush, rename); accepts a baseline whose state file it wrote and answers with
//                     a file-scoped delta of the files added and removed since (empty when nothing changed)
//   baseline-reject   persistent-state that rejects every baseline and replaces the workspace
//   save-state-fails  persistent-state whose workspace/saveState answers an error
//   save-state-false  persistent-state whose workspace/saveState answers saved:false
//   save-state-hangs  persistent-state whose workspace/saveState never answers unless cancelled
//   save-state-wrong-id persistent-state whose workspace/saveState echoes another state id
//   save-state-slow   persistent-state whose workspace/saveState takes 1.5 s
//   content-fingerprints persistent-state whose fingerprint also covers each file's content, and whose
//                     workspace/applyChanges for a file it already holds sends no fact for it (its facts do not
//                     depend on content): a content edit moves the fingerprints and changes no fact
//   fingerprints-without-capability normal, but sends fingerprints on every delta anyway
//   accept-with-replacement persistent-state that answers accepted with a workspace replacement
//   reject-with-file-scope  persistent-state that answers rejected with a file-scoped delta
//   unsolicited-baseline normal, but answers baseline "accepted" to every workspace/index
//
// --fail-marker <path>: while that file exists, workspace/index and workspace/applyChanges emit their delta
// and then answer complete:false (a request that fails after its delta committed).

var behavior = GetOption(args, "--behavior") ?? "normal";
var markerPath = GetOption(args, "--marker");
var failMarker = GetOption(args, "--fail-marker");

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

var state = new WorkerState(behavior, markerPath, failMarker);
var connection = new JsonRpcConnection(stdin, stdout);

connection.AddRequestHandler(ParserProtocolMethods.Initialize, (p, ct) => state.HandleInitializeAsync(p, ct));
connection.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, (p, ct) => state.HandleOpenWorkspaceAsync(p, ct));
connection.AddRequestHandlerWithId(ParserProtocolMethods.IndexWorkspace,
    (requestId, p, ct) => state.HandleIndexAsync(requestId, p, ct, connection));
connection.AddRequestHandlerWithId(ParserProtocolMethods.ApplyChanges,
    (requestId, p, ct) => state.HandleApplyChangesAsync(requestId, p, ct, connection));
connection.AddRequestHandler(ParserProtocolMethods.SaveState, (p, ct) => state.HandleSaveStateAsync(p, ct));
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

internal sealed class WorkerState(string behavior, string? markerPath, string? failMarker)
{
    private const string ParserId = "fake";
    private const string ParserVersion = "1.0.0-test";

    // The files whose facts this process has emitted, which is all its "state" holds.
    private readonly SortedSet<string> _files = new(StringComparer.Ordinal);

    public bool ShutdownRequested { get; set; }

    private bool Persistent => behavior is "persistent-state" or "baseline-reject" or "save-state-fails" or "save-state-false"
        or "save-state-hangs" or "save-state-wrong-id" or "accept-with-replacement" or "reject-with-file-scope"
        or "save-state-slow" or "content-fingerprints";

    // Fingerprints are sent by every persistent behaviour, and by one that sends them without the capability.
    private bool SendsFingerprints => Persistent || behavior == "fingerprints-without-capability";

    private bool Fails => failMarker is not null && File.Exists(failMarker);

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
                NativeSyntaxTree: behavior == "native-advertised-missing",
                PersistentState: Persistent),
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
        if (index.Baseline is not null && !Persistent)
        {
            throw new JsonRpcRemoteException(ParserProtocolErrorCodes.InvalidRequest,
                "workspace/index carried a baseline, but this worker did not declare persistentState.");
        }

        if (behavior == "slow-index")
        {
            // Cooperative cancellation: $/cancel cancels ct, and the connection
            // answers the request with RequestCancelled on our behalf.
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }

        if (behavior != "missing-progress")
        {
            if (behavior == "regressing-progress" && index.Files.Count > 0)
            {
                await SendProgressAsync(1, index.Files.Count, index.Files[0]);
                await SendProgressAsync(0, index.Files.Count, null);
            }
            else
            {
                var total = behavior == "invalid-progress-total"
                    ? index.Files.Count + 1
                    : index.Files.Count;
                var currentFile = behavior == "invalid-progress-file"
                    ? Path.Combine(Path.GetTempPath(), "outside.fake")
                    : index.Files.LastOrDefault();
                await SendProgressAsync(index.Files.Count, total, currentFile);
            }
        }

        string? outcome = behavior == "unsolicited-baseline" ? BaselineOutcomes.Accepted : null;
        string? reason = null;
        AnalysisDelta delta;
        if (index.Baseline is { } baseline)
        {
            var restored = behavior is "baseline-reject" or "reject-with-file-scope" ? null : Restore(baseline, out reason);
            var accepted = restored is not null || behavior == "accept-with-replacement";
            outcome = accepted ? BaselineOutcomes.Accepted : BaselineOutcomes.Rejected;
            reason = accepted ? null : reason ?? "this worker rejects every baseline";
            if (restored is not null && behavior != "accept-with-replacement")
            {
                // Only what differs from the restored state: files added since, and files removed since.
                var current = new SortedSet<string>(index.Files, StringComparer.Ordinal);
                var added = current.Where(file => !restored.Contains(file)).ToList();
                var removed = restored.Where(file => !current.Contains(file)).ToList();
                delta = BuildDelta(index.WorkspaceId, index.Generation, requestId, replacesWorkspace: false,
                    added, [.. added, .. removed]);
            }
            else
            {
                delta = BuildDelta(index.WorkspaceId, index.Generation, requestId,
                    replacesWorkspace: behavior != "reject-with-file-scope", index.Files);
            }
        }
        else
        {
            delta = BuildDelta(index.WorkspaceId, index.Generation, requestId, replacesWorkspace: true, index.Files);
        }
        _files.Clear();
        _files.UnionWith(index.Files);
        if (SendsFingerprints) delta = delta with { Fingerprints = Fingerprints(index.WorkspaceId) };
        await connection.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta,
            ParserProtocolJsonContext.Default.AnalysisDelta, ct);

        var result = new IndexWorkspaceResult(index.WorkspaceId, index.Generation, DeltasEmitted: 1, Complete: !Fails,
            Baseline: outcome, BaselineReason: reason);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.IndexWorkspaceResult);

        Task SendProgressAsync(int processed, int total, string? currentFile)
        {
            var progress = new AnalysisProgress(
                ParserId, ParserVersion, index.WorkspaceId, index.Generation, requestId,
                processed, total, currentFile);
            var progressRequestId = behavior == "invalid-progress-request"
                ? requestId + 999
                : requestId;
            progress = progress with { RequestId = progressRequestId };
            return connection.NotifyAsync(
                ParserProtocolMethods.AnalysisProgressNotification, progress,
                ParserProtocolJsonContext.Default.AnalysisProgress, ct);
        }
    }

    public async Task<JsonElement?> HandleApplyChangesAsync(
        long requestId, JsonElement? paramsElement, CancellationToken ct, JsonRpcConnection connection)
    {
        MaybeCrash();

        var apply = Deserialize(paramsElement, ParserProtocolJsonContext.Default.ApplyChangesParams);
        // content-fingerprints: a file it already holds has the same facts whatever its content, so it is not resent.
        var changes = behavior == "content-fingerprints"
            ? apply.Changes.Where(c => !(c.ChangeType == FileChangeKinds.Changed && _files.Contains(c.Path))).ToList()
            : apply.Changes.ToList();
        var survivingFiles = changes
            .Where(c => c.ChangeType != FileChangeKinds.Deleted)
            .Select(c => c.Path)
            .ToList();

        // The delta's scope is every touched file (deleted ones included, so their
        // facts are replaced with nothing); nodes are emitted for survivors only.
        var delta = BuildDelta(apply.WorkspaceId, apply.Generation, requestId,
            replacesWorkspace: false,
            files: survivingFiles,
            replacesFiles: changes.Select(c => c.Path).ToList());
        foreach (var change in apply.Changes)
        {
            if (change.ChangeType == FileChangeKinds.Deleted) _files.Remove(change.Path);
            else _files.Add(change.Path);
        }
        if (SendsFingerprints) delta = delta with { Fingerprints = Fingerprints(apply.WorkspaceId) };
        await connection.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta,
            ParserProtocolJsonContext.Default.AnalysisDelta, ct);

        var result = new ApplyChangesResult(apply.WorkspaceId, apply.Generation, DeltasEmitted: 1, Complete: !Fails);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.ApplyChangesResult);
    }

    public async Task<JsonElement?> HandleSaveStateAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var save = Deserialize(paramsElement, ParserProtocolJsonContext.Default.SaveStateParams);
        if (!Persistent)
        {
            throw new JsonRpcRemoteException(ParserProtocolErrorCodes.InvalidRequest,
                "workspace/saveState sent to a worker that did not declare persistentState.");
        }
        switch (behavior)
        {
            case "save-state-fails":
                throw new JsonRpcRemoteException(ParserProtocolErrorCodes.InternalError, "the disk is full (fake)");
            case "save-state-hangs":
                await Task.Delay(Timeout.Infinite, ct);
                break;
            case "save-state-false":
                return Serialize(new SaveStateResult(save.WorkspaceId, save.StateId, Saved: false));
            case "save-state-slow":
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
                break;
        }

        // The state: its id, the files emitted and their fingerprints, written beside the path, flushed, renamed.
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("stateId", save.StateId);
            writer.WriteString("workspaceId", save.WorkspaceId);
            writer.WriteString("fingerprint", Fingerprints(save.WorkspaceId)[0].Value);
            writer.WriteStartArray("files");
            foreach (var file in _files) writer.WriteStringValue(file);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        var bytes = buffer.ToArray();
        var temp = save.StatePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, save.StatePath, overwrite: true);
        var id = behavior == "save-state-wrong-id" ? new string('0', save.StateId.Length) : save.StateId;
        return Serialize(new SaveStateResult(save.WorkspaceId, id, Saved: true, bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    /// <summary>The files a state file this worker wrote holds, when it is the one the baseline names; else null and why.</summary>
    private static SortedSet<string>? Restore(IndexBaseline baseline, out string? reason)
    {
        reason = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(baseline.StatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "state file is missing";
            return null;
        }
        // Untrusted until the hash matches.
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != baseline.StateSha256)
        {
            reason = "state file hash does not match";
            return null;
        }
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.GetProperty("stateId").GetString() != baseline.StateId)
        {
            reason = "state id does not match";
            return null;
        }
        var fingerprint = baseline.Fingerprints.Count == 1 ? baseline.Fingerprints[0].Value : null;
        if (root.GetProperty("fingerprint").GetString() != fingerprint)
        {
            reason = "fingerprints do not match";
            return null;
        }
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in root.GetProperty("files").EnumerateArray()) files.Add(file.GetString()!);
        return files;
    }

    /// <summary>
    /// One unit, the workspace, whose fingerprint is the SHA-256 of the sorted file list (content-fingerprints: each
    /// file followed by its content).
    /// </summary>
    private List<UnitFingerprint> Fingerprints(string workspaceId)
    {
        var text = behavior == "content-fingerprints"
            ? string.Join("\n", _files.Select(file => file + "\n" + (File.Exists(file) ? File.ReadAllText(file) : "")))
            : string.Join("\n", _files);
        return [new UnitFingerprint(workspaceId, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))))];
    }

    private static JsonElement? Serialize(SaveStateResult result)
        => JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.SaveStateResult);

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

    private AnalysisDelta BuildDelta(
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
            nodes.Add(new ProtocolNode(classId, $"fake:{stem}.Class", $"{stem}Class", "class", "fake", file,
                StartLine: 1, EndLine: 10, StartColumn: 1, EndColumn: 1,
                // The persistent family's facts do not depend on the generation, so facts restored from a baseline
                // equal those of a fresh scan.
                Metadata: Persistent ? null : new Dictionary<string, string> { ["gen"] = generation.ToString() }));
            nodes.Add(new ProtocolNode(methodId, $"fake:{stem}.Class.Method", $"{stem}Method", "method", "fake", file,
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
