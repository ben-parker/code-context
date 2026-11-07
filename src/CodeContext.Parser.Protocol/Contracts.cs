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

/// <summary>
/// What a worker supports. <see cref="PersistentState"/> is optional on the wire: absent reads as false,
/// and false is not written, so a worker that never declares it sends what it always sent. Only a worker
/// that declares it is sent <c>baseline</c> or <c>workspace/saveState</c>.
/// </summary>
public sealed record WorkerCapabilities(
    [property: JsonPropertyName("workspaceAnalysis")] bool WorkspaceAnalysis,
    [property: JsonPropertyName("incrementalUpdates")] bool IncrementalUpdates,
    [property: JsonPropertyName("semanticAnalysis")] bool SemanticAnalysis,
    [property: JsonPropertyName("nativeSyntaxTree")] bool NativeSyntaxTree,
    [property: JsonPropertyName("persistentState"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool PersistentState = false);

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
/// <paramref name="Files"/> is a set and must not contain duplicate paths under the
/// host platform's path comparer.
/// </summary>
public sealed record IndexWorkspaceParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("baseline")] IndexBaseline? Baseline = null);

/// <summary>
/// The facts the host already holds for this worker's workspace, offered at most once per worker process that
/// declared <see cref="WorkerCapabilities.PersistentState"/>: with its first mutating request, when that request
/// is a <c>workspace/index</c>. <paramref name="StateId"/> is opaque to the worker.
/// <paramref name="StatePath"/> is an absolute path the host computed, to a file this worker wrote on an
/// earlier <c>workspace/saveState</c>; the host recorded its id and SHA-256 (lowercase hex) beside the facts
/// and hands back the fingerprints the worker last sent. The file lives in the repository and must be
/// treated as untrusted bytes until its hash matches.
/// </summary>
public sealed record IndexBaseline(
    [property: JsonPropertyName("statePath")] string StatePath,
    [property: JsonPropertyName("stateId")] string StateId,
    [property: JsonPropertyName("stateSha256")] string StateSha256,
    [property: JsonPropertyName("fingerprints")] IReadOnlyList<UnitFingerprint> Fingerprints);

/// <summary>One unit's fingerprint, computed by the worker and opaque to the host, which stores it and hands it back.</summary>
public sealed record UnitFingerprint(
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// <paramref name="Baseline"/> answers a request that carried one, and only such a request: one of
/// <see cref="BaselineOutcomes"/>. Accepted means the deltas were file-scoped against the baseline;
/// rejected means they replaced the workspace, as for a request without one.
/// </summary>
public sealed record IndexWorkspaceResult(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("deltasEmitted")] int DeltasEmitted,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("baseline")] string? Baseline = null,
    [property: JsonPropertyName("baselineReason")] string? BaselineReason = null);

public static class BaselineOutcomes
{
    /// <summary>The worker restored its state; the request's deltas are file-scoped (<c>replacesWorkspace:false</c>).</summary>
    public const string Accepted = "accepted";

    /// <summary>The worker could not use the baseline; the request's deltas replace the workspace.</summary>
    public const string Rejected = "rejected";
}

/// <summary>
/// Asks a worker that declared <see cref="WorkerCapabilities.PersistentState"/> to save what it needs to
/// restore the facts it has emitted to <paramref name="StatePath"/>, an absolute path the host chose, under
/// <paramref name="StateId"/>. Sent only between mutations, so the state describes exactly the facts the host
/// holds. The worker writes <c>&lt;statePath&gt;.tmp</c>, flushes it to disk, renames it over the path and
/// answers when the file is durable.
/// </summary>
public sealed record SaveStateParams(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("statePath")] string StatePath,
    [property: JsonPropertyName("stateId")] string StateId);

/// <summary>
/// <paramref name="Saved"/> false, or an error, means no state for this capture. When saved,
/// <paramref name="Bytes"/> is the file's length and <paramref name="Sha256"/> the lowercase hex SHA-256 of its
/// bytes; the host records both and checks the length.
/// </summary>
public sealed record SaveStateResult(
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("stateId")] string StateId,
    [property: JsonPropertyName("saved")] bool Saved,
    [property: JsonPropertyName("bytes")] long? Bytes = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null);

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
/// different workers can never collide. A symbol from outside the workspace that the code
/// uses is a stub: <c>metadata.external</c> is <c>"true"</c>, <c>metadata.assembly</c> names
/// where it came from, and <c>filePath</c> is a virtual <c>external:</c> path, which goes
/// through file-scoped replacement like any other file.
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
/// How much of one project's declared context a worker found. A worker with no notion of a
/// project sends nothing. <c>state</c> is one of <see cref="ProjectCoverageStates"/>.
/// </summary>
public sealed record ProjectCoverage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("targetFramework")] string? TargetFramework = null,
    [property: JsonPropertyName("inactiveTargetFrameworks")] IReadOnlyList<string>? InactiveTargetFrameworks = null,
    [property: JsonPropertyName("missingReferences")] IReadOnlyList<string>? MissingReferences = null,
    [property: JsonPropertyName("analyzerPackages")] IReadOnlyList<string>? AnalyzerPackages = null,
    [property: JsonPropertyName("unresolvedNames")] int UnresolvedNames = 0,
    [property: JsonPropertyName("commonUnresolvedNames")] IReadOnlyList<string>? CommonUnresolvedNames = null,
    [property: JsonPropertyName("notes")] IReadOnlyList<string>? Notes = null);

public static class ProjectCoverageStates
{
    /// <summary>Everything the project declares was found.</summary>
    public const string Complete = "complete";

    /// <summary>Analyzed as its own project, but some of what it declares was not found.</summary>
    public const string Partial = "partial";

    /// <summary>Analyzed without knowing its project, against the worker's own defaults.</summary>
    public const string Fallback = "fallback";
}

/// <summary>
/// Live progress for a <c>workspace/index</c> request. This reports analysis work only;
/// graph facts remain buffered until the request's terminal delta commits atomically.
/// </summary>
public sealed record AnalysisProgress(
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("parserVersion")] string ParserVersion,
    [property: JsonPropertyName("workspaceId")] string WorkspaceId,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("requestId")] long RequestId,
    [property: JsonPropertyName("filesProcessed")] int FilesProcessed,
    [property: JsonPropertyName("filesTotal")] int FilesTotal,
    [property: JsonPropertyName("currentFile"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? CurrentFile);

/// <summary>
/// A batch of normalized facts published by a worker (worker → host notification).
/// workspace/index responses replace every fact this parser owns in the workspace
/// (<c>replacesWorkspace:true</c>). workspace/applyChanges responses are file-scoped
/// (<c>replacesWorkspace:false</c>): they carry only the facts of files whose emitted
/// facts changed — including untouched files whose facts changed through cross-file
/// resolution — and <c>replacesFiles</c> lists those files plus any removed since the
/// last emission, as byte-for-byte the same strings the nodes carry in
/// <c>filePath</c> (the host matches them raw, with no normalization). A file listed
/// with no facts is deleted. Every chunk of one request must agree on
/// <c>replacesWorkspace</c> (the host ORs it) and should repeat the full
/// <c>replacesFiles</c> list (the host unions it). A worker that declared
/// <see cref="WorkerCapabilities.PersistentState"/> sends <c>fingerprints</c> on the first chunk of every
/// request; the host keeps the last set per shard and a request that carries none clears it.
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
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<ProtocolDiagnostic>? Diagnostics = null,
    [property: JsonPropertyName("coverage")] IReadOnlyList<ProjectCoverage>? Coverage = null,
    [property: JsonPropertyName("fingerprints")] IReadOnlyList<UnitFingerprint>? Fingerprints = null);
