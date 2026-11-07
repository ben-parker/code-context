using System.Text.Json;
using CodeContext.Parser.Protocol;

namespace CodeContext.CSharp.Worker;

// CodeContext C# language worker. A private child of the repository host: speaks
// JSON-RPC 2.0 with Content-Length framing over stdin/stdout, opens no ports,
// registers no global instance, and exits when stdin reaches EOF (the mandatory
// self-cleaning signal if the host dies). Roslyn lives here, not in the host.
internal static class Program
{
    public static async Task<int> Main()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        var connection = new JsonRpcConnection(stdin, stdout);
        var host = new CSharpWorkerHost(connection);

        connection.AddRequestHandler(ParserProtocolMethods.Initialize, host.HandleInitializeAsync);
        connection.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, host.HandleOpenWorkspaceAsync);
        connection.AddRequestHandlerWithId(ParserProtocolMethods.IndexWorkspace, host.HandleIndexWorkspaceAsync);
        connection.AddRequestHandlerWithId(ParserProtocolMethods.ApplyChanges, host.HandleApplyChangesAsync);
        connection.AddRequestHandler(ParserProtocolMethods.GetNativeSyntaxTree, host.HandleNativeSyntaxTreeAsync);
        connection.AddRequestHandler(ParserProtocolMethods.SaveState, host.HandleSaveStateAsync);
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
    }
}

/// <summary>Reads each request's JSON, hands it to the <see cref="WorkerSession"/> and writes its answer.</summary>
internal sealed class CSharpWorkerHost : IWorkerOutput
{
    private readonly JsonRpcConnection _connection;
    private readonly WorkerSession _session;

    public CSharpWorkerHost(JsonRpcConnection connection)
    {
        _connection = connection;
        _session = new WorkerSession(this);
    }

    public Task<JsonElement?> HandleInitializeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        _ = Deserialize(paramsElement, ParserProtocolJsonContext.Default.InitializeParams);
        return Task.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(WorkerSession.Initialize(), ParserProtocolJsonContext.Default.InitializeResult));
    }

    public async Task<JsonElement?> HandleOpenWorkspaceAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var result = await _session.OpenAsync(Deserialize(paramsElement, ParserProtocolJsonContext.Default.OpenWorkspaceParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.OpenWorkspaceResult);
    }

    public async Task<JsonElement?> HandleIndexWorkspaceAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        var result = await _session.IndexAsync(requestId, Deserialize(paramsElement, ParserProtocolJsonContext.Default.IndexWorkspaceParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.IndexWorkspaceResult);
    }

    public async Task<JsonElement?> HandleApplyChangesAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        var result = await _session.ApplyAsync(requestId, Deserialize(paramsElement, ParserProtocolJsonContext.Default.ApplyChangesParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.ApplyChangesResult);
    }

    public async Task<JsonElement?> HandleNativeSyntaxTreeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var result = await _session.SyntaxTreeAsync(Deserialize(paramsElement, ParserProtocolJsonContext.Default.NativeSyntaxTreeParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.NativeSyntaxTreeResult);
    }

    public async Task<JsonElement?> HandleSaveStateAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        var result = await _session.SaveStateAsync(Deserialize(paramsElement, ParserProtocolJsonContext.Default.SaveStateParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.SaveStateResult);
    }

    Task IWorkerOutput.ProgressAsync(AnalysisProgress progress, CancellationToken ct)
        => _connection.NotifyAsync(
            ParserProtocolMethods.AnalysisProgressNotification, progress, ParserProtocolJsonContext.Default.AnalysisProgress, ct);

    Task IWorkerOutput.DeltaAsync(AnalysisDelta delta, CancellationToken ct)
        => _connection.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta, ParserProtocolJsonContext.Default.AnalysisDelta, ct);

    /// <summary>Diagnostics go to stderr: stdout carries the protocol only.</summary>
    void IWorkerOutput.Diagnostic(string line) => Console.Error.WriteLine(line);

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
