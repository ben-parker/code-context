using System.Text.Json;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;

namespace CodeContext.Api.Commands;

public sealed record InitSettings(
    string Path,
    int? ExplicitPort,
    int IdleTimeoutMinutes,
    bool Wait,
    bool Json);

internal sealed record InitRuntime(
    HttpClient Http,
    Func<string, int?, int, CancellationToken, Task<DetachedStartResult>> StartDetachedAsync,
    TextWriter Output,
    TextWriter Error,
    TimeProvider TimeProvider,
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    TimeSpan ReadinessTimeout,
    Func<string, bool>? DirectoryExists = null);

/// <summary>
/// Implements <c>codecontext init</c>: proactively warms the index by discovering or
/// starting the background instance (and optionally waiting for the first scan) so an
/// agent's first query does not pay the cold-start cost.
/// Exit codes: 0 success, 1 argument/path or instance-validation error,
/// 3 <c>--wait</c> readiness timeout, 4 detached-startup failure.
/// </summary>
public static class InitCommandHandler
{
    public static async Task<int> ExecuteAsync(InitSettings settings, CancellationToken ct)
    {
        var registry = new InstanceRegistry();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var orchestrator = new DetachedStartOrchestrator(registry);
        var runtime = new InitRuntime(
            http,
            (root, port, idleTimeout, token) =>
                orchestrator.StartAsync(root, port, idleTimeout, null, token),
            Console.Out,
            Console.Error,
            TimeProvider.System,
            Task.Delay,
            TimeSpan.FromMinutes(5));

        return await ExecuteAsync(settings, runtime, ct);
    }

    internal static async Task<int> ExecuteAsync(
        InitSettings settings,
        InitRuntime runtime,
        CancellationToken ct)
    {
        string rootPath;
        try
        {
            rootPath = StartCommandHandler.NormalizePath(settings.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            runtime.Error.WriteLine($"Invalid path '{settings.Path}': {ex.Message}");
            return 1;
        }

        var directoryExists = runtime.DirectoryExists ?? Directory.Exists;
        if (!directoryExists(rootPath))
        {
            runtime.Error.WriteLine($"Path does not exist: {rootPath}");
            return 1;
        }

        DetachedStartResult started;
        try
        {
            started = await runtime.StartDetachedAsync(
                rootPath, settings.ExplicitPort, settings.IdleTimeoutMinutes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.Error.WriteLine($"Failed to start CodeContext: {ex.Message}");
            return 4;
        }

        if (!started.Success || started.Instance is null)
        {
            runtime.Error.WriteLine(started.ErrorMessage ?? "Failed to start the detached process.");
            return 4;
        }

        var instance = started.Instance;
        if (started.WasStarted)
        {
            var logSuffix = string.IsNullOrEmpty(started.LogFile)
                ? string.Empty
                : $" Log: {started.LogFile}";
            runtime.Error.WriteLine(
                $"CodeContext started for {rootPath} on port {instance.Port} (pid {instance.Pid}). " +
                $"Indexing in background.{logSuffix}");
        }
        else
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(instance.RootPath, rootPath, comparison))
            {
                runtime.Error.WriteLine(
                    $"CodeContext is already running for {instance.RootPath} " +
                    $"on port {instance.Port} (pid {instance.Pid}).");
            }
            else
            {
                runtime.Error.WriteLine(
                    $"CodeContext is already running for {instance.RootPath} " +
                    $"on port {instance.Port} (pid {instance.Pid}); it covers {rootPath}.");
            }
        }

        if (settings.Json)
        {
            runtime.Output.Write(JsonSerializer.Serialize(
                instance, InstanceRegistryJsonContext.Default.InstanceRecord));
        }

        if (!settings.Wait)
        {
            return 0;
        }

        ReadinessOutcome readiness;
        try
        {
            readiness = await ReadinessWaiter.WaitUntilReadyAsync(
                instance,
                rootPath,
                runtime.Http,
                runtime.Error,
                runtime.TimeProvider,
                runtime.DelayAsync,
                runtime.ReadinessTimeout,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.Error.WriteLine($"Failed to read instance status: {ex.Message}");
            return 1;
        }

        if (readiness.Result == ReadinessResult.Timeout)
        {
            runtime.Error.WriteLine(
                $"Indexing did not become ready within " +
                $"{runtime.ReadinessTimeout.TotalMinutes:0} minutes. Run " +
                $"codecontext status --path {QuotePath(rootPath)} for details.");
            return 3;
        }

        if (readiness.Result == ReadinessResult.Invalid)
        {
            return 1;
        }

        runtime.Error.WriteLine(ReadyMessage(rootPath, readiness.Status));
        return 0;
    }

    private static string ReadyMessage(string rootPath, StatusResponseDto? status)
    {
        var fileCount = status?.Database?.FileCount ?? status?.FileCount ?? 0;
        var nodeCount = status?.Database?.NodeCount ?? status?.NodeCount ?? 0;
        return fileCount > 0 || nodeCount > 0
            ? $"Index ready for {rootPath} ({fileCount} files, {nodeCount} nodes)."
            : $"Index ready for {rootPath}.";
    }

    private static string QuotePath(string path)
        => path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;
}
