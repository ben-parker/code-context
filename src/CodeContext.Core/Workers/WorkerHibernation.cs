using System.Globalization;
using CodeContext.Core.Memory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>When <see cref="WorkerHibernation"/> stops idle workers; immutable.</summary>
public sealed record WorkerHibernationSettings
{
    /// <summary>The configuration key; <c>CodeContext__WorkerHibernateSeconds</c> in the environment.</summary>
    public const string ConfigurationKey = "CodeContext:WorkerHibernateSeconds";

    /// <summary><see cref="ConfigurationKey"/> as an environment variable.</summary>
    public const string EnvironmentVariable = "CodeContext__WorkerHibernateSeconds";

    /// <summary>
    /// Fifteen minutes. A wake costs a few seconds (the C# worker restoring its saved state, the TypeScript worker
    /// re-reading its files) on top of the change that caused it, so it should not land between edits of one working
    /// session, which come seconds to minutes apart; after a quarter of an hour of silence the session has paused and
    /// the workers' memory, most of an idle instance's, is worth more.
    /// </summary>
    public const int DefaultSeconds = 900;

    /// <summary>The largest idle time accepted from configuration: one day.</summary>
    public const int MaxSeconds = 86_400;

    /// <summary>Time a worker must spend without an operation before its process is stopped. Zero turns hibernation off.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromSeconds(DefaultSeconds);

    public bool Enabled => Idle > TimeSpan.Zero;

    public static readonly WorkerHibernationSettings Disabled = new() { Idle = TimeSpan.Zero };

    /// <summary>
    /// The settings for a configured idle time in whole seconds: absent means the default, 0 turns hibernation off, a
    /// value above one day is one day. Anything else (negative, not an integer) is refused: null.
    /// </summary>
    public static WorkerHibernationSettings? FromSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new WorkerHibernationSettings();
        if (!long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return null;
        return new WorkerHibernationSettings { Idle = TimeSpan.FromSeconds(Math.Min(seconds, MaxSeconds)) };
    }

    /// <summary>Reads <see cref="ConfigurationKey"/>; a value that is not valid keeps the default and is logged.</summary>
    internal static WorkerHibernationSettings Read(IConfiguration? configuration, ILogger? logger = null)
    {
        var text = configuration?[ConfigurationKey];
        if (FromSeconds(text) is { } settings) return settings;
        logger?.LogWarning("Ignoring configuration {Key} = '{Value}': not a valid value; keeping {Kept}.",
            ConfigurationKey, text, DefaultSeconds);
        return new WorkerHibernationSettings();
    }
}

/// <summary>
/// Stops language worker processes that have had no work for <see cref="WorkerHibernationSettings.Idle"/>, while the
/// host keeps serving their committed facts (memory plan M3, cache plan 3.9).
///
/// The end of every worker operation arms one timer. When it fires, a maintenance command on the coordinator's loop
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
    private readonly ITimer? _timer;
    private readonly object _gate = new();

    // Guarded by _gate.
    private bool _stopped;
    private bool _checking;
    private bool _activityDuringCheck;
    private int _checks;

    public WorkerHibernation(
        LanguageWorkerService workers,
        IndexCoordinator coordinator,
        WorkerHibernationSettings settings,
        TimeProvider time,
        Func<bool>? snapshotWritten,
        ILogger? logger = null,
        QuietTimeTrim? memoryTrim = null)
    {
        _memoryTrim = memoryTrim;
        _workers = workers;
        _coordinator = coordinator;
        _settings = settings;
        _time = time;
        _logger = logger;
        if (!settings.Enabled) return;

        workers.Time = time;
        workers.SnapshotWritten = snapshotWritten;
        workers.Waker = (parserId, ct) => coordinator.RunMutationAsync(c => workers.WakeAsync(parserId, c), ct);
        workers.ActivityEnded = _ => OnActivityEnded();
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

    private void OnActivityEnded()
    {
        lock (_gate)
        {
            if (_stopped) return;
            // A check in flight re-arms when it ends.
            if (_checking) _activityDuringCheck = true;
            else _timer?.Change(_settings.Idle, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_stopped || _checking) return;
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
                    if (_activityDuringCheck && (next is null || now + _settings.Idle < next)) next = now + _settings.Idle;
                    if (next is { } due) _timer!.Change(due > now ? due - now : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>One pass over the workers, on the coordinator loop; returns when the next pass is due, if one is.</summary>
    private async Task<DateTimeOffset?> CheckAsync(CancellationToken ct)
    {
        DateTimeOffset? next = null;
        foreach (var parserId in _workers.ParserIds)
        {
            var outcome = await _workers.TryHibernateAsync(parserId, _settings.Idle, ct).ConfigureAwait(false);
            // Stopping a process allocates little, so the trim's allocation threshold would skip the native memory
            // it lets go (measured: about 11 MB on this repository's host).
            if (outcome == LanguageWorkerService.HibernationOutcome.Hibernated) _memoryTrim?.RequestRelease();
            DateTimeOffset? due = outcome switch
            {
                LanguageWorkerService.HibernationOutcome.NotIdle => _workers.LastActivity(parserId) + _settings.Idle,
                // The state is saved with the next snapshot, which follows a commit within the snapshot delays.
                LanguageWorkerService.HibernationOutcome.StateNotSaved => _time.GetUtcNow() + _settings.Idle,
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
    /// safe): absent means <see cref="WorkerHibernationSettings.DefaultSeconds"/>, 0 turns hibernation off.
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
                sp.GetService<QuietTimeTrim>());
        });
        services.AddHostedService(sp => sp.GetRequiredService<WorkerHibernation>());
        return services;
    }
}
