using System.IO.Pipelines;
using System.Text.Json;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Exercises the endpoint over in-memory duplex pipes: one connection plays the host,
/// the other the worker, exactly as they would across process stdio.
/// </summary>
public class JsonRpcConnectionTests : IAsyncLifetime
{
    private readonly Pipe _hostToWorker = new();
    private readonly Pipe _workerToHost = new();
    private JsonRpcConnection _host = null!;
    private JsonRpcConnection _worker = null!;

    public Task InitializeAsync()
    {
        _host = new JsonRpcConnection(_workerToHost.Reader.AsStream(), _hostToWorker.Writer.AsStream());
        _worker = new JsonRpcConnection(_hostToWorker.Reader.AsStream(), _workerToHost.Writer.AsStream());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _worker.DisposeAsync();
    }

    private void StartBoth()
    {
        _ = _host.StartAsync();
        _ = _worker.StartAsync();
    }

    [Fact]
    public async Task Invoke_RoundTripsTypedRequestAndResult()
    {
        _worker.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, (p, _) =>
        {
            var open = p!.Value.Deserialize(ParserProtocolJsonContext.Default.OpenWorkspaceParams)!;
            var result = new OpenWorkspaceResult(open.WorkspaceId, Opened: true, Message: "hello");
            return Task.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement(result, ParserProtocolJsonContext.Default.OpenWorkspaceResult));
        });
        StartBoth();

        var result = await new ParserWorkerClient(_host).OpenWorkspaceAsync(
            new OpenWorkspaceParams("ws-1", "/repo", [], []));

        Assert.True(result.Opened);
        Assert.Equal("ws-1", result.WorkspaceId);
        Assert.Equal("hello", result.Message);
    }

    [Fact]
    public async Task Invoke_UnknownMethod_ThrowsMethodNotFound()
    {
        StartBoth();

        var ex = await Assert.ThrowsAsync<JsonRpcRemoteException>(
            () => new ParserWorkerClient(_host).OpenWorkspaceAsync(new OpenWorkspaceParams("ws", "/", [], [])));

        Assert.Equal(ParserProtocolErrorCodes.MethodNotFound, ex.Code);
    }

    [Fact]
    public async Task Notification_DispatchesToHandler()
    {
        var received = new TaskCompletionSource<AnalysisDelta>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.AddNotificationHandler(ParserProtocolMethods.AnalysisDeltaNotification, p =>
        {
            received.TrySetResult(p!.Value.Deserialize(ParserProtocolJsonContext.Default.AnalysisDelta)!);
            return Task.CompletedTask;
        });
        StartBoth();

        var delta = new AnalysisDelta("fake", "1.0", "ws-1", 1, 1, true, [], [], [], true);
        await _worker.NotifyAsync(
            ParserProtocolMethods.AnalysisDeltaNotification, delta,
            ParserProtocolJsonContext.Default.AnalysisDelta);

        var observed = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ws-1", observed.WorkspaceId);
    }

    [Fact]
    public async Task Cancel_ReachesHandlerToken_AndInvokerObservesCancellation()
    {
        var handlerSawCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker.AddRequestHandler(ParserProtocolMethods.IndexWorkspace, async (_, ct) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                handlerSawCancel.TrySetResult();
                throw;
            }
            return null;
        });
        StartBoth();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ParserWorkerClient(_host).IndexWorkspaceAsync(
                new IndexWorkspaceParams("ws-1", 1, []), cts.Token));

        await handlerSawCancel.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Eof_FailsPendingRequestsWithConnectionClosed()
    {
        _worker.AddRequestHandler(ParserProtocolMethods.IndexWorkspace,
            async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return null; });
        StartBoth();

        var pending = new ParserWorkerClient(_host).IndexWorkspaceAsync(new IndexWorkspaceParams("ws-1", 1, []));
        await Task.Delay(100); // let the request reach the worker
        await _worker.DisposeAsync(); // closes the worker→host stream: EOF at the host

        await Assert.ThrowsAsync<ParserConnectionClosedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task MalformedPayload_FaultsReadLoopWithProtocolViolation()
    {
        var readLoop = _host.StartAsync();

        var garbage = "not json"u8.ToArray();
        await HeaderFraming.WriteFrameAsync(_workerToHost.Writer.AsStream(), garbage);

        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => readLoop.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvalidJsonRpcEnvelope_FaultsInsteadOfBeingIgnored()
    {
        var readLoop = _host.StartAsync();
        var invalid = "{\"jsonrpc\":\"2.0\"}"u8.ToArray();
        await HeaderFraming.WriteFrameAsync(_workerToHost.Writer.AsStream(), invalid);

        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => readLoop.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
