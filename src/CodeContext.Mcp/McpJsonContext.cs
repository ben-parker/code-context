using System.Text.Json.Serialization;
using CodeContext.Core.Services;

namespace CodeContext.Mcp;

// Typed JSON error envelopes for the MCP tools. These replace the anonymous objects that
// CodeContextTools previously fed to JsonSerializer.Serialize(...) (the untyped, reflection-
// based overload — the source of the IL2026/IL3050 trim/AOT warnings).
//
// The historical MCP error JSON was produced with JsonSerializerOptions.Default: verbatim
// property names and DefaultIgnoreCondition = Never (nulls written). McpJsonContext therefore
// sets NO PropertyNamingPolicy (verbatim) and DefaultIgnoreCondition = Never, and the envelope
// records carry explicit lowercase [JsonPropertyName]s to match the old anonymous member names.
// The nested MultiContextRequest is serialized verbatim (PascalCase) through this context,
// exactly as the default serializer produced it. Property/parameter order is significant and
// must match the historical shape; ErrorContractTests locks this byte-for-byte.

public record McpContextError(
    [property: JsonPropertyName("error")] McpContextErrorBody Error);

public record McpContextErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] McpContextErrorDetails Details);

public record McpContextErrorDetails(
    [property: JsonPropertyName("identifier")] string? Identifier,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("includeTests")] bool IncludeTests,
    [property: JsonPropertyName("includeContent")] bool IncludeContent,
    [property: JsonPropertyName("exact")] bool? Exact,
    [property: JsonPropertyName("includeRelated")] bool IncludeRelated,
    [property: JsonPropertyName("includeMetrics")] bool IncludeMetrics,
    [property: JsonPropertyName("maxMatches")] int MaxMatches,
    [property: JsonPropertyName("maxRelationships")] int MaxRelationships,
    [property: JsonPropertyName("maxCallSites")] int MaxCallSites,
    [property: JsonPropertyName("maxTestFiles")] int MaxTestFiles,
    [property: JsonPropertyName("maxTestMethods")] int MaxTestMethods,
    [property: JsonPropertyName("expandAmbiguous")] bool ExpandAmbiguous,
    [property: JsonPropertyName("containingType")] string? ContainingType,
    [property: JsonPropertyName("namespace")] string? Namespace,
    [property: JsonPropertyName("signature")] string? Signature,
    [property: JsonPropertyName("sourceFile")] string? SourceFile,
    [property: JsonPropertyName("relation")] string? Relation,
    [property: JsonPropertyName("view")] ContextResponseView View);

public record McpMultiContextError(
    [property: JsonPropertyName("error")] McpMultiContextErrorBody Error);

public record McpMultiContextErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] MultiContextRequest Details);

public record McpStatusError(
    [property: JsonPropertyName("error")] McpStatusErrorBody Error);

public record McpStatusErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(McpContextError))]
[JsonSerializable(typeof(McpMultiContextError))]
[JsonSerializable(typeof(McpStatusError))]
[JsonSerializable(typeof(MultiContextRequest))]
internal partial class McpJsonContext : JsonSerializerContext
{
}
