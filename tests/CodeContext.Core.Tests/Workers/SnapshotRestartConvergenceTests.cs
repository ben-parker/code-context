using System.Text;
using System.Text.Json;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Convergence tier 2 of plan phase 6, with real processes through the production host path: the extended
/// edit walk runs against one host's services (coordinator, load step, worker service and supervisor, delta
/// applier, snapshot service) over a real C# worker process. At a restart the host stops: the snapshot
/// service captures, asks the worker to save its state and writes the snapshot; a new host loads it at
/// start-up and offers the new worker process its baseline. Edits reach the running host as watcher
/// batches, or the disk while it is down. After every step the graph (every field of every node and edge)
/// and the coverage equal a fresh scan's, and every restart's worker accepted its baseline.
/// </summary>
public abstract class SnapshotRestartConvergence : IAsyncLifetime
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-restart-walk-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private CSharpWorkerPipeline _fresh = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        _fresh = new CSharpWorkerPipeline(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _fresh.DisposeAsync();
        Directory.Delete(_base, recursive: true);
    }

    /// <summary>Only the stop captures: a quiet-period capture must not run beside a batch delivered past the coordinator.</summary>
    private PersistentStateTests.Instance Start() => new(Root, KeyPath, CSharpWorkerPipeline.CSharpWorkerRegistration(),
        settings => settings with { QuietPeriod = SnapshotServiceSettings.MaxDelayValue, MaxDelay = SnapshotServiceSettings.MaxDelayValue },
        cache: true);

    /// <param name="restartEvery">A restart after every this many steps.</param>
    /// <param name="downShare">Out of 10, how many restarts happen with their step's edits made while down; the rest deliver them first.</param>
    protected async Task WalkAsync(int seed, int steps, int restartEvery, int downShare)
    {
        var random = new Random(seed);
        var walk = new EditWalk(Root, seed, extended: true);
        walk.WriteAll();
        var instance = Start();
        try
        {
            await instance.StartAsync();
            await instance.WaitForAsync(() => instance.ScanState.Phase == ScanPhase.Ready, "the first scan");
            await AssertConvergedAsync(instance, walk, seed, "initial scan");

            var (restarts, accepted) = (0, 0);
            for (var step = 1; step <= steps; step++)
            {
                if (step % restartEvery != 0)
                {
                    await instance.GraphUpdate.ProcessFileChangesAsync(walk.TakeStep(), CancellationToken.None);
                    await AssertConvergedAsync(instance, walk, seed, $"step {step}");
                    continue;
                }

                var down = random.Next(10) < downShare;
                if (!down) await instance.GraphUpdate.ProcessFileChangesAsync(walk.TakeStep(), CancellationToken.None);
                await instance.StopAsync();
                Assert.True(instance.Snapshots.Status.LastWriteError is null, $"Seed {seed}, step {step}: {instance.Snapshots.Status.LastWriteError}");
                await instance.DisposeAsync();
                if (down) walk.TakeDownStep();

                instance = Start();
                await instance.StartAsync();
                await instance.WaitForAsync(() => instance.ScanState.Phase is ScanPhase.Ready or ScanPhase.Error, "the verification scan");
                Assert.True(instance.ScanState.Phase == ScanPhase.Ready, $"Seed {seed}, step {step}: the verification failed.");
                restarts++;
                var verification = instance.CacheState.GetStatus()?.Verification?.GetValueOrDefault("csharp");
                Assert.True(verification == CacheState.Baseline, $"Seed {seed}, step {step}: the C# shard was verified '{verification}'.");
                accepted++;
                await AssertConvergedAsync(instance, walk, seed, $"step {step} (restart{(down ? ", down" : string.Empty)})");
            }

            Console.WriteLine($"Tier 2, seed {seed}: {restarts} restarts, {accepted} accepted baselines; {string.Join(", ", walk.Kinds.OrderBy(kind => kind.Key, StringComparer.Ordinal).Select(kind => $"{kind.Key}={kind.Value}"))}");
            Assert.Equal(steps / restartEvery, restarts);
            Assert.Equal(restarts, accepted);
            foreach (var kind in EditWalk.ExtendedKinds)
            {
                Assert.True(walk.Kinds.GetValueOrDefault(kind) > 0, $"Seed {seed} made no '{kind}' edit.");
            }
        }
        finally
        {
            await instance.DisposeAsync();
        }
    }

    /// <summary>The first run writes one snapshot and state file; a restart with nothing changed leaves both as they were.</summary>
    protected async Task UnchangedRestartsAsync()
    {
        new EditWalk(Root, seed: 1, extended: true).WriteAll();
        await using (var first = Start())
        {
            await first.StartAsync();
            await first.WaitForAsync(() => first.ScanState.Phase == ScanPhase.Ready, "the first scan");
            await first.StopAsync();
            Assert.True(first.Snapshots.Status.WritesCompleted == 1, $"Warnings: {string.Join(" | ", first.Warnings)}");
        }
        var saved = PersistedFiles();
        Assert.Single(saved.Keys, name => name.EndsWith(".ccsnap", StringComparison.Ordinal));
        Assert.Single(saved.Keys, name => name.StartsWith("worker-", StringComparison.Ordinal));

        for (var restart = 1; restart <= 2; restart++)
        {
            await using var next = Start();
            await next.StartAsync();
            await next.WaitForAsync(() => next.ScanState.Phase is ScanPhase.Ready or ScanPhase.Error, "the verification scan");
            var verification = next.CacheState.GetStatus()?.Verification?.GetValueOrDefault("csharp");
            Assert.True(verification == CacheState.Baseline, $"Restart {restart} was verified '{verification}'. Warnings: {string.Join(" | ", next.Warnings)}");
            await next.StopAsync();
            Assert.Equal(0, next.Snapshots.Status.WritesCompleted);
            var after = PersistedFiles();
            Assert.Equal(saved.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
            Assert.All(saved, file => Assert.Equal(file.Value, after[file.Key]));
        }
    }

    /// <summary>Each snapshot and worker state file of the workspace, by name, with its bytes.</summary>
    private Dictionary<string, byte[]> PersistedFiles()
    {
        var workspace = Path.Combine(Root, ".codecontext", "cache", "default");
        return Directory.GetFiles(workspace, "*.ccsnap").Concat(Directory.GetFiles(workspace, "worker-*"))
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
    }

    private async Task AssertConvergedAsync(PersistentStateTests.Instance instance, EditWalk walk, int seed, string step)
    {
        await _fresh.GraphUpdateService.PerformInitialScanAsync(Root, null, CancellationToken.None);
        var expected = GraphFacts.Of(
            await _fresh.RepositoryFactory.CreateNodeRepository().GetAllAsync(),
            await _fresh.RepositoryFactory.CreateEdgeRepository().GetAllAsync());
        var actual = GraphFacts.Of(instance.Database);
        var expectedCoverage = Coverage(AnalysisCoverageRegistry.ForStore(_fresh.RepositoryFactory).GetProjects());
        var actualCoverage = Coverage(AnalysisCoverageRegistry.ForStore(instance.Factory).GetProjects());
        if (expected.SequenceEqual(actual) && expectedCoverage == actualCoverage) return;

        var message = new StringBuilder($"Seed {seed} diverged at {step}.\n");
        message.AppendLine("Recent edits:").AppendJoin('\n', walk.History.TakeLast(8)).AppendLine();
        message.AppendLine("Missing:").AppendJoin('\n', expected.Except(actual).Take(10)).AppendLine();
        message.AppendLine("Stale:").AppendJoin('\n', actual.Except(expected).Take(10)).AppendLine();
        if (expectedCoverage != actualCoverage) message.AppendLine($"Coverage:\n{expectedCoverage}\n{actualCoverage}");
        Assert.Fail(message.ToString());
    }

    private static string Coverage(IReadOnlyList<ProjectCoverage> coverage)
        => JsonSerializer.Serialize(coverage.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage);
}

/// <summary>Tier 2, restarting between every two steps: 2 seeds of 25 steps.</summary>
public sealed class SnapshotRestartConvergenceTests : SnapshotRestartConvergence
{
    [Theory]
    [InlineData(1)]
    [InlineData(20261106)]
    public Task RestartingBetweenAllSteps_EveryStepMatchesAFreshScan(int seed) => WalkAsync(seed, steps: 25, restartEvery: 1, downShare: 5);

    [Fact]
    public Task UnchangedRestart_RewritesNeitherTheSnapshotNorTheState() => UnchangedRestartsAsync();
}

/// <summary>Tier 2, restarting at every 8th step with its edits made while down: 2 seeds of 120 steps.</summary>
public sealed class SnapshotRestartLongWalkConvergenceTests : SnapshotRestartConvergence
{
    [Theory]
    [InlineData(2)]
    [InlineData(20261107)]
    public Task RestartingAtEvery8thStep_EveryStepMatchesAFreshScan(int seed) => WalkAsync(seed, steps: 120, restartEvery: 8, downShare: 10);
}
