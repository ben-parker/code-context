using System.Collections.Immutable;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// One shard as a snapshot sees it: the published reference, the annotation the store recorded for
    /// it (parser version, coverage, fingerprints, the paired worker state) and, for a heap shard, its
    /// in-place version (bumped only by a legacy in-place write).
    /// </summary>
    internal readonly record struct SnapshotShardView(
        ShardKey Key, IGraphShard Shard, ShardAnnotation? Annotation, long HeapVersion)
    {
        /// <summary>The worker state file a snapshot of this shard records: the annotation's, only while it describes this shard.</summary>
        public WorkerStateBinding? State => Annotation?.StateFor(Shard);

        public bool SameAs(SnapshotShardView other)
            => Key == other.Key
                && ReferenceEquals(Shard, other.Shard)
                && HeapVersion == other.HeapVersion
                && ShardAnnotation.SamePersisted(Annotation, other.Annotation, Shard);
    }

    /// <summary>
    /// What a snapshot would contain: every shard with at least one fact except the un-owned
    /// <see cref="ShardKey.Default"/> one (never persisted), in shard key order. A shard whose facts were all
    /// removed is absent, so an empty key is not a change. Two views are the same when every shard reference
    /// and heap version is, and every annotation holds the same persisted values
    /// (<see cref="ShardAnnotation.SamePersisted"/>): a commit that changes no fact keeps every reference, and
    /// one that confirms what is recorded (a baseline the worker accepted with nothing to change, a rescan
    /// with an equal coverage report) changes nothing a snapshot holds, so it leaves the view, and the store,
    /// clean.
    /// </summary>
    internal sealed class SnapshotView
    {
        public static readonly SnapshotView Empty = new([]);

        private SnapshotView(IReadOnlyList<SnapshotShardView> shards) => Shards = shards;

        public IReadOnlyList<SnapshotShardView> Shards { get; }

        public static SnapshotView Of(
            IEnumerable<KeyValuePair<ShardKey, IGraphShard>> publishedInKeyOrder,
            ImmutableSortedDictionary<ShardKey, ShardAnnotation> annotations)
        {
            var shards = new List<SnapshotShardView>();
            foreach (var (key, shard) in publishedInKeyOrder)
            {
                if (key == ShardKey.Default || (shard.NodeCount == 0 && shard.EdgeCount == 0)) continue;
                annotations.TryGetValue(key, out var annotation);
                shards.Add(new SnapshotShardView(key, shard, annotation, (shard as InMemoryDatabase.GraphState)?.Version ?? 0));
            }
            return new SnapshotView(shards);
        }

        public bool SameAs(SnapshotView other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (Shards.Count != other.Shards.Count) return false;
            for (var i = 0; i < Shards.Count; i++)
                if (!Shards[i].SameAs(other.Shards[i])) return false;
            return true;
        }
    }

    /// <summary>
    /// A capture latched on the coordinator loop (plan 3.5, step 2): the shards of one committed
    /// generation with the annotation (parser version, coverage) each was committed with, all from one
    /// hold of the store's commit lock. Everything it references is immutable except through legacy
    /// in-place writes, which the writer detects through <see cref="SnapshotShardView.HeapVersion"/> and
    /// answers by discarding its file.
    /// </summary>
    internal sealed record SnapshotCapture(
        SnapshotView View,
        long StoreGeneration,
        DateTimeOffset CapturedAt,
        double CaptureMilliseconds)
    {
        /// <summary>
        /// Names of the worker state files this capture asked a worker to write (step 1), recorded or not. A
        /// capture that is not written deletes them, so a state file never outlives the snapshot it was made for.
        /// </summary>
        public IReadOnlyList<string> NewStateFiles { get; init; } = [];
    }
}
