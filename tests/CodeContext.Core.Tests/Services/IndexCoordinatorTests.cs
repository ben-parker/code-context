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
