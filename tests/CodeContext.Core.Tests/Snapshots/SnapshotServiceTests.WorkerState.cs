using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// Capture step 1 (plan 3.5, phase 5): the workers' state saves before the latch, what a capture records of
/// them, how state files follow their snapshot, and each way the pairing between a shard and a worker's state
/// breaks. After a break the capture writes the shard without state and the next start offers no baseline.
/// The worker is a stub here (<see cref="StubStateSaver"/>); the session half of the pairing proof is tested with
/// the real fake worker in <c>PersistentStateTests</c>.
/// </summary>
public partial class SnapshotServiceTests
{
    private static readonly ShardKey Other = new("other", "default");

    /// <summary>What a capable worker's commit carries: fingerprints, and the lineage of its process's request.</summary>
    private static ShardAnnotation Produced(long request, bool establishes = true) => new("1.0", null)
    {
        Fingerprints = [new UnitFingerprint("default", "fp")],
        Lineage = new ShardLineage(Incarnation: 7, request, establishes),
    };

    private static SnapshotLoadResult LoadNewest(SnapshotServiceHarness harness)
    {
        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single()), harness.Key(), harness.Expectations());
        Assert.True(loaded.Loaded, loaded.Reason);
        return loaded;
    }

    private static SnapshotShardInfo FakeShardInfo(SnapshotServiceHarness harness)
        => LoadNewest(harness).Shards.Single(shard => shard.Key == SnapshotServiceHarness.Shard).Info;

    /// <summary>A new start on the same root: the baseline its first index of the fake worker would be offered.</summary>
    private static async Task<IndexBaseline?> NextStartBaselineAsync(string basePath)
    {
        await using var next = new SnapshotServiceHarness(basePath);
        await next.StartAsync();
        return new WorkerBaselines(next.Database, () => next.Startup.Directory).TryGetBaseline("fake", "default", "1.0");
    }

    [Fact]
    public async Task Capture_SavesWorkerStateBeforeLatchingTheStore()
    {
        var basePath = NewBase();
        try
        {
            string stateName;
            var database = new InMemoryDatabase();
            var saver = new StubStateSaver(database);
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver))
            {
                await harness.StartAsync();
                await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));
                await harness.AdvanceAsync(Quiet);

                // Asked once, before the latch: the store held no state then, and the snapshot pairs the shard with it.
                var call = Assert.Single(saver.Calls);
                Assert.Null(call.AnnotationAtSave!.State);
                Assert.Equal(new ShardLineage(7, 1, true), call.Lineage);
                stateName = Path.GetFileName(call.Parameters.StatePath);
                Assert.Equal(harness.WorkspacePath, Path.GetDirectoryName(call.Parameters.StatePath));
                Assert.True(WorkerStateFile.TryParseName(stateName, out var parser, out var sequence));
                Assert.Equal("fake", parser);
                Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var snapshotSequence));
                Assert.Equal(snapshotSequence, sequence);

                var info = FakeShardInfo(harness);
                Assert.Equal(stateName, info.StateFileName);
                Assert.Equal(call.Parameters.StateId, info.StateId);
                Assert.True(WorkerStateFile.IsSha256(info.StateSha256));
                Assert.Equal("[{\"unit\":\"default\",\"value\":\"fp\"}]", System.Text.Encoding.UTF8.GetString(info.Fingerprints!));
                Assert.Equal(new FileInfo(call.Parameters.StatePath).Length, harness.Service.Status.LastWorkerStateBytes);
                Assert.Equal([stateName], harness.StateFiles());
            }

            // The next start offers it: the loaded shard, its parser version, id, hash, fingerprints and file.
            var baseline = await NextStartBaselineAsync(basePath);
            Assert.NotNull(baseline);
            Assert.Equal(stateName, Path.GetFileName(baseline.StatePath));
            Assert.Equal("fp", Assert.Single(baseline.Fingerprints).Value);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Theory]
    [InlineData("Fail")]
    [InlineData("Null")]
    [InlineData("WrongId")]
    [InlineData("WrongLength")]
    public async Task FailedSave_WritesTheShardWithoutState(string mode)
    {
        var basePath = NewBase();
        try
        {
            var database = new InMemoryDatabase();
            var saver = new StubStateSaver(database) { Behaviour = Enum.Parse<StubStateSaver.Mode>(mode) };
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver))
            {
                await harness.StartAsync();
                await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));
                await harness.AdvanceAsync(Quiet);

                Assert.Single(saver.Calls);
                Assert.Equal(1, harness.Service.Status.WritesCompleted);
                Assert.Null(FakeShardInfo(harness).StateFileName);
                Assert.NotNull(FakeShardInfo(harness).Fingerprints);
                // A file the worker did write is not left behind.
                Assert.Empty(harness.StateFiles());
                Assert.Null(harness.Service.Status.LastWorkerStateBytes);
            }
            Assert.Null(await NextStartBaselineAsync(basePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task HungSave_IsGivenUpAfterTheTimeout_AndTheShardIsWrittenWithoutState()
    {
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database) { Behaviour = StubStateSaver.Mode.Hang };
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver,
            configure: settings => settings with { SaveStateTimeout = TimeSpan.FromSeconds(5) });
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));

        harness.Time.Advance(Quiet);
        await SnapshotServiceHarness.WaitUntilAsync(() => saver.Calls.Count == 1, "the save to start");
        // The coordinator is held only for the save, and only until the time limit.
        Assert.False(harness.Coordinator.IsBusy);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await harness.IdleAsync();

        Assert.Equal(1, harness.Service.Status.WritesCompleted);
        Assert.Null(FakeShardInfo(harness).StateFileName);
    }

    [Fact]
    public async Task StateFiles_AreRetiredWithTheirSnapshot()
    {
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver);
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));
        await harness.AdvanceAsync(Quiet);
        var first = Assert.Single(harness.StateFiles());

        // New facts from the same process: a new state file with the new snapshot; the old pair is retired.
        await harness.CommitAsync(() => harness.CommitFacts(2, annotation: Produced(2)));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, saver.Calls.Count);
        var second = Assert.Single(harness.StateFiles());
        Assert.NotEqual(first, second);
        Assert.Single(harness.Snapshots());
        Assert.Equal(second, FakeShardInfo(harness).StateFileName);

        // A shard that did not change keeps its file, which is not saved again, when another shard is written.
        await harness.CommitAsync(() => Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1,
            [SnapshotServiceHarness.Node("O", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null))));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, saver.Calls.Count);
        Assert.Equal([second], harness.StateFiles());
        Assert.Equal(second, FakeShardInfo(harness).StateFileName);
        Assert.Equal(3, harness.Service.Status.WritesCompleted);
    }

    [Fact]
    public async Task ShutdownCapture_SavesState_AndACaptureAfterStopAsksNoWorker()
    {
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver);
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));

        // The final capture on a graceful stop saves the worker's state like any other.
        await harness.Service.StopAsync(default);
        Assert.Single(saver.Calls);
        Assert.Single(harness.Snapshots());
        Assert.Equal(FakeShardInfo(harness).StateFileName, Assert.Single(harness.StateFiles()));

        harness.CommitFacts(2, annotation: Produced(2));
        Assert.NotNull(await harness.Service.CaptureAsync(default));
        Assert.Single(saver.Calls);
    }

    [Fact]
    public async Task StopWhileTheQuietCaptureIsSaving_TheFinalSnapshotUsesThatSave_AndNamesAFileThatExists()
    {
        var basePath = NewBase();
        try
        {
            var database = new InMemoryDatabase();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saver = new StubStateSaver(database) { BeforeSave = _ => release.Task };
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver))
            {
                await harness.StartAsync();
                await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));

                // The quiet capture starts and its worker is saving when the stop comes.
                harness.Time.Advance(Quiet);
                await SnapshotServiceHarness.WaitUntilAsync(() => saver.Calls.Count == 1, "the quiet capture's save");
                var stop = harness.Service.StopAsync(default);
                release.SetResult();
                await stop;

                // The final snapshot pairs the shard with the file that save wrote, which is there; nothing was saved twice.
                Assert.Single(saver.Calls);
                Assert.Equal(1, harness.Service.Status.WritesCompleted);
                var stateName = FakeShardInfo(harness).StateFileName!;
                Assert.Equal(Path.GetFileName(saver.Calls[0].Parameters.StatePath), stateName);
                Assert.True(File.Exists(Path.Combine(harness.WorkspacePath, stateName)));
                Assert.Equal([stateName], harness.StateFiles());
            }
            Assert.NotNull(await NextStartBaselineAsync(basePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureThatIsNotTaken_UnRecordsTheStateItSaved_BeforeDeletingItsFile()
    {
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver);
        await harness.StartAsync();
        harness.CommitFacts(1, annotation: Produced(1));
        Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, [SnapshotServiceHarness.Node("O", key: Other)], [],
            new CommitScope(Other.ParserId, Other.WorkspaceId, null)
            {
                Annotation = new ShardAnnotation("1.0", null) { Fingerprints = [new UnitFingerprint("default", "o")], Lineage = new ShardLineage(8, 1, true) },
            }));

        // The fake shard's save is recorded; then a reconciliation is staged during the other's, so the latch is refused.
        saver.BeforeSave = parserId =>
        {
            if (parserId == Other.ParserId) database.BeginReconciliation();
            return Task.CompletedTask;
        };
        Assert.Null(await harness.Service.CaptureAsync(default));
        database.RollbackReconciliation();
        Assert.Equal(2, saver.Calls.Count);

        // Nothing names the deleted file: the binding went with it, so the next capture asks again.
        var (shard, annotation) = database.CurrentShard(SnapshotServiceHarness.Shard);
        Assert.Null(annotation!.State);
        Assert.NotNull(annotation.LineageFor(shard));
        Assert.Empty(harness.StateFiles());

        saver.BeforeSave = null;
        var capture = await harness.Service.CaptureAsync(default);
        Assert.NotNull(capture);
        Assert.Equal(4, saver.Calls.Count);
        var states = capture.View.Shards.Select(view => view.State).OfType<WorkerStateBinding>().ToList();
        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.True(File.Exists(Path.Combine(harness.WorkspacePath, state.FileName))));
    }

    /// <summary>Commits facts with a lineage (and the edge <paramref name="setUp"/> returns), writes, applies <paramref name="breakIt"/> through the coordinator, and writes again.</summary>
    private static async Task AssertBreakWritesNoStateAsync(Action<SnapshotServiceHarness> breakIt, Func<SnapshotServiceHarness, CodeEdge?>? setUp = null)
    {
        var basePath = NewBase();
        try
        {
            var database = new InMemoryDatabase();
            var saver = new StubStateSaver(database);
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver))
            {
                await harness.StartAsync();
                await harness.CommitAsync(() =>
                {
                    var edge = setUp?.Invoke(harness);
                    var nodes = Enumerable.Range(0, 3).Select(i => SnapshotServiceHarness.Node($"N1_{i}")).ToList();
                    Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1, nodes,
                        edge is null ? [] : [edge], new CommitScope("fake", "default", null) { Annotation = Produced(1) }));
                });
                await harness.AdvanceAsync(Quiet);
                Assert.NotNull(FakeShardInfo(harness).StateFileName);

                await harness.CommitAsync(() => breakIt(harness));
                await harness.AdvanceAsync(Quiet);
                Assert.Equal(2, harness.Service.Status.WritesCompleted);
                Assert.Single(saver.Calls);
                Assert.Null(FakeShardInfo(harness).StateFileName);
                Assert.Empty(harness.StateFiles());
            }
            Assert.Null(await NextStartBaselineAsync(basePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public Task ForeignSweep_BreaksThePairing()
        => AssertBreakWritesNoStateAsync(
            setUp: harness =>
            {
                // The other shard's node, which an edge of the fake shard points to (committed after this).
                var target = SnapshotServiceHarness.Node("Target", key: Other);
                Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1,
                    [target], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null)));
                return new CodeEdge
                {
                    Id = "fake:default:N1_0->uses->" + target.Id,
                    SourceId = "fake:default:N1_0",
                    TargetId = target.Id,
                    Type = "USES",
                    Metadata = SnapshotTestSupport.Owned(SnapshotServiceHarness.Shard),
                };
            },
            breakIt: harness =>
            {
                // The other shard drops it: the fake shard's edge to it is swept, a new reference, the same annotation.
                Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1,
                    [SnapshotServiceHarness.Node("Else", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null)));
            });

    [Fact]
    public Task Prune_BreaksThePairing()
        => AssertBreakWritesNoStateAsync(harness =>
        {
            var ghost = SnapshotServiceHarness.Node("Ghost");
            ghost.FilePath = "Ghost.fake";
            // A fact of a file no longer present, carried by a commit without an annotation, then pruned.
            Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1, [ghost], [],
                new CommitScope("fake", "default", ["Ghost.fake"])));
            Assert.True(harness.Database.PruneFilesNotPresent(["A.fake"]) > 0);
        });

    [Fact]
    public Task LegacyInPlaceWrite_BreaksThePairing()
        => AssertBreakWritesNoStateAsync(harness => harness.Database.UpsertNode(SnapshotServiceHarness.Node("Legacy")));

    [Fact]
    public Task CommitThatDoesNotFollowTheLineage_BreaksThePairing()
        // Request 3 after request 1: request 2 failed or was rolled back, so the worker holds what the store does not.
        => AssertBreakWritesNoStateAsync(harness => harness.CommitFacts(9, annotation: Produced(3, establishes: false)));

    [Fact]
    public async Task LoadedShard_KeepsItsStateOnlyWhileItIsTheLoadedReference()
    {
        var basePath = NewBase();
        try
        {
            string stateName;
            var database = new InMemoryDatabase();
            await using (var first = new SnapshotServiceHarness(basePath, database: database, workers: new StubStateSaver(database)))
            {
                await first.StartAsync();
                await first.CommitAsync(() => first.CommitFacts(1, annotation: Produced(1)));
                await first.AdvanceAsync(Quiet);
                stateName = FakeShardInfo(first).StateFileName!;
            }

            // Loaded, never verified, and another shard changes: the snapshot written carries the loaded pairing.
            var secondSaver = new StubStateSaver(new InMemoryDatabase());
            await using (var second = new SnapshotServiceHarness(basePath, workers: secondSaver))
            {
                await second.StartAsync();
                Assert.True(second.Database.NodeCount > 0);
                await second.CommitAsync(() => Assert.True(second.Database.TryCommitGeneration(second.Database.LastCommittedGeneration + 1,
                    [SnapshotServiceHarness.Node("O", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null))));
                await second.AdvanceAsync(Quiet);
                Assert.Empty(secondSaver.Calls);
                Assert.Equal(stateName, FakeShardInfo(second).StateFileName);
                Assert.Equal([stateName], second.StateFiles());
            }
            Assert.Equal(stateName, Path.GetFileName((await NextStartBaselineAsync(basePath))!.StatePath));

            // Loaded, then changed by a commit that no process of this run established (a watcher batch while the
            // verification failed): the snapshot written carries no state, and the next start offers none.
            await using (var third = new SnapshotServiceHarness(basePath, workers: new StubStateSaver(new InMemoryDatabase())))
            {
                await third.StartAsync();
                await third.CommitAsync(() => third.CommitFacts(5, annotation: Produced(4, establishes: false)));
                await third.AdvanceAsync(Quiet);
                Assert.Null(FakeShardInfo(third).StateFileName);
                Assert.Empty(third.StateFiles());
            }
            Assert.Null(await NextStartBaselineAsync(basePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyInstance_SavesNoState()
    {
        await using var writer = new SnapshotServiceHarness();
        await writer.StartAsync();
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var reader = new SnapshotServiceHarness(writer.BasePath, database: database, workers: saver);
        await reader.StartAsync();
        Assert.Equal(SnapshotCacheMode.ReadOnly, reader.Service.Status.Mode);
        database.TryCommitGeneration(1, [SnapshotServiceHarness.Node("X")], [], new CommitScope("fake", "default", null) { Annotation = Produced(1) });
        Assert.NotNull(await reader.Service.CaptureAsync(default));
        Assert.Empty(saver.Calls);
    }

    private static string NewBase() => Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
}
