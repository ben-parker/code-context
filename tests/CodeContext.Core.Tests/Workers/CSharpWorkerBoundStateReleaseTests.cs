using CodeContext.Core.Services;
using Microsoft.CodeAnalysis;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// After a quiet time the worker drops what its compilations have bound (declared symbols, member tables) and keeps
/// the trees and the walks. Binding is deterministic, so whatever is walked afterwards finds the same facts: a
/// release may cost the next edit time and may never change an answer.
/// </summary>
public sealed class CSharpWorkerBoundStateReleaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-release-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task AReleaseDropsEveryCompilation_AndKeepsItsTreesAndReferences()
    {
        var walk = new EditWalk(_root, seed: 5);
        walk.WriteAll();
        var host = new InProcessCSharpHost(_root);
        await host.IndexAsync();
        var before = host.Analyzer.Compilations().ToList();
        Assert.True(before.Count > 1);

        Assert.Equal(before.Count, host.Session.ReleaseBoundState());

        var after = host.Analyzer.Compilations().ToList();
        Assert.Equal(before.Count, after.Count);
        for (var index = 0; index < before.Count; index++)
        {
            // A new compilation has bound nothing; the old one, and all it declared, is no longer held.
            Assert.NotSame(before[index], after[index]);
            Assert.Equal(before[index].SyntaxTrees, after[index].SyntaxTrees);
            Assert.Equal(before[index].AssemblyName, after[index].AssemblyName);
            Assert.Equal(
                before[index].References.OfType<PortableExecutableReference>().Select(reference => reference.FilePath),
                after[index].References.OfType<PortableExecutableReference>().Select(reference => reference.FilePath));
            // Each unit binds against the new compilations of what it references, not the ones let go.
            Assert.All(after[index].References.OfType<CompilationReference>(), reference => Assert.Contains(reference.Compilation, after));
            Assert.Equal(
                before[index].References.OfType<CompilationReference>().Select(reference => reference.Compilation.AssemblyName),
                after[index].References.OfType<CompilationReference>().Select(reference => reference.Compilation.AssemblyName));
        }
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public async Task AnEditAfterARelease_SendsWhatItSendsWithoutOne_AndEveryStepMatchesAFreshScan(int seed)
    {
        var walk = new EditWalk(_root, seed);
        walk.WriteAll();
        var released = new InProcessCSharpHost(_root);
        var kept = new InProcessCSharpHost(_root);
        await released.IndexAsync();
        await kept.IndexAsync();

        for (var step = 1; step <= 40; step++)
        {
            var changes = walk.TakeStep();
            released.Session.ReleaseBoundState();
            await released.ApplyAsync(changes);
            await kept.ApplyAsync(changes);

            Assert.True(kept.Facts().SequenceEqual(released.Facts()), $"seed {seed}, step {step}: the graphs differ.\n{string.Join('\n', walk.History)}");
            Assert.Equal(kept.Coverage(), released.Coverage());
            Assert.Equal(kept.Analyzer.LastWalkCount, released.Analyzer.LastWalkCount);
            if (step % 10 == 0)
            {
                var fresh = new InProcessCSharpHost(_root);
                await fresh.IndexAsync();
                Assert.True(fresh.Facts().SequenceEqual(released.Facts()), $"seed {seed}, step {step}: not a fresh scan's graph.\n{string.Join('\n', walk.History)}");
            }
        }
        Assert.True(walk.BodyEdits >= 5 && walk.TopLevelEdits >= 5, $"{walk.BodyEdits} body and {walk.TopLevelEdits} top-level edits.");
    }

    [Fact]
    public async Task AReleaseBetweenOpenAndApply_LosesNoPendingChange()
    {
        // The host opens before every mutation; a release can land between the two.
        var walk = new EditWalk(_root, seed: 13);
        walk.WriteAll();
        var host = new InProcessCSharpHost(_root);
        await host.IndexAsync();
        for (var step = 1; step <= 12; step++)
        {
            var changes = walk.TakeStep();
            await host.ApplyAsync(changes, betweenOpenAndApply: () => host.Session.ReleaseBoundState());
            var fresh = new InProcessCSharpHost(_root);
            await fresh.IndexAsync();
            Assert.True(fresh.Facts().SequenceEqual(host.Facts()), $"step {step}\n{string.Join('\n', walk.History)}");
        }
    }

    [Fact]
    public void TheReleaseWaitsForALongerQuietThanTheTrim_AndGoesWithIt()
    {
        var settings = CodeContext.CSharp.Worker.BoundStateRelease.SettingsFor(trimEnabled: true);
        Assert.True(settings.QuietPeriod >= TimeSpan.FromSeconds(60));
        Assert.True(settings.QuietPeriod > new CodeContext.CSharp.Worker.Memory.QuietTimeTrimSettings().QuietPeriod);
        Assert.False(CodeContext.CSharp.Worker.BoundStateRelease.SettingsFor(trimEnabled: false).Enabled);
    }

    [Fact]
    public void AfterTheQuietTime_ItReleasesOnce_AndNotAgainUntilThereWasWorkWorthIt()
    {
        var time = new CodeContext.Core.Tests.Snapshots.ManualTimeProvider();
        long allocated = 0;
        var released = 0;
        var lines = new List<string>();
        using var release = CodeContext.CSharp.Worker.Program.CreateBoundStateRelease(
            CodeContext.CSharp.Worker.BoundStateRelease.SettingsFor(trimEnabled: true),
            () => { released++; return 3; }, lines.Add, time, () => allocated);

        // A request that bound something, then a pause shorter than the quiet time: nothing yet.
        using (release.BeginActivity()) allocated += 100L * 1024 * 1024;
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(0, released);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, released);
        Assert.StartsWith("release: bound state of 3 unit(s)", Assert.Single(lines));

        // A request that allocated next to nothing bound next to nothing.
        using (release.BeginActivity()) allocated += 1024;
        time.Advance(TimeSpan.FromSeconds(120));
        Assert.Equal(1, released);
    }
}
