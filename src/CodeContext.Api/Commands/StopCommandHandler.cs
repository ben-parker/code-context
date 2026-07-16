using System.Diagnostics;
using CodeContext.Core.Instances;

namespace CodeContext.Api.Commands;

public static class StopCommandHandler
{
    public static async Task<int> ExecuteAsync(string? path, bool all, CancellationToken ct)
    {
        var registry = new InstanceRegistry();

        List<InstanceRecord> targets;
        if (all)
        {
            targets = registry.GetAll().ToList();
            if (targets.Count == 0)
            {
                Console.Error.WriteLine("No running instances.");
                return 0;
            }
        }
        else
        {
            var lookupPath = path ?? Directory.GetCurrentDirectory();
            var target = registry.FindForPath(lookupPath);
            if (target is null)
            {
                Console.Error.WriteLine($"No running CodeContext instance found for {lookupPath}.");
                return 1;
            }
            targets = [target];
        }

        var failures = 0;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var instance in targets)
        {
            // Never signal or kill a PID the record no longer describes (PID reuse,
            // stale entry): validate the identity fingerprint first.
            if (!InstanceIdentity.Matches(instance))
            {
                Console.Error.WriteLine(
                    $"Record for {instance.RootPath} (pid {instance.Pid}) no longer matches a live instance; removing stale entry.");
                registry.Unregister(instance.RootPath,
                    string.IsNullOrEmpty(instance.InstanceId) ? null : instance.InstanceId);
                continue;
            }

            Console.Error.WriteLine($"Stopping {instance.RootPath} (port {instance.Port}, pid {instance.Pid})...");
            try
            {
                await http.PostAsync(
                    $"http://localhost:{instance.Port}/api/shutdown?instanceId={Uri.EscapeDataString(instance.InstanceId)}",
                    content: null, ct);
            }
            catch
            {
                // instance not responding — fall through to the kill path
            }

            if (!await WaitForExitAsync(instance.Pid, TimeSpan.FromSeconds(5), ct))
            {
                try
                {
                    // Re-validate identity right before the kill: the graceful window may
                    // have let the process exit and the OS reuse its PID.
                    if (InstanceIdentity.Matches(instance))
                    {
                        Process.GetProcessById(instance.Pid).Kill(entireProcessTree: true);
                    }
                }
                catch (ArgumentException)
                {
                    // already gone
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to kill pid {instance.Pid}: {ex.Message}");
                    failures++;
                    continue;
                }
            }

            registry.Unregister(instance.RootPath,
                string.IsNullOrEmpty(instance.InstanceId) ? null : instance.InstanceId);
            Console.Error.WriteLine("Stopped.");
        }

        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (Process.GetProcessById(pid).HasExited) return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(200, ct);
        }
        return false;
    }
}
