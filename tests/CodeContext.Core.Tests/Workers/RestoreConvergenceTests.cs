using System.Text;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Convergence tier 1 of plan phase 6, in process: the extended edit walk, with the worker's state saved
/// and a new worker restored from it between every two steps. A step's edits either reach the worker as
/// a watcher batch before it saves, or reach the disk while it is down (a down step), so only the restored
/// worker's verification can find them. After every restore the facts the host holds (every field of
/// every node and edge) and the coverage recorded with them must equal a fresh scan's. Every restore must
/// accept its baseline: a tier whose baselines were all rejected would prove nothing.
/// </summary>
public sealed class RestoreConvergenceTests : IDisposable
{
    private const int Steps = 120;

    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-restore-walk-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string StateDirectory => Path.Combine(Root, ".codecontext", "cache", "default");

    public RestoreConvergenceTests()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StateDirectory);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(20261106)]
    public async Task EveryStepMatchesAFreshScan(int seed)
    {
        var random = new Random(seed);
        var walk = new EditWalk(Root, seed, extended: true);
        walk.WriteAll();
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        await AssertConvergedAsync(host, walk, seed, "initial scan");

        var accepted = 0;
        var walked = 0;
        for (var step = 1; step <= Steps; step++)
        {
            var down = random.Next(3) == 0;
            if (!down) await host.ApplyAsync(walk.TakeStep());
            foreach (var file in Directory.GetFiles(StateDirectory)) File.Delete(file);
            var baseline = await host.SaveAsync(StateDirectory);
            if (down) walk.TakeDownStep();

            host.Restart();
            var result = await host.IndexAsync(baseline);
            Assert.True(result.Baseline == BaselineOutcomes.Accepted, $"Seed {seed}, step {step}: baseline {result.Baseline}: {result.BaselineReason}");
            accepted++;
            walked += VerifyLine.Parse(host.LastVerify).Walked;
            await AssertConvergedAsync(host, walk, seed, $"step {step}{(down ? " (down)" : string.Empty)}");
        }

        Console.WriteLine($"Tier 1, seed {seed}: {accepted} accepted baselines, {walked} files walked; {string.Join(", ", walk.Kinds.OrderBy(kind => kind.Key, StringComparer.Ordinal).Select(kind => $"{kind.Key}={kind.Value}"))}");
        Assert.Equal(Steps, accepted);
        Assert.True(walked > 0, "No restore walked anything.");
        foreach (var kind in EditWalk.ExtendedKinds)
        {
            Assert.True(walk.Kinds.GetValueOrDefault(kind) > 0, $"Seed {seed} made no '{kind}' edit.");
        }
    }

    private async Task AssertConvergedAsync(InProcessCSharpHost host, EditWalk walk, int seed, string step)
    {
        var fresh = new InProcessCSharpHost(Root);
        await fresh.IndexAsync();
        var expected = fresh.Facts();
        var actual = host.Facts();
        if (expected.SequenceEqual(actual) && fresh.Coverage() == host.Coverage()) return;

        var message = new StringBuilder($"Seed {seed} diverged at {step}.\n");
        message.AppendLine("Recent edits:").AppendJoin('\n', walk.History.TakeLast(8)).AppendLine();
        message.AppendLine("Missing:").AppendJoin('\n', expected.Except(actual).Take(10)).AppendLine();
        message.AppendLine("Stale:").AppendJoin('\n', actual.Except(expected).Take(10)).AppendLine();
        if (fresh.Coverage() != host.Coverage()) message.AppendLine($"Coverage:\n{fresh.Coverage()}\n{host.Coverage()}");
        Assert.Fail(message.ToString());
    }
}
