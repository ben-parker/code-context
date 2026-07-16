using System.Text.Json.Serialization;

namespace CodeContext.Parser.Protocol;

/// <summary>Parameters for the <c>initialize</c> handshake (host → worker).</summary>
public sealed record InitializeParams(
    [property: JsonPropertyName("hostName")] string HostName,
    [property: JsonPropertyName("hostVersion")] string HostVersion,
    [property: JsonPropertyName("rootPath")] string RootPath,
    [property: JsonPropertyName("minProtocolVersion")] int MinProtocolVersion,
    [property: JsonPropertyName("maxProtocolVersion")] int MaxProtocolVersion,
    [property: JsonPropertyName("configuration")] IReadOnlyDictionary<string, string>? Configuration = null);

/// <summary>
/// Source-span semantics declared by the worker so the host can interpret positions:
/// the base index for lines/columns and whether end positions are inclusive.
/// </summary>
public sealed record SpanSemantics(
    [property: JsonPropertyName("lineBase")] int LineBase,
    [property: JsonPropertyName("columnBase")] int ColumnBase,
    [property: JsonPropertyName("endIsInclusive")] bool EndIsInclusive);

public sealed record WorkerCapabilities(
    [property: JsonPropertyName("workspaceAnalysis")] bool WorkspaceAnalysis,
    [property: JsonPropertyName("incrementalUpdates")] bool IncrementalUpdates,
    [property: JsonPropertyName("semanticAnalysis")] bool SemanticAnalysis,
    [property: JsonPropertyName("nativeSyntaxTree")] bool NativeSyntaxTree);

/// <summary>Result of the <c>initialize</c> handshake (worker → host).</summary>
public sealed record InitializeResult(
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("parserVersion")] string ParserVersion,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("languages")] IReadOnlyList<string> Languages,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string> Extensions,
    [property: JsonPropertyName("projectMarkers")] IReadOnlyList<string> ProjectMarkers,
    [property: JsonPropertyName("capabilities")] WorkerCapabilities Capabilities,
    [property: JsonPropertyName("spanSemantics")] SpanSemantics SpanSemantics);

public sealed record OpenWorkspaceParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("rootPath")] string RootPath,
    [property: JsonPropertyName("projectMarkers")] IReadOnlyList<string> ProjectMarkers,
    [property: JsonPropertyName("approvedFiles")] IReadOnlyList<string> ApprovedFiles);

public sealed record OpenWorkspaceResult(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("opened")] bool Opened,
    [property: JsonPropertyName("message")] string? Message = null);

/// <summary>
/// Starts (or replaces) a complete generation for a workspace. The worker responds
/// after emitting all <c>analysis/delta</c> notifications for the generation.
/// </summary>
public sealed record IndexWorkspaceParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files);

public sealed record IndexWorkspaceResult(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("deltasEmitted")] int DeltasEmitted,
    [property: JsonPropertyName("complete")] bool Complete);

public static class FileChangeKinds
{
    public const string Created = "created";
    public const string Changed = "changed";
    public const string Deleted = "deleted";
    public const string Renamed = "renamed";
}

public sealed record FileChangeDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("changeType")] string ChangeType,
    [property: JsonPropertyName("oldPath")] string? OldPath = null);

public sealed record ApplyChangesParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("changes")] IReadOnlyList<FileChangeDto> Changes);

public sealed record ApplyChangesResult(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("deltasEmitted")] int DeltasEmitted,
    [property: JsonPropertyName("complete")] bool Complete);

public sealed record CancelParams(
    [property: JsonPropertyName("requestId")] long RequestId);

/// <summary>
/// Requests a parser-native syntax tree for an approved file. <c>start</c> and
/// <c>length</c> are optional UTF-16 offsets; when supplied, the worker returns the
/// smallest syntax node containing that range. The tree shape is deliberately
/// parser-specific and identified by <see cref="NativeSyntaxTreeResult.Format"/>.
/// </summary>
public sealed record NativeSyntaxTreeParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("start")] int? Start = null,
    [property: JsonPropertyName("length")] int? Length = null,
    [property: JsonPropertyName("maxDepth")] int MaxDepth = 8);

/// <summary>
/// Common envelope around a language-native tree. Consumers must branch on
/// <c>parserId</c> and <c>format</c>; the payload is supplemental to, and never
/// persisted in, the normalized relationship graph.
/// </summary>
public sealed record NativeSyntaxTreeResult(
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("parserVersion")] string ParserVersion,
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("tree")] System.Text.Json.JsonElement Tree,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("view")] string View = "full");

/// <summary>
/// A normalized graph node. Stored spans are canonical zero-based, end-exclusive;
/// the host converts from the worker's declared <see cref="SpanSemantics"/>. <c>id</c>
/// must be stable and namespaced by language and
/// parser/workspace ownership (e.g. <c>csharp:ws-1:MyNs.MyClass</c>) so facts from
/// different workers can never collide.
/// </summary>
public sealed record ProtocolNode(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("startLine")] int StartLine = 0,
    [property: JsonPropertyName("endLine")] int EndLine = 0,
    [property: JsonPropertyName("startColumn")] int StartColumn = 0,
    [property: JsonPropertyName("endColumn")] int EndColumn = 0,
    [property: JsonPropertyName("namespace")] string? Namespace = null,
    [property: JsonPropertyName("visibility")] string? Visibility = null,
    [property: JsonPropertyName("signature")] string? Signature = null,
    [property: JsonPropertyName("returnType")] string? ReturnType = null,
    [property: JsonPropertyName("parameters")] string? Parameters = null,
    [property: JsonPropertyName("modifiers")] string? Modifiers = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// A normalized graph edge. <c>kind</c> is a versioned string (e.g. "calls",
/// "implements"), not a closed cross-language enum.
/// </summary>
public sealed record ProtocolEdge(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("targetId")] string TargetId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>A symbolic reference the worker could not resolve; input to later linker stages.</summary>
public sealed record UnresolvedReference(
    [property: JsonPropertyName("sourceNodeId")] string SourceNodeId,
    [property: JsonPropertyName("targetName")] string TargetName,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record ProtocolDiagnostic(
    [property: JsonPropertyName("filePath")] string? FilePath,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// A batch of normalized facts published by a worker (worker → host notification).
/// The delta replaces either the listed files' facts or — when
/// <c>replacesWorkspace</c> — every fact this parser owns in the workspace.
/// </summary>
public sealed record AnalysisDelta(
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("parserVersion")] string ParserVersion,
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("requestId")] long RequestId,
    [property: JsonPropertyName("replacesWorkspace")] bool ReplacesWorkspace,
    [property: JsonPropertyName("replacesFiles")] IReadOnlyList<string> ReplacesFiles,
    [property: JsonPropertyName("nodes")] IReadOnlyList<ProtocolNode> Nodes,
    [property: JsonPropertyName("edges")] IReadOnlyList<ProtocolEdge> Edges,
    [property: JsonPropertyName("isLastForRequest")] bool IsLastForRequest,
    [property: JsonPropertyName("unresolvedReferences")] IReadOnlyList<UnresolvedReference>? UnresolvedReferences = null,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ProtocolDiagnostic>? Diagnostics = null);
