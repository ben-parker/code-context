using System.Globalization;
using CodeContext.Core.Memory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>
/// When <see cref="WorkerHibernation"/> stops idle workers; immutable. By default the idle time follows the wake cost:
/// a worker whose last index from its files took under <see cref="QuickRead"/> hibernates after <see cref="QuickIdle"/>,
/// any other (a slower one, or one that has only restored saved state) after <see cref="Idle"/>. A configured
/// <c>CodeContext:WorkerHibernateSeconds</c> sets one idle time for every worker; 0 turns hibernation off.
/// </summary>
public sealed record WorkerHibernationSettings
{
    /// <summary>The configuration key; <c>CodeContext__WorkerHibernateSeconds</c> in the environment.</summary>
    public const string ConfigurationKey = "CodeContext:WorkerHibernateSeconds";

    /// <summary><see cref="ConfigurationKey"/> as an environment variable.</summary>
    public const string EnvironmentVariable = "CodeContext__WorkerHibernateSeconds";

    /// <summary>
    /// Fifteen minutes, for a worker whose wake is slow or not known to be quick. A wake costs its index on top of the
    /// change that caused it, and the C# worker after restoring saved state then binds its compilations cold on the
    /// first edit (jellyfin: 23.5 s against 1.1 to 3.4 s awake), so it should not land between edits of one working
    /// session; after a quarter of an hour of silence the session has paused and the memory is worth more.
    /// </summary>
    public const int DefaultSeconds = 900;

    /// <summary>
    /// Two minutes, for a worker that reads its files in under <see cref="QuickReadSeconds"/>: its wake adds about that
    /// read to one edit (the TypeScript worker on this repository: 2.4 s instead of 1.2 s) and returns its whole
    /// process (about 210 MB there) for the rest of the pause.
    /// </summary>
    public const int QuickIdleSeconds = 120;

    /// <summary>The longest index from the files that still counts as a quick wake.</summary>
    public const int QuickReadSeconds = 5;

    /// <summary>The largest idle time accepted from configuration: one day.</summary>
    public const int MaxSeconds = 86_400;

    /// <summary>Idle time before a worker whose wake is not known to be quick is stopped. Zero turns hibernation off.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(DefaultSeconds);

    /// <summary>Idle time before a worker whose last index from its files took under <see cref="QuickRead"/> is stopped.</summary>
    public TimeSpan QuickIdle { get; init; } = TimeSpan.FromSeconds(QuickIdleSeconds);

    /// <summary>See <see cref="QuickReadSeconds"/>.</summary>
    public TimeSpan QuickRead { get; init; } = TimeSpan.FromSeconds(QuickReadSeconds);

    public bool Enabled => Idle > TimeSpan.Zero;

    /// <summary>The shorter of the two idle times.</summary>
    internal TimeSpan ShortestIdle => QuickIdle < Idle ? QuickIdle : Idle;

    public static readonly WorkerHibernationSettings Disabled = Fixed(TimeSpan.Zero);

    /// <summary>One idle time for every worker, whatever its wake costs, as a configured value gives.</summary>
    public static WorkerHibernationSettings Fixed(TimeSpan idle) => new() { Idle = idle, QuickIdle = idle };

    /// <summary>
    /// The idle time for a worker whose last index from its files took <paramref name="lastRead"/> (null: none yet,
    /// for instance a worker that has only restored saved state).
    /// </summary>
    public TimeSpan IdleFor(TimeSpan? lastRead)
        => Enabled && lastRead is { } read && read < QuickRead ? QuickIdle : Idle;

    /// <summary>
    /// The settings for a configured idle time in whole seconds: absent means the default policy, 0 turns hibernation
    /// off, any other value applies to every worker, and a value above one day is one day. Anything else (negative,
    /// not an integer) is refused: null.
    /// </summary>
    public static WorkerHibernationSettings? FromSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new WorkerHibernationSettings();
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return null;
        return Fixed(TimeSpan.FromSeconds(Math.Min(seconds, MaxSeconds)));
    }

    /// <summary>Reads <see cref="ConfigurationKey"/>; a value that is not valid keeps the default policy and is logged.</summary>
    internal static WorkerHibernationSettings Read(IConfiguration? configuration, ILogger? logger = null)
    {
        var text = configuration?[ConfigurationKey];
        if (FromSeconds(text) is { } settings) return settings;
        logger?.LogWarning("Ignoring configuration {Key} = '{Value}': not a valid value; keeping the default ({Quick} s for a worker that reads its files in under {Read} s, else {Kept} s).",
            ConfigurationKey, text, QuickIdleSeconds, QuickReadSeconds, DefaultSeconds);
        return new WorkerHibernationSettings();
    }
}

/// <summary>
/// Stops language worker processes that have had no work for their idle time
/// (<see cref="WorkerHibernationSettings.IdleFor"/>), while the host keeps serving their committed facts (memory plan
/// M3, cache plan 3.9).
///
/// The end of every worker operation arms one timer, unless it is already due sooner. When it fires, a maintenance command on the coordinator's loop
/// asks the worker service to hibernate each worker (<see cref="LanguageWorkerService.TryHibernateAsync"/>), so the
/// decision and the stop happen between mutations, never during a batch, scan or snapshot capture, and an operation
/// queued meanwhile simply runs after it. The check re-reads each worker's activity under its lock, so work that
/// arrived after the timer fired keeps the worker up. The command never makes the coordinator busy, so it does not
/// extend the instance's idle lease.
///
/// Waking needs nothing from here: the next operation on a hibernated worker re-indexes it first (on the loop, like
/// any operation), and a syntax-tree request asks the coordinator for that re-index through
/// <see cref="LanguageWorkerService.Waker"/>.
/// </summary>
internal sealed class WorkerHibernation : IHostedService, IDisposable
{
    private readonly LanguageWorkerService _workers;
    private readonly IndexCoordinator _coordinator;
    private readonly WorkerHibernationSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly QuietTimeTrim? _memoryTrim;
    private readonly Func<bool>? _writesSnapshots;
    private readonly ITimer? _timer;
    private readonly object _gate = new();

    /// <summary>
    /// How soon a worker found idle with its state not yet saved is looked at again (or its idle time, if shorter):
    /// long enough for a pending snapshot, which follows a commit within the snapshot delays, to be written.
    /// </summary>
    internal static readonly TimeSpan StateRetry = TimeSpan.FromSeconds(30);

    // Guarded by _gate.
    private bool _stopped;
    private bool _checking;
    private bool _activityDuringCheck;
    private DateTimeOffset? _due;
    private int _checks;

    public WorkerHibernation(
        LanguageWorkerService workers,
        IndexCoordinator coordinator,
        WorkerHibernationSettings settings,
        TimeProvider time,
        Func<bool>? snapshotWritten,
        ILogger? logger = null,
        QuietTimeTrim? memoryTrim = null,
        Func<bool>? writesSnapshots = null)
    {
        _memoryTrim = memoryTrim;
        _writesSnapshots = writesSnapshots;
        _workers = workers;
        _coordinator = coordinator;
        _settings = settings;
        _time = time;
        _logger = logger;
        if (!settings.Enabled) return;

        workers.Time = time;
        workers.SnapshotWritten = snapshotWritten;
        workers.Waker = (parserId, ct) => coordinator.RunMutationAsync(c => workers.WakeAsync(parserId, c), ct);
        workers.ActivityEnded = OnActivityEnded;
        _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public bool Enabled => _timer is not null;

    /// <summary>The settings it was built with.</summary>
    internal WorkerHibernationSettings Settings => _settings;

    /// <summary>Checks run so far. For tests.</summary>
    internal int Checks
    {
        get { lock (_gate) return _checks; }
    }

    /// <summary>A check is queued or running. For tests.</summary>
    internal bool IsChecking
    {
        get { lock (_gate) return _checking; }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate) _stopped = true;
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    /// <summary>The idle time of the parser's worker, from how long its last index from its files took.</summary>
    internal TimeSpan IdleFor(string parserId) => _settings.IdleFor(_workers.LastReadDuration(parserId));

    private void OnActivityEnded(string parserId)
    {
        lock (_gate)
        {
            if (_stopped) return;
            // A check in flight re-arms when it ends.
            if (_checking)
            {
                _activityDuringCheck = true;
                return;
            }
            // Workers can have different idle times, so a later activity must not postpone a check due sooner; a check
            // that finds this worker busy again re-arms for when it will be idle.
            var due = _time.GetUtcNow() + IdleFor(parserId);
            if (_due is { } armed && armed <= due) return;
            ArmLocked(due);
        }
    }

    private void ArmLocked(DateTimeOffset due)
    {
        _due = due;
        var now = _time.GetUtcNow();
        _timer?.Change(due > now ? due - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_stopped || _checking) return;
            _due = null;
            _checking = true;
            _activityDuringCheck = false;
        }
        DateTimeOffset? next = null;
        _coordinator.RunMaintenanceAsync(async ct => next = await CheckAsync(ct).ConfigureAwait(false))
            .ContinueWith(task =>
            {
                if (task.IsFaulted) _logger?.LogError(task.Exception, "Worker hibernation check failed.");
                lock (_gate)
                {
                    _checking = false;
                    _checks++;
                    if (_stopped) return;
                    var now = _time.GetUtcNow();
                    // Which worker had work is not recorded; the shorter idle time may check early, never late.
                    if (_activityDuringCheck && (next is null || now + _settings.ShortestIdle < next)) next = now + _settings.ShortestIdle;
                    if (next is { } due) ArmLocked(due);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>One pass over the workers, on the coordinator loop; returns when the next pass is due, if one is.</summary>
    private async Task<DateTimeOffset?> CheckAsync(CancellationToken ct)
    {
        DateTimeOffset? next = null;
        foreach (var parserId in _workers.ParserIds)
        {
            var idle = IdleFor(parserId);
            var outcome = await _workers.TryHibernateAsync(parserId, idle, ct).ConfigureAwait(false);
            // Stopping a process allocates little, so the trim's allocation threshold would skip the native memory
            // it lets go (measured: about 11 MB on this repository's host).
            if (outcome == LanguageWorkerService.HibernationOutcome.Hibernated) _memoryTrim?.RequestRelease();
            DateTimeOffset? due = outcome switch
            {
                LanguageWorkerService.HibernationOutcome.NotIdle => _workers.LastActivity(parserId) + idle,
                // The state is saved with the next snapshot, which follows a commit within the snapshot delays: a commit
                // of another worker's just before this check leaves it pending, so look again soon, not an idle time
                // later. Where no snapshot is written (cache off, read-only) it never is, and nothing looks again until
                // the worker has had work.
                LanguageWorkerService.HibernationOutcome.StateNotSaved => _writesSnapshots?.Invoke() == true
                    ? _time.GetUtcNow() + (idle < StateRetry ? idle : StateRetry)
                    : null,
                _ => null,
            };
            if (due is { } d && (next is null || d < next)) next = d;
        }
        return next;
    }

    public void Dispose()
    {
        lock (_gate) _stopped = true;
        _timer?.Dispose();
    }
}

public static class WorkerHibernationServiceCollectionExtensions
{
    /// <summary>
    /// Registers worker hibernation as a hosted service. Call after the snapshot writer (it reads whether the snapshot
    /// on disk is current). The idle time is read by hand from <c>CodeContext:WorkerHibernateSeconds</c> (Native-AOT
    /// safe): absent means the default policy (<see cref="WorkerHibernationSettings"/>), a value applies to every
    /// worker, 0 turns hibernation off.
    /// </summary>
    public static IServiceCollection AddCodeContextWorkerHibernation(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(WorkerHibernation).FullName!);
            var snapshots = sp.GetService<SnapshotService>();
            return new WorkerHibernation(
                sp.GetRequiredService<LanguageWorkerService>(),
                sp.GetRequiredService<IndexCoordinator>(),
                WorkerHibernationSettings.Read(sp.GetService<IConfiguration>(), logger),
                TimeProvider.System,
                snapshots is null ? null : () => snapshots.WrittenMatchesStore,
                logger,
                sp.GetService<QuietTimeTrim>(),
                snapshots is null ? null : () => snapshots.WritesSnapshots);
        });
        services.AddHostedService(sp => sp.GetRequiredService<WorkerHibernation>());
        return services;
    }
}
