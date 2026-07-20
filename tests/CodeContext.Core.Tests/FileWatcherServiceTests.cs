using Xunit;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;

namespace CodeContext.Core.Tests
{
    public class FileWatcherServiceTests
    {
        private readonly IOptions<CodeContextOptions> _mockOptions;
        private readonly ILogger<FileWatcherService> _mockLogger;
        private readonly IIndexCoordinator _mockCoordinator;
        private readonly ILanguageWorkerService _mockWorkerService;
        private readonly ScanStateService _scanState = new();

        public FileWatcherServiceTests()
        {
            _mockOptions = Substitute.For<IOptions<CodeContextOptions>>();
            _mockLogger = Substitute.For<ILogger<FileWatcherService>>();
            _mockCoordinator = Substitute.For<IIndexCoordinator>();
            _mockWorkerService = Substitute.For<ILanguageWorkerService>();
            _mockWorkerService.OwnedExtensions.Returns([".cs", ".mts"]);
        }

        [Fact]
        public async Task StartAsync_And_StopAsync_CompleteWithoutException()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);

            _mockOptions.Value.Returns(new CodeContextOptions { RootPath = tempDir });

            var watcherService = new FileWatcherService(
                _mockOptions, _mockLogger, _mockCoordinator, _scanState, _mockWorkerService);

            // Act & Assert
            await watcherService.StartAsync(CancellationToken.None);
            Assert.True(_scanState.WatcherActive);
            await watcherService.StopAsync(CancellationToken.None);
            Assert.False(_scanState.WatcherActive);

            // Cleanup
            Directory.Delete(tempDir, true);
        }

        [Fact]
        public async Task FileChange_IsForwardedToCoordinator()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(tempDir);
            _mockOptions.Value.Returns(new CodeContextOptions { RootPath = tempDir });

            var forwarded = new TaskCompletionSource<(string path, FileChangeType type)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _mockCoordinator
                .NotifyFileChangedAsync(Arg.Any<string>(), Arg.Any<FileChangeType>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    forwarded.TrySetResult((callInfo.ArgAt<string>(0), callInfo.ArgAt<FileChangeType>(1)));
                    return ValueTask.CompletedTask;
                });

            var watcherService = new FileWatcherService(
                _mockOptions, _mockLogger, _mockCoordinator, _scanState, _mockWorkerService);
            await watcherService.StartAsync(CancellationToken.None);
            try
            {
                var filePath = Path.Combine(tempDir, "New.cs");
                await File.WriteAllTextAsync(filePath, "public class New { }");

                var (path, _) = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(filePath, path);
            }
            finally
            {
                await watcherService.StopAsync(CancellationToken.None);
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public async Task WorkerOwnedExtension_IsForwardedWithoutASeparateOptionsAllowList()
        {
            var tempDir = Directory.CreateTempSubdirectory("cc-watcher-extension-").FullName;
            _mockOptions.Value.Returns(new CodeContextOptions { RootPath = tempDir });

            var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mockCoordinator
                .NotifyFileChangedAsync(Arg.Any<string>(), Arg.Any<FileChangeType>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    forwarded.TrySetResult(callInfo.ArgAt<string>(0));
                    return ValueTask.CompletedTask;
                });

            var watcherService = new FileWatcherService(
                _mockOptions, _mockLogger, _mockCoordinator, _scanState, _mockWorkerService);
            await watcherService.StartAsync(CancellationToken.None);
            try
            {
                var filePath = Path.Combine(tempDir, "module.mts");
                await File.WriteAllTextAsync(filePath, "export const value = 1;\n");

                Assert.Equal(filePath, await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                await watcherService.StopAsync(CancellationToken.None);
                Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task GitIgnoreChange_IsForwardedAsAReconciliationControlEvent()
        {
            var tempDir = Directory.CreateTempSubdirectory("cc-watcher-ignore-").FullName;
            _mockOptions.Value.Returns(new CodeContextOptions { RootPath = tempDir });
            var forwarded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mockCoordinator
                .NotifyFileChangedAsync(Arg.Any<string>(), Arg.Any<FileChangeType>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    if (Path.GetFileName(callInfo.ArgAt<string>(0)) == ".gitignore")
                        forwarded.TrySetResult(callInfo.ArgAt<string>(0));
                    return ValueTask.CompletedTask;
                });

            var watcherService = new FileWatcherService(
                _mockOptions, _mockLogger, _mockCoordinator, _scanState, _mockWorkerService);
            await watcherService.StartAsync(CancellationToken.None);
            try
            {
                var ignoreFile = Path.Combine(tempDir, ".gitignore");
                await File.WriteAllTextAsync(ignoreFile, "generated/\n");
                Assert.Equal(ignoreFile, await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                await watcherService.StopAsync(CancellationToken.None);
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
