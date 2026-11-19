using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Memory;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Outcome = CodeContext.Core.Workers.LanguageWorkerService.HibernationOutcome;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Worker hibernation (memory plan M3) with the fake worker and the production wiring of the coordinator, the worker
/// service, the applier, the snapshot service and the hibernation timer on fake time: an idle worker exits only when
/// it can be woken correctly, the next change, refresh or syntax tree wakes it through the coordinator, and the graph
/// after a wake equals a fresh scan.
/// </summary>
public sealed class WorkerHibernationTests : IAsyncLifetime
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    private readonly string _base = Directory.CreateTempSubdirectory("cc-hibernate-").FullName;
    private readonly List<IAsyncDisposable> _started = [];
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private string FailMarker => Path.Combine(_base, "fail.marker");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "a.fake"), "a");
        File.WriteAllText(Path.Combine(Root, "b.fake"), "b");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var started in Enumerable.Reverse(_started)) await started.DisposeAsync();
        Directory.Delete(_base, recursive: true);
    }

    /// <summary>A host over the root with hibernation on fake time; snapshots are written 50 ms after a commit.</summary>
    private Task<Host> StartAsync(string behavior = "persistent-state", bool cache = true, TimeSpan? snapshotQuiet = null)
        => StartAsync(new PersistentStateTests.Instance(Root, KeyPath, ["--behavior", behavior, "--fail-marker", FailMarker],
            settings => settings with { QuietPeriod = snapshotQuiet ?? TimeSpan.FromMilliseconds(50) }, cache));

    private Task<Host> StartAsync(RegisteredWorker worker)
        => StartAsync(new PersistentStateTests.Instance(Root, KeyPath, worker,
            settings => settings with { QuietPeriod = TimeSpan.FromMilliseconds(50) }, cache: true));

    private async Task<Host> StartAsync(PersistentStateTests.Instance instance, Func<TimeProvider, QuietTimeTrim>? memoryTrim = null)
    {
        var time = new ManualTimeProvider();
        var hibernation = new WorkerHibernation(instance.Workers, instance.Coordinator, new WorkerHibernationSettings { Idle = Idle },
            time, () => instance.Snapshots.WrittenMatchesStore, memoryTrim: memoryTrim?.Invoke(time));
        var host = new Host(instance, hibernation, time);
        _started.Add(host);
        await instance.StartAsync();
        await hibernation.StartAsync(CancellationToken.None);
        await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the first scan");
        return host;
    }

    private Task<List<string>> FreshScanAsync(string behavior = "persistent-state")
        => FreshScanAsync(new PersistentStateTests.Instance(Root, KeyPath, ["--behavior", behavior], null, cache: false));

    private async Task<List<string>> FreshScanAsync(PersistentStateTests.Instance fresh)
    {
        await using var _ = fresh;
        await fresh.StartAsync();
        await fresh.WaitForAsync(() => fresh.ScanState.Phase == ScanPhase.Ready, "a fresh scan");
        var facts = Facts(fresh.Database);
        await fresh.StopAsync();
        return facts;
    }

    [Fact]
    public async Task IdleWorker_WithItsStateWritten_Hibernates_AndStatusSaysSo()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        var pid = host.Session.ProcessId!.Value;

        await host.IdleAsync();

        Assert.True(host.Instance.Workers.IsHibernated("fake"));
        Assert.True(HasExited(pid));
        var status = await host.Instance.Status.GetStatusAsync();
        var session = Assert.Single(status.Parsers.Sessions!);
        Assert.Equal("hibernated", session.State);
        Assert.Null(session.Pid);
        Assert.Null(session.LastError);
        Assert.Contains("Fake Worker", status.Parsers.Enabled);
        // The graph is current as of the last commit: answers are not labelled as cached or stale.
        Assert.Equal("ready", status.Indexing.Status);
        Assert.False(status.Indexing.Cache?.ServingCached ?? false);
        Assert.Equal(4, host.Instance.Database.NodeCount);
    }

    [Fact]
    public async Task Hibernation_AsksTheHostForAMemoryTrim_ThoughItAllocatedLittle()
    {
        var trims = 0;
        QuietTimeTrim? trim = null;
        var host = await StartAsync(
            new PersistentStateTests.Instance(Root, KeyPath, ["--behavior", "persistent-state", "--fail-marker", FailMarker],
                settings => settings with { QuietPeriod = TimeSpan.FromMilliseconds(50) }, cache: true),
            time => trim = new QuietTimeTrim(new QuietTimeTrimSettings(), time, allocatedBytes: () => 0, trim: () =>
            {
                Interlocked.Increment(ref trims);
                return new QuietTimeTrimResult(0, 0, TimeSpan.Zero);
            }));
        using var _ = trim;
        await host.WaitForStateWrittenAsync();

        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(0, Volatile.Read(ref trims));
        // The stopped process's pipes, threads and buffers are native memory the allocation counter does not see.
        host.Time.Advance(TimeSpan.FromSeconds(QuietTimeTrimSettings.DefaultQuietSeconds));
        Assert.Equal(1, Volatile.Read(ref trims));
    }

    [Fact]
    public async Task PersistentWorker_WhoseStateIsNotWrittenYet_StaysUp_AndHibernatesOnceItIs()
    {
        var host = await StartAsync(snapshotQuiet: TimeSpan.FromHours(1));
        await host.IdleAsync();
        Assert.False(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(Outcome.StateNotSaved, await host.TryHibernateNowAsync());
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
    }

    [Fact]
    public async Task PersistentWorker_WithTheCacheOff_NeverHibernates()
    {
        var host = await StartAsync(cache: false);
        await host.IdleAsync();
        Assert.False(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(Outcome.StateNotSaved, await host.TryHibernateNowAsync());
    }

    [Fact]
    public async Task ReadOnlyInstance_NeverHibernatesAPersistentWorker()
    {
        var writer = await StartAsync();
        await writer.WaitForStateWrittenAsync();
        var reader = await StartAsync();
        Assert.Equal(SnapshotCacheMode.ReadOnly, reader.Instance.Snapshots.Status.Mode);
        await reader.IdleAsync();
        Assert.False(reader.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(Outcome.StateNotSaved, await reader.TryHibernateNowAsync());
    }

    [Fact]
    public async Task WorkerWithoutTheCapability_HibernatesWithoutASnapshot_AndReReadsOnWake()
    {
        var host = await StartAsync("strict", cache: false);
        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("fake"));

        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        var before = host.Instance.Sink.Deltas.Count;
        await host.Instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "c.fake"));

        // The wake replaced the workspace from the files, which already holds the change: nothing follows it.
        var wake = Assert.Single(host.Instance.Sink.Deltas.Skip(before));
        Assert.True(wake.ReplacesWorkspace);
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.Equal(await FreshScanAsync("strict"), Facts(host.Instance.Database));
    }

    [Fact]
    public async Task EachOperation_ResetsTheIdleClock_AndAStatusReadDoesNot()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        host.Time.Advance(Idle - TimeSpan.FromMinutes(1));
        await host.Instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake"));
        await host.WaitForStateWrittenAsync();
        await host.AdvanceAndCheckAsync(TimeSpan.FromMinutes(2));
        Assert.False(host.Instance.Workers.IsHibernated("fake"));

        _ = await host.Instance.Status.GetStatusAsync();
        await host.AdvanceAndCheckAsync(Idle);
        Assert.True(host.Instance.Workers.IsHibernated("fake"));
    }

    [Fact]
    public async Task EditsWhileHibernated_WakeTheWorker_ThroughItsSavedState_AndEqualAFreshScan()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        var pid = host.Session.ProcessId;
        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("fake"));

        // Watcher events, as the watcher delivers them while the worker is down.
        File.Delete(Path.Combine(Root, "b.fake"));
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        var before = host.Instance.Sink.Deltas.Count;
        await host.Instance.Coordinator.NotifyFileChangedAsync(Path.Combine(Root, "b.fake"), FileChangeType.Deleted);
        await host.Instance.Coordinator.NotifyFileChangedAsync(Path.Combine(Root, "c.fake"), FileChangeType.Created);
        await host.Instance.WaitForAsync(() => !host.Instance.Workers.IsHibernated("fake") && !host.Instance.Coordinator.IsBusy, "the wake");

        // The wake was offered the saved state and sent only what changed (no replacement), then the touch followed.
        Assert.Contains(host.Instance.WorkerLog.Entries, entry => entry.Message.Contains("woke: its saved state was accepted", StringComparison.Ordinal));
        var deltas = host.Instance.Sink.Deltas.Skip(before).ToList();
        Assert.NotEmpty(deltas);
        Assert.DoesNotContain(deltas, delta => delta.ReplacesWorkspace);
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.NotEqual(pid, host.Session.ProcessId);
        Assert.Equal(0, host.Session.RestartCount);
        Assert.Equal(await FreshScanAsync(), Facts(host.Instance.Database));
    }

    [Fact]
    public async Task ChangeQueuedAroundTheHibernation_IsApplied_WhicheverRunsFirst()
    {
        var host = await StartAsync();
        for (var round = 0; round < 4; round++)
        {
            await host.WaitForStateWrittenAsync();
            var path = Path.Combine(Root, $"n{round}.fake");
            File.WriteAllText(path, "n");
            // The check and the event race onto the coordinator's queue.
            host.Time.Advance(Idle);
            await host.Instance.Coordinator.NotifyFileChangedAsync(path, FileChangeType.Created);
            await host.Instance.WaitForAsync(() => !host.Hibernation.IsChecking && !host.Instance.Coordinator.IsBusy, "the check and the batch");
            await host.Instance.WaitForAsync(() => host.Instance.Database.EnumerateNodes().Any(node => node.FilePath == path), "the new file's facts");
            Assert.NotEqual(ParserSessionState.Failed, host.Session.State);
        }
        await host.Instance.WaitForAsync(() => !host.Instance.Coordinator.IsBusy, "the last batch");
        Assert.Equal(await FreshScanAsync(), Facts(host.Instance.Database));
    }

    [Fact]
    public async Task FailedWake_IsReportedAsAFailure_AndTheNextChangeWakesAgain()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("fake"));

        File.WriteAllText(FailMarker, "fail");
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        await Assert.ThrowsAsync<ParserWorkerFailedException>(() => host.Instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "c.fake")));
        Assert.Equal(ParserSessionState.Failed, host.Session.State);
        Assert.Equal("failed", Assert.Single((await host.Instance.Status.GetStatusAsync()).Parsers.Sessions!).State);

        File.Delete(FailMarker);
        await host.Instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "c.fake"));
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.False(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(await FreshScanAsync(), Facts(host.Instance.Database));
    }

    [Fact]
    public async Task ExplicitRefresh_WakesTheWorker_ThroughItsSavedState()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        await host.IdleAsync();
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");

        Assert.NotNull(await host.Instance.Coordinator.TryRequestFullRescanAsync());
        await host.Instance.WaitForAsync(() => host.Instance.ScanState.Phase == ScanPhase.Ready && !host.Instance.Coordinator.IsBusy, "the refresh");

        Assert.False(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.False(host.Instance.Sink.Deltas.Last().ReplacesWorkspace);
        Assert.Equal(await FreshScanAsync(), Facts(host.Instance.Database));
    }

    [Fact]
    public async Task SyntaxTreeOnAHibernatedWorker_WakesItThroughTheCoordinator()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        await host.IdleAsync();
        var before = host.Instance.Sink.Deltas.Count;

        // The fake worker exposes no syntax tree: the request fails as it always has, after the wake.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            host.Instance.Workers.GetNativeSyntaxTreeAsync(Path.Combine(Root, "a.fake")));

        Assert.False(host.Instance.Workers.IsHibernated("fake"));
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.True(host.Instance.Sink.Deltas.Count > before);
    }

    [Fact]
    public async Task CrashUnrelatedToHibernation_StillRestartsAsBefore()
    {
        var host = await StartAsync();
        await host.WaitForStateWrittenAsync();
        using (var process = Process.GetProcessById(host.Session.ProcessId!.Value))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }
        await host.Instance.WaitForAsync(() => host.Session.State == ParserSessionState.Failed, "the crash report");
        await host.Instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake"));
        Assert.Equal(1, host.Session.RestartCount);
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.False(host.Instance.Workers.IsHibernated("fake"));
    }

    /// <summary>
    /// The real C# worker: hibernated with its state saved, an edit made while it is down wakes it through that state,
    /// and the graph equals a fresh scan.
    /// </summary>
    [Fact]
    public async Task CSharpWorker_EditWhileHibernated_WakesThroughItsSavedState_AndEqualsAFreshScan()
    {
        foreach (var name in new[] { "a.fake", "b.fake" }) File.Delete(Path.Combine(Root, name));
        File.WriteAllText(Path.Combine(Root, "Service.cs"), "namespace Demo; public class Service { public int Run() => 1; }\n");
        File.WriteAllText(Path.Combine(Root, "Caller.cs"), "namespace Demo; public class Caller { public int Go() => new Service().Run(); }\n");
        var host = await StartAsync(CSharpWorkerPipeline.CSharpWorkerRegistration());
        await host.WaitForStateWrittenAsync("csharp");
        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("csharp"));
        Assert.Equal(ParserSessionState.Hibernated, host.Session.State);

        var caller = Path.Combine(Root, "Caller.cs");
        File.WriteAllText(caller,
            "namespace Demo; public class Caller { public int Go() => new Service().Run(); public int Again() => new Service().Run() + 1; }\n");
        await host.Instance.Coordinator.NotifyFileChangedAsync(caller, FileChangeType.Changed);
        await host.Instance.WaitForAsync(() => host.Instance.Database.EnumerateNodes().Any(node => node.Name == "Again")
            && !host.Instance.Coordinator.IsBusy, "the woken worker's commit");

        Assert.Contains(host.Instance.WorkerLog.Entries, entry => entry.Message.Contains("woke: its saved state was accepted", StringComparison.Ordinal));
        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.Equal(await FreshScanAsync(new PersistentStateTests.Instance(Root, KeyPath, CSharpWorkerPipeline.CSharpWorkerRegistration(), null, cache: false)),
            Facts(host.Instance.Database));
    }

    /// <summary>The real TypeScript worker: hibernated without saved state, an edit wakes it, it re-reads, and the graph equals a fresh scan.</summary>
    [Fact]
    [Trait("Category", "ExternalTooling")]
    public async Task TypeScriptWorker_EditWhileHibernated_ReReadsOnWake_AndEqualsAFreshScan()
    {
        foreach (var name in new[] { "a.fake", "b.fake" }) File.Delete(Path.Combine(Root, name));
        File.WriteAllText(Path.Combine(Root, "service.ts"), "export class Service { run(): number { return 1; } }\n");
        File.WriteAllText(Path.Combine(Root, "caller.ts"),
            "import { Service } from './service';\nexport class Caller { go(): number { return new Service().run(); } }\n");
        var host = await StartAsync(TypeScriptWorkerProtocolTests.TypeScriptWorkerRegistration());
        await host.IdleAsync();
        Assert.True(host.Instance.Workers.IsHibernated("typescript"));

        var caller = Path.Combine(Root, "caller.ts");
        File.WriteAllText(caller,
            "import { Service } from './service';\nexport class Caller { go(): number { return new Service().run(); } again(): number { return new Service().run(); } }\n");
        await host.Instance.Coordinator.NotifyFileChangedAsync(caller, FileChangeType.Changed);
        await host.Instance.WaitForAsync(() => host.Instance.Database.EnumerateNodes().Any(node => node.Name == "again")
            && !host.Instance.Coordinator.IsBusy, "the woken worker's commit");

        Assert.Equal(ParserSessionState.Ready, host.Session.State);
        Assert.Equal(await FreshScanAsync(new PersistentStateTests.Instance(Root, KeyPath, TypeScriptWorkerProtocolTests.TypeScriptWorkerRegistration(), null, cache: false)),
            Facts(host.Instance.Database));
    }

    /// <summary>The graph's facts without the generation number the fake worker stamps on each fact.</summary>
    private static List<string> Facts(InMemoryDatabase database)
        => GraphFacts.Of(database).Select(fact => System.Text.RegularExpressions.Regex.Replace(fact, "\"gen\":\"[0-9]+\",?", "")).ToList();

    private static bool HasExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class Host(PersistentStateTests.Instance instance, WorkerHibernation hibernation, ManualTimeProvider time) : IAsyncDisposable
    {
        public PersistentStateTests.Instance Instance { get; } = instance;
        public WorkerHibernation Hibernation { get; } = hibernation;
        public ManualTimeProvider Time { get; } = time;

        public ParserSessionSnapshot Session => Instance.Sessions.GetSnapshots().Single();

        /// <summary>The worker's state is saved with a snapshot that holds the store as it is now.</summary>
        public Task WaitForStateWrittenAsync(string parserId = "fake")
            => Instance.WaitForAsync(() => !Instance.Coordinator.IsBusy && Instance.Snapshots.WrittenMatchesStore && StateRecorded(parserId),
                "the worker state to be written");

        private bool StateRecorded(string parserId)
        {
            var (shard, annotation) = Instance.Database.CurrentShard(InMemoryDatabase.ShardKey.From(parserId, "default"));
            return shard is not null && annotation?.StateFor(shard) is not null;
        }

        /// <summary>Lets the idle time pass and waits for the check it triggers.</summary>
        public Task IdleAsync() => AdvanceAndCheckAsync(Idle);

        /// <summary>Advances fake time; a hibernation check that falls due starts inside the advance, and is awaited.</summary>
        public async Task AdvanceAndCheckAsync(TimeSpan by)
        {
            Time.Advance(by);
            await Instance.WaitForAsync(() => !Hibernation.IsChecking, "the hibernation check");
        }

        public Task<LanguageWorkerService.HibernationOutcome> TryHibernateNowAsync()
            => Instance.Workers.TryHibernateAsync("fake", Idle, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await Hibernation.StopAsync(CancellationToken.None);
            Hibernation.Dispose();
            await Instance.DisposeAsync();
        }
    }
}
