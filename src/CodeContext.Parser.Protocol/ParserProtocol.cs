namespace CodeContext.Parser.Protocol;

/// <summary>
/// Protocol-wide constants. The wire format is JSON-RPC 2.0 with Content-Length
/// framing over stdin/stdout; the canonical contract is
/// <c>protocol/parser-protocol.schema.json</c>, which external workers implement
/// directly — this assembly is a convenience for .NET hosts/workers only.
/// </summary>
public static class ParserProtocol
{
    /// <summary>Current protocol version spoken by this assembly.</summary>
    public const int Version = 1;

    public const string JsonRpcVersion = "2.0";
}

public static class ParserProtocolMethods
{
    /// <summary>Handshake: version negotiation, parser identity, capabilities.</summary>
    public const string Initialize = "initialize";

    /// <summary>Opens a logical workspace (project root) inside the watched tree.</summary>
    public const string OpenWorkspace = "workspace/open";

    /// <summary>Starts or replaces a complete workspace generation.</summary>
    public const string IndexWorkspace = "workspace/index";

    /// <summary>Applies an ordered batch of file changes to an open workspace.</summary>
    public const string ApplyChanges = "workspace/applyChanges";

    /// <summary>Requests graceful worker exit; the worker must also exit on stdin EOF.</summary>
    public const string Shutdown = "shutdown";

    /// <summary>Notification (host → worker): cooperatively cancel an in-flight request.</summary>
    public const string CancelNotification = "$/cancel";

    /// <summary>Notification (worker → host): a batch of normalized graph facts.</summary>
    public const string AnalysisDeltaNotification = "analysis/delta";

    /// <summary>Notification (worker → host): live file-analysis progress for workspace/index.</summary>
    public const string AnalysisProgressNotification = "analysis/progress";

    /// <summary>Optional capability: language-native syntax tree for one file.</summary>
    public const string GetNativeSyntaxTree = "syntaxTree/get";
}

/// <summary>JSON-RPC 2.0 error codes used by the protocol.</summary>
public static class ParserProtocolErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>The request was cancelled via <see cref="ParserProtocolMethods.CancelNotification"/>.</summary>
    public const int RequestCancelled = -32800;

    /// <summary>The host and worker share no compatible protocol version.</summary>
    public const int IncompatibleProtocolVersion = -32000;
}
