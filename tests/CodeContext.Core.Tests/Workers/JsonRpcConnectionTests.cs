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
    public async Task InvokeVoid_AcceptsTheNullResultTheSchemaGivesShutdown_AndTheConnectionStaysUp()
    {
        // parser-protocol.schema.json: "shutdown": { "params": null, "result": null }. A handler that returns
        // nothing is answered with exactly that: {"jsonrpc":"2.0","id":n,"result":null}.
        _worker.AddRequestHandler(ParserProtocolMethods.Shutdown, (_, _) => Task.FromResult<JsonElement?>(null));
        _worker.AddRequestHandler(ParserProtocolMethods.OpenWorkspace, (_, _) => Task.FromResult<JsonElement?>(
            JsonSerializer.SerializeToElement(new OpenWorkspaceResult("ws", Opened: true, Message: null),
                ParserProtocolJsonContext.Default.OpenWorkspaceResult)));
        StartBoth();

        await _host.InvokeVoidAsync(ParserProtocolMethods.Shutdown).WaitAsync(TimeSpan.FromSeconds(30));

        // A refused envelope would have ended the read loop and failed this request.
        var open = await new ParserWorkerClient(_host).OpenWorkspaceAsync(new OpenWorkspaceParams("ws", "/", [], []))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(open.Opened);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":null}", true, false)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", true, false)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32603,\"message\":\"x\"}}", false, true)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}", false, false)]
    public void ResultMember_IsPresentWhenWrittenAsNull_AndAbsentWhenLeftOut(string json, bool hasResult, bool hasError)
    {
        var message = JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.JsonRpcMessage)!;
        Assert.Equal(hasResult, message.Result is not null);
        Assert.Equal(hasError, message.Error is not null);
        if (json.Contains("\"result\":null", StringComparison.Ordinal))
        {
            Assert.Equal(JsonValueKind.Null, message.Result!.Value.ValueKind);
            // And it is written back as it was read.
            Assert.Contains("\"result\":null",
                JsonSerializer.Serialize(message, ParserProtocolJsonContext.Default.JsonRpcMessage), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ARequest_IsWrittenWithoutAResultMember()
    {
        var json = JsonSerializer.Serialize(new JsonRpcMessage { Id = 1, Method = "x" }, ParserProtocolJsonContext.Default.JsonRpcMessage);
        Assert.DoesNotContain("result", json, StringComparison.Ordinal);
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

        // The suite shares this machine and is heavier now; five seconds was missed under load.
        var observed = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("ws-1", observed.WorkspaceId);
    }

    [Fact]
    public async Task ProgressNotification_RoundTripsTypedPayload()
    {
        var received = new TaskCompletionSource<AnalysisProgress>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _host.AddNotificationHandler(ParserProtocolMethods.AnalysisProgressNotification, p =>
        {
            received.TrySetResult(
                p!.Value.Deserialize(ParserProtocolJsonContext.Default.AnalysisProgress)!);
            return Task.CompletedTask;
        });
        StartBoth();

        var progress = new AnalysisProgress(
            "fake", "1.0", "ws-1", 3, 7, 5, 10, "/repo/Five.fake");
        await _worker.NotifyAsync(
            ParserProtocolMethods.AnalysisProgressNotification, progress,
            ParserProtocolJsonContext.Default.AnalysisProgress);

        Assert.Equal(progress, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void EmptyProgress_SerializesRequiredNullCurrentFile()
    {
        var progress = new AnalysisProgress("fake", "1.0", "ws-1", 1, 1, 0, 0, null);

        var json = JsonSerializer.Serialize(
            progress, ParserProtocolJsonContext.Default.AnalysisProgress);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("currentFile").ValueKind);
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

    // A null "result" was invisible to the reader before "result": null was read as present (it must be,
    // for shutdown's answer). Two messages that carry one were tolerated then and stay tolerated: a
    // JSON-RPC 1.0 style error response, which also writes "result": null, and a request or notification,
    // where a result member means nothing and is ignored.

    [Fact]
    public async Task AnErrorResponse_WithANullResult_IsAnErrorResponse_AndTheConnectionStaysUp()
    {
        var readLoop = _host.StartAsync();
        using var requests = new FrameReader(_hostToWorker.Reader.AsStream());

        var first = _host.InvokeVoidAsync(ParserProtocolMethods.Shutdown);
        var id = await ReadRequestIdAsync(requests);
        await WriteRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":null,\"error\":{{\"code\":-32603,\"message\":\"boom\"}}}}");
        var error = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", error.Message);

        var second = _host.InvokeVoidAsync(ParserProtocolMethods.Shutdown);
        id = await ReadRequestIdAsync(requests);
        await WriteRawAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":null}}");
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(readLoop.IsCompleted);
    }

    [Fact]
    public async Task ARequestAndANotification_WithANullResult_AreHandled_AndTheConnectionStaysUp()
    {
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.AddNotificationHandler("test/ping", _ => { notified.TrySetResult(); return Task.CompletedTask; });
        _host.AddRequestHandler("test/echo", (_, _) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(7)));
        var readLoop = _host.StartAsync();
        using var answers = new FrameReader(_hostToWorker.Reader.AsStream());

        await WriteRawAsync("{\"jsonrpc\":\"2.0\",\"method\":\"test/ping\",\"result\":null}");
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WriteRawAsync("{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"test/echo\",\"result\":null}");
        using var frame = (await answers.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
        var answer = JsonSerializer.Deserialize(frame.Payload, ParserProtocolJsonContext.Default.JsonRpcMessage)!;
        Assert.Equal(41, answer.Id);
        Assert.Equal(7, answer.Result!.Value.GetInt32());
        Assert.False(readLoop.IsCompleted);
    }

    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"error\":{\"code\":-32603,\"message\":\"x\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":0,\"error\":{\"code\":-32603,\"message\":\"x\"}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"test/ping\",\"result\":{}}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"method\":\"test/ping\",\"error\":{\"code\":-32603,\"message\":\"x\"}}")]
    public async Task AResponseWithAResultAndAnError_OrWithNeither_OrAMethodWithAValue_IsStillRefused(string json)
    {
        var readLoop = _host.StartAsync();
        await WriteRawAsync(json);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(() => readLoop.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private Task WriteRawAsync(string json)
        => HeaderFraming.WriteFrameAsync(_workerToHost.Writer.AsStream(), System.Text.Encoding.UTF8.GetBytes(json));

    private static async Task<long> ReadRequestIdAsync(FrameReader reader)
    {
        using var frame = (await reader.ReadFrameAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))!.Value;
        return JsonSerializer.Deserialize(frame.Payload, ParserProtocolJsonContext.Default.JsonRpcMessage)!.Id!.Value;
    }
}
