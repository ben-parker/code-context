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

        [Fact]
        public async Task GitIgnoreBelowAMandatoryExclusion_IsNotAControlEvent()
        {
            // Nothing below .git/, .codecontext/ or node_modules/ is ever selected, so an ignore file
            // there cannot change selection and must not trigger a full rescan; one in an ordinary
            // subdirectory (and one whose own name only resembles an exclusion) still does.
            var tempDir = Directory.CreateTempSubdirectory("cc-watcher-ignore-").FullName;
            _mockOptions.Value.Returns(new CodeContextOptions { RootPath = tempDir });
            var forwarded = new System.Collections.Concurrent.ConcurrentQueue<string>();
            _mockCoordinator
                .NotifyFileChangedAsync(Arg.Any<string>(), Arg.Any<FileChangeType>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    forwarded.Enqueue(callInfo.ArgAt<string>(0));
                    return ValueTask.CompletedTask;
                });
            var excluded = new[] { ".codecontext/cache", ".git", "node_modules/pkg", "src/NODE_MODULES" }
                .Select(dir => Path.Combine(tempDir, dir.Replace('/', Path.DirectorySeparatorChar))).ToList();
            var included = new[] { "src", "node_modules.old" }
                .Select(dir => Path.Combine(tempDir, dir)).ToList();
            foreach (var dir in excluded.Concat(included)) Directory.CreateDirectory(dir);

            var watcherService = new FileWatcherService(
                _mockOptions, _mockLogger, _mockCoordinator, _scanState, _mockWorkerService);
            await watcherService.StartAsync(CancellationToken.None);
            try
            {
                foreach (var dir in excluded.Concat(included))
                    await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), "*\n");
                var expected = included.Select(dir => Path.Combine(dir, ".gitignore")).ToList();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!expected.All(forwarded.Contains) && DateTime.UtcNow < deadline) await Task.Delay(20);
                // Events arrive in order: a sentinel after the excluded ones were written proves they were seen.
                var sentinel = Path.Combine(tempDir, "Sentinel.cs");
                await File.WriteAllTextAsync(sentinel, "class Sentinel { }");
                while (!forwarded.Contains(sentinel) && DateTime.UtcNow < deadline) await Task.Delay(20);

                Assert.All(expected, path => Assert.Contains(path, forwarded));
                Assert.Contains(sentinel, forwarded);
                var unexpected = forwarded.Where(path => Path.GetFileName(path) == ".gitignore").Except(expected).ToList();
                if (OperatingSystem.IsWindows())
                {
                    Assert.Empty(unexpected);
                }
                else
                {
                    // Ordinal on Unix: src/NODE_MODULES is an ordinary directory there.
                    Assert.Equal(new[] { Path.Combine(excluded[3], ".gitignore") }, unexpected);
                }
            }
            finally
            {
                await watcherService.StopAsync(CancellationToken.None);
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
