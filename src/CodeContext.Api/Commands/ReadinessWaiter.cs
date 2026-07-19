using System.Text.Json;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;

namespace CodeContext.Api.Commands;

internal enum ReadinessResult
{
    Ready,
    Invalid,
    Timeout,
}

internal readonly record struct ReadinessOutcome(ReadinessResult Result, StatusResponseDto? Status)
{
    public static readonly ReadinessOutcome Invalid = new(ReadinessResult.Invalid, null);
    public static readonly ReadinessOutcome Timeout = new(ReadinessResult.Timeout, null);

    public static ReadinessOutcome Ready(StatusResponseDto status) => new(ReadinessResult.Ready, status);
}

/// <summary>
/// Polls <c>GET /api/status</c> until an instance reports indexing readiness, validating
/// the API contract version, the watched root, and the instance identity. Shared by
/// <c>query</c> (30s cap) and <c>init</c> (5-minute cap) so the validation lives in one place.
/// </summary>
internal static class ReadinessWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private const int ExpectedContractVersion = 1;

    public static async Task<ReadinessOutcome> WaitUntilReadyAsync(
        InstanceRecord instance,
        string lookupPath,
        HttpClient http,
        TextWriter error,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeSpan readinessTimeout,
        CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + readinessTimeout;
        while (true)
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var remaining = deadline - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    probeCts.CancelAfter(remaining);
                using var response = await http.GetAsync(
                    $"http://localhost:{instance.Port}/api/status", probeCts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var rejection = await response.Content.ReadAsStringAsync(probeCts.Token);
                    error.WriteLine(
                        $"Status API rejected the request ({(int)response.StatusCode} " +
                        $"{response.ReasonPhrase}): {rejection}");
                    return ReadinessOutcome.Invalid;
                }

                var body = await response.Content.ReadAsStringAsync(probeCts.Token);
                var status = JsonSerializer.Deserialize(
                    body, CodeContextJsonContext.Default.StatusResponseDto);
                if (status is null
                    || status.Api is null
                    || status.Indexing is null
                    || status.System is null
                    || string.IsNullOrWhiteSpace(status.Indexing.RootPath))
                {
                    error.WriteLine("The instance returned an incomplete status response.");
                    return ReadinessOutcome.Invalid;
                }

                if (status.Api.ContractVersion != ExpectedContractVersion)
                {
                    error.WriteLine(
                        $"API contract mismatch: expected {ExpectedContractVersion}, " +
                        $"received {status.Api.ContractVersion}.");
                    return ReadinessOutcome.Invalid;
                }

                if (!PathsEqual(status.Indexing.RootPath, instance.RootPath)
                    || !IsSameOrAncestor(status.Indexing.RootPath, lookupPath))
                {
                    error.WriteLine(
                        $"Instance root mismatch: registry has '{instance.RootPath}', " +
                        $"status returned '{status.Indexing.RootPath}'.");
                    return ReadinessOutcome.Invalid;
                }

                if (string.IsNullOrEmpty(instance.InstanceId)
                    || !string.Equals(
                        status.System.InstanceId, instance.InstanceId, StringComparison.Ordinal))
                {
                    error.WriteLine(
                        $"Instance identity mismatch for port {instance.Port}.");
                    return ReadinessOutcome.Invalid;
                }

                if (string.Equals(status.Indexing.Status, "ready", StringComparison.OrdinalIgnoreCase))
                {
                    return ReadinessOutcome.Ready(status);
                }
            }
            catch (HttpRequestException)
            {
                // A newly started instance may not have opened the status endpoint yet.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // A probe timed out; the deadline check below decides whether to retry.
            }
            catch (JsonException ex)
            {
                error.WriteLine($"Invalid status response: {ex.Message}");
                return ReadinessOutcome.Invalid;
            }

            if (timeProvider.GetUtcNow() >= deadline)
            {
                return ReadinessOutcome.Timeout;
            }

            var delay = deadline - timeProvider.GetUtcNow();
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            await delayAsync(delay < PollInterval ? delay : PollInterval, ct);
        }
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(
            StartCommandHandler.NormalizePath(first),
            StartCommandHandler.NormalizePath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsSameOrAncestor(string root, string path)
    {
        var normalizedRoot = StartCommandHandler.NormalizePath(root);
        var normalizedPath = StartCommandHandler.NormalizePath(path);
        if (PathsEqual(normalizedRoot, normalizedPath)) return true;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            || (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar
                && normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison));
    }
}
