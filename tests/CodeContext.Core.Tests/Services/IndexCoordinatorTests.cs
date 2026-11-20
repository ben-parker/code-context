using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class IndexCoordinatorTests : IAsyncLifetime
    {
        private readonly IGraphUpdateService _graphUpdateService = Substitute.For<IGraphUpdateService>();
        private readonly ScanStateService _scanState = new();
        private readonly string _tempDir;
        private readonly IndexCoordinator _coordinator;
        private readonly ConcurrentQueue<IReadOnlyList<FileChange>> _batches = new();

        public IndexCoordinatorTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDir);

            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    _batches.Enqueue(callInfo.ArgAt<IReadOnlyList<FileChange>>(0));
                    return Task.CompletedTask;
                });

            var options = Options.Create(new CodeContextOptions { RootPath = _tempDir });
            _coordinator = new IndexCoordinator(_graphUpdateService, _scanState, options, NullLoggerFactory.Instance);
        }

        public async Task InitializeAsync()
        {
            await _coordinator.StartAsync(CancellationToken.None);
            await WaitForAsync(() => _scanState.Phase == ScanPhase.Ready, "startup scan to finish");
        }

        public async Task DisposeAsync()
        {
            await _coordinator.StopAsync(CancellationToken.None);
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        private static async Task WaitForAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}.");
                await Task.Delay(25);
            }
        }

        private IEnumerable<FileChange> AllProcessedChanges() => _batches.SelectMany(b => b);

        [Fact]
        public async Task StartUp_RunsResumableScan_AndReportsReady()
        {
            await _graphUpdateService.Received(1).PerformResumableScanAsync(
                _tempDir, Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>());
            Assert.Equal(ScanPhase.Ready, _scanState.Phase);
        }

        [Fact]
        public async Task BurstOfChanges_LosesNoPaths()
        {
            // Under the old single global debounce timer, later changes cancelled
            // earlier pending ones; the coordinator must deliver every distinct path.
            var paths = Enumerable.Range(0, 25).Select(i => Path.Combine(_tempDir, $"File{i}.cs")).ToList();
            foreach (var path in paths)
            {
                await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            }

            await WaitForAsync(
                () => AllProcessedChanges().Select(c => c.Path).Distinct().Count() == paths.Count,
                "all paths to be processed");

            var processed = AllProcessedChanges().Select(c => c.Path).ToHashSet();
            Assert.All(paths, p => Assert.Contains(p, processed));
        }

        [Fact]
        public async Task RepeatedEventsForSamePath_AreCoalescedIntoOneChange()
        {
            var path = Path.Combine(_tempDir, "Hot.cs");
            for (var i = 0; i < 5; i++)
            {
                await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            }

            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == path), "the change to be processed");
            // Give the loop a moment to (incorrectly) emit further batches, then assert one delivery.
            await Task.Delay(700);
            Assert.Equal(1, AllProcessedChanges().Count(c => c.Path == path));
        }

        [Fact]
        public async Task FilesReadAsOf_MovesWithAScanOrABatch_NotWithARefreshOfOneFile()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            Assert.NotNull(afterStartup);
            Assert.True(afterStartup <= DateTime.UtcNow);

            // One file read again says nothing of the others.
            await _coordinator.RefreshFileAsync(Path.Combine(_tempDir, "One.cs"));
            Assert.Equal(afterStartup, _coordinator.FilesReadAsOfUtc);

            var path = Path.Combine(_tempDir, "Changed.cs");
            await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == path), "the change to be processed");
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > afterStartup, "the batch to move the time");

            var beforeRescan = _coordinator.FilesReadAsOfUtc;
            Assert.NotNull(await _coordinator.TryRequestFullRescanAsync());
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > beforeRescan, "the rescan to move the time");
        }

        [Fact]
        public async Task FilesReadAsOf_DoesNotMoveWithABatchThatLeftChangesQueuedBehindIt()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            var blocker = Path.Combine(_tempDir, "Blocker.cs");
            var first = Path.Combine(_tempDir, "First.cs");
            var refreshed = Path.Combine(_tempDir, "Refreshed.cs");
            var behind = Path.Combine(_tempDir, "Behind.cs");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DateTime? afterFirstBatch = null;
            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var batch = callInfo.ArgAt<IReadOnlyList<FileChange>>(0);
                    _batches.Enqueue(batch);
                    if (batch.Any(change => change.Path == blocker)) await gate.Task;
                    // The refresh runs after the batch that held First and before the one that holds Behind.
                    if (batch.Any(change => change.Path == refreshed)) afterFirstBatch = _coordinator.FilesReadAsOfUtc ?? DateTime.MinValue;
                });

            // While the loop is held, a change, a refresh (which ends the batch the change is in) and a change behind it.
            var holding = _coordinator.RefreshFileAsync(blocker);
            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == blocker), "the loop to be held");
            await _coordinator.NotifyFileChangedAsync(first, FileChangeType.Changed);
            var refresh = _coordinator.RefreshFileAsync(refreshed);
            await _coordinator.NotifyFileChangedAsync(behind, FileChangeType.Changed);
            gate.SetResult();
            await holding;
            await refresh;

            // The batch that held First did not take Behind: the files are not all read as of its start.
            Assert.Contains(_batches, batch => batch.Count == 1 && batch[0].Path == first);
            Assert.Equal(afterStartup, afterFirstBatch);
            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == behind), "the change behind to be processed");
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > afterStartup, "the batch that took every change to move the time");
        }

        [Fact]
        public async Task FilesReadAsOf_DoesNotMoveWithABatchWhoseChangedDirectoriesCouldNotBeRead()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            _graphUpdateService.ExpandSubtreesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new IOException("the directory cannot be listed"));
            var path = Path.Combine(_tempDir, "Changed.cs");

            await _coordinator.NotifySubtreeChangedAsync(Path.Combine(_tempDir, "moved"));
            await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == path), "the change to be processed");
            await WaitForAsync(() => !_coordinator.IsBusy, "the batch to end");

            Assert.Equal(afterStartup, _coordinator.FilesReadAsOfUtc);
        }

        [Fact]
        public async Task FilesReadAsOf_IsEarlierThanTheReadBeganByTheCoarsenessOfFileTimes()
        {
            // A file system stamps a change with a clock that may run a tick behind the one read here: a file changed
            // just after a batch began must not carry a time before it.
            var path = Path.Combine(_tempDir, "Changed.cs");
            DateTime? processingBegan = null;
            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    processingBegan = DateTime.UtcNow;
                    _batches.Enqueue(callInfo.ArgAt<IReadOnlyList<FileChange>>(0));
                    return Task.CompletedTask;
                });
            var afterStartup = _coordinator.FilesReadAsOfUtc;

            await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > afterStartup, "the batch to move the time");

            Assert.True(_coordinator.FilesReadAsOfUtc <= processingBegan - IndexCoordinator.FileTimeCoarseness,
                $"{_coordinator.FilesReadAsOfUtc:O} is not {IndexCoordinator.FileTimeCoarseness.TotalMilliseconds} ms before {processingBegan:O}");
        }

        [Fact]
        public async Task FilesReadAsOf_StaysUnsetWhileNoScanHasSucceeded()
        {
            var failing = Substitute.For<IGraphUpdateService>();
            failing.PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("worker failed"));
            var scanState = new ScanStateService();
            var coordinator = new IndexCoordinator(failing, scanState,
                Options.Create(new CodeContextOptions { RootPath = _tempDir }), NullLoggerFactory.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(() => scanState.Phase == ScanPhase.Error, "the scan to fail");
                Assert.Null(coordinator.FilesReadAsOfUtc);
            }
            finally
            {
                await coordinator.StopAsync(CancellationToken.None);
            }
        }

        // ---- What a batch may do to the time, by the state the coordinator is in ----

        private const string OneLineDiff = "--- a/A.cs\n+++ b/A.cs\n@@ -1 +1 @@\n-a\n+b\n";

        /// <summary>Diff impact over an empty index of this root, dated by <paramref name="freshness"/>.</summary>
        private ContextService ImpactService(IIndexFreshness freshness)
            => GraphTestGraph.Service(new CodeContext.Core.Repositories.InMemory.InMemoryDatabase(), _tempDir, freshness);

        private async Task<string> ImpactRefusalAsync(IIndexFreshness freshness)
            => (await Assert.ThrowsAsync<IndexNotReadyException>(
                () => ImpactService(freshness).GetImpactAsync(new GraphImpactRequest { Diff = OneLineDiff }))).Message;

        /// <summary>A batch that succeeds: one file of a worker, processed, then the loop idle again.</summary>
        private async Task RunBatchAsync(IndexCoordinator coordinator, string name)
        {
            var path = Path.Combine(_tempDir, name);
            await coordinator.NotifyFileChangedAsync(path, FileChangeType.Changed);
            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == path), $"{name} to be processed");
            await WaitForAsync(() => !coordinator.IsBusy, "the batch to end");
        }

        [Fact]
        public async Task AfterAStartupScanThatFailed_NoBatchMakesTheFilesRead_AndImpactSaysWhy()
        {
            // One worker failed the startup scan (the reconciliation rolled back to the loaded or empty store); a
            // batch another worker handles says nothing of the files that worker never read.
            var failing = Substitute.For<IGraphUpdateService>();
            failing.PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("worker 'csharp' failed"));
            failing.ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    _batches.Enqueue(callInfo.ArgAt<IReadOnlyList<FileChange>>(0));
                    return Task.CompletedTask;
                });
            var scanState = new ScanStateService();
            var coordinator = new IndexCoordinator(failing, scanState,
                Options.Create(new CodeContextOptions { RootPath = _tempDir }), NullLoggerFactory.Instance);
            await coordinator.StartAsync(CancellationToken.None);
            try
            {
                await WaitForAsync(() => scanState.Phase == ScanPhase.Error, "the scan to fail");
                await RunBatchAsync(coordinator, "Other.ts");

                Assert.Null(coordinator.FilesReadAsOfUtc);
                var refusal = await ImpactRefusalAsync(coordinator);
                Assert.Contains("worker 'csharp' failed", refusal);
                Assert.Contains("/api/index/refresh", refusal);
            }
            finally
            {
                await coordinator.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task AFullRescanThatFails_LeavesTheTimeUnknownUntilOneSucceeds()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            Assert.NotNull(afterStartup);
            _graphUpdateService
                .PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("worker 'typescript' exited"));

            Assert.NotNull(await _coordinator.TryRequestFullRescanAsync());
            await WaitForAsync(() => _scanState.Phase == ScanPhase.Error, "the rescan to fail");
            await RunBatchAsync(_coordinator, "Changed.cs");

            Assert.Null(_coordinator.FilesReadAsOfUtc);
            Assert.Contains("worker 'typescript' exited", await ImpactRefusalAsync(_coordinator));

            _graphUpdateService
                .PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
            Assert.NotNull(await _coordinator.TryRequestFullRescanAsync());
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > afterStartup, "a rescan that succeeds to set the time");
            Assert.Null(_coordinator.FilesReadNote);
        }

        [Fact]
        public async Task AfterABatchAWorkerFailed_LaterBatchesDoNotMoveTheTime_UntilAFullRescan()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            var broken = Path.Combine(_tempDir, "Broken.cs");
            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var batch = callInfo.ArgAt<IReadOnlyList<FileChange>>(0);
                    _batches.Enqueue(batch);
                    return batch.Any(change => change.Path == broken)
                        ? Task.FromException(new InvalidOperationException("worker 'csharp' crashed"))
                        : Task.CompletedTask;
                });

            await RunBatchAsync(_coordinator, "Broken.cs");
            await RunBatchAsync(_coordinator, "Fine.ts");

            // The failed worker's facts are as fresh as before the batch it failed, not as the later one.
            Assert.Equal(afterStartup, _coordinator.FilesReadAsOfUtc);
            Assert.Contains("worker 'csharp' crashed", _coordinator.FilesReadNote);
            // A file changed since is not mapped, and the answer says why it will not be read by itself.
            File.WriteAllText(Path.Combine(_tempDir, "A.cs"), "b\n");
            var impact = await ImpactService(_coordinator).GetImpactAsync(new GraphImpactRequest { Diff = OneLineDiff });
            var entry = Assert.Single(impact.Unmapped);
            Assert.Equal("notYetIndexed", entry.Reason);
            Assert.Contains("worker 'csharp' crashed", entry.Detail);

            Assert.NotNull(await _coordinator.TryRequestFullRescanAsync());
            await WaitForAsync(() => _coordinator.FilesReadAsOfUtc > afterStartup, "the rescan to move the time");
            Assert.Null(_coordinator.FilesReadNote);
        }

        [Fact]
        public async Task AfterABatchWhoseDirectoriesCouldNotBeListed_LaterBatchesDoNotMoveTheTime()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            _graphUpdateService.ExpandSubtreesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new IOException("the directory cannot be listed"));
            await _coordinator.NotifySubtreeChangedAsync(Path.Combine(_tempDir, "moved"));
            await RunBatchAsync(_coordinator, "First.cs");

            await RunBatchAsync(_coordinator, "Later.cs");

            // What the unlisted directory holds was never read: a later batch must not vouch for it.
            Assert.Equal(afterStartup, _coordinator.FilesReadAsOfUtc);
            Assert.Contains("could not be listed", _coordinator.FilesReadNote);
        }

        [Fact]
        public async Task AFailedRefreshOfOneFile_StopsTheTimeToo()
        {
            var afterStartup = _coordinator.FilesReadAsOfUtc;
            var broken = Path.Combine(_tempDir, "Broken.cs");
            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var batch = callInfo.ArgAt<IReadOnlyList<FileChange>>(0);
                    _batches.Enqueue(batch);
                    return batch.Any(change => change.Path == broken)
                        ? Task.FromException(new InvalidOperationException("worker 'csharp' crashed"))
                        : Task.CompletedTask;
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() => _coordinator.RefreshFileAsync(broken));
            await RunBatchAsync(_coordinator, "Fine.ts");

            Assert.Equal(afterStartup, _coordinator.FilesReadAsOfUtc);
            Assert.NotNull(_coordinator.FilesReadNote);
        }

        [Fact]
        public async Task CreateThenDelete_CoalescesToDelete()
        {
            var path = Path.Combine(_tempDir, "Transient.cs");
            await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Created);
            await _coordinator.NotifyFileChangedAsync(path, FileChangeType.Deleted);

            await WaitForAsync(() => AllProcessedChanges().Any(c => c.Path == path), "the change to be processed");
            var change = Assert.Single(AllProcessedChanges(), c => c.Path == path);
            Assert.Equal(FileChangeType.Deleted, change.Type);
        }

        [Fact]
        public async Task TryRequestFullRescan_ReturnsIncreasingOperationIds()
        {
            var first = await _coordinator.TryRequestFullRescanAsync();
            Assert.NotNull(first);
            await WaitForAsync(() => _scanState.Phase == ScanPhase.Ready, "first rescan");

            var second = await _coordinator.TryRequestFullRescanAsync();
            Assert.NotNull(second);
            Assert.True(second > first);
            await WaitForAsync(() => _scanState.Phase == ScanPhase.Ready, "second rescan");
        }

        [Fact]
        public async Task TryRequestFullRescan_WhileScanning_ReturnsNull()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _graphUpdateService
                .PerformInitialScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .Returns(_ => release.Task);

            var first = await _coordinator.TryRequestFullRescanAsync();
            Assert.NotNull(first);

            var second = await _coordinator.TryRequestFullRescanAsync();
            Assert.Null(second);

            release.SetResult();
            await WaitForAsync(() => _scanState.Phase == ScanPhase.Ready, "rescan to finish");
        }

        [Fact]
        public async Task RefreshFileAsync_CompletesAfterProcessing()
        {
            var path = Path.Combine(_tempDir, "Single.cs");

            await _coordinator.RefreshFileAsync(path);

            var change = Assert.Single(AllProcessedChanges(), c => c.Path == path);
            Assert.Equal(FileChangeType.Changed, change.Type);
        }

        [Fact]
        public async Task RefreshFileAsync_PropagatesFailure()
        {
            _graphUpdateService
                .ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _coordinator.RefreshFileAsync(Path.Combine(_tempDir, "Broken.cs")));
        }
    }
}
