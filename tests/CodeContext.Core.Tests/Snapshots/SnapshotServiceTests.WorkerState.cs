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

    /// <summary>
    /// The stop waits for a capture already requested. Here that capture's result is handled late, after the
    /// stop unless the stop waits for it; handled after a stop that did not wait, it would find the service
    /// stopped and delete the state file the final snapshot names.
    /// </summary>
    [Fact]
    public async Task StopWhileTheQuietCaptureIsSaving_WaitsForIt_SoItCannotDeleteAFileTheFinalSnapshotNames()
    {
        var database = new InMemoryDatabase();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saver = new StubStateSaver(database) { BeforeSave = _ => release.Task };
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver);
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));

        Task? stop = null;
        harness.Service.BeforeCaptureHandled = () => System.Threading.SpinWait.SpinUntil(() => stop?.IsCompleted == true, TimeSpan.FromSeconds(1));
        harness.Time.Advance(Quiet);
        await SnapshotServiceHarness.WaitUntilAsync(() => saver.Calls.Count == 1, "the quiet capture's save");
        stop = harness.Service.StopAsync(default);
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        await SnapshotServiceHarness.WaitUntilAsync(() => !harness.Service.IsCaptureRequested, "the quiet capture to be handled");

        var stateName = FakeShardInfo(harness).StateFileName;
        Assert.NotNull(stateName);
        Assert.True(File.Exists(Path.Combine(harness.WorkspacePath, stateName)), $"{stateName} is gone");
        Assert.Single(saver.Calls);
    }

    /// <summary>
    /// A stop that timed out while a capture was saving: that capture, handled after the stop, is not written, so
    /// it un-records the binding it recorded before deleting its file, and the store names no file that is gone.
    /// </summary>
    [Fact]
    public async Task CaptureHandledAfterAStopThatTimedOut_UnRecordsItsStateBeforeDeletingTheFile()
    {
        var database = new InMemoryDatabase();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saver = new StubStateSaver(database) { BeforeSave = _ => release.Task };
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver);
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: Produced(1)));

        harness.Time.Advance(Quiet);
        await SnapshotServiceHarness.WaitUntilAsync(() => saver.Calls.Count == 1, "the quiet capture's save");
        var stop = harness.Service.StopAsync(default);
        harness.Time.Advance(harness.Settings.ShutdownFlushTimeout);
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SnapshotCacheMode.Stopped, harness.Service.Status.Mode);

        release.SetResult();
        await SnapshotServiceHarness.WaitUntilAsync(() => saver.FilesWritten == 1, "the abandoned save to write its file");
        // The flag clears before the files are dropped, so the file's going is what says the capture was handled. The
        // binding must be gone by then: it is un-recorded first.
        await SnapshotServiceHarness.WaitUntilAsync(() => !harness.StateFiles().Any(), "the state file to be deleted");
        Assert.Null(database.CurrentShard(SnapshotServiceHarness.Shard).Annotation!.State);
        Assert.Empty(harness.Snapshots());
    }

    /// <summary>
    /// A stop during a scan returns at once even when a capture was requested before it and is queued behind the
    /// scan's long mutation: waiting for that capture would hold the stop for the whole flush timeout.
    /// </summary>
    [Fact]
    public async Task Shutdown_DuringAScan_DoesNotWaitForACaptureQueuedBehindAMutation()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync();

        using var hold = new System.Threading.ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Mutation = () =>
        {
            entered.TrySetResult();
            hold.Wait(TimeSpan.FromSeconds(30));
        };
        var mutation = harness.Coordinator.RefreshFileAsync(Path.Combine(harness.RootPath, "A.fake"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        harness.Time.Advance(Quiet);
        await SnapshotServiceHarness.WaitUntilAsync(() => harness.Service.IsCaptureRequested, "the quiet capture to be queued");
        Assert.True(harness.ScanState.TryBeginScan());

        // The clock does not move: only a stop that does not wait for the queued capture completes.
        await harness.Service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(harness.Snapshots());

        hold.Set();
        await mutation;
        harness.ScanState.CompleteScan();
    }

    private static async Task CommitAndWriteAsync(SnapshotServiceHarness harness, int request)
    {
        await harness.CommitAsync(() => harness.CommitFacts(request, annotation: Produced(request)));
        await harness.AdvanceAsync(Quiet);
    }

    [Fact]
    public async Task FailedWrites_DoNotPileUpStateFiles()
    {
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver,
            configure: settings => settings with { WrapStream = _ => new FailingStream() });
        await harness.StartAsync();

        for (var round = 1; round <= 5; round++) await CommitAndWriteAsync(harness, round);

        Assert.Equal(5, harness.Service.Status.WritesFailed);
        Assert.Equal(5, saver.Calls.Count);
        Assert.Empty(harness.Snapshots());
        // Only the newest save stays: the store's binding still names it.
        Assert.Equal([Path.GetFileName(saver.Calls[^1].Parameters.StatePath)], harness.StateFiles());
    }

    [Fact]
    public async Task AfterFailedWrites_ASuccessfulWriteAndARestartStillAcceptTheBaseline()
    {
        var basePath = NewBase();
        try
        {
            var failing = true;
            string stateName;
            var database = new InMemoryDatabase();
            var saver = new StubStateSaver(database);
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver,
                configure: settings => settings with { WrapStream = stream => System.Threading.Volatile.Read(ref failing) ? new FailingStream() : stream }))
            {
                await harness.StartAsync();
                for (var round = 1; round <= 5; round++) await CommitAndWriteAsync(harness, round);
                stateName = Path.GetFileName(saver.Calls[^1].Parameters.StatePath);

                // Another shard changes: the binding still describes the fake shard, so its file is written, not saved again.
                System.Threading.Volatile.Write(ref failing, false);
                await harness.CommitAsync(() => Assert.True(harness.Database.TryCommitGeneration(harness.Database.LastCommittedGeneration + 1,
                    [SnapshotServiceHarness.Node("O", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null))));
                await harness.AdvanceAsync(Quiet);

                Assert.Equal(1, harness.Service.Status.WritesCompleted);
                Assert.Equal(5, saver.Calls.Count);
                Assert.Equal(stateName, FakeShardInfo(harness).StateFileName);
                Assert.Equal([stateName], harness.StateFiles());
            }
            Assert.Equal(stateName, Path.GetFileName((await NextStartBaselineAsync(basePath))!.StatePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task FailedWrites_KeepTheStateFileOfTheSnapshotOnDisk()
    {
        var basePath = NewBase();
        try
        {
            var failing = false;
            string stateName;
            var database = new InMemoryDatabase();
            var saver = new StubStateSaver(database);
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: saver,
                configure: settings => settings with { WrapStream = stream => System.Threading.Volatile.Read(ref failing) ? new FailingStream() : stream }))
            {
                await harness.StartAsync();
                await CommitAndWriteAsync(harness, 1);
                stateName = FakeShardInfo(harness).StateFileName!;

                System.Threading.Volatile.Write(ref failing, true);
                for (var round = 2; round <= 5; round++) await CommitAndWriteAsync(harness, round);

                Assert.Equal(4, harness.Service.Status.WritesFailed);
                Assert.Equal(stateName, FakeShardInfo(harness).StateFileName);
                // The snapshot's file and the newest save, which the store's binding names.
                Assert.Equal(new[] { stateName, Path.GetFileName(saver.Calls[^1].Parameters.StatePath) }.Order(StringComparer.Ordinal), harness.StateFiles());
            }
            Assert.Equal(stateName, Path.GetFileName((await NextStartBaselineAsync(basePath))!.StatePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    /// <summary>A legacy in-place write moves the written shard off its anchor; the snapshot on disk still names the file.</summary>
    [Fact]
    public async Task FailedWrites_KeepTheSnapshotsStateFile_AfterAnInPlaceWriteMovedItsShardOffTheAnchor()
    {
        var basePath = NewBase();
        try
        {
            var failing = false;
            string stateName;
            var database = new InMemoryDatabase();
            await using (var harness = new SnapshotServiceHarness(basePath, database: database, workers: new StubStateSaver(database),
                configure: settings => settings with { WrapStream = stream => System.Threading.Volatile.Read(ref failing) ? new FailingStream() : stream }))
            {
                await harness.StartAsync();
                await CommitAndWriteAsync(harness, 1);
                stateName = FakeShardInfo(harness).StateFileName!;

                System.Threading.Volatile.Write(ref failing, true);
                harness.Database.UpsertNode(SnapshotServiceHarness.Node("Legacy"));
                for (var round = 2; round <= 3; round++) await CommitAndWriteAsync(harness, round);

                Assert.Equal(2, harness.Service.Status.WritesFailed);
                Assert.Contains(stateName, harness.StateFiles());
            }
            Assert.Equal(stateName, Path.GetFileName((await NextStartBaselineAsync(basePath))!.StatePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    /// <summary>The loaded view lacks a dropped shard, but the snapshot on disk names its state file, as the start-up cleanup knows.</summary>
    [Fact]
    public async Task FailedWrite_KeepsTheStateFileOfAShardDroppedAtLoad()
    {
        var basePath = NewBase();
        try
        {
            string stateName;
            var database = new InMemoryDatabase();
            await using (var first = new SnapshotServiceHarness(basePath, database: database, workers: new StubStateSaver(database)))
            {
                await first.StartAsync();
                await first.CommitAsync(() =>
                {
                    first.CommitFacts(1, annotation: Produced(1));
                    Assert.True(first.Database.TryCommitGeneration(first.Database.LastCommittedGeneration + 1,
                        [SnapshotServiceHarness.Node("O", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null)));
                });
                await first.AdvanceAsync(Quiet);
                stateName = FakeShardInfo(first).StateFileName!;
            }

            await using (var second = new SnapshotServiceHarness(basePath, isIndexedParser: parser => parser == Other.ParserId,
                configure: settings => settings with { WrapStream = _ => new FailingStream() }))
            {
                await second.StartAsync();
                await second.CommitAsync(() => Assert.True(second.Database.TryCommitGeneration(second.Database.LastCommittedGeneration + 1,
                    [SnapshotServiceHarness.Node("O2", key: Other)], [], new CommitScope(Other.ParserId, Other.WorkspaceId, null))));
                await second.AdvanceAsync(Quiet);

                Assert.Equal(1, second.Service.Status.WritesFailed);
                Assert.Equal([stateName], second.StateFiles());
            }
            Assert.Equal(stateName, Path.GetFileName((await NextStartBaselineAsync(basePath))!.StatePath));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    /// <summary>
    /// A write that failed verification and whose file could not be removed: the next start would try that file, so
    /// its state file outlives the binding, until a written snapshot retires both. Read-only stops a delete only on Windows.
    /// </summary>
    [Fact]
    public async Task FailedWrite_KeepsTheStateFileOfASnapshotItCouldNotRemove()
    {
        if (!OperatingSystem.IsWindows()) return;
        var mode = "leave a damaged file";
        var database = new InMemoryDatabase();
        var saver = new StubStateSaver(database);
        await using var harness = new SnapshotServiceHarness(database: database, workers: saver, configure: settings => settings with
        {
            WrapStream = stream => System.Threading.Volatile.Read(ref mode) == "full disk" ? new FailingStream() : stream,
            BeforeRename = temp =>
            {
                if (System.Threading.Volatile.Read(ref mode) != "leave a damaged file") return;
                var bytes = File.ReadAllBytes(temp);
                bytes[bytes.Length / 2] ^= 0xff;
                File.WriteAllBytes(temp, bytes);
                File.SetAttributes(temp, FileAttributes.ReadOnly);
            },
        });
        await harness.StartAsync();
        try
        {
            await CommitAndWriteAsync(harness, 1);
            Assert.Contains("does not open", harness.Service.Status.LastWriteError);
            var leftover = Assert.Single(harness.Snapshots());
            var named = Assert.Single(harness.StateFiles());

            // The store's binding moves on, and a failure's cleanup finds only that snapshot still naming the file.
            System.Threading.Volatile.Write(ref mode, "full disk");
            await CommitAndWriteAsync(harness, 2);
            Assert.Equal(2, harness.Service.Status.WritesFailed);
            Assert.Contains(named, harness.StateFiles());

            System.Threading.Volatile.Write(ref mode, "working");
            await CommitAndWriteAsync(harness, 3);
            Assert.NotEqual(leftover, Assert.Single(harness.Snapshots()));
            Assert.Equal(FakeShardInfo(harness).StateFileName, Assert.Single(harness.StateFiles()));
        }
        finally
        {
            // A failed assertion must not leave a read-only file that stops the harness deleting its directory.
            System.Threading.Volatile.Write(ref mode, "working");
            foreach (var snapshot in harness.Snapshots()) File.SetAttributes(Path.Combine(harness.WorkspacePath, snapshot), FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task FailedWrite_DeletesOnlyThisCodesStateFileNames()
    {
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with { WrapStream = _ => new FailingStream() });
        await harness.StartAsync();
        string[] foreign = ["worker-notes.txt", "worker-fake.0000000001.abcdefgh.state", "worker-fake.1.abcdef01.state", "worker-.0000000001.abcdef01.state"];
        foreach (var name in foreign) File.WriteAllText(Path.Combine(harness.WorkspacePath, name), "not ours");
        var orphan = WorkerStateFile.FormatName("fake", 99, new string('a', WorkerStateFile.StateIdLength))!;
        File.WriteAllText(Path.Combine(harness.WorkspacePath, orphan), "ours");

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);

        Assert.Equal(1, harness.Service.Status.WritesFailed);
        Assert.Equal(foreign.Order(StringComparer.Ordinal), harness.StateFiles());
    }

    /// <summary>A write that outlives a timed-out stop may no longer hold the lock, so its failure deletes nothing: the file may be a newer instance's.</summary>
    [Fact]
    public async Task FailedWrite_AfterAStopThatTimedOut_DeletesNoStateFile()
    {
        using var release = new System.Threading.ManualResetEventSlim();
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            BeforeRename = _ =>
            {
                release.Wait(TimeSpan.FromSeconds(20));
                throw new IOException("the rename failed");
            },
        });
        await harness.StartAsync();
        var orphan = WorkerStateFile.FormatName("fake", 99, new string('a', WorkerStateFile.StateIdLength))!;
        File.WriteAllText(Path.Combine(harness.WorkspacePath, orphan), "a newer instance");
        await harness.CommitAsync();

        var stop = harness.Service.StopAsync(default);
        await SnapshotServiceHarness.WaitUntilAsync(() => harness.TempFiles().Count == 1, "the final write to start");
        harness.Time.Advance(harness.Settings.ShutdownFlushTimeout);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SnapshotCacheMode.Stopped, harness.Service.Status.Mode);

        release.Set();
        await harness.Service.CurrentWrite;
        Assert.Equal(1, harness.Service.Status.WritesFailed);
        Assert.Equal([orphan], harness.StateFiles());
    }

    private static string NewBase() => Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
}
