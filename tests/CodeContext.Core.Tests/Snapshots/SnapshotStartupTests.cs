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
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using NSubstitute;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The load step of plan 3.5, "Startup", as the coordinator's first step: which snapshot is loaded, what
/// is published with it, what the cache state says, and that a loaded start writes nothing it did not
/// change. The scan service is a substitute: the scan these tests see is whatever they configure.
/// </summary>
public class SnapshotStartupTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    private static List<ProjectCoverage> Coverage() => [new("App", "src/App.fakeproj", ProjectCoverageStates.Partial, 2, MissingReferences: ["Lib.dll"])];

    /// <summary>A first run that writes one snapshot of <paramref name="facts"/> facts with an annotation, then stops.</summary>
    private static async Task<string> WriteFirstRunAsync(string basePath, int facts = 3)
    {
        await using var first = new SnapshotServiceHarness(basePath);
        await first.StartAsync();
        await first.CommitAsync(() => first.CommitFacts(1, facts, annotation: new ShardAnnotation("1.0.0-test", Coverage())));
        await first.AdvanceAsync(Quiet);
        return first.Snapshots().Single();
    }

    private static string NewBase() => Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restart_LoadsTheSnapshot_WithItsAnnotationAndCoverage_AndDoesNotRewriteIt()
    {
        var basePath = NewBase();
        try
        {
            var name = await WriteFirstRunAsync(basePath);
            var coverage = new AnalysisCoverageRegistry();
            await using var second = new SnapshotServiceHarness(basePath, coverage: coverage);
            await second.StartAsync();

            Assert.Equal(new[] { "N1_0", "N1_1", "N1_2" }, second.Database.EnumerateNodes().Select(node => node.Name));
            Assert.True(second.Database.TryLatchCommitted(out _, out var annotations, out var generation));
            // A loaded store starts at a fresh process's generation; the header's is informational.
            Assert.Equal(0, generation);
            Assert.Equal("1.0.0-test", annotations[SnapshotServiceHarness.Shard].ParserVersion);
            Assert.Equal("Lib.dll", annotations[SnapshotServiceHarness.Shard].Coverage!.Single().MissingReferences!.Single());
            Assert.Equal("App", coverage.GetProjects().Single().Name);
            // The loaded view counts as written: nothing changed, so nothing is written again.
            await second.AdvanceAsync(TimeSpan.FromMinutes(5));
            Assert.Equal(0, second.Service.Status.WritesCompleted);
            Assert.Equal(new[] { name }, second.Snapshots());

            // The verification scan (a substitute here) committed: verified, the loaded snapshot still the newest.
            var status = second.CacheState.GetStatus()!;
            Assert.Equal(CacheState.Verified, status.State);
            Assert.False(status.ServingCached);
            Assert.Equal(1, status.SnapshotSequence);
            Assert.Equal(CacheState.Replaced, status.Verification!["fake"]);
            await second.GraphUpdate.Received(1).PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
            await second.GraphUpdate.DidNotReceive().PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task BeforeTheScanCommits_AnswersAreLabelledVerifying()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(gate.Task);
            try
            {
                await second.Coordinator.StartAsync(CancellationToken.None);
                await SnapshotServiceHarness.WaitUntilAsync(() => second.CacheState.GetStatus() is not null, "the load step");

                var status = second.CacheState.GetStatus()!;
                Assert.Equal(CacheState.Verifying, status.State);
                Assert.True(status.ServingCached);
                Assert.Equal(CacheState.Pending, status.Verification!["fake"]);
                var notice = second.CacheState.GetNotice()!;
                Assert.Equal(CacheState.Verifying, notice.State);
                Assert.EndsWith("Z", notice.SnapshotAt);
                Assert.Equal(ScanPhase.Scanning, second.ScanState.Phase);
                Assert.Equal(3, second.Database.NodeCount);
            }
            finally
            {
                // A failed assertion must not leave the scan waiting forever: the stop would wait for it.
                gate.TrySetResult();
            }
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the verification scan");
            Assert.Null(second.CacheState.GetNotice());
            await second.Service.StartAsync(CancellationToken.None);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task FailedVerification_KeepsTheLoadedFacts_Unverified()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("the worker failed")));
            await second.Coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed scan");

            Assert.Equal(3, second.Database.NodeCount);
            Assert.Equal(CacheState.Unverified, second.CacheState.GetStatus()!.State);
            Assert.True(second.CacheState.GetStatus()!.ServingCached);
            Assert.Equal(CacheState.Unverified, second.CacheState.GetNotice()!.State);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptSnapshot_IsIgnoredWithItsReason_AndDeleted()
    {
        var basePath = NewBase();
        try
        {
            var name = await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            var path = Path.Combine(second.WorkspacePath, name);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.GraphUpdate.PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(gate.Task);

            try
            {
                await second.Coordinator.StartAsync(CancellationToken.None);
                await SnapshotServiceHarness.WaitUntilAsync(() => second.CacheState.GetStatus() is not null, "the load step");
                var status = second.CacheState.GetStatus()!;
                Assert.Equal(CacheState.Ignored, status.State);
                Assert.False(status.ServingCached);
                Assert.Contains(name, status.Reason);
                Assert.Contains(nameof(SnapshotRejection.Authentication), status.Reason);
                Assert.Null(second.CacheState.GetNotice());
                Assert.Equal(0, second.Database.NodeCount);
                Assert.False(File.Exists(path));
            }
            finally
            {
                // Then the start is a normal one: the resumable scan, and verified once it commits. Released in
                // any case, so a failed assertion cannot leave the stop waiting for the scan.
                gate.TrySetResult();
            }
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the scan");
            Assert.Equal(CacheState.Verified, second.CacheState.GetStatus()!.State);
            Assert.Null(second.CacheState.GetStatus()!.SnapshotSequence);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Theory]
    [InlineData(nameof(SnapshotRejection.RootPath))]
    [InlineData(nameof(SnapshotRejection.HostVersion))]
    public async Task SnapshotOfAnotherRootOrHostVersion_IsRefused(string expected)
    {
        var firstBase = NewBase();
        var secondBase = NewBase();
        try
        {
            var name = await WriteFirstRunAsync(firstBase);
            var firstKey = Path.Combine(firstBase, "home", ".codecontext", "cache.key");
            // Same key (so the refusal is not the HMAC), copied into another root, or read by another host build.
            var otherRoot = expected == nameof(SnapshotRejection.RootPath);
            await using var second = new SnapshotServiceHarness(otherRoot ? secondBase : firstBase, settings => settings with
            {
                KeyPath = firstKey,
                HostVersion = otherRoot ? settings.HostVersion : "0.11.1-other",
            });
            if (otherRoot)
            {
                Directory.CreateDirectory(second.WorkspacePath);
                File.Copy(Path.Combine(firstBase, "repo", ".codecontext", "cache", "default", name), Path.Combine(second.WorkspacePath, name));
            }
            await second.StartAsync();
            Assert.Equal(0, second.Database.NodeCount);
            // Refused before hashing, logged with its reason; the scan (committed at once here) then verifies.
            Assert.Contains(second.Logger.Entries, entry => entry.Message.StartsWith("No snapshot was used", StringComparison.Ordinal)
                && entry.Message.Contains(expected));
            Assert.Equal(CacheState.Verified, second.CacheState.GetStatus()!.State);
        }
        finally
        {
            Directory.Delete(firstBase, recursive: true);
            if (Directory.Exists(secondBase)) Directory.Delete(secondBase, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyInstance_LoadsTheSnapshot_AndDeletesNothing()
    {
        var basePath = NewBase();
        try
        {
            await using var writer = new SnapshotServiceHarness(basePath);
            await writer.StartAsync();
            await writer.CommitAsync(() => writer.CommitFacts(4));
            await writer.AdvanceAsync(Quiet);
            var temp = Path.Combine(writer.WorkspacePath, CacheDirectory.TempNameFor(SnapshotFile.FormatName(77, 0x12345678)));
            File.WriteAllBytes(temp, [1]);
            var files = Directory.GetFiles(writer.WorkspacePath).Order(StringComparer.Ordinal).ToList();

            await using var reader = new SnapshotServiceHarness(basePath);
            await reader.StartAsync();
            Assert.Equal(SnapshotCacheMode.ReadOnly, reader.Service.Status.Mode);
            Assert.Equal(new[] { "N4_0", "N4_1", "N4_2" }, reader.Database.EnumerateNodes().Select(node => node.Name));
            Assert.Equal(files, Directory.GetFiles(writer.WorkspacePath).Order(StringComparer.Ordinal).ToList());
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task ShardOfAParserNoWorkerIndexes_IsDropped_AndTheRestLoads()
    {
        var basePath = NewBase();
        try
        {
            var other = new ShardKey("gone", "default");
            await using (var first = new SnapshotServiceHarness(basePath))
            {
                await first.StartAsync();
                await first.CommitAsync(() =>
                {
                    first.CommitFacts(1);
                    Assert.True(first.Database.TryCommitGeneration(first.Database.LastCommittedGeneration + 1,
                        [SnapshotServiceHarness.Node("Gone", key: other)], [], new CommitScope(other.ParserId, other.WorkspaceId, null)));
                });
                await first.AdvanceAsync(Quiet);
            }

            await using var second = new SnapshotServiceHarness(basePath, isIndexedParser: parser => parser == "fake");
            await second.StartAsync();
            Assert.Equal(new[] { "N1_0", "N1_1", "N1_2" }, second.Database.EnumerateNodes().Select(node => node.Name));
            Assert.Equal(new[] { "fake" }, second.CacheState.GetStatus()!.Verification!.Keys);
            Assert.Contains(second.Logger.Entries, entry => entry.Message.Contains("gone") && entry.Message.Contains("not loaded"));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task SequenceZero_IsNeverLoaded_AndIsDeleted()
    {
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(2);
        var zero = harness.PlantSnapshot(0);
        harness.Database.Reset();

        await harness.StartAsync();
        Assert.False(File.Exists(zero));
        Assert.Equal(0, harness.Database.NodeCount);
        Assert.Null(harness.Startup.Result!.LoadedView);
    }

    [Fact]
    public async Task CacheOff_RunsNoStep_AndReportsNothing()
    {
        await using var harness = new SnapshotServiceHarness(enabled: false);
        await harness.StartAsync();
        Assert.Null(harness.CacheState.GetStatus());
        Assert.Null(harness.CacheState.GetNotice());
        Assert.Empty(Directory.GetFileSystemEntries(harness.RootPath));
        Assert.False(File.Exists(harness.KeyPath));
        await harness.GraphUpdate.Received(1).PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(nameof(SnapshotRejection.Authentication), false, true)]
    [InlineData(nameof(SnapshotRejection.Structure), false, true)]
    [InlineData(nameof(SnapshotRejection.Unexpected), false, true)]
    [InlineData(nameof(SnapshotRejection.Io), true, true)]      // failed while the file was being hashed
    [InlineData(nameof(SnapshotRejection.Io), false, false)]    // could not even be opened
    [InlineData(nameof(SnapshotRejection.HostVersion), false, false)]
    [InlineData(nameof(SnapshotRejection.TooLong), false, false)]
    public void CostlyRefusals_AreThoseAtOrAfterAuthentication(string rejection, bool passedHostMeta, bool costly)
        => Assert.Equal(costly, SnapshotStartup.IsCostly(Enum.Parse<SnapshotRejection>(rejection), passedHostMeta));

    private static ContextService Context(SnapshotServiceHarness harness)
        => new(new InMemoryNodeRepository(harness.Database), new InMemoryEdgeRepository(harness.Database),
            new InMemoryFileMetadataRepository(), cacheState: harness.CacheState);

    [Fact]
    public async Task QueryAnsweredRightAfterThePublish_BeforeTheStepEnds_IsLabelled()
    {
        // Readers run while the load step does. The step's own hook after the publish (before it returns its
        // outcome) is where an unlabelled snapshot answer could be served.
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            SnapshotServiceHarness? second = null;
            CompactContextResponse? answer = null;
            await using (second = new SnapshotServiceHarness(basePath,
                             onLoaded: _ => answer = Context(second!).GetCompactContextAsync("N1_0").GetAwaiter().GetResult()))
            {
                await second.StartAsync();
                Assert.NotNull(answer);
                Assert.Equal(1, answer!.TotalMatches);
                Assert.Equal(CacheState.Verifying, answer.Cache?.State);
            }
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task Ready_IsNotObservable_BeforeTheScanHasCommittedAndLeftVerifying()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            ScanPhase? phaseAtCompletion = null;
            CacheStatusDto? cacheAtCompletion = null;
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    // The workers are done and say so; the reconciliation commits only after this returns.
                    call.Arg<IScanProgressReporter?>()!.ReportComplete(3, TimeSpan.FromSeconds(1));
                    phaseAtCompletion = second.ScanState.Phase;
                    cacheAtCompletion = second.CacheState.GetStatus();
                    return Task.CompletedTask;
                });
            await second.StartAsync();

            Assert.Equal(ScanPhase.Scanning, phaseAtCompletion);
            Assert.Equal(CacheState.Verifying, cacheAtCompletion!.State);
            Assert.Equal(ScanPhase.Ready, second.ScanState.Phase);
            Assert.Equal(CacheState.Verified, second.CacheState.GetStatus()!.State);
            Assert.Equal(3, second.ScanState.FilesProcessed);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task AfterAFailedVerification_ARefreshOrABatchThatCommits_LeavesItUnverified()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("the worker failed")));
            await second.Coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed scan");

            // A single-file refresh that commits: it checks one file, not the snapshot.
            await second.CommitAsync();
            Assert.Equal(ScanPhase.Error, second.ScanState.Phase);
            Assert.Equal(CacheState.Unverified, second.CacheState.GetStatus()!.State);
            Assert.Equal(CacheState.Unverified, second.CacheState.GetNotice()!.State);

            // A watcher batch that commits: the same.
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Mutation = () =>
            {
                second.CommitFacts(9);
                applied.TrySetResult();
            };
            await second.Coordinator.NotifyFileChangedAsync(Path.Combine(second.RootPath, "A.fake"), FileChangeType.Changed);
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await SnapshotServiceHarness.WaitUntilAsync(() => !second.Coordinator.IsBusy, "the batch");
            Assert.Equal(ScanPhase.Error, second.ScanState.Phase);
            Assert.Equal(CacheState.Unverified, second.CacheState.GetStatus()!.State);
            Assert.True(second.CacheState.GetStatus()!.ServingCached);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task AfterAFailedVerification_ARescanReadsVerifyingWhileItRuns_ThenVerified()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException(new InvalidOperationException("the worker failed")));
            await second.Coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Error, "the failed scan");
            Assert.Equal(CacheState.Unverified, second.CacheState.GetNotice()!.State);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.GraphUpdate.PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>())
                .Returns(gate.Task);
            try
            {
                Assert.NotNull(await second.Coordinator.TryRequestFullRescanAsync());
                Assert.Equal(ScanPhase.Scanning, second.ScanState.Phase);
                Assert.Equal(CacheState.Verifying, second.CacheState.GetNotice()!.State);
                Assert.Equal(CacheState.Verifying, second.CacheState.GetStatus()!.State);
            }
            finally
            {
                gate.TrySetResult();
            }
            await SnapshotServiceHarness.WaitUntilAsync(() => second.ScanState.Phase == ScanPhase.Ready, "the rescan");
            Assert.Equal(CacheState.Verified, second.CacheState.GetStatus()!.State);
            Assert.Null(second.CacheState.GetNotice());
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task WriterStoppedDuringTheLoad_TakesNothingOver()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            // The host's order: the service starts (and waits for the step), then stops before the step has run.
            await second.Service.StartAsync(CancellationToken.None);
            await second.Service.StopAsync(CancellationToken.None);
            await second.Coordinator.StartAsync(CancellationToken.None);
            await SnapshotServiceHarness.WaitUntilAsync(() => second.Startup.Result is not null, "the load step");

            Assert.Equal(SnapshotCacheMode.Stopped, second.Service.Status.Mode);
            Assert.Equal(0, second.Time.TimersCreated);
            // The lock stayed with the step, whose Dispose releases it: the next writer is not refused.
            second.Startup.Dispose();
            await using var third = new SnapshotServiceHarness(basePath);
            await third.StartAsync();
            Assert.Equal(SnapshotCacheMode.Writer, third.Service.Status.Mode);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task StopDuringTheLoadStep_StartsNoScan()
    {
        var root = Path.GetTempPath();
        var scanState = new ScanStateService();
        var graphUpdate = Substitute.For<IGraphUpdateService>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cacheState = new CacheState(enabled: true, () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(15));
            return new CacheLoadOutcome(CacheLoadKind.NothingFound);
        });
        using var coordinator = new IndexCoordinator(graphUpdate, scanState,
            Microsoft.Extensions.Options.Options.Create(new CodeContextOptions { RootPath = root }),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, cacheState: cacheState);
        await coordinator.StartAsync(CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)), "the load step did not start");

        // The stop is requested while the (not cancellable) load runs; the scan after it never starts.
        var stop = coordinator.StopAsync(CancellationToken.None);
        release.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(15));
        await graphUpdate.DidNotReceive().PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
        await graphUpdate.DidNotReceive().PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
        Assert.Equal(ScanPhase.NotStarted, scanState.Phase);
    }

    [Fact]
    public async Task LoadedStore_AcceptsTheVerificationScansFirstGeneration()
    {
        var basePath = NewBase();
        try
        {
            await WriteFirstRunAsync(basePath);
            await using var second = new SnapshotServiceHarness(basePath);
            Assert.Equal(CacheLoadKind.Loaded, second.Startup.Run().Kind);
            Assert.Equal(0, second.Database.LastCommittedGeneration);

            // The verification scan's first delta, from a fresh worker (generation 1), inside its reconciliation.
            var store = new InMemoryCodeGraphRepository(second.Database);
            var applier = new AnalysisDeltaApplier(store, Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisDeltaApplier>.Instance);
            store.BeginReconciliation();
            Assert.True(await applier.ApplyAsync(new AnalysisDelta(
                ParserId: "fake", ParserVersion: "1.0.0-test", WorkspaceId: "default", Generation: 1, RequestId: 1,
                ReplacesWorkspace: true, ReplacesFiles: [],
                Nodes: [new ProtocolNode("fake:default:Fresh", "fake:Fresh", "Fresh", "class", "fake", "A.fake")],
                Edges: [], IsLastForRequest: true)));
            store.CommitReconciliation();
            Assert.Equal(new[] { "Fresh" }, second.Database.EnumerateNodes().Select(node => node.Name));
            Assert.Equal(1, second.Database.LastCommittedGeneration);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task StepThatThrows_LeavesNoCache_AndTheScanRuns()
    {
        var database = new InMemoryDatabase();
        var scanState = new ScanStateService();
        var graphUpdate = Substitute.For<IGraphUpdateService>();
        var cacheState = new CacheState(enabled: true, () => throw new InvalidOperationException("boom"));
        using var coordinator = new IndexCoordinator(graphUpdate, scanState,
            Microsoft.Extensions.Options.Options.Create(new CodeContextOptions { RootPath = Path.GetTempPath() }),
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, cacheState: cacheState);
        await coordinator.StartAsync(CancellationToken.None);
        await SnapshotServiceHarness.WaitUntilAsync(() => scanState.Phase == ScanPhase.Ready, "the startup scan");
        Assert.Null(cacheState.GetStatus());
        await graphUpdate.Received(1).PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter?>(), Arg.Any<CancellationToken>());
        await coordinator.StopAsync(CancellationToken.None);
    }
}
