using System.Text.Json;
using CodeContext.Core.Instances;

namespace CodeContext.Api.Commands;

public static class ListCommandHandler
{
    public static int Execute(bool json)
    {
        var registry = new InstanceRegistry();
        var instances = registry.GetAll();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                instances.ToList(), InstanceRegistryJsonContext.Default.ListInstanceRecord));
            return 0;
        }

        if (instances.Count == 0)
        {
            Console.WriteLine("No running instances.");
            return 0;
        }

        Console.WriteLine($"{"PORT",-6} {"PID",-8} {"BACKEND",-9} {"UPTIME",-10} ROOT");
        foreach (var instance in instances)
        {
            var uptime = DateTimeOffset.UtcNow - instance.StartedAt;
            Console.WriteLine(
                $"{instance.Port,-6} {instance.Pid,-8} {instance.Backend,-9} {FormatDuration(uptime),-10} {instance.RootPath}");
        }
        return 0;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration.TotalDays >= 1) return $"{(int)duration.TotalDays}d{duration.Hours}h";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours}h{duration.Minutes}m";
        if (duration.TotalMinutes >= 1) return $"{(int)duration.TotalMinutes}m";
        return $"{(int)duration.TotalSeconds}s";
    }
}
