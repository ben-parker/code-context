using System.ComponentModel;
using System.Text.Json;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;

namespace CodeContext.Mcp;

// Tool discovery is manual and reflection-free (see McpToolCatalog); these methods are the
// invocation bodies the catalog dispatches to. The [Description] attributes are retained as
// living documentation of each parameter's contract (they no longer drive schema generation).
public sealed class CodeContextTools
{
    [Description("Get comprehensive context for a code identifier. Returns relationships, dependencies, tests, and metrics. A cache object means the answer comes from a saved index not yet re-checked; a miss is then not conclusive.")]
    public static async Task<string> GetContext(
        IContextService contextService,
        [Description("Canonical returned identifier, symbol name, or file path")] string identifier,
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
        [Description("Maximum test files returned; zero is count-only")] int maxTestFiles = 5,
        [Description("Maximum test methods returned per file; zero is count-only")] int maxTestMethods = 5,
        [Description("Expand bounded ambiguous matches instead of returning summaries")] bool expandAmbiguous = false,
        [Description("Filter members by their containing type")] string? containingType = null,
        [Description("Filter by exact namespace or module")] string? @namespace = null,
        [Description("Filter by exact signature")] string? signature = null,
        [Description("Filter by repository-relative or absolute source file")] string? sourceFile = null,
        [Description("Comma-separated relation kinds to filter uses, usedBy and possibleCallers (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only")] string? relation = null,
        [Description("Response shape; compact is the default")] ContextResponseView view = ContextResponseView.Compact,
        [Description("Whether to include library symbols, which name matches and uses otherwise leave out")] bool includeExternal = false)
    {
        try
        {
            if (view == ContextResponseView.Full)
            {
                if (!string.IsNullOrEmpty(relation))
                    throw new ArgumentException(
                        "relation is only supported for view=compact.", nameof(relation));
                var full = await contextService.GetCompleteContextAsync(
                    identifier, type, depth, includeTests, includeContent, exact ?? false,
                    includeRelated, includeMetrics, maxTestFiles, maxTestMethods, containingType,
                    @namespace, signature, sourceFile, includeExternal);
                return JsonSerializer.Serialize(full, CodeContextJsonContext.Default.CompleteContextResponse);
            }

            var compact = await contextService.GetCompactContextAsync(
                identifier, type, depth, includeTests, includeContent, exact,
                includeRelated, includeMetrics, maxMatches, maxRelationships, expandAmbiguous,
                maxCallSites, maxTestFiles, maxTestMethods, containingType, @namespace, signature,
                sourceFile, relation, includeExternal);
            return JsonSerializer.Serialize(
                compact, CodeContextJsonContext.Default.CompactContextResponse);
        }
        catch (Exception ex)
        {
            var error = new McpContextError(new McpContextErrorBody(
                "CONTEXT_ERROR", ex.Message,
                new McpContextErrorDetails(
                    identifier, type, depth, includeTests, includeContent, exact,
                    includeRelated, includeMetrics, maxMatches, maxRelationships,
                    maxCallSites, maxTestFiles, maxTestMethods, expandAmbiguous, containingType,
                    @namespace, signature, sourceFile, relation, view)));
            throw new InvalidOperationException(
                JsonSerializer.Serialize(error, McpJsonContext.Default.McpContextError));
        }
    }

    [Description("Get context for multiple identifiers in a single request. Useful for batch operations. A cache object means the answer comes from a saved index not yet re-checked; a miss is then not conclusive.")]
    public static async Task<string> GetMultiContext(
        IContextService contextService,
        [Description("List of identifiers to get context for")] string[] identifiers,
        [Description("Optional type filter applied to every identifier")] string? type = null,
        [Description("How many relationship levels to traverse")] int depth = 1,
        [Description("Whether to include classified test evidence")] bool includeTests = false,
        [Description("Exact matching; omit for exact-first fallback")] bool? exact = null,
        [Description("Maximum entries returned per relationship list")] int maxRelationships = 3,
        [Description("Maximum source locations per aggregated relationship")] int maxCallSites = 3,
        [Description("Maximum test files returned; zero is count-only")] int maxTestFiles = 5,
        [Description("Maximum test methods per file; zero is count-only")] int maxTestMethods = 5,
        [Description("Expand bounded ambiguous matches")] bool expandAmbiguous = false,
        [Description("Containing type filter")] string? containingType = null,
        [Description("Exact namespace or module filter")] string? @namespace = null,
        [Description("Exact signature filter")] string? signature = null,
        [Description("Repository-relative or absolute source file filter")] string? sourceFile = null,
        [Description("Comma-separated relation kinds to filter uses, usedBy and possibleCallers (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only")] string? relation = null,
        [Description("Response shape; compact is the default")] ContextResponseView view = ContextResponseView.Compact,
        [Description("Whether to include library symbols, which name matches and uses otherwise leave out")] bool includeExternal = false)
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
            MaxTestFiles = maxTestFiles,
            MaxTestMethods = maxTestMethods,
            ExpandAmbiguous = expandAmbiguous,
            ContainingType = containingType,
            Namespace = @namespace,
            Signature = signature,
            SourceFile = sourceFile,
            IncludeExternal = includeExternal,
            RelationshipTypes = string.IsNullOrWhiteSpace(relation)
                ? new List<string>()
                : relation.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList(),
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
            var error = new McpMultiContextError(new McpMultiContextErrorBody(
                "MULTI_CONTEXT_ERROR", ex.Message, multiRequest));
            throw new InvalidOperationException(
                JsonSerializer.Serialize(error, McpJsonContext.Default.McpMultiContextError));
        }
    }

    /// <summary>A graph tool's checked arguments (see <c>McpToolCatalog.GraphArguments</c>), read with the tool's defaults.</summary>
    public sealed class GraphArguments(IReadOnlyDictionary<string, JsonElement> values)
    {
        public string? Str(string key) => values.TryGetValue(key, out var value) ? value.GetString() : null;

        public int Int(string key, int fallback) => values.TryGetValue(key, out var value) ? value.GetInt32() : fallback;

        public bool Bool(string key) => values.TryGetValue(key, out var value) && value.GetBoolean();
    }

    [Description("Find how one symbol reaches another through recorded relationships and dispatch.")]
    public static async Task<string> GetPath(IGraphQueryService graph, GraphArguments args, CancellationToken cancellationToken)
    {
        var response = await graph.FindPathAsync(new GraphPathRequest
        {
            From = args.Str("from")!,
            To = args.Str("to")!,
            MaxDepth = args.Int("maxDepth", GraphQueryLimits.DefaultPathDepth),
            Relation = args.Str("relation"),
            IncludeTests = args.Bool("includeTests"),
        }, cancellationToken);
        return JsonSerializer.Serialize(response, CodeContextJsonContext.Default.GraphPathResponse);
    }

    [Description("List what depends on a symbol or file, transitively.")]
    public static async Task<string> GetAffected(IGraphQueryService graph, GraphArguments args, CancellationToken cancellationToken)
    {
        var response = await graph.GetAffectedAsync(new GraphAffectedRequest
        {
            Identifier = args.Str("identifier")!,
            MaxDepth = args.Int("maxDepth", GraphQueryLimits.DefaultAffectedDepth),
            Relation = args.Str("relation"),
            IncludeTests = args.Bool("includeTests"),
            Limit = args.Int("limit", GraphQueryLimits.DefaultLimit),
        }, cancellationToken);
        return JsonSerializer.Serialize(response, CodeContextJsonContext.Default.GraphAffectedResponse);
    }

    [Description("List what a diff could break.")]
    public static async Task<string> GetImpact(IGraphQueryService graph, GraphArguments args, CancellationToken cancellationToken)
    {
        var response = await graph.GetImpactAsync(new GraphImpactRequest
        {
            Diff = args.Str("diff")!,
            MaxDepth = args.Int("maxDepth", GraphQueryLimits.DefaultAffectedDepth),
            Relation = args.Str("relation"),
            IncludeTests = args.Bool("includeTests"),
            Limit = args.Int("limit", GraphQueryLimits.DefaultLimit),
        }, cancellationToken);
        return JsonSerializer.Serialize(response, CodeContextJsonContext.Default.GraphImpactResponse);
    }

    [Description("List the symbols with the most direct dependents.")]
    public static async Task<string> GetHubs(IGraphQueryService graph, GraphArguments args, CancellationToken cancellationToken)
    {
        var response = await graph.GetHubsAsync(new GraphHubsRequest
        {
            Limit = args.Int("limit", GraphQueryLimits.DefaultLimit),
            Relation = args.Str("relation"),
            Type = args.Str("type"),
            Within = args.Str("within"),
            IncludeTests = args.Bool("includeTests"),
            IncludeExternal = args.Bool("includeExternal"),
        }, cancellationToken);
        return JsonSerializer.Serialize(response, CodeContextJsonContext.Default.GraphHubsResponse);
    }

    [Description("Get comprehensive system health and debugging information")]
    public static async Task<string> GetStatus(IStatusService statusService)
    {
        try
        {
            var status = await statusService.GetStatusAsync();

            return JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto);
        }
        catch (Exception ex)
        {
            var error = new McpStatusError(new McpStatusErrorBody("STATUS_ERROR", ex.Message));
            throw new InvalidOperationException(
                JsonSerializer.Serialize(error, McpJsonContext.Default.McpStatusError));
        }
    }
}
