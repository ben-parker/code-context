using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The store half of the worker-state pairing (plan phase 5): how a commit's annotation carries fingerprints, a
/// lineage and a state file, and every way the store voids them. The annotation is staged and rolled back with
/// a reconciliation; the anchor ties lineage and state to one shard reference and heap version.
/// </summary>
public class WorkerStatePairingStoreTests
{
    private static readonly ShardKey A = new("fake", "ws-1");
    private static readonly ShardKey B = new("other", "ws-1");

    private static CodeNode Node(ShardKey key, string name, string file = "a.fake") => new()
    {
        Id = $"{key.ParserId}:{key.WorkspaceId}:{name}",
        Identifier = $"{key.ParserId}:{name}",
        Name = name,
        Type = "Class",
        FilePath = file,
        Metadata = new Dictionary<string, string> { ["parserId"] = key.ParserId, ["workspaceId"] = key.WorkspaceId },
    };

    private static ShardAnnotation Produced(long incarnation, long request, bool establishes) => new("1.0", null)
    {
        Fingerprints = [new UnitFingerprint("u", "v")],
        Lineage = new ShardLineage(incarnation, request, establishes),
    };

    /// <summary>The annotation of a commit answering a baseline that offered <paramref name="offeredStateId"/>, accepted.</summary>
    private static ShardAnnotation Accepted(long incarnation, long request, string offeredStateId) => Produced(incarnation, request, establishes: true) with
    {
        Lineage = new ShardLineage(incarnation, request, Establishes: true, BaselineStateId: offeredStateId),
    };

    private static void Commit(InMemoryDatabase database, ShardKey key, IReadOnlyList<CodeNode> nodes, ShardAnnotation? annotation,
        IReadOnlyCollection<string>? files = null)
        => Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1, nodes, [],
            new CommitScope(key.ParserId, key.WorkspaceId, files) { Annotation = annotation }));

    private static (IGraphShard Shard, ShardAnnotation? Annotation) Latched(InMemoryDatabase database, ShardKey key)
    {
        Assert.True(database.TryLatchCommitted(out var shards, out var annotations, out _));
        annotations.TryGetValue(key, out var annotation);
        return (shards[key], annotation);
    }

    private static WorkerStateBinding Binding() => new("worker-fake.0000000001.aaaaaaaa.state", new string('a', 32), new string('b', 64), 5);

    /// <summary>Records a state file with the published shard, as a capture does.</summary>
    private static void RecordState(InMemoryDatabase database, ShardKey key)
    {
        var (shard, annotation) = Latched(database, key);
        Assert.True(database.TryRecordWorkerState(key, shard, annotation!, Binding()));
    }

    [Fact]
    public void Lineage_StartsAtAnEstablishingCommit_AndExtendsOnlyByTheSameProcessNextRequest()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        var (shard, annotation) = Latched(database, A);
        Assert.Equal(new ShardLineage(1, 1, true), annotation!.LineageFor(shard));

        Commit(database, A, [Node(A, "V2", "b.fake")], Produced(1, 2, establishes: false), ["b.fake"]);
        (shard, annotation) = Latched(database, A);
        Assert.Equal(2, annotation!.LineageFor(shard)!.Request);

        // A gap (request 3 failed or was rolled back): no lineage, and none until a new establishing commit.
        Commit(database, A, [Node(A, "V4", "c.fake")], Produced(1, 4, establishes: false), ["c.fake"]);
        Assert.Null(Latched(database, A).Annotation!.Lineage);
        Commit(database, A, [Node(A, "V5", "c.fake")], Produced(1, 5, establishes: false), ["c.fake"]);
        Assert.Null(Latched(database, A).Annotation!.Lineage);

        // Another process: its next request does not extend this one's lineage.
        Commit(database, A, [Node(A, "V6")], Produced(1, 6, establishes: true));
        Commit(database, A, [Node(A, "V7", "c.fake")], Produced(2, 7, establishes: false), ["c.fake"]);
        Assert.Null(Latched(database, A).Annotation!.Lineage);
    }

    [Fact]
    public void Lineage_RolledBackWithAReconciliation_LeavesAGap()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        database.BeginReconciliation();
        Commit(database, A, [Node(A, "V2")], Produced(1, 2, establishes: true));
        database.RollbackReconciliation();

        // The published lineage is request 1 again, while the process has done request 2.
        var (shard, annotation) = Latched(database, A);
        Assert.Equal(1, annotation!.LineageFor(shard)!.Request);
        Assert.False(WorkerStatePairing.Holds(annotation.LineageFor(shard), 1, lastRequest: 2, live: true, declaresPersistentState: true));
        Commit(database, A, [Node(A, "V3", "b.fake")], Produced(1, 3, establishes: false), ["b.fake"]);
        Assert.Null(Latched(database, A).Annotation!.Lineage);
    }

    [Fact]
    public void Pairing_HoldsOnlyForTheLiveCapableProcessAtItsLastRequest()
    {
        var lineage = new ShardLineage(4, 9, true);
        Assert.True(WorkerStatePairing.Holds(lineage, 4, 9, live: true, declaresPersistentState: true));
        Assert.False(WorkerStatePairing.Holds(null, 4, 9, true, true));
        Assert.False(WorkerStatePairing.Holds(lineage, 5, 9, true, true));
        Assert.False(WorkerStatePairing.Holds(lineage, 4, 10, true, true));
        Assert.False(WorkerStatePairing.Holds(lineage, 4, 9, live: false, declaresPersistentState: true));
        Assert.False(WorkerStatePairing.Holds(lineage, 4, 9, live: true, declaresPersistentState: false));
    }

    [Fact]
    public void ForeignSweepPruneAndLegacyWrite_VoidThePairing_ButKeepTheAnnotation()
    {
        foreach (var change in new Action<InMemoryDatabase>[]
                 {
                     database => Assert.True(database.PruneFilesNotPresent(["a.fake"]) > 0),
                     database => database.UpsertNode(Node(A, "Legacy")),
                 })
        {
            var database = new InMemoryDatabase();
            Commit(database, A, [Node(A, "V1"), Node(A, "Gone", "gone.fake")], Produced(1, 1, establishes: true));
            RecordState(database, A);
            var before = Latched(database, A).Annotation;
            change(database);
            var (shard, annotation) = Latched(database, A);
            Assert.Same(before, annotation);
            Assert.Null(annotation!.LineageFor(shard));
            Assert.Null(annotation.StateFor(shard));
        }
    }

    [Fact]
    public void State_StaysOnlyWithAnUnchangedShardAndAFileScopedCommit()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        RecordState(database, A);

        // A baseline confirmation (file-scoped, nothing changed): the state still describes the shard.
        Commit(database, A, [], Accepted(2, 2, Binding().StateId), []);
        var (shard, annotation) = Latched(database, A);
        Assert.NotNull(annotation!.StateFor(shard));
        Assert.Equal(new ShardLineage(2, 2, true, Binding().StateId), annotation.LineageFor(shard));

        // A fact change drops it.
        Commit(database, A, [Node(A, "V2", "b.fake")], Produced(2, 3, establishes: false), ["b.fake"]);
        (shard, annotation) = Latched(database, A);
        Assert.Null(annotation!.StateFor(shard));
        Assert.NotNull(annotation.LineageFor(shard));

        // A replacement drops it even when it changes no fact.
        RecordState(database, A);
        Commit(database, A, Latched(database, A).Shard.GetAllNodes().ToList(), Produced(2, 4, establishes: true));
        (shard, annotation) = Latched(database, A);
        Assert.Null(annotation!.StateFor(shard));
    }

    [Fact]
    public void State_IsDroppedByAFileScopedCommitThatChangesNoFactButMovesTheFingerprintsOrTheParserVersion()
    {
        foreach (var moved in new Func<ShardAnnotation, ShardAnnotation>[]
                 {
                     produced => produced with { Fingerprints = [new UnitFingerprint("u", "moved")] },
                     produced => produced with { Fingerprints = [new UnitFingerprint("u", "v"), new UnitFingerprint("w", "v")] },
                     produced => produced with { Fingerprints = null },
                     produced => new ShardAnnotation("2.0", produced.Coverage) { Fingerprints = produced.Fingerprints, Lineage = produced.Lineage },
                 })
        {
            var database = new InMemoryDatabase();
            Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
            RecordState(database, A);
            var shardBefore = Latched(database, A).Shard;

            // A whitespace edit or a project-file change that alters no fact: no fact replaced, other fingerprints.
            Commit(database, A, [], moved(Produced(1, 2, establishes: false)), ["fake.proj"]);
            var (shard, annotation) = Latched(database, A);
            Assert.Same(shardBefore, shard);
            Assert.Null(annotation!.StateFor(shard));
            // The lineage still holds, so the next capture saves a state that matches the new fingerprints.
            Assert.Equal(2, annotation.LineageFor(shard)!.Request);
        }

        // Equal fingerprints by value, in a new list, and the same parser version: the state stays.
        var same = new InMemoryDatabase();
        Commit(same, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        RecordState(same, A);
        Commit(same, A, [], Produced(1, 2, establishes: false), ["fake.proj"]);
        var (sameShard, sameAnnotation) = Latched(same, A);
        Assert.Equal(Binding(), sameAnnotation!.StateFor(sameShard));
    }

    [Fact]
    public void AcceptedBaseline_StartsALineageOnlyWhileTheShardCarriesTheOfferedState()
    {
        // The recorded state, offered and still carried: the lineage starts.
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        RecordState(database, A);
        Commit(database, A, [], Accepted(2, 2, Binding().StateId), []);
        Assert.Equal(new ShardLineage(2, 2, true, Binding().StateId), Latched(database, A).Annotation!.Lineage);

        foreach (var change in new Action<InMemoryDatabase>[]
                 {
                     // Another state id recorded since the offer.
                     database => Assert.True(database.TryRecordWorkerState(A, Latched(database, A).Shard, Latched(database, A).Annotation!,
                         Binding() with { StateId = new string('c', 32) })),
                     // The shard moved off the state's anchor (a legacy in-place write).
                     database => database.UpsertNode(Node(A, "Legacy")),
                     // The state un-recorded.
                     database => database.ClearWorkerState(A, Latched(database, A).Annotation!.State!),
                 })
        {
            database = new InMemoryDatabase();
            Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
            RecordState(database, A);
            change(database);

            // Applied as an ordinary file-scoped commit: the facts commit, but no lineage starts, so nothing is saved.
            Commit(database, A, [Node(A, "V2", "b.fake")], Accepted(2, 2, Binding().StateId), ["b.fake"]);
            var (shard, annotation) = Latched(database, A);
            Assert.Contains(shard.GetAllNodes(), node => node.Name == "V2");
            Assert.Null(annotation!.LineageFor(shard));
            Assert.Null(annotation.Lineage);
        }

        // A shard that never had a recorded state: the same.
        var fresh = new InMemoryDatabase();
        Commit(fresh, A, [Node(A, "V1")], new ShardAnnotation("1.0", null) { Fingerprints = [new UnitFingerprint("u", "v")] });
        Commit(fresh, A, [], Accepted(2, 2, Binding().StateId), []);
        Assert.Null(Latched(fresh, A).Annotation!.Lineage);

        // A replacement establishes whatever the shard was.
        Commit(fresh, A, [Node(A, "V3")], Accepted(2, 3, Binding().StateId));
        Assert.NotNull(Latched(fresh, A).Annotation!.Lineage);
    }

    [Fact]
    public void ClearWorkerState_UnRecordsOnlyThatVeryBinding()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        RecordState(database, A);
        var recorded = Latched(database, A).Annotation!.State!;

        // An equal binding that is not the recorded instance is not the one to clear.
        database.ClearWorkerState(A, recorded with { });
        Assert.Same(recorded, Latched(database, A).Annotation!.State);

        var generation = database.LastCommittedGeneration;
        database.ClearWorkerState(A, recorded);
        var (shard, annotation) = Latched(database, A);
        Assert.Null(annotation!.State);
        // Not a commit, and the lineage stays, so a later capture can save again.
        Assert.Equal(generation, database.LastCommittedGeneration);
        Assert.NotNull(annotation.LineageFor(shard));
    }

    [Fact]
    public void RecordWorkerState_OnlyForTheExpectedShardAndAnnotation_AndNeverWhileStaged()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        var (shard, annotation) = Latched(database, A);

        Commit(database, A, [Node(A, "V2")], Produced(1, 2, establishes: true));
        Assert.False(database.TryRecordWorkerState(A, shard, annotation!, Binding()));

        (shard, annotation) = Latched(database, A);
        database.BeginReconciliation();
        Assert.False(database.TryRecordWorkerState(A, shard, annotation!, Binding()));
        database.RollbackReconciliation();
        var generation = database.LastCommittedGeneration;
        Assert.True(database.TryRecordWorkerState(A, shard, annotation!, Binding()));
        Assert.Equal(Binding(), Latched(database, A).Annotation!.StateFor(shard));
        // Not a commit: the facts and the generation stay.
        Assert.Same(shard, Latched(database, A).Shard);
        Assert.Equal(generation, database.LastCommittedGeneration);

        // A shard without a lineage has no anchor, so nothing is recorded with it.
        Commit(database, B, [Node(B, "W1")], new ShardAnnotation("w1", null));
        var (other, otherAnnotation) = Latched(database, B);
        Assert.False(database.TryRecordWorkerState(B, other, otherAnnotation!, Binding()));
    }

    [Fact]
    public void ShardSwap_CarriesThePairingToTheEquivalentShard()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], Produced(1, 1, establishes: true));
        RecordState(database, A);
        var (shard, _) = Latched(database, A);
        var copy = InMemoryDatabase.GraphState.CopyOf(shard);
        Assert.True(database.TryReplaceShard(A, shard, copy));
        var annotation = Latched(database, A).Annotation!;
        Assert.NotNull(annotation.StateFor(copy));
        Assert.NotNull(annotation.LineageFor(copy));
    }

    [Fact]
    public void Fingerprints_AreTheCommitsOwn_AndCompareByValueInTheView()
    {
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", null) { Fingerprints = [new("u", "1")] });
        var (shards, annotations) = database.LatchPublished();
        var before = SnapshotView.Of(shards, annotations);

        // Equal fingerprints in a new list, and a new lineage: nothing a snapshot holds changed.
        Commit(database, A, [], new ShardAnnotation("v1", null) { Fingerprints = [new("u", "1")], Lineage = new ShardLineage(3, 1, true) }, []);
        (shards, annotations) = database.LatchPublished();
        Assert.True(before.SameAs(SnapshotView.Of(shards, annotations)));

        Commit(database, A, [], new ShardAnnotation("v1", null) { Fingerprints = [new("u", "2")] }, []);
        (shards, annotations) = database.LatchPublished();
        Assert.False(before.SameAs(SnapshotView.Of(shards, annotations)));

        // A commit without fingerprints clears them.
        Commit(database, A, [], new ShardAnnotation("v1", null), []);
        Assert.Null(Latched(database, A).Annotation!.Fingerprints);
    }

    [Fact]
    public async Task PublishedCoverage_DoesNotWaitForACommitInProgress()
    {
        static List<ProjectCoverage> Coverage(string name) => [new(name, null, ProjectCoverageStates.Complete, 1)];
        var database = new InMemoryDatabase();
        Commit(database, A, [Node(A, "V1")], new ShardAnnotation("v1", Coverage("One")));
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        database.InsideCommitLock = () =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        };
        var commit = Task.Run(() => Commit(database, A, [Node(A, "V2")], new ShardAnnotation("v2", Coverage("Two"))));
        try
        {
            Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
            // The commit holds the lock: the coverage read returns at once, with the published coverage.
            // (A read that waited would time out here, since the commit cannot finish until released.)
            var read = await Task.Run(() => database.PublishedCoverage()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("One", read.Single().Name);
        }
        finally
        {
            release.Set();
        }
        await commit;
        database.InsideCommitLock = null;
        Assert.Equal("Two", database.PublishedCoverage().Single().Name);
    }
}
