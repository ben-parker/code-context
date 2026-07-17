using CodeContext.Api.Endpoints;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

#if ENABLE_OPENAPI
        // OpenAPI is a development-only convenience; the package and this registration are
        // excluded from Release/published binaries (see OpenApiSupport / CodeContext.Api.csproj).
        OpenApiSupport.AddOpenApi(services);
#endif
    }

    public static void ConfigureCoreServices(
        IServiceCollection services, string rootPath, bool isProduction, int port = 7890,
        int idleTimeoutMinutes = 120, string? instanceId = null,
        ApplicationStartTime? applicationStartTime = null)
    {
        // Configure services. Console logging is added here for both host modes; the MCP path
        // (RunMcpAsync) subsequently redirects it to stderr so stdout stays protocol-only.
        services.AddLogging(options => options.AddConsole());
        services.AddSingleton(applicationStartTime ?? new ApplicationStartTime(DateTimeOffset.UtcNow));

        services.Configure<CodeContextOptions>(options =>
        {
            options.RootPath = rootPath;
            options.InstanceId = instanceId ?? Guid.NewGuid().ToString("N");
            options.Port = port;
            options.IdleTimeoutMinutes = idleTimeoutMinutes;
        });

        // All languages are parsed out-of-process: the worker catalog below discovers
        // workers/<name>/ next to the host binary and routes their extensions over the
        // parser protocol. There is no in-process parser seam.

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
        // Per-worker env overlays (e.g. GC A/B knobs) come from the CodeContext:WorkerEnvironment
        // configuration section; this instance flows into LanguageWorkerService's optional
        // workerOptions parameter via constructor injection. Absent section => defaults, no overlay.
        services.AddSingleton(sp =>
            ParserWorkerOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
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

#if ENABLE_OPENAPI
        // Dev-only OpenAPI document + /api/schema redirect. Compiled out of Release entirely;
        // the runtime IsDevelopment() check is belt-and-braces for a Debug non-dev environment.
        if (app.Environment.IsDevelopment())
        {
            OpenApiSupport.MapOpenApi(app);
        }
#endif

        app.MapCodeContextEndpoints();
    }
}

