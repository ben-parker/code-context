using System.Diagnostics;
using System.Text.Json;
using CodeContext.Api.Lifecycle;
using CodeContext.Core.Instances;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;

namespace CodeContext.Api.Commands;

public record StartSettings(
    string Path,
    int? ExplicitPort,
    bool Mcp,
    bool Detach,
    int IdleTimeoutMinutes,
    string? LogFile,
    string? InstanceId = null);

public static class StartCommandHandler
{
    public static async Task<int> ExecuteAsync(StartSettings settings, CancellationToken ct)
    {
        var rootPath = NormalizePath(settings.Path);
        if (!Directory.Exists(rootPath))
        {
            Console.Error.WriteLine($"Path does not exist: {rootPath}");
            return 1;
        }

        if (settings.Mcp)
        {
            return await RunMcpAsync(rootPath, ct);
        }

        var registry = new InstanceRegistry();
        if (settings.Detach)
        {
            var result = await new DetachedStartOrchestrator(registry).StartAsync(
                rootPath,
                settings.ExplicitPort,
                settings.IdleTimeoutMinutes,
                settings.LogFile,
                ct);
            if (!result.Success || result.Instance is null)
            {
                Console.Error.WriteLine(result.ErrorMessage ?? "Failed to start the detached process.");
                return 1;
            }

            Console.WriteLine(JsonSerializer.Serialize(
                result.Instance, InstanceRegistryJsonContext.Default.InstanceRecord));
            if (result.WasStarted)
            {
                Console.Error.WriteLine(
                    $"CodeContext started for {rootPath} on port {result.Instance.Port} " +
                    $"(pid {result.Instance.Pid}). Log: {result.LogFile}");
            }
            else
            {
                var detachComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var coversSuffix = string.Equals(result.Instance.RootPath, rootPath, detachComparison)
                    ? string.Empty
                    : $"; it covers {rootPath}.";
                Console.Error.WriteLine(
                    $"CodeContext is already running for {result.Instance.RootPath} " +
                    $"on port {result.Instance.Port} (pid {result.Instance.Pid}){coversSuffix}");
            }
            return 0;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Same-or-ancestor match: an agent re-running `start` (or starting from a subdirectory
        // of an already-indexed root) attaches to the existing instance instead of forking a
        // duplicate scan, since the ancestor's recursive scan already covers the subdirectory.
        var existing = registry.FindForPath(rootPath);
        if (existing is not null)
        {
            if (string.Equals(existing.RootPath, rootPath, comparison))
            {
                Console.Error.WriteLine($"CodeContext is already running for {existing.RootPath} on port {existing.Port} (pid {existing.Pid}).");
            }
            else
            {
                Console.Error.WriteLine($"CodeContext is already running for {existing.RootPath} on port {existing.Port} (pid {existing.Pid}); it covers {rootPath}.");
            }
            Console.WriteLine(JsonSerializer.Serialize(existing, InstanceRegistryJsonContext.Default.InstanceRecord));
            return 0;
        }

        if (settings.ExplicitPort is int explicitPort && !PortAllocator.IsPortFree(explicitPort))
        {
            Console.Error.WriteLine($"Port {explicitPort} is already in use.");
            return 1;
        }

        // Foreground run; when the port was auto-allocated, tolerate bind races by retrying.
        var attempts = 0;
        var scanFrom = PortAllocator.DefaultStartPort;
        while (true)
        {
            var port = settings.ExplicitPort ?? PortAllocator.AllocatePort(scanFrom);
            try
            {
                await RunWebHostAsync(rootPath, port, settings, registry, ct);
                return 0;
            }
            catch (IOException) when (settings.ExplicitPort is null && ++attempts < 5)
            {
                scanFrom = port + 1;
            }
        }
    }

    private static async Task RunWebHostAsync(
        string rootPath, int port, StartSettings settings, IInstanceRegistry registry, CancellationToken ct)
    {
        var applicationStartTime = new ApplicationStartTime(DateTimeOffset.UtcNow);
        RedirectOutputIfRequested(settings.LogFile);

        var instanceId = string.IsNullOrEmpty(settings.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : settings.InstanceId;

        // Slim builder: localhost HTTP only, so the IIS/HTTPS/EventLog wiring a full builder
        // adds is dead weight and dead cold-start time. Kestrel, config, and console logging
        // remain (console logging is also ensured by ConfigureCoreServices' AddConsole).
        var builder = WebApplication.CreateSlimBuilder();
        ProgramHelpers.ConfigureCoreServices(
            builder.Services, rootPath, builder.Environment.IsProduction(),
            port, settings.IdleTimeoutMinutes, instanceId, applicationStartTime);
        ProgramHelpers.AddRestApi(builder.Services);
        builder.Services.AddSingleton<IdleTracker>();
        builder.Services.AddHostedService<IdleShutdownService>();

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();
        app.Logger.LogInformation("Current directory is {Directory}", Environment.CurrentDirectory);

        // Initialize the repository factory before starting the app
        using (var scope = app.Services.CreateScope())
        {
            var repositoryFactory = scope.ServiceProvider.GetRequiredService<IRepositoryFactory>();
            await repositoryFactory.InitializeAsync(rootPath);
        }

        var idleTracker = app.Services.GetRequiredService<IdleTracker>();
        var apiMetrics = app.Services.GetRequiredService<IApiMetrics>();
        app.Use(async (context, next) =>
        {
            // /healthz is a liveness probe; it must not keep an otherwise idle instance alive.
            if (!context.Request.Path.StartsWithSegments("/healthz"))
            {
                idleTracker.Touch();
            }
            var startedAt = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                apiMetrics.Record(Stopwatch.GetElapsedTime(startedAt));
            }
        });

        ProgramHelpers.ConfigureApiEndpoints(app);

        var record = new InstanceRecord
        {
            RootPath = rootPath,
            Port = port,
            Pid = Environment.ProcessId,
            Backend = "inmemory",
            StartedAt = applicationStartTime.Value,
            InstanceId = instanceId,
            ProcessStartTime = InstanceIdentity.TryGetProcessStartTime(Environment.ProcessId, out var ownStart)
                ? ownStart
                : null,
        };
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            try { registry.Register(record); }
            catch (Exception ex) { app.Logger.LogWarning(ex, "Failed to register instance in the registry."); }
        });
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            try { registry.Unregister(rootPath, instanceId); }
            catch { /* prune-on-read is the fallback */ }
        });

        await app.RunAsync(ct);
    }

    private static async Task<int> RunMcpAsync(string rootPath, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();
        ProgramHelpers.ConfigureCoreServices(builder.Services, rootPath, builder.Environment.IsProduction());
        Mcp.ProgramHelpers.AddMcpServer(builder.Services);

        // In MCP mode stdout carries the JSON-RPC protocol; any log line written there corrupts
        // the stream. Drop every console provider registered so far (the Host default console and
        // ConfigureCoreServices' AddConsole, both stdout) and re-add a single console that writes
        // ALL levels to stderr. Applied last so it wins.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        var app = builder.Build();

        // Initialize the repository factory before starting the app
        using (var scope = app.Services.CreateScope())
        {
            var repositoryFactory = scope.ServiceProvider.GetRequiredService<IRepositoryFactory>();
            await repositoryFactory.InitializeAsync(rootPath);
        }

        await app.RunAsync(ct);
        return 0;
    }

    private static void RedirectOutputIfRequested(string? logFile)
    {
        if (string.IsNullOrEmpty(logFile)) return;

        var fullPath = System.IO.Path.GetFullPath(logFile);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = TextWriter.Synchronized(new StreamWriter(stream) { AutoFlush = true });
        Console.SetOut(writer);
        Console.SetError(writer);
    }

    internal static string NormalizePath(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
}
