using System.Text.Json.Serialization;
using System.Text.Json;
using CodeContext.Core.Services;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Serialization;

// DTOs for the graph repository's JSON compatibility contract.
public record NodeDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("start_col")] int StartCol,
    [property: JsonPropertyName("end_col")] int EndCol,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("return_type")] string? ReturnType,
    [property: JsonPropertyName("parameters")] string? Parameters,
    [property: JsonPropertyName("modifiers")] string? Modifiers,
    [property: JsonPropertyName("metrics")] string? Metrics,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("identifier")] string? Identifier = null
);

// Extended node DTOs for special cases
public record NodeWithEdgeInfoDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("start_col")] int StartCol,
    [property: JsonPropertyName("end_col")] int EndCol,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("return_type")] string? ReturnType,
    [property: JsonPropertyName("parameters")] string? Parameters,
    [property: JsonPropertyName("modifiers")] string? Modifiers,
    [property: JsonPropertyName("metrics")] string? Metrics,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("edge_info")] EdgeDto? EdgeInfo
);

public record NodeWithRelationshipTypeDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("start_col")] int StartCol,
    [property: JsonPropertyName("end_col")] int EndCol,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("return_type")] string? ReturnType,
    [property: JsonPropertyName("parameters")] string? Parameters,
    [property: JsonPropertyName("modifiers")] string? Modifiers,
    [property: JsonPropertyName("metrics")] string? Metrics,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("relationship_type")] string? RelationshipType
);

public record NodeWithRelationshipPathDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("file_path")] string? FilePath,
    [property: JsonPropertyName("start_line")] int StartLine,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("start_col")] int StartCol,
    [property: JsonPropertyName("end_col")] int EndCol,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("visibility")] string? Visibility,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("return_type")] string? ReturnType,
    [property: JsonPropertyName("parameters")] string? Parameters,
    [property: JsonPropertyName("modifiers")] string? Modifiers,
    [property: JsonPropertyName("metrics")] string? Metrics,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata,
    [property: JsonPropertyName("relationship_path")] List<EdgeDto>? RelationshipPath
);

public record EdgeDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("target_id")] string? TargetId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata
);

public record StatsDto(
    [property: JsonPropertyName("total_nodes")] int TotalNodes,
    [property: JsonPropertyName("total_edges")] int TotalEdges,
    [property: JsonPropertyName("nodes_by_type")] Dictionary<string, int> NodesByType,
    [property: JsonPropertyName("edges_by_type")] Dictionary<string, int> EdgesByType
);

public record InheritanceHierarchyDto(
    [property: JsonPropertyName("parents")] List<NodeWithRelationshipTypeDto> Parents,
    [property: JsonPropertyName("children")] List<NodeWithRelationshipTypeDto> Children
);

public record FileMetadataDto(
    [property: JsonPropertyName("file_path")] string FilePath,
    [property: JsonPropertyName("last_modified")] string LastModified,
    [property: JsonPropertyName("last_scanned")] string LastScanned,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_message")] string? ErrorMessage
);

// The historical `_query_stats` field was a `Dictionary<string, object>` debug blob that no
// code path ever populated (no producer anywhere in the solution) and, being null-by-default
// under WhenWritingNull, never appeared on the wire. It is dropped here to remove the only
// `object`-typed (reflection-requiring, AOT-hostile) member in the serialization surface.
public record CountResponseDto(
    [property: JsonPropertyName("count")] int Count
);

public record ReconcileStatsDto(
    [property: JsonPropertyName("nodes_merged")] int NodesMerged,
    [property: JsonPropertyName("edges_merged")] int EdgesMerged,
    [property: JsonPropertyName("nodes_deleted")] int NodesDeleted,
    [property: JsonPropertyName("operation")] string Operation
);

// Enhanced Status DTOs
public record StatusResponseDto(
    [property: JsonPropertyName("system")] SystemStatusDto System,
    [property: JsonPropertyName("indexing")] IndexingStatusDto Indexing,
    [property: JsonPropertyName("database")] DatabaseStatusDto Database,
    [property: JsonPropertyName("watchers")] WatcherStatusDto Watchers,
    [property: JsonPropertyName("parsers")] ParserStatusDto Parsers,
    [property: JsonPropertyName("api")] ApiStatusDto Api,
    // Backward compatibility fields
    [property: JsonPropertyName("indexed")] bool Indexed,
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("nodeCount")] int NodeCount
);

public record SystemStatusDto(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("informationalVersion")] string InformationalVersion,
    [property: JsonPropertyName("uptime")] string Uptime,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("memoryUsage")] string MemoryUsage,
    [property: JsonPropertyName("apiHealth")] string ApiHealth,
    [property: JsonPropertyName("instanceId")] string? InstanceId = null
);

public record IndexingStatusDto(
    [property: JsonPropertyName("indexed")] bool Indexed,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastScanAt")] string? LastScanAt,
    [property: JsonPropertyName("scanDuration")] string? ScanDuration,
    [property: JsonPropertyName("rootPath")] string RootPath,
    [property: JsonPropertyName("filesByStatus")] Dictionary<string, int> FilesByStatus,
    [property: JsonPropertyName("filesProcessed")] int FilesProcessed = 0,
    [property: JsonPropertyName("filesTotal")] int FilesTotal = 0,
    [property: JsonPropertyName("lastError")] string? LastError = null,
    [property: JsonPropertyName("operationId")] long OperationId = 0
);

public record DatabaseStatusDto(
    [property: JsonPropertyName("fileCount")] int FileCount,
    [property: JsonPropertyName("nodeCount")] int NodeCount,
    [property: JsonPropertyName("edgeCount")] int EdgeCount,
    [property: JsonPropertyName("nodeTypes")] Dictionary<string, int> NodeTypes,
    [property: JsonPropertyName("languageBreakdown")] Dictionary<string, int> LanguageBreakdown,
    [property: JsonPropertyName("repositoryType")] string RepositoryType,
    [property: JsonPropertyName("edgeTypes")] Dictionary<string, int>? EdgeTypes = null
);

public record WatcherStatusDto(
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("watchedPaths")] List<string> WatchedPaths,
    [property: JsonPropertyName("ignoredPatterns")] List<string> IgnoredPatterns,
    [property: JsonPropertyName("pendingChanges")] int PendingChanges,
    [property: JsonPropertyName("ignoreSourceCount")] int IgnoreSourceCount = 0,
    [property: JsonPropertyName("ignoredPathCount")] int IgnoredPathCount = 0,
    [property: JsonPropertyName("mandatoryExclusions")] IReadOnlyList<string>? MandatoryExclusions = null
);

public record ParserStatusDto(
    [property: JsonPropertyName("enabled")] List<string> Enabled,
    [property: JsonPropertyName("available")] List<string> Available,
    [property: JsonPropertyName("status")] Dictionary<string, string> Status,
    [property: JsonPropertyName("sessions")] List<ParserSessionDto>? Sessions = null
);

/// <summary>
/// Per-parser session detail (plan: notNeeded/starting/indexing/ready/unavailable/
/// failed/stopped). Process fields are null for in-process parsers. Clients must treat
/// empty query results as conclusive only when the relevant session is "ready".
/// </summary>
public record ParserSessionDto(
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("pid")] int? Pid,
    [property: JsonPropertyName("restartCount")] int RestartCount,
    [property: JsonPropertyName("parserVersion")] string? ParserVersion,
    [property: JsonPropertyName("protocolVersion")] int? ProtocolVersion,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt
);

public record ApiStatusDto(
    [property: JsonPropertyName("endpoints")] List<string> Endpoints,
    [property: JsonPropertyName("requestCount")] int RequestCount,
    [property: JsonPropertyName("averageResponseTime")] string AverageResponseTime,
    [property: JsonPropertyName("contractVersion")] int ContractVersion
);

public record HealthzResponseDto(
    [property: JsonPropertyName("status")] string Status
);

public record ShutdownResponseDto(
    [property: JsonPropertyName("message")] string Message
);

public record RefreshStartedResponseDto(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("operationId")] long OperationId
);

public record RefreshFileResponseDto(
    [property: JsonPropertyName("message")] string Message
);

public record NativeSyntaxTreeRequestDto(
    [property: JsonPropertyName("filePath")] string FilePath,
    [property: JsonPropertyName("start")] int? Start = null,
    [property: JsonPropertyName("length")] int? Length = null,
    [property: JsonPropertyName("maxDepth")] int MaxDepth = 2,
    [property: JsonPropertyName("view")] string View = "compact"
);

// Source generation for AOT - include all types used in serialization
[JsonSerializable(typeof(NodeDto))]
[JsonSerializable(typeof(NodeWithEdgeInfoDto))]
[JsonSerializable(typeof(NodeWithRelationshipTypeDto))]
[JsonSerializable(typeof(NodeWithRelationshipPathDto))]
[JsonSerializable(typeof(EdgeDto))]
[JsonSerializable(typeof(StatsDto))]
[JsonSerializable(typeof(InheritanceHierarchyDto))]
[JsonSerializable(typeof(FileMetadataDto))]
[JsonSerializable(typeof(CountResponseDto))]
[JsonSerializable(typeof(ReconcileStatsDto))]
[JsonSerializable(typeof(StatusResponseDto))]
[JsonSerializable(typeof(SystemStatusDto))]
[JsonSerializable(typeof(IndexingStatusDto))]
[JsonSerializable(typeof(DatabaseStatusDto))]
[JsonSerializable(typeof(WatcherStatusDto))]
[JsonSerializable(typeof(ParserStatusDto))]
[JsonSerializable(typeof(ParserSessionDto))]
[JsonSerializable(typeof(List<ParserSessionDto>))]
[JsonSerializable(typeof(ApiStatusDto))]
[JsonSerializable(typeof(HealthzResponseDto))]
[JsonSerializable(typeof(ShutdownResponseDto))]
[JsonSerializable(typeof(RefreshStartedResponseDto))]
[JsonSerializable(typeof(RefreshFileResponseDto))]
[JsonSerializable(typeof(NativeSyntaxTreeRequestDto))]
[JsonSerializable(typeof(NativeSyntaxTreeResult))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(CompleteContextResponse))]
[JsonSerializable(typeof(CompactContextResponse))]
[JsonSerializable(typeof(CompactMatchFacets))]
[JsonSerializable(typeof(CompactContextMatch))]
[JsonSerializable(typeof(CompactCodeNode))]
[JsonSerializable(typeof(CompactRelationships))]
[JsonSerializable(typeof(CompactTesting))]
[JsonSerializable(typeof(CompactTestFile))]
[JsonSerializable(typeof(ContextResponseView))]
[JsonSerializable(typeof(ContextMatch))]
[JsonSerializable(typeof(ContextRelationships))]
[JsonSerializable(typeof(ContextTransitiveRelationship))]
[JsonSerializable(typeof(ContextTesting))]
[JsonSerializable(typeof(ContextMetrics))]
[JsonSerializable(typeof(TestFileInfo))]
[JsonSerializable(typeof(MultiContextRequest))]
[JsonSerializable(typeof(CodeNode))]
[JsonSerializable(typeof(CodeGraph))]
[JsonSerializable(typeof(CodeEdge))]
[JsonSerializable(typeof(List<NodeDto>))]
[JsonSerializable(typeof(IReadOnlyList<NodeDto>))]
[JsonSerializable(typeof(List<NodeWithEdgeInfoDto>))]
[JsonSerializable(typeof(IReadOnlyList<NodeWithEdgeInfoDto>))]
[JsonSerializable(typeof(List<NodeWithRelationshipTypeDto>))]
[JsonSerializable(typeof(IReadOnlyList<NodeWithRelationshipTypeDto>))]
[JsonSerializable(typeof(List<NodeWithRelationshipPathDto>))]
[JsonSerializable(typeof(List<EdgeDto>))]
[JsonSerializable(typeof(IReadOnlyList<EdgeDto>))]
[JsonSerializable(typeof(List<FileMetadataDto>))]
[JsonSerializable(typeof(IReadOnlyList<FileMetadataDto>))]
[JsonSerializable(typeof(List<CompleteContextResponse>))]
[JsonSerializable(typeof(List<CompactContextResponse>))]
[JsonSerializable(typeof(List<ContextMatch>))]
[JsonSerializable(typeof(List<CodeNode>))]
[JsonSerializable(typeof(List<CodeEdge>))]
[JsonSerializable(typeof(List<TestFileInfo>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, List<NodeWithRelationshipTypeDto>>))]
[JsonSerializable(typeof(int))]
// Phase 3a: typed REST error envelopes (replace anonymous objects).
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(ContextErrorResponse))]
[JsonSerializable(typeof(MultiContextErrorResponse))]
[JsonSerializable(typeof(RefreshErrorResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class CodeContextJsonContext : JsonSerializerContext
{
}
