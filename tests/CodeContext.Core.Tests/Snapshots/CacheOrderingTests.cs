using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Api;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Orderings and wiring the cache surface depends on (re-review of phase 4's fix round): coverage is a store
/// read and is taken before the last cache reading; the coverage read never waits for a commit; the scan is
/// reported committed to the cache state before it is reported ready; the host reads coverage through the
/// store; a stop during the hand-over wins; a load that failed after announcing facts is still verified by a
/// full scan; and every snapshot refusal is classified.
/// </summary>
public class CacheOrderingTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    private static string NewBase() => Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));

    /// <summary>A snapshot of three facts whose coverage is partial, so a miss reports it.</summary>
    private static async Task WriteFirstRunAsync(string basePath)
    {
        await using var first = new SnapshotServiceHarness(basePath);
        await first.StartAsync();
        await first.CommitAsync(() => first.CommitFacts(1, annotation: new ShardAnnotation("1.0.0-test",
            [new ProjectCoverage("App", "src/App.fakeproj", ProjectCoverageStates.Partial, 2, MissingReferences: ["Lib.dll"])])));
        await first.AdvanceAsync(Quiet);
    }

    /// <summary>The store-backed registry, whose first read runs <see cref="BeforeFirstRead"/> (the load step, here).</summary>
    private sealed class LoadOnFirstRead(IAnalysisCoverageRegistry inner) : IAnalysisCoverageRegistry
    {
        private int _reads;

        public Action? BeforeFirstRead { get; set; }

        public void Report(string parserId, string workspaceId, IReadOnlyList<ProjectCoverage> projects) => inner.Report(parserId, workspaceId, projects);

        public IReadOnlyList<ProjectCoverage> GetProjects()
        {
            if (Interlocked.Increment(ref _reads) == 1) BeforeFirstRead?.Invoke();
            return inner.GetProjects();
        }
    }

    [Fact]
    public async Task CoverageFromTheSnapshot_IsNeverServedUnlabelled_AtTheLoad()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            foreach (var status in new[] { false, true })
            {
                var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
                var coverage = new LoadOnFirstRead(AnalysisCoverageRegistry.ForStore(factory));
                await using var second = new SnapshotServiceHarness(basePath, database: factory.Database, coverage: coverage);
                // The load runs while the reader is between its store reads and its last cache reading.
                coverage.BeforeFirstRead = () => Assert.True(second.CacheState.RunLoadStep());
                if (!status)
                {
                    var context = new ContextService(new InMemoryNodeRepository(factory.Database), new InMemoryEdgeRepository(factory.Database),
                        new InMemoryFileMetadataRepository(), coverage: coverage, cacheState: second.CacheState);
                    var miss = await context.GetCompactContextAsync("Missing");
                    Assert.Equal(0, miss.TotalMatches);
                    Assert.True(miss.Cache is not null || miss.Coverage is null, "snapshot coverage went out without a cache notice");
                    Assert.NotNull(miss.Coverage);
                }
                else
                {
                    var service = new StatusService(new InMemoryNodeRepository(factory.Database), new InMemoryEdgeRepository(factory.Database),
                        new InMemoryFileMetadataRepository(), factory, Options.Create(new CodeContextOptions { RootPath = second.RootPath }),
                        second.ScanState, new ParserSessionRegistry(), new ApiMetrics(), coverage: coverage, cacheState: second.CacheState);
                    var response = await service.GetStatusAsync();
                    Assert.NotNull(response.Coverage);
                    Assert.True(response.Indexing.Cache?.ServingCached == true, "snapshot coverage went out without a cache object");
                    Assert.True(response.Coverage!.Cached);
                }
            }
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    /// <summary>A scan state that records what the cache state said when the scan was reported ready.</summary>
    private sealed class RecordingScanState(ScanStateService inner, Func<CacheStatusDto?> cache) : IScanStateService, IScanProgressReporter
    {
        public bool? ServingCachedAtCompletion { get; private set; }
        public ScanPhase Phase => inner.Phase;
        public int FilesProcessed => inner.FilesProcessed;
        public int FilesTotal => inner.FilesTotal;
        public DateTimeOffset? LastScanStartedAt => inner.LastScanStartedAt;
        public TimeSpan? LastScanDuration => inner.LastScanDuration;
        public string? LastError => inner.LastError;
        public bool WatcherActive { get => inner.WatcherActive; set => inner.WatcherActive = value; }
        public long OperationId => inner.OperationId;
        public bool TryBeginScan() => inner.TryBeginScan();
        public void FailScan(string error) => inner.FailScan(error);

        public void CompleteScan()
        {
            ServingCachedAtCompletion = cache()?.ServingCached;
            inner.CompleteScan();
        }

        public void ReportProgress(int processed, int total, string currentFile) => ((IScanProgressReporter)inner).ReportProgress(processed, total, currentFile);
        public void ReportError(string filePath, string error) => ((IScanProgressReporter)inner).ReportError(filePath, error);
        public void ReportComplete(int processed, TimeSpan elapsed) => ((IScanProgressReporter)inner).ReportComplete(processed, elapsed);
    }

    private static (IndexCoordinator Coordinator, IGraphUpdateService GraphUpdate, RecordingScanState ScanState, CacheState Cache, string Root) Coordinator(
        Func<Action<CacheLoadOutcome>, CacheLoadOutcome> load)
    {
        var root = Directory.CreateTempSubdirectory("cc-order-").FullName;
        var graphUpdate = Substitute.For<IGraphUpdateService>();
        var cache = new CacheState(enabled: true, load);
        var scanState = new RecordingScanState(new ScanStateService(), cache.GetStatus);
        var coordinator = new IndexCoordinator(graphUpdate, scanState, Options.Create(new CodeContextOptions { RootPath = root }),
            NullLoggerFactory.Instance, cacheState: cache);
        return (coordinator, graphUpdate, scanState, cache, root);
    }

    private static CacheLoadOutcome Loaded => new(CacheLoadKind.Loaded, null, DateTime.UtcNow.Ticks, 1, 100, ["fake"]);

    [Fact]
    public async Task TheScanIsCommittedToTheCacheState_BeforeItIsReportedReady()
    {
        var (coordinator, _, scanState, cache, root) = Coordinator(announce =>
        {
            announce(Loaded);
            return Loaded;
        });
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => scanState.Phase == ScanPhase.Ready, "the startup scan");
            Assert.False(scanState.ServingCachedAtCompletion);
            Assert.Equal(CacheState.Verified, cache.GetStatus()!.State);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ALoadThatFailedAfterAnnouncingItsFacts_IsStillVerifiedByAFullScan()
    {
        var (coordinator, graphUpdate, scanState, cache, root) = Coordinator(announce =>
        {
            announce(Loaded);
            throw new InvalidOperationException("failed after the publish");
        });
        try
        {
            await coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => scanState.Phase == ScanPhase.Ready, "the startup scan");
            Assert.True(cache.LoadAnnounced);
            // The full-scan flow, in which every worker replaces what was loaded; not the resumable one.
            await graphUpdate.Received(1).PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
            await graphUpdate.DidNotReceive().PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
            coordinator.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheHostReadsCoverageThroughThePublishedAnnotations()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["CodeContext:CacheEnabled"] = "false";
        ProgramHelpers.ConfigureCoreServices(builder.Services, Path.GetTempPath(), isProduction: false);
        builder.Logging.ClearProviders();
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<IAnalysisCoverageRegistry>();
        var database = ((InMemoryRepositoryFactory)host.Services.GetRequiredService<IRepositoryFactory>()).Database;

        Assert.Empty(registry.GetProjects());
        Assert.True(database.TryCommitGeneration(1, [SnapshotServiceHarness.Node("A")], [],
            new CommitScope("fake", "default", null) { Annotation = new ShardAnnotation("1", [new ProjectCoverage("App", null, ProjectCoverageStates.Complete, 1)]) }));
        // Nothing reported it: the registry reads what the store recorded with the published shard.
        Assert.Equal("App", registry.GetProjects().Single().Name);
        database.BeginReconciliation();
        Assert.True(database.TryCommitGeneration(2, [SnapshotServiceHarness.Node("B")], [],
            new CommitScope("fake", "default", null) { Annotation = new ShardAnnotation("1", [new ProjectCoverage("Staged", null, ProjectCoverageStates.Complete, 1)]) }));
        Assert.Equal("App", registry.GetProjects().Single().Name);
        database.RollbackReconciliation();
    }

    [Fact]
    public async Task AStopDuringTheHandOver_IsNotOverwrittenByTheWriterMode()
    {
        await using var harness = new SnapshotServiceHarness();
        // The service creates its timer between taking the writer's state and setting its mode: stop there.
        harness.Time.OnCreateTimer = () =>
        {
            harness.Time.OnCreateTimer = null;
            harness.Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        };
        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Stopped, harness.Service.Status.Mode);
        harness.CommitFacts(1);
        harness.Service.OnMutationCommandCompleted();
        Assert.True(harness.Service.IsIdle);
    }

    [Fact]
    public void EveryRefusalKind_IsClassifiedAsProvingInvalidOrNot()
    {
        foreach (var rejection in Enum.GetValues<SnapshotRejection>())
        {
            var proves = SnapshotStartup.RejectionsProvingInvalid.Contains(rejection);
            var provesNothing = SnapshotStartup.RejectionsProvingNothing.Contains(rejection);
            Assert.True(proves ^ provesNothing, $"{rejection} must be listed in exactly one of the two sets.");
            Assert.Equal(proves, SnapshotStartup.ProvesInvalid(rejection));
        }
    }
}
