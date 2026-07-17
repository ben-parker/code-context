using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CodeContext.Parser.Protocol;

/// <summary>
/// A bidirectional JSON-RPC 2.0 endpoint over a framed byte stream pair. Used by the
/// host (sends requests, receives <c>analysis/delta</c> notifications) and by workers
/// (serves requests, honors <c>$/cancel</c>).
///
/// Dispatch rules: notifications are awaited inline on the read loop so a worker's
/// deltas are fully processed before the request response that follows them;
/// incoming requests run on the thread pool so a long handler (e.g. indexing) cannot
/// block cancellation notifications.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly FrameReader _frameReader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcMessage>> _pendingRequests = new();
    private readonly ConcurrentDictionary<string, Func<long, JsonElement?, CancellationToken, Task<JsonElement?>>> _requestHandlers = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement?, Task>> _notificationHandlers = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inFlightIncoming = new();
    private readonly CancellationTokenSource _disposal = new();
    private readonly object _startLock = new();
    private long _nextRequestId;
    private Task? _readLoop;

    /// <summary>Grace period to wait for the remote's response after sending <c>$/cancel</c>.</summary>
    public TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    public JsonRpcConnection(Stream input, Stream output, int maxPayloadBytes = HeaderFraming.DefaultMaxPayloadBytes)
    {
        _input = input;
        _output = output;
        _frameReader = new FrameReader(input, maxPayloadBytes);
    }

    public void AddRequestHandler(string method, Func<JsonElement?, CancellationToken, Task<JsonElement?>> handler)
        => _requestHandlers[method] = (_, parameters, ct) => handler(parameters, ct);

    /// <summary>
    /// Registers a handler that also receives the JSON-RPC request id. Workers use
    /// this id in streamed <c>analysis/delta.requestId</c> messages so the host can
    /// reject unsolicited or mis-correlated facts.
    /// </summary>
    public void AddRequestHandlerWithId(
        string method,
        Func<long, JsonElement?, CancellationToken, Task<JsonElement?>> handler)
        => _requestHandlers[method] = handler;

    public void AddNotificationHandler(string method, Func<JsonElement?, Task> handler)
        => _notificationHandlers[method] = handler;

    /// <summary>
    /// Starts the read loop. The returned task completes on EOF, faults with
    /// <see cref="ParserProtocolViolationException"/> on malformed input, and always
    /// fails any still-pending outgoing requests on the way out.
    /// </summary>
    public Task StartAsync()
    {
        lock (_startLock)
        {
            _readLoop ??= Task.Run(ReadLoopAsync);
            return _readLoop;
        }
    }

    /// <summary>The running read-loop task, if <see cref="StartAsync"/> was called.</summary>
    public Task? Completion => _readLoop;

    private async Task ReadLoopAsync()
    {
        Exception? fault = null;
        try
        {
            while (true)
            {
                var frame = await _frameReader.ReadFrameAsync(_disposal.Token).ConfigureAwait(false);
                if (frame is not { } lease)
                {
                    break; // clean EOF
                }

                JsonRpcMessage? message;
                // The lease's pooled buffer is only borrowed for the synchronous deserialize:
                // deserialization copies any JsonElement bytes into their own document, so the
                // buffer can be returned to the pool immediately afterwards.
                try
                {
                    message = JsonSerializer.Deserialize(lease.Payload, ParserProtocolJsonContext.Default.JsonRpcMessage);
                }
                catch (JsonException ex)
                {
                    throw new ParserProtocolViolationException("Frame payload is not valid JSON-RPC.", ex);
                }
                finally
                {
                    lease.Dispose();
                }
                if (message is null)
                {
                    throw new ParserProtocolViolationException("Frame payload deserialized to null.");
                }

                ValidateEnvelope(message);

                await DispatchAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            fault = ex;
            throw;
        }
        finally
        {
            // Pending requests learn *why* the conversation died, not just that it did:
            // a supervisor surfaces this reason as the session's failure detail.
            var reason = fault switch
            {
                null => "The JSON-RPC connection closed.",
                ParserProtocolViolationException => $"Malformed protocol output: {fault.Message}",
                _ => $"The JSON-RPC connection failed: {fault.Message}",
            };
            FailAllPending(new ParserConnectionClosedException(reason));
            foreach (var cts in _inFlightIncoming.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private static void ValidateEnvelope(JsonRpcMessage message)
    {
        if (!string.Equals(message.JsonRpc, ParserProtocol.JsonRpcVersion, StringComparison.Ordinal))
        {
            throw new ParserProtocolViolationException(
                $"Unsupported JSON-RPC version '{message.JsonRpc}'.");
        }

        var hasMethod = message.Method is not null;
        var hasId = message.Id is not null;
        var hasResult = message.Result is not null;
        var hasError = message.Error is not null;

        if (hasMethod)
        {
            if (string.IsNullOrWhiteSpace(message.Method) || hasResult || hasError)
            {
                throw new ParserProtocolViolationException("Invalid JSON-RPC request/notification envelope.");
            }
            return;
        }

        if (!hasId || hasResult == hasError)
        {
            throw new ParserProtocolViolationException(
                "A JSON-RPC response must have an id and exactly one of result or error.");
        }
    }

    private async Task DispatchAsync(JsonRpcMessage message)
    {
        if (message.IsResponse)
        {
            if (_pendingRequests.TryRemove(message.Id!.Value, out var pending))
            {
                pending.TrySetResult(message);
            }
            return;
        }

        if (message.IsNotification)
        {
            if (message.Method == ParserProtocolMethods.CancelNotification)
            {
                HandleCancelNotification(message.Params);
            }
            else if (_notificationHandlers.TryGetValue(message.Method!, out var handler))
            {
                await handler(message.Params).ConfigureAwait(false);
            }
            return;
        }

        if (message.IsRequest)
        {
            var id = message.Id!.Value;
            if (!_requestHandlers.TryGetValue(message.Method!, out var handler))
            {
                await TrySendErrorAsync(id, ParserProtocolErrorCodes.MethodNotFound,
                    $"Method '{message.Method}' is not supported.").ConfigureAwait(false);
                return;
            }

            // Register cancellation before scheduling the handler. Otherwise the read
            // loop can consume a following $/cancel notification before the task gets
            // a thread-pool turn, permanently losing the cancellation signal.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            if (!_inFlightIncoming.TryAdd(id, cts))
            {
                cts.Dispose();
                await TrySendErrorAsync(id, ParserProtocolErrorCodes.InvalidRequest,
                    $"Request id {id} is already in flight.").ConfigureAwait(false);
                return;
            }

            // Fire-and-forget with its own error boundary: handler outcomes are
            // reported to the remote as JSON-RPC responses, never as loop faults.
            _ = Task.Run(() => RunIncomingRequestAsync(message, handler, cts));
        }
    }

    private void HandleCancelNotification(JsonElement? paramsElement)
    {
        if (paramsElement is not { } element) return;
        CancelParams? cancel;
        try
        {
            cancel = element.Deserialize(ParserProtocolJsonContext.Default.CancelParams);
        }
        catch (JsonException)
        {
            return;
        }
        if (cancel is not null && _inFlightIncoming.TryGetValue(cancel.RequestId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task RunIncomingRequestAsync(
        JsonRpcMessage request,
        Func<long, JsonElement?, CancellationToken, Task<JsonElement?>> handler,
        CancellationTokenSource cts)
    {
        var id = request.Id!.Value;
        try
        {
            var result = await handler(id, request.Params, cts.Token).ConfigureAwait(false);
            await TrySendResponseAsync(new JsonRpcMessage { Id = id, Result = result ?? NullElement })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TrySendErrorAsync(id, ParserProtocolErrorCodes.RequestCancelled, "The request was cancelled.")
                .ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex)
        {
            await TrySendErrorAsync(id, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TrySendErrorAsync(id, ParserProtocolErrorCodes.InternalError, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _inFlightIncoming.TryRemove(id, out _);
            cts.Dispose();
        }
    }

    public async Task<TResult> InvokeAsync<TParams, TResult>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> paramsTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken ct = default,
        Action<long>? requestStarted = null)
    {
        var paramsElement = JsonSerializer.SerializeToElement(parameters, paramsTypeInfo);
        var response = await InvokeRawAsync(method, paramsElement, ct, requestStarted).ConfigureAwait(false);

        if (response.Error is { } error)
        {
            if (error.Code == ParserProtocolErrorCodes.RequestCancelled)
            {
                throw new OperationCanceledException(error.Message);
            }
            throw new JsonRpcRemoteException(error.Code, error.Message);
        }

        if (response.Result is not { } result || result.ValueKind == JsonValueKind.Null)
        {
            throw new ParserProtocolViolationException($"Response to '{method}' carried no result.");
        }
        return result.Deserialize(resultTypeInfo)
            ?? throw new ParserProtocolViolationException($"Response to '{method}' deserialized to null.");
    }

    /// <summary>Invokes a parameterless request whose successful result is <c>null</c> (e.g. <c>shutdown</c>).</summary>
    public async Task InvokeVoidAsync(string method, CancellationToken ct = default)
    {
        var response = await InvokeRawAsync(method, NullElement, ct, requestStarted: null).ConfigureAwait(false);
        if (response.Error is { } error)
        {
            throw new JsonRpcRemoteException(error.Code, error.Message);
        }
    }

    private async Task<JsonRpcMessage> InvokeRawAsync(
        string method,
        JsonElement paramsElement,
        CancellationToken ct,
        Action<long>? requestStarted)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = completion;

        try
        {
            requestStarted?.Invoke(id);
            await SendAsync(new JsonRpcMessage { Id = id, Method = method, Params = paramsElement }, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            throw;
        }

        try
        {
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation: tell the remote, then give it a grace period
            // to answer (with RequestCancelled or a late result) before giving up.
            await TryNotifyCancelAsync(id).ConfigureAwait(false);
            try
            {
                await completion.Task.WaitAsync(CancelGracePeriod).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Timed out or connection died; fall through to cancellation below.
            }
            _pendingRequests.TryRemove(id, out _);
            throw;
        }
    }

    private async Task TryNotifyCancelAsync(long requestId)
    {
        try
        {
            await NotifyAsync(
                ParserProtocolMethods.CancelNotification,
                new CancelParams(requestId),
                ParserProtocolJsonContext.Default.CancelParams).ConfigureAwait(false);
        }
        catch
        {
            // The connection may already be gone; cancellation still proceeds locally.
        }
    }

    public Task NotifyAsync<TParams>(
        string method,
        TParams parameters,
        JsonTypeInfo<TParams> paramsTypeInfo,
        CancellationToken ct = default)
    {
        var paramsElement = JsonSerializer.SerializeToElement(parameters, paramsTypeInfo);
        return SendAsync(new JsonRpcMessage { Method = method, Params = paramsElement }, ct);
    }

    private async Task TrySendResponseAsync(JsonRpcMessage response)
    {
        try
        {
            await SendAsync(response, _disposal.Token).ConfigureAwait(false);
        }
        catch
        {
            // The connection is closing; the remote can no longer observe the result.
        }
    }

    private Task TrySendErrorAsync(long id, int code, string message)
        => TrySendResponseAsync(new JsonRpcMessage
        {
            Id = id,
            Error = new JsonRpcError { Code = code, Message = message },
        });

    private async Task SendAsync(JsonRpcMessage message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, ParserProtocolJsonContext.Default.JsonRpcMessage);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await HeaderFraming.WriteFrameAsync(_output, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (var id in _pendingRequests.Keys.ToArray())
        {
            if (_pendingRequests.TryRemove(id, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _disposal.CancelAsync().ConfigureAwait(false);
        FailAllPending(new ParserConnectionClosedException("The JSON-RPC connection was disposed."));
        // Closing the output stream is the EOF signal that tells a worker to exit.
        try { _output.Dispose(); } catch { }
        try { _input.Dispose(); } catch { }
        if (_readLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { }
        }
        _disposal.Dispose();
        _writeLock.Dispose();
        _frameReader.Dispose();
    }
}
