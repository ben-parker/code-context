using System.Text.Json;
using CodeContext.CSharp.Worker.Memory;
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
        using var memoryTrim = CreateMemoryTrim();
        CSharpWorkerHost? started = null;
        using var boundStateRelease = CreateBoundStateRelease(
            BoundStateRelease.SettingsFor(memoryTrim.Enabled), () => started?.ReleaseBoundState() ?? 0, Console.Error.WriteLine);
        var host = started = new CSharpWorkerHost(connection, memoryTrim, boundStateRelease);

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

    /// <summary>
    /// The quiet-time memory trim (<see cref="QuietTimeTrim"/>), configured by the environment variable
    /// <c>CodeContext__MemoryTrimSeconds</c>, which the host forwards from its own <c>CodeContext:MemoryTrimSeconds</c>:
    /// absent means 15 s, 0 turns it off. Its one line per trim goes to stderr.
    /// </summary>
    /// <summary>
    /// The release of bound compilations (<see cref="BoundStateRelease"/>), on the trim's own clockwork: after the
    /// quiet time, and only when enough was allocated since the last one for there to be something bound, it
    /// releases and then collects as a trim does, so the memory goes back to the system. One line each to stderr.
    /// </summary>
    internal static QuietTimeTrim CreateBoundStateRelease(
        QuietTimeTrimSettings settings, Func<int> release, Action<string> log, TimeProvider? time = null, Func<long>? allocatedBytes = null)
    {
        var units = 0;
        return new QuietTimeTrim(
            settings,
            time ?? TimeProvider.System,
            allocatedBytes,
            trim: () =>
            {
                units = release();
                return QuietTimeTrim.CompactAndDecommit();
            },
            trimmed: result => log(
                $"release: bound state of {units} unit(s), working set {result.WorkingSetBefore / 1048576.0:F0} MB -> {result.WorkingSetAfter / 1048576.0:F0} MB, pause {result.Pause.TotalMilliseconds:F0} ms"));
    }

    internal static QuietTimeTrim CreateMemoryTrim()
    {
        var text = Environment.GetEnvironmentVariable(QuietTimeTrimSettings.EnvironmentVariable);
        var settings = QuietTimeTrimSettings.FromSeconds(text);
        if (settings is null)
        {
            Console.Error.WriteLine($"csharp-worker: ignoring {QuietTimeTrimSettings.EnvironmentVariable}='{text}': not a valid value; keeping {QuietTimeTrimSettings.DefaultQuietSeconds}.");
            settings = new QuietTimeTrimSettings();
        }
        return new QuietTimeTrim(settings, TimeProvider.System, trimmed: result => Console.Error.WriteLine(
            $"trim: working set {result.WorkingSetBefore / 1048576.0:F0} MB -> {result.WorkingSetAfter / 1048576.0:F0} MB, pause {result.Pause.TotalMilliseconds:F0} ms"));
    }
}

/// <summary>
/// When the worker lets go of what its compilations have bound. A walk of every file of a project leaves the
/// project's whole symbol table behind (on a large solution, a quarter of the live heap after a declaration
/// edit), and nothing uses it until the next edit, which rebuilds only what it touches. So after a longer quiet
/// time than the memory trim's, the compilations are made over unbound and a trim follows to return the memory.
/// The first edit afterwards binds a little more than it would have.
/// </summary>
internal static class BoundStateRelease
{
    /// <summary>
    /// Well past the trim's 15 s: a pause in typing should not cost the next edit its bindings, a break should.
    /// </summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(60);

    /// <summary>On with the memory trim and off with it (<c>CodeContext__MemoryTrimSeconds=0</c>): one switch for both.</summary>
    public static QuietTimeTrimSettings SettingsFor(bool trimEnabled)
        => trimEnabled
            ? new QuietTimeTrimSettings { QuietPeriod = QuietPeriod, MinInterval = QuietPeriod }
            : QuietTimeTrimSettings.Disabled;
}

/// <summary>Reads each request's JSON, hands it to the <see cref="WorkerSession"/> and writes its answer.</summary>
internal sealed class CSharpWorkerHost : IWorkerOutput
{
    private readonly JsonRpcConnection _connection;
    private readonly WorkerSession _session;
    private readonly QuietTimeTrim? _memoryTrim;
    private readonly QuietTimeTrim? _boundStateRelease;

    public CSharpWorkerHost(JsonRpcConnection connection, QuietTimeTrim? memoryTrim = null, QuietTimeTrim? boundStateRelease = null)
    {
        _connection = connection;
        _session = new WorkerSession(this);
        _memoryTrim = memoryTrim;
        _boundStateRelease = boundStateRelease;
    }

    public int ReleaseBoundState() => _session.ReleaseBoundState();

    /// <summary>Each request is work: neither a trim nor a release starts until it has answered.</summary>
    private Work Working() => new(_memoryTrim?.BeginActivity(), _boundStateRelease?.BeginActivity());

    private readonly struct Work(IDisposable? trim, IDisposable? release) : IDisposable
    {
        public void Dispose()
        {
            trim?.Dispose();
            release?.Dispose();
        }
    }

    public Task<JsonElement?> HandleInitializeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
        _ = Deserialize(paramsElement, ParserProtocolJsonContext.Default.InitializeParams);
        return Task.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(WorkerSession.Initialize(), ParserProtocolJsonContext.Default.InitializeResult));
    }

    public async Task<JsonElement?> HandleOpenWorkspaceAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
        var result = await _session.OpenAsync(Deserialize(paramsElement, ParserProtocolJsonContext.Default.OpenWorkspaceParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.OpenWorkspaceResult);
    }

    public async Task<JsonElement?> HandleIndexWorkspaceAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
        var result = await _session.IndexAsync(requestId, Deserialize(paramsElement, ParserProtocolJsonContext.Default.IndexWorkspaceParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.IndexWorkspaceResult);
    }

    public async Task<JsonElement?> HandleApplyChangesAsync(long requestId, JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
        var result = await _session.ApplyAsync(requestId, Deserialize(paramsElement, ParserProtocolJsonContext.Default.ApplyChangesParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.ApplyChangesResult);
    }

    public async Task<JsonElement?> HandleNativeSyntaxTreeAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
        var result = await _session.SyntaxTreeAsync(Deserialize(paramsElement, ParserProtocolJsonContext.Default.NativeSyntaxTreeParams), ct);
        return JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.NativeSyntaxTreeResult);
    }

    public async Task<JsonElement?> HandleSaveStateAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        using var work = Working();
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
