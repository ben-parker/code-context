using CodeContext.Api.Endpoints;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Reflection;
using System.Text.Json.Serialization;

namespace CodeContext.Api;

public static class ProgramHelpers
{
    // public static WebApplication CreateWebApplication(string[] args, string rootPath, int port)
    // {
    //     var builder = WebApplication.CreateBuilder(args);
    //     ConfigureServices(builder, rootPath);
    //     ConfigureWebHost(builder.WebHost, port);

    //     var app = builder.Build();
    //     ConfigureApiEndpoints(app);

    //     return app;
    // }

    public static void AddRestApi(IServiceCollection services)
    {
        // Configure JSON serialization for AOT
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, CodeContextJsonContext.Default);
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // Configure OpenAPI
        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer<OpenApiDocumentTransformer>();
            options.AddOperationTransformer<ParameterDescriptionTransformer>();
            options.AddSchemaTransformer<CodeNodeArraySchemaTransformer>();
        });
    }

    public static void ConfigureCoreServices(
        IServiceCollection services, string rootPath, bool isProduction, int port = 7890,
        int idleTimeoutMinutes = 120, string? instanceId = null,
        ApplicationStartTime? applicationStartTime = null)
    {
        using var loggerFactory = LoggerFactory.Create(loggingBuilder =>
        {
            loggingBuilder.AddConsole();
            loggingBuilder.AddDebug();
        });

        var logger = loggerFactory.CreateLogger("ProgramHelpers");

        logger.LogInformation("Current directory is {Directory}", Environment.CurrentDirectory);

        // Configure services
        services.AddLogging(options => options.AddConsole());
        services.AddSingleton(applicationStartTime ?? new ApplicationStartTime(DateTimeOffset.UtcNow));

        services.Configure<CodeContextOptions>(options =>
        {
            options.RootPath = rootPath;
            options.InstanceId = instanceId ?? Guid.NewGuid().ToString("N");
            options.Port = port;
            options.IdleTimeoutMinutes = idleTimeoutMinutes;
        });

        // No in-process language parsers remain: C# and TypeScript both run
        // out-of-process. The worker catalog below discovers workers/<name>/ next to
        // the host binary and routes their extensions over the parser protocol.
        // (ILanguageParser stays as the seam for tests and future in-process adapters.)

        // Register services
        services.AddSingleton<IParserSessionRegistry, ParserSessionRegistry>();
        services.AddSingleton<IRepositoryFileSelector, RepositoryFileSelector>();
        services.AddSingleton<IGraphUpdateService, GraphUpdateService>();
        services.AddSingleton<IContextService, ContextService>();
        services.AddSingleton<IApiMetrics, ApiMetrics>();
        services.AddSingleton<IStatusService, StatusService>();
        services.AddSingleton<ScanStateService>();
        services.AddSingleton<IScanStateService>(sp => sp.GetRequiredService<ScanStateService>());

        services.AddCodeContextRepositories();

        // Language workers: discovered from workers/<name>/worker-manifest.json next
        // to the host binary. Streamed analysis deltas commit atomically through the
        // generational store.
        services.AddSingleton<IWorkerCatalog, WorkerCatalog>();
        services.AddSingleton<IAnalysisDeltaSink>(sp => new AnalysisDeltaApplier(
            (IGenerationalGraphStore)sp.GetRequiredService<IRepositoryFactory>().CreateGraphRepository(),
            sp.GetRequiredService<ILogger<AnalysisDeltaApplier>>()));
        services.AddSingleton<LanguageWorkerService>();
        services.AddSingleton<ILanguageWorkerService>(sp => sp.GetRequiredService<LanguageWorkerService>());
        services.AddHostedService(sp => sp.GetRequiredService<LanguageWorkerService>());

        // The coordinator is the sole writer-side entry point (startup scan, refreshes,
        // watcher batches); register it before the watcher so its channel exists first.
        services.AddSingleton<IndexCoordinator>();
        services.AddSingleton<IIndexCoordinator>(sp => sp.GetRequiredService<IndexCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<IndexCoordinator>());
        services.AddHostedService<FileWatcherService>();
    }

    public static void ConfigureApiEndpoints(WebApplication app)
    {
        // app.UseExceptionHandler();
        // app.UseStatusCodePages();

        app.MapOpenApi();

        app.MapCodeContextEndpoints();

        // Custom /api/schema endpoint that serves the OpenAPI specification
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
                if (ParameterDescriptions.TryGetValue(parameter.Name, out var description))
                {
                    parameter.Description = description;
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
        AddFullViewAlternative(document, "/api/context/complete", OperationType.Get);
        AddFullViewAlternative(document, "/api/context/multi", OperationType.Post);
        await Task.CompletedTask;
    }

    private static void AddFullViewAlternative(
        OpenApiDocument document, string path, OperationType operationType)
    {
        if (!document.Paths.TryGetValue(path, out var pathItem)
            || !pathItem.Operations.TryGetValue(operationType, out var operation)
            || !operation.Responses.TryGetValue("200", out var response)
            || !operation.Responses.TryGetValue("206", out var fullResponse)
            || response.Content is null
            || fullResponse.Content is null
            || !response.Content.TryGetValue("application/json", out var compactMedia)
            || !fullResponse.Content.TryGetValue("application/json", out var fullMedia)
            || compactMedia.Schema is not { } current
            || fullMedia.Schema is not { } full)
            return;

        compactMedia.Schema = new OpenApiSchema { OneOf = [current, full] };
        operation.Responses.Remove("206");
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
            if (schema.Type == "array" && schema.Items != null && !schema.Items.Properties.Any())
            {
                schema.Items = new OpenApiSchema
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.Schema,
                        Id = "CodeNode"
                    }
                };
            }
        }

        // Fix TestFileInfo.TestMethods array
        if (context.JsonPropertyInfo?.Name == "testMethods")
        {
            if (schema.Type == "array" && schema.Items != null && !schema.Items.Properties.Any())
            {
                schema.Items = new OpenApiSchema
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.Schema,
                        Id = "CodeNode"
                    }
                };
            }
        }

        return Task.CompletedTask;
    }
}
