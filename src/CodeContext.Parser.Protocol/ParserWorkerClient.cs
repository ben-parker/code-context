namespace CodeContext.Parser.Protocol;

/// <summary>
/// Typed host-side view of a worker connection: one method per protocol request.
/// Owns no process/lifecycle concerns — that's the supervisor's job.
/// </summary>
public sealed class ParserWorkerClient(JsonRpcConnection connection)
{
    public JsonRpcConnection Connection { get; } = connection;

    public Task<InitializeResult> InitializeAsync(InitializeParams parameters, CancellationToken ct = default)
        => Connection.InvokeAsync(
            ParserProtocolMethods.Initialize, parameters,
            ParserProtocolJsonContext.Default.InitializeParams,
            ParserProtocolJsonContext.Default.InitializeResult, ct);

    public Task<OpenWorkspaceResult> OpenWorkspaceAsync(OpenWorkspaceParams parameters, CancellationToken ct = default)
        => Connection.InvokeAsync(
            ParserProtocolMethods.OpenWorkspace, parameters,
            ParserProtocolJsonContext.Default.OpenWorkspaceParams,
            ParserProtocolJsonContext.Default.OpenWorkspaceResult, ct);

    public Task<IndexWorkspaceResult> IndexWorkspaceAsync(
        IndexWorkspaceParams parameters,
        CancellationToken ct = default,
        Action<long>? requestStarted = null)
        => Connection.InvokeAsync(
            ParserProtocolMethods.IndexWorkspace, parameters,
            ParserProtocolJsonContext.Default.IndexWorkspaceParams,
            ParserProtocolJsonContext.Default.IndexWorkspaceResult, ct, requestStarted);

    public Task<ApplyChangesResult> ApplyChangesAsync(
        ApplyChangesParams parameters,
        CancellationToken ct = default,
        Action<long>? requestStarted = null)
        => Connection.InvokeAsync(
            ParserProtocolMethods.ApplyChanges, parameters,
            ParserProtocolJsonContext.Default.ApplyChangesParams,
            ParserProtocolJsonContext.Default.ApplyChangesResult, ct, requestStarted);

    public Task<NativeSyntaxTreeResult> GetNativeSyntaxTreeAsync(
        NativeSyntaxTreeParams parameters,
        CancellationToken ct = default)
        => Connection.InvokeAsync(
            ParserProtocolMethods.GetNativeSyntaxTree, parameters,
            ParserProtocolJsonContext.Default.NativeSyntaxTreeParams,
            ParserProtocolJsonContext.Default.NativeSyntaxTreeResult, ct);

    public Task ShutdownAsync(CancellationToken ct = default)
        => Connection.InvokeVoidAsync(ParserProtocolMethods.Shutdown, ct);
}
