#if ENABLE_OPENAPI
using CodeContext.Core;
using CodeContext.Core.Serialization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Reflection;

namespace CodeContext.Api;

// OpenAPI support is a development-only convenience. The Microsoft.AspNetCore.OpenApi package
// (and its Microsoft.OpenApi transitive) is referenced only in Debug builds, and the
// ENABLE_OPENAPI compile constant is defined only in Debug (see CodeContext.Api.csproj), so
// Release/published binaries carry zero OpenAPI code -- no document generation, no transformers,
// and none of the Assembly.GetExecutingAssembly() reflection below. A runtime IsDevelopment()
// guard at the call sites is kept as belt-and-braces.
internal static class OpenApiSupport
{
    public static void AddOpenApi(IServiceCollection services)
    {
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<OpenApiDocumentTransformer>();
            options.AddOperationTransformer<ParameterDescriptionTransformer>();
            options.AddSchemaTransformer<CodeNodeArraySchemaTransformer>();
        });
    }

    public static void MapOpenApi(WebApplication app)
    {
        app.MapOpenApi();

        // Custom /api/schema endpoint that serves the OpenAPI specification.
        app.MapGet("/api/schema", () => Results.Redirect("/openapi/v1.json"))
            .WithName("GetOpenApiSchema")
            .WithSummary("Get OpenAPI 3.0 specification")
            .WithDescription("Returns the OpenAPI 3.0 specification for the CodeContext API");
    }
}

/// <summary>
/// Transformer to add parameter descriptions to OpenAPI operations
/// </summary>
internal sealed class ParameterDescriptionTransformer : IOpenApiOperationTransformer
{
    private static readonly Dictionary<string, string> ParameterDescriptions = new()
    {
        ["identifier"] = "Canonical returned identifier, name, or repository-relative/absolute file path",
        ["type"] = "Filter by type (Class, Method, Interface, Property, etc.)",
        ["depth"] = "How many relationship levels to traverse (0-10)",
        ["includeTests"] = "Whether to include test-related information",
        ["includeContent"] = "Whether to include file content snippets",
        ["exact"] = "Exact matching; omit for exact-first matching with substring fallback",
        ["includeRelated"] = "Whether to include loosely related same-file/namespace symbols",
        ["includeMetrics"] = "Whether to include heuristic code metrics",
        ["maxMatches"] = "Maximum ambiguous candidate summaries to return (1-50)",
        ["maxRelationships"] = "Maximum entries returned per relationship list (1-100)",
        ["maxCallSites"] = "Maximum locations returned per aggregated relationship (0-100)",
        ["maxTestFiles"] = "Maximum test files returned; zero is count-only (0-100)",
        ["maxTestMethods"] = "Maximum test methods returned per file; zero is count-only (0-100)",
        ["expandAmbiguous"] = "Expand bounded ambiguous matches instead of returning summaries",
        ["containingType"] = "Filter members by containing type",
        ["namespace"] = "Filter by exact namespace or module",
        ["signature"] = "Filter by exact signature",
        ["sourceFile"] = "Filter by repository-relative or absolute source file",
        ["view"] = "Response shape: compact (default) or full",
        ["path"] = "Path to the file to refresh. If omitted, performs full scan."
    };

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Parameters != null)
        {
            foreach (var parameter in operation.Parameters)
            {
                // Microsoft.OpenApi 2.0: IOpenApiParameter exposes Description as read-only;
                // only the concrete OpenApiParameter (as opposed to a $ref) is mutable.
                if (parameter is OpenApiParameter concrete
                    && concrete.Name is { } name
                    && ParameterDescriptions.TryGetValue(name, out var description))
                {
                    concrete.Description = description;
                }
            }
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Transformer to customize the OpenAPI document with additional metadata
/// </summary>
internal sealed class OpenApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "CodeContext API",
            Version = AssemblyVersionInfo.GetInformationalVersion(Assembly.GetExecutingAssembly()),
            Description = "Local code context API for LLMs - provides dependency graph and code relationships",
            Contact = new OpenApiContact
            {
                Name = "CodeContext Team",
                Url = new Uri("https://github.com/you/codecontext")
            }
        };

        document.Servers = new List<OpenApiServer>
        {
            new OpenApiServer
            {
                Url = $"http://localhost:{context.ApplicationServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<CodeContextOptions>>().Value.Port}",
                Description = "Local CodeContext server"
            }
        };

        // Minimal APIs keep only one schema when several .Produces<T>(200) calls use
        // the same status code. Endpoints expose full view temporarily as a 206 schema
        // anchor; fold that generated schema into the actual 200 union.
        AddFullViewAlternative(document, "/api/context/complete", HttpMethod.Get);
        AddFullViewAlternative(document, "/api/context/multi", HttpMethod.Post);
        await Task.CompletedTask;
    }

    private static void AddFullViewAlternative(
        OpenApiDocument document, string path, HttpMethod operationType)
    {
        if (!document.Paths.TryGetValue(path, out var pathItem)
            || pathItem.Operations is not { } operations
            || !operations.TryGetValue(operationType, out var operation)
            || operation.Responses is not { } responses
            || !responses.TryGetValue("200", out var response)
            || !responses.TryGetValue("206", out var fullResponse)
            || response.Content is not { } compactContent
            || fullResponse.Content is not { } fullContent
            || !compactContent.TryGetValue("application/json", out var compactMedia)
            || !fullContent.TryGetValue("application/json", out var fullMedia)
            || compactMedia.Schema is not { } current
            || fullMedia.Schema is not { } full)
            return;

        compactMedia.Schema = new OpenApiSchema { OneOf = [current, full] };
        responses.Remove("206");
    }
}

/// <summary>
/// Transformer to fix empty items in array schemas for AOT compatibility
/// </summary>
internal sealed class CodeNodeArraySchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        // Fix ContextRelationships arrays
        if (context.JsonPropertyInfo?.Name == "usedBy" ||
            context.JsonPropertyInfo?.Name == "relatedItems" ||
            context.JsonPropertyInfo?.Name == "uses" ||
            context.JsonPropertyInfo?.Name == "methodFamilyMembers" ||
            context.JsonPropertyInfo?.Name == "staticallyBoundTargets")
        {
            // Flags test: nullable list properties carry JsonSchemaType.Null | Array in Microsoft.OpenApi 2.x
            if (schema.Type is { } type && (type & JsonSchemaType.Array) != 0
                && schema.Items is { } items
                && (items.Properties is null || items.Properties.Count == 0))
            {
                schema.Items = new OpenApiSchemaReference("CodeNode", null, null);
            }
        }

        // Fix TestFileInfo.TestMethods array
        if (context.JsonPropertyInfo?.Name == "testMethods")
        {
            if (schema.Type is { } type && (type & JsonSchemaType.Array) != 0
                && schema.Items is { } items
                && (items.Properties is null || items.Properties.Count == 0))
            {
                schema.Items = new OpenApiSchemaReference("CodeNode", null, null);
            }
        }

        return Task.CompletedTask;
    }
}
#endif
