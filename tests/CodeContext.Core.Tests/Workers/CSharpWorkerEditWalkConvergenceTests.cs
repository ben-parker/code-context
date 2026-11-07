using System.Text;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The gate on analyzing only what an edit can affect: however a workspace got into a state,
/// its graph must equal what a fresh scan of that state produces. Each seed walks a workspace
/// of several projects through body-only and top-level edits, in single files and batches,
/// and compares the graph through the real worker, delta applier and store after every step.
/// A failure names the seed, the step and the edits leading to it, and replays exactly.
/// </summary>
public sealed class CSharpWorkerEditWalkConvergenceTests : IAsyncLifetime
{
    private const int Steps = 120;

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-walk-").FullName;
    private CSharpWorkerPipeline _incremental = null!;
    private CSharpWorkerPipeline _fresh = null!;

    public Task InitializeAsync()
    {
        _incremental = new CSharpWorkerPipeline(_root);
        _fresh = new CSharpWorkerPipeline(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _incremental.DisposeAsync();
        await _fresh.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20261104)]
    public async Task EveryStepOfARandomWalkMatchesAFreshScan(int seed)
    {
        var walk = new EditWalk(_root, seed);
        walk.WriteAll();
        await _incremental.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        await AssertConverged(walk, seed, "initial scan");

        for (var step = 1; step <= Steps; step++)
        {
            var changes = walk.TakeStep();
            await _incremental.GraphUpdateService.ProcessFileChangesAsync(changes, CancellationToken.None);
            await AssertConverged(walk, seed, $"step {step}");
        }

        // A walk that never exercised what it is for proves nothing.
        Assert.True(walk.BodyEdits >= Steps / 4, $"Only {walk.BodyEdits} body edits.");
        Assert.True(walk.TopLevelEdits >= Steps / 4, $"Only {walk.TopLevelEdits} top-level edits.");
    }

    private async Task AssertConverged(EditWalk walk, int seed, string step)
    {
        await _fresh.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

        var expected = await Facts(_fresh);
        var actual = await Facts(_incremental);
        if (expected.SequenceEqual(actual)) return;

        var message = new StringBuilder($"Seed {seed} diverged at {step}.\n");
        message.AppendLine("Recent edits:").AppendJoin('\n', walk.History.TakeLast(6)).AppendLine();
        message.AppendLine("Missing:").AppendJoin('\n', expected.Except(actual).Take(10)).AppendLine();
        message.AppendLine("Stale:").AppendJoin('\n', actual.Except(expected).Take(10));
        Assert.Fail(message.ToString());
    }

    private static async Task<List<string>> Facts(CSharpWorkerPipeline pipeline)
    {
        var nodes = await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        return nodes.Select(node =>
                $"N {node.Id}|{node.Identifier}|{node.FilePath}|{node.StartLine}:{node.StartCol}-{node.EndLine}:{node.EndCol}|{node.Signature}|{node.Name}|{node.Type}")
            .Concat(edges.Select(edge => $"E {edge.Id}|{edge.SourceId}|{edge.TargetId}|{edge.Type}"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
