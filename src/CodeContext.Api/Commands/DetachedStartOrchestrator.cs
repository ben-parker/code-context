using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeContext.Core.Instances;

namespace CodeContext.Api.Commands;

internal sealed record DetachedStartResult(
    bool Success,
    InstanceRecord? Instance,
    bool WasStarted,
    string? LogFile,
    string? ErrorMessage)
{
    public static DetachedStartResult Started(InstanceRecord instance, string? logFile = null)
        => new(true, instance, true, logFile, null);

    public static DetachedStartResult Existing(InstanceRecord instance)
        => new(true, instance, false, null, null);

    public static DetachedStartResult Failed(string message, string? logFile = null)
        => new(false, null, false, logFile, message);
}

internal interface IDetachedProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
}

internal sealed class SystemDetachedProcess(Process process) : IDetachedProcess
{
    public int Id => process.Id;
    public bool HasExited => process.HasExited;
    public int ExitCode => process.ExitCode;
    public void Dispose() => process.Dispose();
}

internal sealed record DetachedStartRuntime(
    Func<string?> GetProcessPath,
    Func<ProcessStartInfo, IDetachedProcess?> StartProcess,
    Func<HttpClient> CreateHttpClient,
    Func<int, bool> IsPortFree,
    Func<int> AllocatePort,
    Func<DateTimeOffset> UtcNow,
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    Func<string, CancellationToken, Task<IAsyncDisposable>> AcquireStartLockAsync,
    TimeSpan StartupTimeout);

/// <summary>
/// Owns the cross-process detached-start transaction shared by <c>start --detach</c>
/// and <c>query</c>.
/// </summary>
internal sealed class DetachedStartOrchestrator
{
    private readonly IInstanceRegistry _registry;
    private readonly DetachedStartRuntime _runtime;

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public DetachedStartOrchestrator(IInstanceRegistry registry)
        : this(registry, CreateDefaultRuntime())
    {
    }

    internal DetachedStartOrchestrator(
        IInstanceRegistry registry,
        DetachedStartRuntime runtime)
    {
        _registry = registry;
        _runtime = runtime;
    }

    public async Task<DetachedStartResult> StartAsync(
        string rootPath,
        int? explicitPort,
        int idleTimeoutMinutes,
        string? requestedLogFile,
        CancellationToken ct)
    {
        await using var startLock = await _runtime.AcquireStartLockAsync(rootPath, ct);

        // Same-or-ancestor match: starting from a subdirectory attaches to the ancestor
        // instance, whose recursive scan already covers the requested path. Attach is
        // best-effort under concurrent cold starts: the start lock is keyed per exact
        // rootPath, so simultaneous first-time starts from an ancestor and its subdirectory
        // hold different locks and can still fork two instances; this check only deduplicates
        // once the ancestor is registered.
        var existing = _registry.FindForPath(rootPath);
        if (existing is not null)
        {
            return DetachedStartResult.Existing(existing);
        }

        if (explicitPort is int requestedPort && !_runtime.IsPortFree(requestedPort))
        {
            return DetachedStartResult.Failed($"Port {requestedPort} is already in use.");
        }

        var exePath = _runtime.GetProcessPath();
        if (string.IsNullOrEmpty(exePath))
        {
            return DetachedStartResult.Failed("Cannot determine the executable path for a detached start.");
        }

        var port = explicitPort ?? _runtime.AllocatePort();
        var logFile = requestedLogFile ?? DefaultLogFile(rootPath);
        var instanceId = Guid.NewGuid().ToString("N");
        var startInfo = CreateStartInfo(
            exePath, rootPath, port, idleTimeoutMinutes, logFile, instanceId);

        using var child = _runtime.StartProcess(startInfo);
        if (child is null)
        {
            return DetachedStartResult.Failed("Failed to start the detached process.", logFile);
        }

        using var http = _runtime.CreateHttpClient();
        var deadline = _runtime.UtcNow() + _runtime.StartupTimeout;
        while (_runtime.UtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (child.HasExited)
            {
                return DetachedStartResult.Failed(
                    AppendLogTail(
                        $"Detached process exited early with code {child.ExitCode}. Log: {logFile}",
                        logFile),
                    logFile);
            }

            var healthy = false;
            try
            {
                using var response = await http.GetAsync($"http://localhost:{port}/healthz", ct);
                healthy = response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                // The child has not opened its listener yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // A single health probe timed out; keep polling within the startup window.
            }

            if (healthy)
            {
                var registered = _registry.GetAll().FirstOrDefault(instance =>
                    string.Equals(instance.RootPath, rootPath, PathComparison)
                    && string.Equals(instance.InstanceId, instanceId, StringComparison.Ordinal)
                    && instance.Pid == child.Id);
                if (registered is not null)
                {
                    return DetachedStartResult.Started(registered, logFile);
                }
            }

            await _runtime.DelayAsync(TimeSpan.FromMilliseconds(300), ct);
        }

        return DetachedStartResult.Failed(
            AppendLogTail(
                $"Detached process (pid {child.Id}) did not become healthy and register within 60s. Log: {logFile}",
                logFile),
            logFile);
    }

    private static DetachedStartRuntime CreateDefaultRuntime()
        => new(
            () => Environment.ProcessPath,
            startInfo => Process.Start(startInfo) is { } process
                ? new SystemDetachedProcess(process)
                : null,
            () => new HttpClient { Timeout = TimeSpan.FromSeconds(2) },
            PortAllocator.IsPortFree,
            () => PortAllocator.AllocatePort(),
            () => DateTimeOffset.UtcNow,
            Task.Delay,
            async (rootPath, ct) => await AcquireStartLockAsync(rootPath, ct),
            TimeSpan.FromSeconds(60));

    private static ProcessStartInfo CreateStartInfo(
        string exePath,
        string rootPath,
        int port,
        int idleTimeoutMinutes,
        string logFile,
        string instanceId)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = rootPath,
            };
        }
        else
        {
            startInfo = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = rootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
        }

        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(rootPath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--idle-timeout");
        startInfo.ArgumentList.Add(idleTimeoutMinutes.ToString());
        startInfo.ArgumentList.Add("--log-file");
        startInfo.ArgumentList.Add(logFile);
        startInfo.ArgumentList.Add("--instance-id");
        startInfo.ArgumentList.Add(instanceId);
        return startInfo;
    }

    private static string AppendLogTail(string message, string logFile, int lines = 20)
    {
        try
        {
            if (!File.Exists(logFile)) return message;
            var tail = File.ReadLines(logFile).TakeLast(lines).ToArray();
            return tail.Length == 0
                ? message
                : message + Environment.NewLine + string.Join(
                    Environment.NewLine, tail.Select(line => $"  {line}"));
        }
        catch
        {
            return message;
        }
    }

    private static string DefaultLogFile(string rootPath)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rootPath.ToLowerInvariant())))[..8];
        var leaf = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(leaf)) leaf = "root";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codecontext", "logs", $"{leaf}-{hash}.log");
    }

    private static async Task<FileStream> AcquireStartLockAsync(string rootPath, CancellationToken ct)
    {
        var identity = OperatingSystem.IsWindows() ? rootPath.ToUpperInvariant() : rootPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codecontext", "start-locks");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, $"{hash}.lock");
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
}
