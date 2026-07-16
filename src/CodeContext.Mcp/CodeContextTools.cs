using System.ComponentModel;
using System.Text.Json;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using ModelContextProtocol.Server;

namespace CodeContext.Mcp;

[McpServerToolType]
public sealed class CodeContextTools
{
    [McpServerTool, Description("Get comprehensive context for a code identifier. Returns relationships, dependencies, tests, and metrics.")]
    public static async Task<string> GetContext(
        IContextService contextService,
        [Description("Name or file path to search for")] string identifier,
        [Description("Filter by type (Class, Method, Interface, Property, etc.)")] string? type = null,
        [Description("How many relationship levels to traverse (0-10)")] int depth = 1,
        [Description("Whether to include test-related information")] bool includeTests = false,
        [Description("Whether to include file content snippets")] bool includeContent = false,
        [Description("Exact matching; omit for exact-first with substring fallback")] bool? exact = null,
        [Description("Whether to include loosely related symbols")] bool includeRelated = false,
        [Description("Whether to include heuristic metrics")] bool includeMetrics = false,
        [Description("Maximum ambiguous candidates returned")] int maxMatches = 5,
        [Description("Maximum entries returned per relationship list")] int maxRelationships = 10,
        [Description("Maximum source locations returned per aggregated relationship; zero is count-only")] int maxCallSites = 3,
        [Description("Expand bounded ambiguous matches instead of returning summaries")] bool expandAmbiguous = false,
        [Description("Stable qualified identity returned by an ambiguous summary")] string? qualifiedIdentifier = null,
        [Description("Filter members by their containing type")] string? containingType = null,
        [Description("Filter by exact namespace or module")] string? @namespace = null,
        [Description("Filter by exact signature")] string? signature = null,
        [Description("Filter by repository-relative or absolute source file")] string? sourceFile = null,
        [Description("Response shape; compact is the default")] ContextResponseView view = ContextResponseView.Compact)
    {
        try
        {
            if (view == ContextResponseView.Full)
            {
                var full = await contextService.GetCompleteContextAsync(
                    identifier, type, depth, includeTests, includeContent, exact ?? false,
                    includeRelated, includeMetrics, qualifiedIdentifier, containingType,
                    @namespace, signature, sourceFile);
                return JsonSerializer.Serialize(full, CodeContextJsonContext.Default.CompleteContextResponse);
            }

            var compact = await contextService.GetCompactContextAsync(
                identifier, type, depth, includeTests, includeContent, exact,
                includeRelated, includeMetrics, maxMatches, maxRelationships, expandAmbiguous,
                maxCallSites, qualifiedIdentifier, containingType, @namespace, signature, sourceFile);
            return JsonSerializer.Serialize(
                compact, CodeContextJsonContext.Default.CompactContextResponse);
        }
        catch (Exception ex)
        {
            var error = new
            {
                error = new
                {
                    code = "CONTEXT_ERROR",
                    message = ex.Message,
                    details = new
                    {
                        identifier, type, depth, includeTests, includeContent, exact,
                        includeRelated, includeMetrics, maxMatches, maxRelationships,
                        maxCallSites, expandAmbiguous, qualifiedIdentifier, containingType,
                        @namespace, signature, sourceFile, view
                    }
                }
            };
            throw new InvalidOperationException(JsonSerializer.Serialize(error));
        }
    }

    [McpServerTool, Description("Get context for multiple identifiers in a single request. Useful for batch operations.")]
    public static async Task<string> GetMultiContext(
        IContextService contextService,
        [Description("List of identifiers to get context for")] string[] identifiers,
        [Description("Optional type filter applied to every identifier")] string? type = null,
        [Description("How many relationship levels to traverse")] int depth = 1,
        [Description("Whether to include classified test evidence")] bool includeTests = false,
        [Description("Exact matching; omit for exact-first fallback")] bool? exact = null,
        [Description("Maximum entries returned per relationship list")] int maxRelationships = 3,
        [Description("Maximum source locations per aggregated relationship")] int maxCallSites = 3,
        [Description("Expand bounded ambiguous matches")] bool expandAmbiguous = false,
        [Description("Stable qualified identity filter")] string? qualifiedIdentifier = null,
        [Description("Containing type filter")] string? containingType = null,
        [Description("Exact namespace or module filter")] string? @namespace = null,
        [Description("Exact signature filter")] string? signature = null,
        [Description("Repository-relative or absolute source file filter")] string? sourceFile = null,
        [Description("Response shape; compact is the default")] ContextResponseView view = ContextResponseView.Compact)
    {
        var multiRequest = new MultiContextRequest
        {
            Identifiers = identifiers.ToList(),
            Type = type,
            Depth = depth,
            View = view,
            IncludeTests = includeTests,
            Exact = exact,
            MaxRelationships = maxRelationships,
            MaxCallSites = maxCallSites,
            ExpandAmbiguous = expandAmbiguous,
            QualifiedIdentifier = qualifiedIdentifier,
            ContainingType = containingType,
            Namespace = @namespace,
            Signature = signature,
            SourceFile = sourceFile,
        };

        try
        {
            if (view == ContextResponseView.Full)
            {
                var full = await contextService.GetMultipleContextAsync(multiRequest);
                return JsonSerializer.Serialize(full, CodeContextJsonContext.Default.ListCompleteContextResponse);
            }

            var compact = await contextService.GetMultipleCompactContextAsync(multiRequest);
            return JsonSerializer.Serialize(
                compact, CodeContextJsonContext.Default.ListCompactContextResponse);
        }
        catch (Exception ex)
        {
            var error = new
            {
                error = new
                {
                    code = "MULTI_CONTEXT_ERROR",
                    message = ex.Message,
                    details = multiRequest
                }
            };
            throw new InvalidOperationException(JsonSerializer.Serialize(error));
        }
    }

    [McpServerTool, Description("Get comprehensive system health and debugging information")]
    public static async Task<string> GetStatus(IStatusService statusService)
    {
        try
        {
            var status = await statusService.GetStatusAsync();

            return JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto);
        }
        catch (Exception ex)
        {
            var error = new
            {
                error = new
                {
                    code = "STATUS_ERROR",
                    message = ex.Message
                }
            };
            throw new InvalidOperationException(JsonSerializer.Serialize(error));
        }
    }
}
