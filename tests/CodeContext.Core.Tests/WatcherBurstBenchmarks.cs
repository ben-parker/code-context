using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests;

/// <summary>
/// Opt-in measure of what the watcher's callbacks cost in a burst such as a branch switch or a package restore:
/// many files created in one directory, then many nested directories. The coordinator is a stub that only counts,
/// so what is measured is the watcher's own work per event, which runs on the thread that drains the system's
/// event queue: when it falls behind, the queue overflows and the host rescans the whole repository. Reports the
/// processor time and allocation the watcher added over creating the same tree unwatched, the notifications that
/// arrived and the overflows seen. Asserts nothing about time. Run with
/// <c>dotnet test -c Release --filter "FullyQualifiedName~WatcherBurstBenchmarks" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class WatcherBurstBenchmarks(ITestOutputHelper output)
{
    private const int Files = 20_000;
    private const int Directories = 3_000;

    [Fact]
    public async Task ABurstOfFilesInOneDirectoryAndOfNestedDirectories()
    {
        var root = Directory.CreateTempSubdirectory("cc-burst-").FullName;
        try
        {
            var unwatched = Measure(() => CreateTree(Path.Combine(root, "unwatched")));

            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(repo);
            long files = 0, subtrees = 0, overflows = 0;
            var coordinator = Substitute.For<IIndexCoordinator>();
            coordinator.NotifyFileChangedAsync(default!, default, default)
                .ReturnsForAnyArgs(_ => { Interlocked.Increment(ref files); return ValueTask.CompletedTask; });
            coordinator.NotifySubtreeChangedAsync(default!, default)
                .ReturnsForAnyArgs(_ => { Interlocked.Increment(ref subtrees); return ValueTask.CompletedTask; });
            coordinator.TryRequestFullRescanAsync(default)
                .ReturnsForAnyArgs(_ => { Interlocked.Increment(ref overflows); return Task.FromResult<long?>(1); });
            var workers = Substitute.For<ILanguageWorkerService>();
            workers.OwnedExtensions.Returns([".cs"]);
            using var watcher = new FileWatcherService(
                Options.Create(new CodeContextOptions { RootPath = repo }), NullLogger<FileWatcherService>.Instance,
                coordinator, new ScanStateService(), workers);
            await watcher.StartAsync(CancellationToken.None);

            var watched = Measure(() =>
            {
                CreateTree(repo);
                // Until the notifications stop arriving: the watcher has drained the queue.
                var seen = -1L;
                while (Interlocked.Read(ref files) + Interlocked.Read(ref subtrees) != seen)
                {
                    seen = Interlocked.Read(ref files) + Interlocked.Read(ref subtrees);
                    Thread.Sleep(1000);
                }
            });
            await watcher.StopAsync(CancellationToken.None);

            var report =
                $"watcher burst ({Files} files in one directory, {Directories} nested directories): " +
                $"processor {(watched.Cpu - unwatched.Cpu).TotalMilliseconds:F0} ms and " +
                $"{(watched.Allocated - unwatched.Allocated) / (1024 * 1024)} MiB allocated over the unwatched tree; " +
                $"{files} file and {subtrees} directory notifications, {overflows} overflow(s)";
            output.WriteLine(report);
            if (Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_OUT") is { Length: > 0 } outPath)
                File.AppendAllText(outPath, report + Environment.NewLine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// What the coordinator then does with such a burst in one batch: every path that appeared is reconciled as a
    /// possible directory, each first put to the link check, which lists each distinct parent once for the batch.
    /// </summary>
    [Fact]
    public async Task OneBatchReconcilingEveryPathOfTheBurst()
    {
        var root = Directory.CreateTempSubdirectory("cc-burst-batch-").FullName;
        try
        {
            CreateTree(root);
            var paths = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).ToList();
            var options = Options.Create(new CodeContextOptions { RootPath = root });
            var workers = Substitute.For<ILanguageWorkerService>();
            workers.OwnedExtensions.Returns([".cs"]);
            workers.TryGetParserForExtension(".cs", out Arg.Any<string>()).Returns(call => { call[1] = "csharp"; return true; });
            var service = new GraphUpdateService(
                new CodeContext.Core.Repositories.InMemory.InMemoryRepositoryFactory(NullLogger<CodeContext.Core.Repositories.InMemory.InMemoryRepositoryFactory>.Instance),
                options, NullLogger<GraphUpdateService>.Instance, new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), workers);

            IReadOnlyList<FileChange> changes = [];
            var measured = Measure(() => changes = service.ExpandSubtreesAsync(paths, CancellationToken.None).GetAwaiter().GetResult());

            var report = $"one batch reconciling {paths.Count} paths: processor {measured.Cpu.TotalMilliseconds:F0} ms, " +
                $"{measured.Allocated / (1024 * 1024)} MiB allocated, {changes.Count} changes";
            output.WriteLine(report);
            if (Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_OUT") is { Length: > 0 } outPath)
                File.AppendAllText(outPath, report + Environment.NewLine);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateTree(string at)
    {
        var flat = Path.Combine(at, "flat");
        Directory.CreateDirectory(flat);
        for (var i = 0; i < Files; i++) File.WriteAllText(Path.Combine(flat, $"F{i}.cs"), "");
        var nested = Path.Combine(at, "nested");
        Directory.CreateDirectory(nested);
        for (var i = 0; i < Directories; i++) Directory.CreateDirectory(Path.Combine(nested, $"d{i % 30}", $"e{i}"));
    }

    private static (TimeSpan Cpu, long Allocated) Measure(Action work)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        work();
        process.Refresh();
        return (process.TotalProcessorTime - cpu, GC.GetTotalAllocatedBytes(precise: true) - allocated);
    }
}
