using CodeContext.Core;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Api.Lifecycle;

/// <summary>
/// Shuts the instance down after a period without API activity so that instances started
/// by coding agents (which cannot be relied on to stop them) don't accumulate as orphans.
/// </summary>
public class IdleShutdownService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    private readonly IdleTracker _idleTracker;
    private readonly IIndexCoordinator _coordinator;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<IdleShutdownService> _logger;
    private readonly TimeSpan _idleTimeout;

    public IdleShutdownService(
        IdleTracker idleTracker,
        IIndexCoordinator coordinator,
        IHostApplicationLifetime lifetime,
        IOptions<CodeContextOptions> options,
        ILogger<IdleShutdownService> logger)
    {
        _idleTracker = idleTracker;
        _coordinator = coordinator;
        _lifetime = lifetime;
        _logger = logger;
        _idleTimeout = TimeSpan.FromMinutes(options.Value.IdleTimeoutMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_idleTimeout <= TimeSpan.Zero)
        {
            _logger.LogInformation("Idle auto-shutdown disabled (idle timeout is 0).");
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // An active index generation is a busy lease: a long scan must not be
                // killed just because nobody polled the API while it ran.
                if (_coordinator.IsBusy)
                {
                    _idleTracker.Touch();
                    continue;
                }

                if (_idleTracker.IdleFor >= _idleTimeout)
                {
                    _logger.LogInformation(
                        "No API activity for {Idle:g} (timeout {Timeout:g}); shutting down.",
                        _idleTracker.IdleFor, _idleTimeout);
                    _lifetime.StopApplication();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
