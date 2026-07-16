using CodeContext.Api.Endpoints;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.Kuzu;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CSnakes.Runtime;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
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

    public static void ConfigureCoreServices(IServiceCollection services, string rootPath, bool isProduction, BackendType backend = BackendType.InMemory, int port = 7890, int idleTimeoutMinutes = 120, string? instanceId = null)
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

        services.Configure<CodeContextOptions>(options =>
        {
            options.RootPath = rootPath;
            options.InstanceId = instanceId ?? Guid.NewGuid().ToString("N");
            options.Backend = backend;
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

        // Kuzu is the only backend that needs Python: keep all CSnakes provisioning
        // inside this branch so the default (in-memory) path has zero Python dependency.
        if (backend == BackendType.Kuzu)
        {
            var pythonBuilder = services.WithPython();

            var home = GetPythonHome(rootPath, isProduction);
            var venv = Path.Join(home, ".venv");

            pythonBuilder
                .WithHome(home)
                .WithVirtualEnvironment(venv)
                .WithPipInstaller("requirements.txt")
                .FromRedistributable();

            // with this, can inject IKuzuApi
            services.AddSingleton(sp => sp.GetRequiredService<IPythonEnvironment>().KuzuApi());
        }

        services.AddCodeContextRepositories(backend);

        // Language workers: discovered from workers/<name>/worker-manifest.json next
        // to the host binary. Streamed analysis deltas commit atomically through the
        // generational store; the Kuzu backend falls back to its JSON reconcile
        // boundary (whole-graph replacement, the pre-existing opt-in limitation).
        services.AddSingleton<IWorkerCatalog, WorkerCatalog>();
        services.AddSingleton<IAnalysisDeltaSink>(sp =>
        {
            var graphRepository = sp.GetRequiredService<IRepositoryFactory>().CreateGraphRepository();
            return graphRepository is IGenerationalGraphStore store
                ? new AnalysisDeltaApplier(store, sp.GetRequiredService<ILogger<AnalysisDeltaApplier>>())
                : new JsonReconcileDeltaSink(graphRepository, sp.GetRequiredService<ILogger<JsonReconcileDeltaSink>>());
        });
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

    private static string GetPythonHome(string rootPath, bool isProduction)
    {
        if (isProduction) return AppContext.BaseDirectory;

        if (Environment.CurrentDirectory.EndsWith("CodeContext.Api"))
        {
            return Path.Join("..", "CodeContext.Python.Kuzu");
        }
        else if (Environment.CurrentDirectory.Contains(Path.Join("bin", "Debug")))
        {
            // Development: Find Python project relative to CodeContext application location
            return Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "..", "..", "..", "CodeContext.Python.Kuzu"));
        }
        else
        {
            return Environment.CurrentDirectory;
        }
    }
}

/// <summary>
/// Transformer to add parameter descriptions to OpenAPI operations
/// </summary>
internal sealed class ParameterDescriptionTransformer : IOpenApiOperationTransformer
{
    private static readonly Dictionary<string, string> ParameterDescriptions = new()
    {
        ["identifier"] = "Name or file path to search for",
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
        ["expandAmbiguous"] = "Expand bounded ambiguous matches instead of returning summaries",
        ["qualifiedIdentifier"] = "Stable qualified identity from an ambiguous summary",
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
            Version = "1.0.0",
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

        await Task.CompletedTask;
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
            context.JsonPropertyInfo?.Name == "uses")
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
