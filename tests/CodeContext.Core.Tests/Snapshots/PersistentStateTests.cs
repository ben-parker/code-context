using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Sdk;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Persistent worker state end to end (plan phase 5), with the real fake worker and the production wiring of
/// the coordinator, the load step, the worker service, the applier and the snapshot service: a restart whose
/// worker accepts its baseline commits a file-scoped delta (empty when nothing changed) instead of a
/// replacement and writes nothing; and each way the session half of the pairing proof breaks (a rolled-back
/// reconciliation, a request that failed after its delta committed, a worker restart, a verification that
/// failed) makes the next capture write the shard without state, so the start after it offers no baseline.
/// </summary>
public sealed class PersistentStateTests : IAsyncLifetime
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-state-").FullName;
    private readonly List<Instance> _started = [];
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private string Workspace => Path.Combine(Root, ".codecontext", "cache", "default");
    private string FailMarker => Path.Combine(_base, "fail.marker");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "a.fake"), "a");
        File.WriteAllText(Path.Combine(Root, "b.fake"), "b");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_base, recursive: true);
        return Task.CompletedTask;
    }

    private Instance Start(string behavior, Func<SnapshotServiceSettings, SnapshotServiceSettings>? configure = null, bool cache = true)
    {
        var instance = new Instance(Root, KeyPath, ["--behavior", behavior, "--fail-marker", FailMarker], configure, cache);
        _started.Add(instance);
        return instance;
    }

    /// <summary>What every host of this test warned about, for a failure message: a save that timed out says so here.</summary>
    private string Warnings()
        => _started.SelectMany(instance => instance.Warnings).ToList() is { Count: > 0 } warnings ? string.Join(" | ", warnings) : "none";

    /// <summary>A first run that scans, saves the worker's state and writes the snapshot on its way down.</summary>
    private async Task FirstRunAsync(string behavior = "persistent-state")
    {
        await using var first = Start(behavior);
        await first.StartAsync();
        await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
        await first.StopAsync();
        if (behavior == "persistent-state") Assert.True(Newest().Shards.Single().Info.StateFileName is not null, $"The first run saved no state. Warnings: {Warnings()}");
    }

    /// <summary>The highest sequence: an older file another process holds stays until a later retire (see <see cref="AFileBusyAtRetire_StaysUntilTheNextStart_AndIsNotUsed"/>).</summary>
    private string NewestPath() => Directory.GetFiles(Workspace, "*.ccsnap").Order(StringComparer.Ordinal).Last();

    private SnapshotLoadResult Newest()
    {
        var path = NewestPath();
        var loaded = SnapshotLoader.Load(path, File.ReadAllBytes(KeyPath), new SnapshotExpectations(
            SnapshotServiceSettings.From(new CodeContextOptions { RootPath = Root, CacheEnabled = false }).HostVersion, Path.GetFullPath(Root), OperatingSystem.IsWindows()));
        Assert.True(loaded.Loaded, loaded.Reason);
        return loaded;
    }

    private ulong NewestSequence()
    {
        Assert.True(SnapshotFile.TryParseName(Path.GetFileName(NewestPath()), out var sequence));
        return sequence;
    }

    private string[] StateFiles() => Directory.GetFiles(Workspace, "worker-*").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    /// <summary>The start after: how its verification treated the fake worker's shard.</summary>
    private async Task<string> NextVerificationAsync(string behavior = "persistent-state")
    {
        await using var next = Start(behavior);
        await next.StartAsync();
        await next.WaitForAsync(() => next.ScanState.Phase == ScanPhase.Ready, "the verification scan");
        return next.CacheState.GetStatus()!.Verification!["fake"];
    }

    [Fact]
    public async Task Restart_CommitsAnEmptyDelta_ReportsBaseline_AndWritesNoSnapshot()
    {
        await FirstRunAsync();
        var info = Newest().Shards.Single().Info;
        Assert.NotNull(info.StateFileName);
        Assert.Equal([info.StateFileName], StateFiles());
        var stateBytes = File.ReadAllBytes(Path.Combine(Workspace, info.StateFileName!));

        await using var second = Start("persistent-state");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");

        // One empty file-scoped delta, not a replacement.
        var delta = Assert.Single(second.Sink.Deltas);
        Assert.False(delta.ReplacesWorkspace);
        Assert.Empty(delta.Nodes);
        Assert.Empty(delta.ReplacesFiles);
        var status = await second.Status.GetStatusAsync();
        Assert.Equal("ready", status.Indexing.Status);
        Assert.Equal(CacheState.Verified, status.Indexing.Cache!.State);
        Assert.Equal(CacheState.Baseline, status.Indexing.Cache.Verification!["fake"]);
        Assert.Equal(stateBytes.Length, status.Indexing.Cache.WorkerStateBytes);
        Assert.Equal(4, second.Database.NodeCount);
        await second.StopAsync();

        // Nothing changed, so nothing was written: the same snapshot and the same state file.
        Assert.Equal(0, second.Snapshots.Status.WritesCompleted);
        Assert.Equal(1UL, NewestSequence());
        Assert.Equal([info.StateFileName], StateFiles());
        Assert.Equal(stateBytes, File.ReadAllBytes(Path.Combine(Workspace, info.StateFileName!)));
    }

    [Fact]
    public async Task EditedWhileDown_TheAcceptedBaselineCommitsOnlyWhatChanged_AndEqualsAFreshScan()
    {
        await FirstRunAsync();
        var firstState = Assert.Single(StateFiles());
        File.Delete(Path.Combine(Root, "b.fake"));
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");

        await using (var second = Start("persistent-state"))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
            var delta = Assert.Single(second.Sink.Deltas);
            Assert.False(delta.ReplacesWorkspace);
            Assert.Equal([Path.Combine(Root, "b.fake"), Path.Combine(Root, "c.fake")], delta.ReplacesFiles.Order(StringComparer.Ordinal));
            Assert.Equal(CacheState.Baseline, second.CacheState.GetStatus()!.Verification!["fake"]);

            await using var fresh = Start("persistent-state", cache: false);
            await fresh.StartAsync();
            await fresh.WaitForAsync(() => fresh.ScanState.Phase == ScanPhase.Ready, "a fresh scan");
            Assert.Equal(GraphFacts.Of(fresh.Database), GraphFacts.Of(second.Database));
            await second.StopAsync();
        }

        // The patched graph is written with a new state file; the old pair is retired.
        Assert.Equal(2UL, NewestSequence());
        var secondState = Assert.Single(StateFiles());
        Assert.NotEqual(firstState, secondState);
        Assert.Equal(secondState, Newest().Shards.Single().Info.StateFileName);
        Assert.Equal(CacheState.Baseline, await NextVerificationAsync());
    }

    [Fact]
    public async Task StopWhileTheQuietCaptureIsSaving_TheFinalSnapshotNamesAStateFileThatExists_AndTheNextStartUsesIt()
    {
        await using (var first = Start("save-state-slow", settings => settings with { QuietPeriod = TimeSpan.FromMilliseconds(100) }))
        {
            await first.StartAsync();
            await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
            // The quiet capture has begun; its worker takes 1.5 s to save, and the stop comes meanwhile.
            await first.WaitForAsync(() => first.Snapshots.IsCaptureRequested, "the quiet capture");
            await first.StopAsync();
            Assert.Equal(1, first.Snapshots.Status.WritesCompleted);
        }

        var stateName = Newest().Shards.Single().Info.StateFileName;
        Assert.NotNull(stateName);
        Assert.True(File.Exists(Path.Combine(Workspace, stateName)), $"{stateName} is gone");
        Assert.Equal([stateName], StateFiles());
        Assert.Equal(CacheState.Baseline, await NextVerificationAsync());
    }

    [Fact]
    public async Task CommitThatMovesOnlyTheFingerprints_SavesANewState_RetiresTheOld_AndTheNextStartAccepts()
    {
        await FirstRunAsync("content-fingerprints");
        var firstState = Assert.Single(StateFiles());
        await using (var second = Start("content-fingerprints"))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
            Assert.Equal(CacheState.Baseline, second.CacheState.GetStatus()!.Verification!["fake"]);
            var facts = GraphFacts.Of(second.Database);

            // A whitespace edit: the worker sends no fact, only fingerprints that moved.
            File.AppendAllText(Path.Combine(Root, "a.fake"), " ");
            await second.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake"));
            Assert.Equal(facts, GraphFacts.Of(second.Database));
            await second.StopAsync();
            Assert.Equal(1, second.Snapshots.Status.WritesCompleted);
        }

        // The capture saved a state matching the new fingerprints, and the old pair is retired.
        Assert.Equal(2UL, NewestSequence());
        var secondState = Assert.Single(StateFiles());
        Assert.NotEqual(firstState, secondState);
        Assert.Equal(secondState, Newest().Shards.Single().Info.StateFileName);
        Assert.Equal(CacheState.Baseline, await NextVerificationAsync("content-fingerprints"));
    }

    [Fact]
    public async Task LanguageWithNoFilesLeft_IsEmptiedThroughItsBaseline()
    {
        await FirstRunAsync();
        File.Delete(Path.Combine(Root, "a.fake"));
        File.Delete(Path.Combine(Root, "b.fake"));
        await using var second = Start("persistent-state");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
        Assert.Equal(0, second.Database.NodeCount);
        Assert.Equal(CacheState.Baseline, second.CacheState.GetStatus()!.Verification!["fake"]);
    }

    [Fact]
    public async Task RejectedBaseline_ReplacesTheWorkspace_AndReportsReplaced()
    {
        await FirstRunAsync();
        await using var second = Start("baseline-reject");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
        Assert.True(Assert.Single(second.Sink.Deltas).ReplacesWorkspace);
        Assert.Equal(CacheState.Replaced, second.CacheState.GetStatus()!.Verification!["fake"]);
        Assert.Equal(4, second.Database.NodeCount);
    }

    [Fact]
    public async Task BaselineAnswerThatDoesNotMatch_FailsTheVerification_AndLeavesTheSnapshotServedUnverified()
    {
        await FirstRunAsync();
        await using var second = Start("accept-with-replacement");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed verification scan");
        Assert.Equal(CacheState.Unverified, second.CacheState.GetStatus()!.State);
        Assert.Equal(4, second.Database.NodeCount);
    }

    [Theory]
    [InlineData("save-state-fails")]
    [InlineData("save-state-false")]
    [InlineData("save-state-wrong-id")]
    public async Task FailedSave_WritesTheShardWithoutState(string behavior)
    {
        await FirstRunAsync(behavior);
        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        Assert.NotNull(Newest().Shards.Single().Info.Fingerprints);
        Assert.Empty(StateFiles());
        Assert.Equal(CacheState.Replaced, await NextVerificationAsync());
    }

    [Fact]
    public async Task HungSave_IsGivenUpAfterTheTimeout_AndTheShardIsWrittenWithoutState()
    {
        var stopwatch = Stopwatch.StartNew();
        await using (var first = Start("save-state-hangs", settings => settings with { SaveStateTimeout = TimeSpan.FromSeconds(1) }))
        {
            await first.StartAsync();
            await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
            await first.StopAsync();
            Assert.Contains(first.Warnings, warning => warning.Contains("did not save its state", StringComparison.Ordinal));
        }
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"took {stopwatch.Elapsed}");
        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        Assert.Empty(StateFiles());
        Assert.Equal(CacheState.Replaced, await NextVerificationAsync());
    }

    /// <summary>
    /// A verification that patched the shard (so the next capture would save state), then <paramref name="breakIt"/>;
    /// the capture on the way down writes the shard without state, and the next start re-walks it.
    /// </summary>
    private async Task AssertBreakAsync(Func<Instance, Task> breakIt)
    {
        try
        {
            // What the proof decides. Whether the old file is gone yet is the retire's business, and a busy file may stay.
            var (stateFile, _, next) = await AfterAPatchedVerificationAsync(breakIt);
            Assert.Null(stateFile);
            Assert.Equal(CacheState.Replaced, next);
        }
        catch (Exception failure)
        {
            throw new XunitException($"{failure.Message}{Environment.NewLine}Warnings: {Warnings()}", failure);
        }
    }

    /// <summary>The state file the capture on the way down recorded, and how the start after verified the shard.</summary>
    private async Task<(string? StateFile, string[] StateFiles, string NextVerification)> AfterAPatchedVerificationAsync(Func<Instance, Task> between)
    {
        await FirstRunAsync();
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        await using (var second = Start("persistent-state"))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
            Assert.Equal(CacheState.Baseline, second.CacheState.GetStatus()!.Verification!["fake"]);
            await between(second);
            await second.StopAsync();
            Assert.True(second.Snapshots.Status.WritesCompleted >= 1);
        }
        var stateFile = Newest().Shards.Single().Info.StateFileName;
        var stateFiles = StateFiles();
        return (stateFile, stateFiles, await NextVerificationAsync());
    }

    [Fact]
    public async Task WithoutABreak_ThePatchedShardIsSavedWithState_AndTheNextStartUsesIt()
    {
        var (stateFile, stateFiles, next) = await AfterAPatchedVerificationAsync(_ => Task.CompletedTask);
        Assert.NotNull(stateFile);
        Assert.Equal([stateFile], stateFiles);
        Assert.Equal(CacheState.Baseline, next);
    }

    [Fact]
    public Task RolledBackReconciliation_AfterTheWorkerEmitted_BreaksThePairing()
        => AssertBreakAsync(async instance =>
        {
            File.WriteAllText(FailMarker, "fail");
            Assert.NotNull(await instance.Coordinator.TryRequestFullRescanAsync());
            await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Error, "the failed rescan");
            File.Delete(FailMarker);
            // The rescan's replacement was emitted and rolled back: the published facts are the verification's.
            Assert.Equal(6, instance.Database.NodeCount);
        });

    [Fact]
    public Task RequestThatFailedAfterItsDeltaCommitted_BreaksThePairing()
        => AssertBreakAsync(async instance =>
        {
            File.WriteAllText(FailMarker, "fail");
            // Its delta was published (no reconciliation), then the request failed.
            await Assert.ThrowsAsync<ParserWorkerFailedException>(() => instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake")));
            File.Delete(FailMarker);
        });

    /// <summary>A capture recorded a state just before the request failed: that state goes too, so the next start re-walks.</summary>
    [Fact]
    public async Task RequestThatFailedAfterACapture_BreaksThePairingToo()
    {
        await FirstRunAsync();
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        await using (var second = Start("persistent-state", settings => settings with { QuietPeriod = TimeSpan.FromMilliseconds(50) }))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.Snapshots.Status.WritesCompleted == 1, "the quiet-period capture");
            Assert.NotNull(Newest().Shards.Single().Info.StateFileName);

            File.WriteAllText(FailMarker, "fail");
            await Assert.ThrowsAsync<ParserWorkerFailedException>(() => second.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake")));
            File.Delete(FailMarker);
            await second.StopAsync();
        }

        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        Assert.Equal(CacheState.Replaced, await NextVerificationAsync());
    }

    /// <summary>
    /// A file another process holds when its snapshot retires (a scanner, on Windows) stays, as the retire allows. It is
    /// referenced by nothing, so it is not offered, and the next start removes it.
    /// </summary>
    [Fact]
    public async Task AFileBusyAtRetire_StaysUntilTheNextStart_AndIsNotUsed()
    {
        if (!OperatingSystem.IsWindows()) return;
        await FirstRunAsync();
        var old = Assert.Single(StateFiles());
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        FileStream? held = null;
        await using (var second = Start("persistent-state", settings => settings with
        {
            BeforeRetire = () => held ??= new FileStream(Path.Combine(Workspace, old), FileMode.Open, FileAccess.Read, FileShare.Read),
        }))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
            File.WriteAllText(FailMarker, "fail");
            await Assert.ThrowsAsync<ParserWorkerFailedException>(() => second.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake")));
            File.Delete(FailMarker);
            await second.StopAsync();
        }
        Assert.NotNull(held);
        Assert.Contains(old, StateFiles());
        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        await held!.DisposeAsync();

        Assert.Equal(CacheState.Replaced, await NextVerificationAsync());
        Assert.DoesNotContain(old, StateFiles());
    }

    [Fact]
    public Task WorkerRestart_BreaksThePairing()
        => AssertBreakAsync(async instance =>
        {
            using (var process = Process.GetProcessById(instance.Sessions.GetSnapshots().Single().ProcessId!.Value))
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
            // The next batch starts a new process; its commit cannot extend the old process's lineage.
            await instance.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake"));
            Assert.Equal(1, instance.Sessions.GetSnapshots().Single().RestartCount);
        });

    [Fact]
    public async Task FailedVerification_ThenAChange_WritesNoState_AndTheNextStartReWalks()
    {
        await FirstRunAsync();
        File.WriteAllText(FailMarker, "fail");
        await using (var second = Start("persistent-state"))
        {
            await second.StartAsync();
            await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed verification scan");
            Assert.Equal(CacheState.Unverified, second.CacheState.GetStatus()!.State);
            File.Delete(FailMarker);
            // A watcher batch on the unverified, loaded shard: facts no process of this run established.
            await second.Coordinator.RefreshFileAsync(Path.Combine(Root, "a.fake"));
            await second.StopAsync();
            Assert.Equal(1, second.Snapshots.Status.WritesCompleted);
        }
        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        Assert.Empty(StateFiles());
        Assert.Equal(CacheState.Replaced, await NextVerificationAsync());
    }

    [Fact]
    public async Task WorkerWithoutTheCapability_IsNeverAskedToSave_AndReplacesAsInPhase4()
    {
        await FirstRunAsync("strict");
        Assert.Null(Newest().Shards.Single().Info.StateFileName);
        Assert.Empty(Newest().Shards.Single().Info.Fingerprints!);
        await using var second = Start("strict");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
        Assert.True(Assert.Single(second.Sink.Deltas).ReplacesWorkspace);
        Assert.Equal(CacheState.Replaced, second.CacheState.GetStatus()!.Verification!["fake"]);
        Assert.Null(second.CacheState.GetStatus()!.WorkerStateBytes);
    }

    /// <summary>Records every chunk, then hands it to the applier with its origin.</summary>
    internal sealed class RecordingSink(IAnalysisDeltaSink inner) : IAnalysisDeltaSink
    {
        public ConcurrentQueue<AnalysisDelta> Deltas { get; } = new();

        public Task<bool> ApplyAsync(AnalysisDelta delta, CancellationToken ct = default)
        {
            Deltas.Enqueue(delta);
            return inner.ApplyAsync(delta, ct);
        }

        Task<bool> IAnalysisDeltaSink.ApplyAsync(AnalysisDelta delta, DeltaOrigin? origin, CancellationToken ct)
        {
            Deltas.Enqueue(delta);
            return inner.ApplyAsync(delta, origin, ct);
        }
    }

    /// <summary>One host's worth of production services over the root, without the HTTP layer.</summary>
    internal sealed class Instance : IAsyncDisposable
    {
        private bool _stopped;

        public Instance(string root, string keyPath, string[] workerArgs,
            Func<SnapshotServiceSettings, SnapshotServiceSettings>? configure, bool cache)
            : this(root, keyPath, FakeWorker(workerArgs), configure, cache)
        {
        }

        public Instance(string root, string keyPath, RegisteredWorker worker,
            Func<SnapshotServiceSettings, SnapshotServiceSettings>? configure, bool cache)
        {
            Factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
            Factory.InitializeAsync(root).Wait();
            var options = Options.Create(new CodeContextOptions { RootPath = root, CacheKeyPath = keyPath, CacheEnabled = cache });
            var metadata = new InMemoryFileMetadataRepository();
            var coverage = AnalysisCoverageRegistry.ForStore(Factory);
            var store = (IGenerationalGraphStore)Factory.CreateGraphRepository();
            Sink = new RecordingSink(new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance, coverage));
            Workers = new LanguageWorkerService(new StaticWorkerCatalog(worker), Sink,
                Sessions, options, new WorkerLogFactory(WorkerLog), TestWorkerOptions.Patient());
            var graphUpdate = new GraphUpdateService(Factory, options, NullLogger<GraphUpdateService>.Instance, metadata, Workers);
            GraphUpdate = graphUpdate;

            var settings = SnapshotServiceSettings.From(options.Value);
            settings = configure?.Invoke(settings) ?? settings;
            Startup = new SnapshotStartup(settings, Factory.Database, coverage, Workers.IsIndexedParser,
                Workers.MarkLoadedFromSnapshot, SnapshotLog);
            CacheState = new CacheState(settings.Enabled, Startup.Run);
            SnapshotServiceCollectionExtensions.ConnectWorkers(Workers, CacheState, Startup, Factory.Database);
            Coordinator = new IndexCoordinator(graphUpdate, ScanState, options, NullLoggerFactory.Instance, cacheState: CacheState);
            Snapshots = new SnapshotService(Coordinator, Factory.Database, ScanState, settings, Startup, TimeProvider.System,
                SnapshotLog, CacheState, Workers);
            Status = new StatusService(Factory.CreateNodeRepository(), Factory.CreateEdgeRepository(), metadata, Factory, options,
                ScanState, Sessions, new ApiMetrics(), coverage: coverage, cacheState: CacheState);
        }

        public InMemoryRepositoryFactory Factory { get; }
        public ListLogger<LanguageWorkerService> WorkerLog { get; } = new();
        public ListLogger<SnapshotService> SnapshotLog { get; } = new();

        /// <summary>What the worker service and the snapshot service warned about, in that order.</summary>
        public IEnumerable<string> Warnings => WorkerLog.Entries.Concat(SnapshotLog.Entries)
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Select(entry => entry.Message);

        /// <summary>The graph update service the coordinator drives, for tests that deliver a batch while the coordinator is idle.</summary>
        public GraphUpdateService GraphUpdate { get; }
        public InMemoryDatabase Database => Factory.Database;
        public RecordingSink Sink { get; }
        public ParserSessionRegistry Sessions { get; } = new();
        public LanguageWorkerService Workers { get; }
        public ScanStateService ScanState { get; } = new();
        public SnapshotStartup Startup { get; }
        public CacheState CacheState { get; }
        public IndexCoordinator Coordinator { get; }
        public SnapshotService Snapshots { get; }
        public StatusService Status { get; }

        public async Task StartAsync()
        {
            await Coordinator.StartAsync(CancellationToken.None);
            await Snapshots.StartAsync(CancellationToken.None);
        }

        public Task WaitForAsync(Func<bool> condition, string what) => SnapshotServiceHarness.WaitUntilAsync(condition, what, 60_000);

        public async Task StopAsync()
        {
            if (_stopped) return;
            _stopped = true;
            await Snapshots.StopAsync(CancellationToken.None);
            await Coordinator.StopAsync(CancellationToken.None);
            await Workers.StopAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Snapshots.Dispose();
            Startup.Dispose();
            Coordinator.Dispose();
            await Workers.DisposeAsync();
        }

        /// <summary>Keeps the worker service's own log; everything else it creates a logger for stays silent.</summary>
        private sealed class WorkerLogFactory(ILogger<LanguageWorkerService> log) : ILoggerFactory
        {
            public ILogger CreateLogger(string categoryName)
                => categoryName == typeof(LanguageWorkerService).FullName ? log : NullLogger.Instance;

            public void AddProvider(ILoggerProvider provider)
            {
            }

            public void Dispose()
            {
            }
        }

        private static RegisteredWorker FakeWorker(string[] args)
        {
            var baseDirectory = AppContext.BaseDirectory;
            var apphost = Path.Combine(baseDirectory,
                OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
            var spec = File.Exists(apphost)
                ? new WorkerLaunchSpec("fake", "Fake Worker", apphost, args)
                : new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
                    [Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll"), .. args]);
            var manifest = new WorkerManifest(
                ManifestVersion: 1, ParserId: "fake", DisplayName: "Fake Worker", Version: "1.0.0-test",
                Command: "test", Args: [], MinProtocolVersion: ParserProtocol.Version, MaxProtocolVersion: ParserProtocol.Version,
                Languages: ["fake"], Extensions: [".fake"], ProjectMarkers: ["fake.proj"]);
            return new RegisteredWorker(manifest, spec, "<in-test>");
        }
    }
}
