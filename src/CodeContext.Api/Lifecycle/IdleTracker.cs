namespace CodeContext.Api.Lifecycle;

/// <summary>Tracks the time of the last meaningful API request, for idle auto-shutdown.</summary>
public class IdleTracker
{
    private long _lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;

    public void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);

    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    public TimeSpan IdleFor => DateTimeOffset.UtcNow - LastActivity;
}
