using System.Text.Json;
using CodeContext.Core.Instances;

namespace CodeContext.Api.Commands;

public static class StatusCommandHandler
{
    public static async Task<int> ExecuteAsync(string? path, CancellationToken ct)
    {
        var lookupPath = path ?? Directory.GetCurrentDirectory();
        var registry = new InstanceRegistry();
        var target = registry.FindForPath(lookupPath);
        if (target is null)
        {
            Console.Error.WriteLine($"No running CodeContext instance found for {lookupPath}.");
            return 1;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var body = await http.GetStringAsync($"http://localhost:{target.Port}/api/status", ct);
            if (!string.IsNullOrEmpty(target.InstanceId))
            {
                using var document = JsonDocument.Parse(body);
                var returnedInstanceId = document.RootElement
                    .GetProperty("system")
                    .GetProperty("instanceId")
                    .GetString();
                if (!string.Equals(returnedInstanceId, target.InstanceId, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"Port {target.Port} answered, but its instance identity does not match the registry record.");
                    return 1;
                }
            }
            Console.WriteLine(body);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Instance is registered (port {target.Port}, pid {target.Pid}) but did not respond: {ex.Message}");
            return 1;
        }
    }
}
