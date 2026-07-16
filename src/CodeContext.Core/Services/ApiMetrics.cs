namespace CodeContext.Core.Services;

public interface IApiMetrics
{
    void Record(TimeSpan elapsed);
    ApiMetricsSnapshot GetSnapshot();
}

public readonly record struct ApiMetricsSnapshot(long RequestCount, TimeSpan AverageResponseTime);

public sealed class ApiMetrics : IApiMetrics
{
    private long _requestCount;
    private long _totalElapsedTicks;

    public void Record(TimeSpan elapsed)
    {
        Interlocked.Add(ref _totalElapsedTicks, elapsed.Ticks);
        Interlocked.Increment(ref _requestCount);
    }

    public ApiMetricsSnapshot GetSnapshot()
    {
        var requestCount = Interlocked.Read(ref _requestCount);
        var totalElapsedTicks = Interlocked.Read(ref _totalElapsedTicks);
        var average = requestCount == 0
            ? TimeSpan.Zero
            : TimeSpan.FromTicks(totalElapsedTicks / requestCount);

        return new ApiMetricsSnapshot(requestCount, average);
    }
}
