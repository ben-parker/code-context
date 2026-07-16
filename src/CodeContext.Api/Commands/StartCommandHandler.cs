using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

        // Serialize the external detached-start transaction (check registry → spawn →
        // wait for child registration) per repository root. Without this lock, two
        // agents starting the same root simultaneously can both observe no record and
        // create duplicate hosts. The detached child is a foreground invocation and
        // therefore does not acquire this parent-side lock.
        await using var startLock = settings.Detach
            ? await AcquireDetachedStartLockAsync(rootPath, ct)
            : null;

        var registry = new InstanceRegistry();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var existing = registry.GetAll().FirstOrDefault(i => string.Equals(i.RootPath, rootPath, comparison));
        if (existing is not null)
        {
            // Idempotent start: an agent re-running `start` should not fail or fork a duplicate.
            Console.Error.WriteLine($"CodeContext is already running for {existing.RootPath} on port {existing.Port} (pid {existing.Pid}).");
            Console.WriteLine(JsonSerializer.Serialize(existing, InstanceRegistryJsonContext.Default.InstanceRecord));
            return 0;
        }

        if (settings.ExplicitPort is int explicitPort && !PortAllocator.IsPortFree(explicitPort))
        {
            Console.Error.WriteLine($"Port {explicitPort} is already in use.");
            return 1;
        }

        if (settings.Detach)
        {
            return await DetachAsync(rootPath, settings, ct);
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
        RedirectOutputIfRequested(settings.LogFile);

        var instanceId = string.IsNullOrEmpty(settings.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : settings.InstanceId;

        var builder = WebApplication.CreateBuilder();
        ProgramHelpers.ConfigureCoreServices(
            builder.Services, rootPath, builder.Environment.IsProduction(),
            port, settings.IdleTimeoutMinutes, instanceId);
        ProgramHelpers.AddRestApi(builder.Services);
        builder.Services.AddSingleton<IdleTracker>();
        builder.Services.AddHostedService<IdleShutdownService>();

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();

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
            StartedAt = DateTimeOffset.UtcNow,
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

    private static async Task<int> DetachAsync(string rootPath, StartSettings settings, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Console.Error.WriteLine("Cannot determine the executable path for a detached start.");
            return 1;
        }

        var port = settings.ExplicitPort ?? PortAllocator.AllocatePort();
        var logFile = settings.LogFile ?? DefaultLogFile(rootPath);
        var instanceId = Guid.NewGuid().ToString("N");

        // The child must not hold our stdout open: a shell that captures this command's
        // output would otherwise block until the *server* exits. On Windows, any
        // CreateProcess with inherited handles leaks the pipe (even with redirected
        // stdio), so launch via ShellExecute, which inherits nothing. On Unix, .NET
        // marks its pipes close-on-exec, so plain redirection is enough. The child
        // writes its own output to the log file in both cases.
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = rootPath,
            };
        }
        else
        {
            psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
        }
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(rootPath);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--idle-timeout");
        psi.ArgumentList.Add(settings.IdleTimeoutMinutes.ToString());
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(logFile);
        psi.ArgumentList.Add("--instance-id");
        psi.ArgumentList.Add(instanceId);

        var child = Process.Start(psi);
        if (child is null)
        {
            Console.Error.WriteLine("Failed to start the detached process.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        var up = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (child.HasExited)
            {
                Console.Error.WriteLine($"Detached process exited early with code {child.ExitCode}. Log: {logFile}");
                PrintLogTail(logFile);
                return 1;
            }
            try
            {
                var response = await http.GetAsync($"http://localhost:{port}/healthz", ct);
                if (response.IsSuccessStatusCode)
                {
                    up = true;
                    break;
                }
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(300, ct);
        }

        if (!up)
        {
            Console.Error.WriteLine($"Detached process (pid {child.Id}) did not respond within 60s. Log: {logFile}");
            PrintLogTail(logFile);
            return 1;
        }

        var record = new InstanceRecord
        {
            RootPath = rootPath,
            Port = port,
            Pid = child.Id,
            Backend = "inmemory",
            StartedAt = DateTimeOffset.UtcNow,
            InstanceId = instanceId,
            ProcessStartTime = InstanceIdentity.TryGetProcessStartTime(child.Id, out var childStart)
                ? childStart
                : null,
        };
        Console.WriteLine(JsonSerializer.Serialize(record, InstanceRegistryJsonContext.Default.InstanceRecord));
        Console.Error.WriteLine($"CodeContext started for {rootPath} on port {port} (pid {child.Id}). Log: {logFile}");
        return 0;
    }

    private static async Task<int> RunMcpAsync(string rootPath, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder();
        ProgramHelpers.ConfigureCoreServices(builder.Services, rootPath, builder.Environment.IsProduction());
        Mcp.ProgramHelpers.AddMcpServer(builder.Services);

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

    private static void PrintLogTail(string logFile, int lines = 20)
    {
        try
        {
            if (!File.Exists(logFile)) return;
            foreach (var line in File.ReadLines(logFile).TakeLast(lines))
            {
                Console.Error.WriteLine($"  {line}");
            }
        }
        catch { /* best effort */ }
    }

    private static string DefaultLogFile(string rootPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToLowerInvariant())))[..8];
        var leaf = System.IO.Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(leaf)) leaf = "root";
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codecontext", "logs", $"{leaf}-{hash}.log");
    }

    private static async Task<FileStream> AcquireDetachedStartLockAsync(
        string rootPath, CancellationToken ct)
    {
        var identity = OperatingSystem.IsWindows() ? rootPath.ToUpperInvariant() : rootPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codecontext", "start-locks");
        Directory.CreateDirectory(directory);
        var lockPath = System.IO.Path.Combine(directory, $"{hash}.lock");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(75);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Timed out waiting for another detached start of '{rootPath}' to finish.", ex);
            }
        }
    }

    internal static string NormalizePath(string path)
        => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
}
