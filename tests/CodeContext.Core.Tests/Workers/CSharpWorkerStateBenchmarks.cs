using System.Diagnostics;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Timings of the C# worker's state (plan phase 6, requirement 9): a real worker process driven through the
/// supervisor, with no host instance. Opt-in (<c>Category=Benchmark</c>) and read-only for the repository:
/// <c>CODECONTEXT_BENCH_ROOT</c> names a repository that is only read; its state files go to a temp
/// directory. <c>CODECONTEXT_BENCH_COPY</c> names a directory copied to a temp directory, where one leaf file
/// and one root file are edited while the worker is down (<c>CODECONTEXT_BENCH_LEAF</c>,
/// <c>CODECONTEXT_BENCH_ROOT_FILE</c>, relative paths). Without the variables the tests do nothing.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class CSharpWorkerStateBenchmarks(ITestOutputHelper output)
{
    private const int Runs = 5;

    private sealed record Run(double Open, double Index, double FirstDelta, string? Verify);

    private static List<string> ApprovedFiles(string root)
        => new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = root })).EnumerateIncludedFiles([".cs"]).ToList();

    private static ParserProcessSupervisor Supervisor(ListLogger<ParserProcessSupervisor> log)
        => new(Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_WORKER") is { Length: > 0 } published
                ? new WorkerLaunchSpec("csharp", "CSharp", published, [])
                : CSharpWorkerPipeline.CSharpWorkerLaunchSpec(),
            TestWorkerOptions.Patient(), log, null,
            new CodeContextOptions { RootPath = Path.GetTempPath() });

    /// <summary>Open, then index (with <paramref name="baseline"/> when given), on a new process; the timings and the verify line.</summary>
    private static async Task<(Run Run, ParserProcessSupervisor Supervisor, IReadOnlyList<UnitFingerprint>? Fingerprints)> IndexAsync(
        string root, List<string> files, IndexBaseline? baseline)
    {
        var log = new ListLogger<ParserProcessSupervisor>();
        var supervisor = Supervisor(log);
        await supervisor.EnsureInitializedAsync();
        IReadOnlyList<UnitFingerprint>? fingerprints = null;
        var clock = new Stopwatch();
        double firstDelta = -1;
        supervisor.DeltaHandler = (delta, _) =>
        {
            if (firstDelta < 0) firstDelta = clock.Elapsed.TotalMilliseconds;
            fingerprints ??= delta.Fingerprints;
            return Task.FromResult(true);
        };
        var open = Stopwatch.StartNew();
        await supervisor.OpenWorkspaceAsync(new OpenWorkspaceParams("default", root, [".csproj", ".sln"], files));
        open.Stop();
        clock.Start();
        var result = await supervisor.IndexWorkspaceAsync(new IndexWorkspaceParams("default", 1, files, baseline));
        clock.Stop();
        if (baseline is not null) Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        var verify = log.Entries.Select(entry => entry.Message).LastOrDefault(message => message.Contains("verify:", StringComparison.Ordinal));
        return (new Run(open.Elapsed.TotalMilliseconds, clock.Elapsed.TotalMilliseconds, firstDelta, verify), supervisor, fingerprints);
    }

    private static async Task<(IndexBaseline Baseline, double Milliseconds, long Bytes)> SaveAsync(
        ParserProcessSupervisor supervisor, string directory, IReadOnlyList<UnitFingerprint> fingerprints)
    {
        var stateId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, $"worker-csharp.0000000001.{stateId[..8]}.state");
        var clock = Stopwatch.StartNew();
        var saved = await supervisor.SaveStateAsync(new SaveStateParams("default", path, stateId));
        clock.Stop();
        Assert.True(saved.Saved);
        return (new IndexBaseline(path, stateId, saved.Sha256!, fingerprints), clock.Elapsed.TotalMilliseconds, saved.Bytes!.Value);
    }

    private void Report(string what, IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        output.WriteLine($"{what}: median {sorted[sorted.Count / 2]:F0} ms, range {sorted[0]:F0}-{sorted[^1]:F0} ms ({string.Join(", ", sorted.Select(v => v.ToString("F0")))})");
    }

    [Fact]
    public async Task UnchangedRepository_FullIndexSaveAndRestore()
    {
        if (Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_ROOT") is not { Length: > 0 } root) return;
        var files = ApprovedFiles(root);
        output.WriteLine($"{root}: {files.Count} files");
        var directory = Directory.CreateDirectory(Path.Combine(root, ".codecontext", "cache", "cc-bench-state")).FullName;
        try
        {
            var (full, saves, restores, sizes) = (new List<Run>(), new List<double>(), new List<Run>(), new List<long>());
            for (var run = 0; run < Runs; run++)
            {
                var (indexed, first, fingerprints) = await IndexAsync(root, files, null);
                full.Add(indexed);
                var (baseline, saveMs, bytes) = await SaveAsync(first, directory, fingerprints!);
                saves.Add(saveMs);
                sizes.Add(bytes);
                await first.DisposeAsync();

                var (restored, second, _) = await IndexAsync(root, files, baseline);
                restores.Add(restored);
                output.WriteLine($"run {run + 1}: {restored.Verify}");
                await second.DisposeAsync();
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            }
            Report("(a) full index: open", full.Select(r => r.Open));
            Report("(a) full index: index", full.Select(r => r.Index));
            Report("(a) full index: open + index", full.Select(r => r.Open + r.Index));
            Report("(b) saveState", saves);
            output.WriteLine($"(b) state file: {string.Join(", ", sizes)} bytes");
            Report("(c) restore unchanged: open", restores.Select(r => r.Open));
            Report("(c) restore unchanged: index to the empty delta", restores.Select(r => r.FirstDelta));
            Report("(c) restore unchanged: open + index", restores.Select(r => r.Open + r.Index));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EditedWhileDown_LeafAndRootFile()
    {
        if (Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_COPY") is not { Length: > 0 } source) return;
        var leaf = Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_LEAF")!;
        var rootFile = Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_ROOT_FILE")!;
        var copy = Directory.CreateTempSubdirectory("cc-bench-copy-").FullName;
        var directory = Directory.CreateDirectory(Path.Combine(copy, ".codecontext", "cache", "cc-bench-state")).FullName;
        try
        {
            CopyTree(source, Path.Combine(copy, "src"));
            var root = copy;
            var files = ApprovedFiles(root);
            output.WriteLine($"copy of {source}: {files.Count} files");
            var (_, first, fingerprints) = await IndexAsync(root, files, null);
            var (baseline, _, _) = await SaveAsync(first, directory, fingerprints!);
            await first.DisposeAsync();

            foreach (var (name, relative) in new[] { ("leaf", leaf), ("root", rootFile) })
            {
                var path = Path.Combine(root, relative);
                var original = await File.ReadAllTextAsync(path);
                var runs = new List<Run>();
                for (var run = 0; run < Runs; run++)
                {
                    await File.WriteAllTextAsync(path, original + $"\ninternal static class BenchEdit{run} {{ }}\n");
                    var (restored, second, _) = await IndexAsync(root, files, baseline);
                    runs.Add(restored);
                    output.WriteLine($"{name} run {run + 1}: {restored.Verify}");
                    await second.DisposeAsync();
                    await File.WriteAllTextAsync(path, original);
                }
                Report($"(d) {name} file edited while down: open", runs.Select(r => r.Open));
                Report($"(d) {name} file edited while down: index to the delta", runs.Select(r => r.FirstDelta));
                Report($"(d) {name} file edited while down: open + index", runs.Select(r => r.Open + r.Index));
            }
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Sources, project files and restore output; no build output.</summary>
    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("node_modules") || parts.Contains(".codecontext")) continue;
            if (parts.Contains("obj") && !(parts.Length == 3 && parts[1] == "obj" && (file.EndsWith(".json") || file.EndsWith(".props") || file.EndsWith(".targets")))) continue;
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
