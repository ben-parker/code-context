using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// A start with a valid snapshot (plan phase 4): answers at once, labelled, while the real fake worker
/// re-indexes everything inside the verification scan; the label goes when the scan commits; a failed
/// scan leaves the loaded answers labelled unverified. Each instance is the production wiring of the
/// coordinator, the load step, the worker service, the applier, the context and status services, with
/// the key and the root in a temp directory.
/// </summary>
public sealed class StartupFromSnapshotTests : IAsyncLifetime
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-startup-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private string Workspace => Path.Combine(Root, ".codecontext", "cache", "default");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "a.fake"), "a");
        File.WriteAllText(Path.Combine(Root, "b.fake"), "b");

        // A first run that scans and writes the snapshot on its way down.
        await using var first = new CachedInstance(Root, KeyPath, "normal");
        await first.StartAsync();
        await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
        Assert.Equal(CacheState.Verified, first.CacheState.GetStatus()!.State);
        Assert.Null(first.CacheState.GetStatus()!.SnapshotSequence);
        await first.StopAsync();
        Assert.Single(Directory.GetFiles(Workspace, "*.ccsnap"));
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_base, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnswersFromTheSnapshotBeforeTheWorkerFinishes()
    {
        await using var second = new CachedInstance(Root, KeyPath, "slow-index");
        await second.StartAsync();
        await second.WaitForAsync(() => second.CacheState.GetStatus() is not null, "the load step");

        var answer = await second.Context.GetCompactContextAsync("aClass");
        Assert.Equal(1, answer.TotalMatches);
        Assert.Equal(CacheState.Verifying, answer.Cache!.State);
        Assert.NotNull(answer.Cache.SnapshotAt);
        var full = await second.Context.GetCompleteContextAsync("aClass", exact: true);
        Assert.Equal(CacheState.Verifying, full.Cache!.State);

        var status = await second.Status.GetStatusAsync();
        Assert.Equal("scanning", status.Indexing.Status);
        Assert.True(status.Indexing.Cache!.ServingCached);
        Assert.Equal(CacheState.Verifying, status.Indexing.Cache.State);
        Assert.Equal(CacheState.Pending, status.Indexing.Cache.Verification!["fake"]);
        Assert.Equal(1, status.Indexing.Cache.SnapshotSequence);
        Assert.True(status.Indexing.Cache.SizeBytes > 0);
        Assert.Null(status.Indexing.Cache.WorkerStateBytes);
    }

    [Fact]
    public async Task LabelGoesWhenTheScanCommits()
    {
        // Edited while the host was down: the snapshot does not know c.fake, the verification finds it.
        File.WriteAllText(Path.Combine(Root, "c.fake"), "c");
        await using var second = new CachedInstance(Root, KeyPath, "normal");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");

        var answer = await second.Context.GetCompactContextAsync("cClass");
        Assert.Equal(1, answer.TotalMatches);
        Assert.Null(answer.Cache);
        var status = await second.Status.GetStatusAsync();
        Assert.Equal("ready", status.Indexing.Status);
        Assert.Equal(CacheState.Verified, status.Indexing.Cache!.State);
        Assert.False(status.Indexing.Cache.ServingCached);
        Assert.Equal(CacheState.Replaced, status.Indexing.Cache.Verification!["fake"]);
        await second.StopAsync();

        // The verified graph was written on the way down, and the older snapshot retired.
        Assert.True(SnapshotFile.TryParseName(Path.GetFileName(Directory.GetFiles(Workspace, "*.ccsnap").Single()), out var sequence));
        Assert.Equal(2UL, sequence);
    }

    [Fact]
    public async Task WorkerFailure_LeavesCachedAnswersLabelledUnverified()
    {
        await using var second = new CachedInstance(Root, KeyPath, "crash-on-index");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed verification scan");

        var answer = await second.Context.GetCompactContextAsync("bClass");
        Assert.Equal(1, answer.TotalMatches);
        Assert.Equal(CacheState.Unverified, answer.Cache!.State);
        var status = await second.Status.GetStatusAsync();
        Assert.Equal("error", status.Indexing.Status);
        Assert.Equal(CacheState.Unverified, status.Indexing.Cache!.State);
        Assert.True(status.Indexing.Cache.ServingCached);
    }

    [Fact]
    public async Task PollingThroughTheVerification_NeverSeesReadyWhileCached_NorAnUnlabelledSnapshotAnswer()
    {
        // a.fake is gone: aClass exists only in the snapshot, so any answer that finds it comes from the snapshot.
        File.Delete(Path.Combine(Root, "a.fake"));
        await using var second = new CachedInstance(Root, KeyPath, "normal");
        var polls = 0;
        var cachedPolls = 0;
        var snapshotAnswers = 0;
        await second.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            Assert.True(DateTime.UtcNow < deadline, "The verification scan did not finish.");
            var answer = await second.Context.GetCompactContextAsync("aClass");
            if (answer.TotalMatches > 0)
            {
                snapshotAnswers++;
                Assert.NotNull(answer.Cache);
            }
            var status = await second.Status.GetStatusAsync();
            polls++;
            var serving = status.Indexing.Cache?.ServingCached == true;
            if (serving) cachedPolls++;
            Assert.False(status.Indexing.Status == "ready" && serving, $"ready while serving cached answers (poll {polls})");
            Assert.False(status.Indexing.Status == "error" && status.Indexing.Cache?.State == CacheState.Verifying, $"error while verifying (poll {polls})");
            if (status.Indexing.Status == "ready") break;
        }

        // Ready: the scan committed, the label is gone and the snapshot's aClass with it.
        var after = await second.Context.GetCompactContextAsync("aClass");
        Assert.Equal(0, after.TotalMatches);
        Assert.Null(after.Cache);
        Assert.True(cachedPolls > 0, $"No poll of {polls} saw the snapshot being served.");
    }

    [Fact]
    public async Task ShardOfARemovedParser_IsDropped()
    {
        // No worker indexes "fake" any more: its shard is not loaded, and nothing of it is served.
        await using var second = new CachedInstance(Root, KeyPath, behavior: null);
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
        Assert.Equal(0, second.Database.NodeCount);
        Assert.Empty(second.CacheState.GetStatus()!.Verification!);
        Assert.Equal(0, (await second.Context.GetCompactContextAsync("aClass")).TotalMatches);
    }

    [Fact]
    public async Task LanguageWithNoFilesLeft_IsEmptied()
    {
        File.Delete(Path.Combine(Root, "a.fake"));
        File.Delete(Path.Combine(Root, "b.fake"));
        await using var second = new CachedInstance(Root, KeyPath, "normal");
        await second.StartAsync();
        await second.WaitForAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");

        Assert.Equal(0, second.Database.NodeCount);
        Assert.Equal(0, second.Database.EdgeCount);
        // The worker was asked even with no files: its generation replaced the loaded shard with nothing.
        Assert.NotEqual(ParserSessionState.NotNeeded, second.Sessions.GetSnapshots().Single().State);
        Assert.Equal(CacheState.Verified, second.CacheState.GetStatus()!.State);
    }

    [Fact]
    public async Task IgnoredSnapshot_ReportsItsReason()
    {
        var path = Directory.GetFiles(Workspace, "*.ccsnap").Single();
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        await using var second = new CachedInstance(Root, KeyPath, "slow-index");
        await second.StartAsync();
        await second.WaitForAsync(() => second.CacheState.GetStatus() is not null, "the load step");
        var status = await second.Status.GetStatusAsync();
        Assert.Equal("scanning", status.Indexing.Status);
        Assert.Equal(CacheState.Ignored, status.Indexing.Cache!.State);
        Assert.False(status.Indexing.Cache.ServingCached);
        Assert.Contains("Authentication", status.Indexing.Cache.Reason);
        Assert.Equal(0, second.Database.NodeCount);
        Assert.Null((await second.Context.GetCompactContextAsync("aClass")).Cache);
    }

    /// <summary>One host's worth of production services over <paramref name="root"/>, without the HTTP layer.</summary>
    private sealed class CachedInstance : IAsyncDisposable
    {
        private bool _stopped;

        /// <param name="behavior">The fake worker's behaviour, or null for no worker at all.</param>
        public CachedInstance(string root, string keyPath, string? behavior)
        {
            Factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
            Factory.InitializeAsync(root).Wait();
            var options = Options.Create(new CodeContextOptions { RootPath = root, CacheKeyPath = keyPath });
            var metadata = new InMemoryFileMetadataRepository();
            // The host's registry: it reads the coverage recorded with the published shards.
            var coverage = AnalysisCoverageRegistry.ForStore(Factory);
            var store = (IGenerationalGraphStore)Factory.CreateGraphRepository();
            Workers = new LanguageWorkerService(
                behavior is null ? new StaticWorkerCatalog() : new StaticWorkerCatalog(FakeWorker(behavior)),
                new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance, coverage),
                Sessions, options, NullLoggerFactory.Instance, TestWorkerOptions.Patient());
            var graphUpdate = new GraphUpdateService(Factory, options, NullLogger<GraphUpdateService>.Instance, metadata, Workers);

            var settings = SnapshotServiceSettings.From(options.Value);
            Startup = new SnapshotStartup(settings, Factory.Database, coverage, Workers.IsIndexedParser,
                Workers.MarkLoadedFromSnapshot, NullLogger<SnapshotService>.Instance);
            CacheState = new CacheState(settings.Enabled, Startup.Run);
            Coordinator = new IndexCoordinator(graphUpdate, ScanState, options, NullLoggerFactory.Instance, cacheState: CacheState);
            Snapshots = new SnapshotService(Coordinator, Factory.Database, ScanState, settings, Startup, TimeProvider.System,
                NullLogger<SnapshotService>.Instance, CacheState);
            Context = new ContextService(Factory.CreateNodeRepository(), Factory.CreateEdgeRepository(), metadata, options, Workers,
                coverage, CacheState);
            Status = new StatusService(Factory.CreateNodeRepository(), Factory.CreateEdgeRepository(), metadata, Factory, options,
                ScanState, Sessions, new ApiMetrics(), coverage: coverage, cacheState: CacheState);
        }

        public InMemoryRepositoryFactory Factory { get; }
        public InMemoryDatabase Database => Factory.Database;
        public ParserSessionRegistry Sessions { get; } = new();
        public LanguageWorkerService Workers { get; }
        public ScanStateService ScanState { get; } = new();
        public SnapshotStartup Startup { get; }
        public CacheState CacheState { get; }
        public IndexCoordinator Coordinator { get; }
        public SnapshotService Snapshots { get; }
        public ContextService Context { get; }
        public StatusService Status { get; }

        public async Task StartAsync()
        {
            // The host's order: workers, coordinator, snapshots.
            await Coordinator.StartAsync(CancellationToken.None);
            await Snapshots.StartAsync(CancellationToken.None);
        }

        public Task WaitForAsync(Func<bool> condition, string what) => SnapshotServiceHarness.WaitUntilAsync(condition, what, 60_000);

        /// <summary>The host's stop order: snapshots (the final write), coordinator, workers.</summary>
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

        private static RegisteredWorker FakeWorker(string behavior)
        {
            var baseDirectory = AppContext.BaseDirectory;
            var apphost = Path.Combine(baseDirectory,
                OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
            var spec = File.Exists(apphost)
                ? new WorkerLaunchSpec("fake", "Fake Worker", apphost, ["--behavior", behavior])
                : new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
                    [Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll"), "--behavior", behavior]);
            var manifest = new WorkerManifest(
                ManifestVersion: 1, ParserId: "fake", DisplayName: "Fake Worker", Version: "1.0.0-test",
                Command: "test", Args: [], MinProtocolVersion: ParserProtocol.Version, MaxProtocolVersion: ParserProtocol.Version,
                Languages: ["fake"], Extensions: [".fake"], ProjectMarkers: ["fake.proj"]);
            return new RegisteredWorker(manifest, spec, "<in-test>");
        }
    }
}
