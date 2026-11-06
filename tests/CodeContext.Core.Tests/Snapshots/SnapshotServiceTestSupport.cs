using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock moves only through <see cref="Advance"/>, which fires every
/// timer that falls due on the way, in due order, on the calling thread.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public int TimersCreated { get; private set; }

    /// <summary>Makes <see cref="CreateTimer"/> throw (a failure late in a start).</summary>
    public bool FailTimerCreation { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (FailTimerCreation) throw new InvalidOperationException("no timers here");
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            TimersCreated++;
        }
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.DueAt!.Value;
                next.DueAt = next.Period is { } period ? _now + period : null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }
        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A logger that keeps what it was given.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Enqueue((logLevel, formatter(state, exception)));

    public int Count(LogLevel level) => Entries.Count(entry => entry.Level == level);
}

/// <summary>
/// One snapshot service over a temp root, with a real coordinator whose single-file refresh commits
/// whatever <see cref="Mutation"/> does. The key lives beside the root, never in the user profile.
/// Everything is under one <c>ccsnap-*</c> directory that disposing deletes.
/// </summary>
internal sealed class SnapshotServiceHarness : IAsyncDisposable
{
    public static readonly ShardKey Shard = new("fake", "default");

    private readonly IGraphUpdateService _graphUpdate = Substitute.For<IGraphUpdateService>();
    private bool _disposed;

    public SnapshotServiceHarness(
        string? basePath = null, Func<SnapshotServiceSettings, SnapshotServiceSettings>? configure = null,
        bool enabled = true, InMemoryDatabase? database = null)
    {
        BasePath = basePath ?? Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        OwnsBase = basePath is null;
        RootPath = Path.Combine(BasePath, "repo");
        KeyPath = Path.Combine(BasePath, "home", ".codecontext", "cache.key");
        Directory.CreateDirectory(RootPath);

        Database = database ?? new InMemoryDatabase();
        _graphUpdate.ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Mutation?.Invoke();
                return Task.CompletedTask;
            });
        Coordinator = new IndexCoordinator(
            _graphUpdate, ScanState, Options.Create(new CodeContextOptions { RootPath = RootPath }), NullLoggerFactory.Instance);

        var settings = new SnapshotServiceSettings
        {
            Enabled = enabled,
            RootPath = RootPath,
            KeyPath = KeyPath,
            HostVersion = SnapshotTestSupport.HostVersion,
            InstanceId = new Guid("5d0c9a1e-7b3f-4c0e-9a6d-2f8b1c4e7a90"),
        };
        Settings = configure?.Invoke(settings) ?? settings;
        Service = new SnapshotService(Coordinator, Database, ScanState, Settings, Time, Logger);
    }

    public string BasePath { get; }
    public bool OwnsBase { get; }
    public string RootPath { get; }
    public string KeyPath { get; }
    public string WorkspacePath => Path.Combine(RootPath, ".codecontext", "cache", "default");
    public InMemoryDatabase Database { get; }
    public ScanStateService ScanState { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public ListLogger<SnapshotService> Logger { get; } = new();
    public IndexCoordinator Coordinator { get; }
    public SnapshotServiceSettings Settings { get; }
    public SnapshotService Service { get; }

    /// <summary>Runs inside the coordinator's refresh command; by default commits the next set of facts.</summary>
    public Action? Mutation { get; set; }

    public int FactSet { get; private set; }

    public async Task StartAsync()
    {
        await Coordinator.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => ScanState.Phase == ScanPhase.Ready, "the startup scan");
        await Service.StartAsync(CancellationToken.None);
    }

    /// <summary>One coordinator mutation command; with no explicit mutation, a commit with new facts.</summary>
    public async Task CommitAsync(Action? mutation = null)
    {
        Mutation = mutation ?? (() => CommitFacts(++FactSet));
        await Coordinator.RefreshFileAsync(Path.Combine(RootPath, "A.fake"));
    }

    /// <summary>Commits <paramref name="count"/> nodes whose names carry <paramref name="set"/>, replacing the shard.</summary>
    public void CommitFacts(int set, int count = 3, string? signature = null, ShardAnnotation? annotation = null)
    {
        var nodes = Enumerable.Range(0, count).Select(i => Node($"N{set}_{i}", signature is null ? null : signature + i)).ToList();
        Assert.True(Database.TryCommitGeneration(Database.LastCommittedGeneration + 1, nodes, [],
            new CommitScope(Shard.ParserId, Shard.WorkspaceId, null) { Annotation = annotation }));
    }

    /// <summary>A commit that changes no fact: the generation advances, every shard reference stays.</summary>
    public void CommitNothing(ShardAnnotation? annotation = null)
        => Assert.True(Database.TryCommitGeneration(Database.LastCommittedGeneration + 1, [], [],
            new CommitScope(Shard.ParserId, Shard.WorkspaceId, []) { Annotation = annotation }));

    public static CodeNode Node(string name, string? signature = null, ShardKey? key = null)
    {
        var shard = key ?? Shard;
        return new CodeNode
        {
            Id = $"{shard.ParserId}:{shard.WorkspaceId}:{name}",
            Identifier = $"{shard.ParserId}:{name}",
            Name = name,
            Type = "Class",
            Language = "fake",
            FilePath = "A.fake",
            Signature = signature,
            Metadata = SnapshotTestSupport.Owned(shard),
        };
    }

    public IReadOnlyList<string> Snapshots()
        => Directory.Exists(WorkspacePath)
            ? Directory.GetFiles(WorkspacePath, "graph.*.ccsnap").Select(path => Path.GetFileName(path))
                .Where(name => SnapshotFile.TryParseName(name, out _)).Order(StringComparer.Ordinal).ToList()
            : [];

    public IReadOnlyList<string> TempFiles()
        => Directory.Exists(WorkspacePath)
            ? Directory.GetFiles(WorkspacePath, "*.tmp").Select(path => Path.GetFileName(path)).ToList()
            : [];

    /// <summary>Advances the clock and waits until the service has nothing requested or in flight.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        Time.Advance(by);
        await IdleAsync();
    }

    public async Task IdleAsync()
    {
        // A capture requested by the timer finishes on the coordinator loop, then its write on a background task.
        await WaitUntilAsync(() => Service.IsIdle, "the snapshot service to go idle");
        await Service.CurrentWrite;
    }

    public SnapshotExpectations Expectations() => new(SnapshotTestSupport.HostVersion, Path.GetFullPath(RootPath), OperatingSystem.IsWindows());

    public byte[] Key() => File.ReadAllBytes(KeyPath);

    /// <summary>Writes a valid snapshot of the current store under <paramref name="sequence"/>, as an earlier run would have.</summary>
    /// <summary>
    /// With <paramref name="key"/>, a file with a valid header and host meta whose HMAC does not verify
    /// with this user's key: refused at authentication, after a full read and hash.
    /// </summary>
    public string PlantSnapshot(ulong sequence, uint id = 0x0badf00d, byte[]? key = null)
    {
        Directory.CreateDirectory(WorkspacePath);
        if (!File.Exists(KeyPath)) SnapshotKey.LoadOrCreate(KeyPath);
        var path = Path.Combine(WorkspacePath, SnapshotFile.FormatName(sequence, id));
        var input = new SnapshotWriteInput
        {
            Shards = SnapshotTestSupport.ShardsOf(Database),
            Host = new SnapshotHostMeta(SnapshotTestSupport.HostVersion, Path.GetFullPath(RootPath), "test", OperatingSystem.IsWindows()),
            Sequence = sequence,
        };
        Assert.True(SnapshotWriter.WriteFile(path, input, key ?? Key()).IsWritten);
        return path;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
            await Task.Delay(5);
        }
    }

    public async Task StopAsync()
    {
        await Service.StopAsync(CancellationToken.None);
        await Coordinator.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await StopAsync();
        }
        finally
        {
            Service.Dispose();
            Coordinator.Dispose();
            if (OwnsBase)
            {
                Directory.Delete(BasePath, recursive: true);
                Assert.False(Directory.Exists(BasePath), $"{BasePath} was not deleted");
            }
        }
    }
}
