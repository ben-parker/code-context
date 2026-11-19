using System.Text.Json;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A call through <c>base</c> is marked statically bound (<see cref="EdgeMetadataKeys.Dispatch"/>), and
/// <see cref="ContextService"/> lists it only for the method it names. The marker is a per-edge fact, so it must
/// survive everything that stores, saves or rebuilds edges: the host's compact store, a snapshot written and loaded
/// on a cached restart (with the C# worker's saved state accepted, so the facts are the snapshot's), and the one-shot
/// <c>query --snapshot</c> path. Each is asked the same question, with the real C# worker.
/// </summary>
public sealed class StaticDispatchSurvivalTests : IDisposable
{
    private const string Base = "csharp:Demo.Folder.Validate()";
    private const string Sibling = "csharp:Demo.CollectionFolder.Validate()";

    private readonly string _base = Directory.CreateTempSubdirectory("cc-dispatch-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");

    public StaticDispatchSurvivalTests()
    {
        Directory.CreateDirectory(Root);
        Write("Folder.cs", "namespace Demo; public class Folder { public virtual int Validate() => 1; }\n");
        Write("CollectionFolder.cs", "namespace Demo; public class CollectionFolder : Folder { public override int Validate() => 2; }\n");
        Write("AggregateFolder.cs",
            "namespace Demo; public class AggregateFolder : Folder { public override int Validate() => base.Validate() + 1; }\n");
        Write("Refresher.cs", "namespace Demo; public class Refresher { public int Refresh(Folder folder) => folder.Validate(); }\n");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// The daemon after its scan, the same host restarted from its snapshot with the worker's saved state accepted, and a
    /// one-shot open of that snapshot with no instance: each holds the marker on the base call and answers alike.
    /// </summary>
    [Fact]
    public async Task BaseCall_AnswersAlike_FromTheDaemon_ACachedRestart_AndAOneShotSnapshot()
    {
        List<string> served;
        await using (var first = Start(cache: true))
        {
            await first.StartAsync();
            await WaitForStateWrittenAsync(first);
            AssertStaticBaseCall(first.Database.EnumerateEdges());
            served = await AnswersAsync(Context(first));
            AssertBaseCallListedOnlyForItsTarget(served);
            await first.StopAsync();
        }

        await using (var restarted = Start(cache: true))
        {
            await restarted.StartAsync();
            await restarted.WaitForAsync(() => restarted.ScanState.Phase == ScanPhase.Ready && restarted.CacheState.GetNotice() is null,
                "the verification scan");
            // The saved state was accepted and nothing changed: the worker sent no fact, so every edge is the snapshot's.
            Assert.All(restarted.Sink.Deltas, delta => Assert.Empty(delta.Edges));
            Assert.DoesNotContain(restarted.Sink.Deltas, delta => delta.ReplacesWorkspace);
            AssertStaticBaseCall(restarted.Database.EnumerateEdges());
            Assert.Equal(served, await AnswersAsync(Context(restarted)));
            await restarted.StopAsync();
        }

        var opened = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = Root, CacheKeyPath = KeyPath },
            new StaticWorkerCatalog(CSharpWorkerPipeline.CSharpWorkerRegistration()),
            DateTime.UtcNow + SnapshotFreshness.Tolerance);
        Assert.True(opened.Snapshot is not null, opened.Reason);
        Assert.Equal(served, await AnswersAsync(opened.Snapshot!.Context, expectCache: CacheState.Snapshot));
    }

    private PersistentStateTests.Instance Start(bool cache)
        => new(Root, KeyPath, CSharpWorkerPipeline.CSharpWorkerRegistration(),
            settings => settings with { QuietPeriod = TimeSpan.FromMilliseconds(50) }, cache);

    private static Task WaitForStateWrittenAsync(PersistentStateTests.Instance instance)
        => instance.WaitForAsync(() =>
        {
            if (instance.ScanState.Phase != ScanPhase.Ready || instance.Coordinator.IsBusy || !instance.Snapshots.WrittenMatchesStore) return false;
            var (shard, annotation) = instance.Database.CurrentShard(InMemoryDatabase.ShardKey.From("csharp", "default"));
            return shard is not null && annotation?.StateFor(shard) is not null;
        }, "the snapshot and the worker state to be written");

    private static void AssertStaticBaseCall(IEnumerable<CodeEdge> edges)
    {
        var calls = edges.Where(edge => edge.Type == "CALLS" && edge.TargetId?.EndsWith("Demo.Folder.Validate()", StringComparison.Ordinal) == true).ToList();
        var throughBase = Assert.Single(calls, edge => edge.SourceId!.EndsWith("Demo.AggregateFolder.Validate()", StringComparison.Ordinal));
        Assert.Equal(EdgeDispatch.Static, throughBase.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Dispatch));
        var dispatched = Assert.Single(calls, edge => edge.SourceId!.EndsWith("Demo.Refresher.Refresh(Demo.Folder)", StringComparison.Ordinal));
        Assert.Null(dispatched.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Dispatch));
    }

    private static void AssertBaseCallListedOnlyForItsTarget(List<string> answers)
    {
        // answers[0] is the base method's, answers[1] the sibling override's.
        Assert.Contains("Demo.AggregateFolder.Validate()", answers[0], StringComparison.Ordinal);
        Assert.Contains("Demo.Refresher.Refresh(Demo.Folder)", answers[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Demo.AggregateFolder.Validate()", answers[1], StringComparison.Ordinal);
        Assert.Contains("Demo.Refresher.Refresh(Demo.Folder)", answers[1], StringComparison.Ordinal);
    }

    /// <summary>The host's context service over an instance's store, as a served query answers (no cache label).</summary>
    private ContextService Context(PersistentStateTests.Instance instance)
        => new(instance.Factory.CreateNodeRepository(), instance.Factory.CreateEdgeRepository(), new InMemoryFileMetadataRepository(),
            Options.Create(new CodeContextOptions { RootPath = Root }), instance.Workers, AnalysisCoverageRegistry.ForStore(instance.Factory));

    /// <summary>Compact answers for the base method and a sibling override, without the cache label.</summary>
    private static async Task<List<string>> AnswersAsync(IContextService context, string? expectCache = null)
    {
        var answers = new List<string>();
        foreach (var identifier in new[] { Base, Sibling })
        {
            var answer = await context.GetCompactContextAsync(identifier, depth: 1, maxRelationships: 10);
            Assert.Equal(expectCache, answer.Cache?.State);
            answer.Cache = null;
            answers.Add(JsonSerializer.Serialize(answer, CodeContextJsonContext.Default.CompactContextResponse));
        }
        return answers;
    }
}
