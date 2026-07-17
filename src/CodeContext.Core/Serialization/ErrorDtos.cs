using System.Text.Json.Serialization;
using CodeContext.Core.Services;

namespace CodeContext.Core.Serialization;

// Typed JSON error envelopes for the REST endpoints. These replace the anonymous objects
// previously passed to Results.BadRequest/Results.Json so the host has a fully
// source-generated (AOT/trim-safe) serialization path. They are serialized through
// CodeContextJsonContext, whose options (PropertyNamingPolicy = camelCase,
// DefaultIgnoreCondition = WhenWritingNull) reproduce the exact wire shape the ASP.NET
// host produced from the anonymous objects. Property/parameter order is significant:
// records serialize in declaration order, which must match the historical shapes.
// ErrorContractTests locks this byte-for-byte.

/// <summary>Simple <c>{ "error": { "code", "message" } }</c> envelope.</summary>
public record ApiErrorResponse(
    [property: JsonPropertyName("error")] ApiError Error);

public record ApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Envelope for <c>/api/context/complete</c> failures, carrying the echoed request parameters.</summary>
public record ContextErrorResponse(
    [property: JsonPropertyName("error")] ContextErrorBody Error);

public record ContextErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] ContextErrorDetails Details);

public record ContextErrorDetails(
    string? Identifier,
    string? Type,
    int Depth,
    bool IncludeTests,
    bool IncludeContent,
    bool? Exact,
    bool IncludeRelated,
    bool IncludeMetrics,
    int MaxMatches,
    int MaxRelationships,
    int MaxCallSites,
    int MaxTestFiles,
    int MaxTestMethods,
    bool ExpandAmbiguous,
    string? ContainingType,
    string? Namespace,
    string? Signature,
    string? SourceFile,
    string? Relation,
    string View);

/// <summary>Envelope for <c>/api/context/multi</c> failures, echoing the parsed request.</summary>
public record MultiContextErrorResponse(
    [property: JsonPropertyName("error")] MultiContextErrorBody Error);

public record MultiContextErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] MultiContextRequest Details);

/// <summary>Envelope for <c>/api/index/refresh</c> failures, echoing the target path.</summary>
public record RefreshErrorResponse(
    [property: JsonPropertyName("error")] RefreshErrorBody Error);

public record RefreshErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] RefreshErrorDetails Details);

public record RefreshErrorDetails(
    string? Path);
