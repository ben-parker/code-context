using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using static SnapshotTestSupport;

/// <summary>
/// A structured fuzzer over re-signed files (adapted from a phase 2 review). It knows the layout and
/// damages what the reader trusts: header fields, directory words, section table fields (including
/// one section's field copied from another), shard header counts, words inside each section (hostile
/// values, small values, single bit flips, nearby values, words copied or swapped from elsewhere in
/// the blob) and random blob bytes. Each damaged file is re-signed, then loaded and opened, and every
/// accessor is asked with ordinary and hostile arguments. Every outcome must be an answer, a
/// <see cref="SnapshotCorruptException"/> or a refusal; never another exception, never a hang (each
/// iteration has a 20 s watchdog), never a file left open (the temp directory must delete).
///
/// Fixed seeds; about 3 s in all. <c>CODECONTEXT_SNAPSHOT_FUZZ_ITERATIONS</c> raises the iterations per seed.
/// </summary>
public class SnapshotFuzzTests
{
    private const int DefaultIterations = 220;

    private static readonly uint[] Hostile =
        [0, 1, 2, 3, 4, 7, 8, 0x7F, 0x80, 0xFF, 0x100, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0x80000001, 0xFFFFFFFE, 0xFFFFFFFF, 0xFFFFFFF0];

    private static readonly string[] HostileQueries = ["", "\uD800", "a\uDC00b", "x", "\U00010400", new string('a', 300), "csharp:", ":"];

    [Theory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public void StructuredDamage_IsAlwaysAnAnswerACorruptExceptionOrARefusal(int seed)
    {
        var graph = new SnapshotGraphGenerator(seed).Generate(maxNodes: 30, maxEdges: 90);
        var database = Commit(graph);
        var words = database.ShardKeys
            .Select(key => ShardReadMatrix.Vocabulary.From((CodeContext.Core.Repositories.InMemory.IGraphShard)database.GetShardState(key)!))
            .ToArray();
        var bytes = WriteBytes(Input(ShardsOf(database)));
        var layout = LayoutOf(bytes);
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("CODECONTEXT_SNAPSHOT_FUZZ_ITERATIONS"), out var configured) && configured > 0
            ? configured
            : DefaultIterations;

        var random = new Random(seed);
        var failures = new List<string>();
        var opened = 0;
        using (var temp = new SnapshotTempDirectory())
        {
            for (var iteration = 0; iteration < iterations && failures.Count < 10; iteration++)
            {
                var damaged = (byte[])bytes.Clone();
                var description = new StringBuilder();
                for (var m = random.Next(1, 4); m > 0; m--) description.Append(Mutate(damaged, layout, random)).Append("; ");
                Resign(damaged);
                var context = $"seed {seed} iteration {iteration}: {description}";
                // Every 16th file goes through a real file too: Load and Open by path, then the file is deleted.
                var path = iteration % 16 == 0 ? temp.Snapshot() : null;
                if (path is not null) File.WriteAllBytes(path, damaged);

                var task = Task.Run(() => Exercise(damaged, path, words, context, failures));
                if (!task.Wait(TimeSpan.FromSeconds(20)))
                {
                    failures.Add("HANG " + context);
                    break;
                }
                if (task.Result) opened++;
                if (path is not null)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"the file stayed open: {exception.GetType().Name} {context}");
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(opened > 0, "no damaged file opened: the fuzzer only exercised the open checks");
    }

    private static string Mutate(byte[] b, FileLayout layout, Random r)
    {
        var shardIndex = r.Next(layout.Shards.Count);
        var shard = layout.Shards[shardIndex];
        uint Value(uint current) => r.Next(5) switch
        {
            0 => Hostile[r.Next(Hostile.Length)],
            1 => current + (uint)r.Next(-9, 10),
            2 => (uint)r.Next(64),
            3 => current ^ (1u << r.Next(32)),
            _ => U32(b, shard.Blob + 4 * r.Next(Math.Min(shard.BlobLength / 4, 1000))),   // a word from elsewhere
        };
        switch (r.Next(8))
        {
            case 0:
            {
                int[] fields = [20, 24, 28, 56, 60, 64, 68, 72, 76];
                var at = fields[r.Next(fields.Length)];
                var value = Value(U32(b, at));
                W32(b, at, value);
                return $"header+{at}={value:X}";
            }
            case 1:
            {
                int[] fields = [0, 4, 8, 12, 48, 52, 20, 40];
                var field = fields[r.Next(fields.Length)];
                var value = Value(U32(b, shard.Entry + field));
                W32(b, shard.Entry + field, value);
                return $"directory[{shardIndex}]+{field}={value:X}";
            }
            case 2:
            {
                var section = r.Next(SnapshotFormat.SectionCount);
                int[] fields = [0, 4, 8, 12, 16, 20];
                var field = fields[r.Next(fields.Length)];
                var at = shard.Blob + SnapshotFormat.ShardHeaderSize + SnapshotFormat.SectionEntrySize * section + field;
                // A third of the time, the same field of another section (an offset or length shuffle).
                var value = r.Next(3) == 0
                    ? U32(b, shard.Blob + SnapshotFormat.ShardHeaderSize + SnapshotFormat.SectionEntrySize * r.Next(SnapshotFormat.SectionCount) + field)
                    : Value(U32(b, at));
                W32(b, at, value);
                return $"shard {shardIndex} table[{section + 1}]+{field}={value:X}";
            }
            case 3:
            {
                int[] fields = [8, 12, 16, 20, 24, 28, 32];
                var field = fields[r.Next(fields.Length)];
                var value = Value(U32(b, shard.Blob + field));
                W32(b, shard.Blob + field, value);
                return $"shard {shardIndex} header+{field}={value:X}";
            }
            case 4:
            case 5:
            case 6:
            {
                var kinds = shard.Sections.Where(section => section.Value.Length >= 4).ToArray();
                var (kind, (offset, length)) = kinds[r.Next(kinds.Length)];
                var word = r.Next(length / 4);
                var at = offset + 4 * word;
                if (r.Next(4) == 0 && length >= 8)
                {
                    var other = offset + 4 * r.Next(length / 4);
                    var first = U32(b, at);
                    W32(b, at, U32(b, other));
                    W32(b, other, first);
                    return $"shard {shardIndex} {kind} swap words {word},{(other - offset) / 4}";
                }
                var value = Value(U32(b, at));
                W32(b, at, value);
                return $"shard {shardIndex} {kind}[{word}]={value:X}";
            }
            default:
            {
                var at = shard.Blob + r.Next(shard.BlobLength);
                var value = (byte)r.Next(256);
                b[at] = value;
                return $"byte {at}={value:X2}";
            }
        }
    }

    /// <summary>Returns whether the file opened. Records every escape that is not an allowed outcome.</summary>
    private static bool Exercise(byte[] damaged, string? path, ShardReadMatrix.Vocabulary[] words, string context, List<string> failures)
    {
        void Fail(string what, Exception exception)
        {
            lock (failures) failures.Add($"{what}: {exception.GetType().Name}: {exception.Message} @ {exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim()} :: {context}");
        }

        void Read(string what, Action action, bool nullArgument = false)
        {
            try
            {
                action();
            }
            catch (SnapshotCorruptException)
            {
            }
            catch (ArgumentNullException) when (nullArgument)
            {
            }
            catch (Exception exception)
            {
                Fail(what, exception);
            }
        }

        void Refusal(string what, SnapshotRejection rejection, string? reason)
        {
            if (rejection == SnapshotRejection.Unexpected) Fail(what, new InvalidOperationException($"refused as unexpected: {reason}"));
        }

        if (path is not null)
        {
            try
            {
                var loaded = SnapshotLoader.Load(path, Key, Expect());
                if (!loaded.Loaded) Refusal("Load", loaded.Rejection, loaded.Reason);
                var byPath = SnapshotFile.Open(path, Key, Expect());
                byPath.File?.Dispose();
                if (byPath.File is null) Refusal("Open", byPath.Rejection, byPath.Reason);
            }
            catch (Exception exception)
            {
                Fail("Load or Open by path", exception);
            }
        }

        SnapshotOpenResult opened;
        try
        {
            opened = OpenInMemory(damaged);
        }
        catch (Exception exception)
        {
            Fail("Open", exception);
            return false;
        }
        if (opened.File is not { } file)
        {
            Refusal("Open", opened.Rejection, opened.Reason);
            return false;
        }

        using (file)
        {
            try
            {
                var loaded = SnapshotLoader.Materialize(file);
                if (!loaded.Loaded) Refusal("Materialize", loaded.Rejection, loaded.Reason);
            }
            catch (Exception exception)
            {
                Fail("Materialize", exception);
            }

            var ids = words.SelectMany(w => w.NodeIds).Concat(HostileQueries).ToArray();
            var edgeIds = words.SelectMany(w => w.EdgeIds).Concat(HostileQueries).ToArray();
            var identifiers = words.SelectMany(w => w.Identifiers).Concat(HostileQueries).ToArray();
            var names = words.SelectMany(w => w.Names).Distinct().Take(10).Concat(HostileQueries).ToArray();
            var paths = words.SelectMany(w => w.Paths).Distinct().Take(10).Concat(HostileQueries).ToArray();
            foreach (var reader in file.Shards)
            {
                for (var i = -1; i <= Math.Min(reader.NodeCount, 64); i++)
                {
                    var k = i;
                    Read("ReadNode", () => reader.ReadNode(k));
                    Read("IsExternal", () => reader.IsExternal(k));
                }
                for (var j = -1; j <= Math.Min(reader.EdgeCount, 160); j++) { var k = j; Read("ReadEdge", () => reader.ReadEdge(k)); }
                foreach (var r in new uint[] { 0, 1, (uint)reader.StringCount, (uint)reader.StringCount + 1, 0xFFFFFFFF, 0x80000000 })
                    Read("GetString", () => reader.GetString(r));
                Read("GetAllNodes", () => reader.GetAllNodes());
                Read("GetAllEdges", () => reader.GetAllEdges());
                Read("GetFilePaths", () => reader.GetFilePaths());
                Read("GetCrossShardEdgeIds", () => reader.GetCrossShardEdgeIds());
                Read("ReadInfo", () => reader.ReadInfo());
                Read("AddStatistics", () => reader.AddStatistics(new(), new()));
                foreach (var id in ids)
                {
                    Read("TryGetNode", () => reader.TryGetNode(id, out _));
                    Read("GetEdgesBySource", () => reader.GetEdgesBySource(id));
                    Read("GetEdgesByTarget", () => reader.GetEdgesByTarget(id));
                }
                foreach (var id in edgeIds) Read("TryGetEdge", () => reader.TryGetEdge(id, out _));
                foreach (var identifier in identifiers) Read("TryGetNodeIdByIdentifier", () => reader.TryGetNodeIdByIdentifier(identifier, out _));
                foreach (var name in names)
                {
                    Read("FindNodesByName exact", () => reader.FindNodesByName(name, true));
                    Read("FindNodesByName substring", () => reader.FindNodesByName(name, false));
                }
                foreach (var requested in paths) Read("FindNodesByPath", () => reader.FindNodesByPath(requested));

                Read("TryGetNode(null)", () => reader.TryGetNode(null!, out _), nullArgument: true);
                Read("TryGetEdge(null)", () => reader.TryGetEdge(null!, out _), nullArgument: true);
                Read("TryGetNodeIdByIdentifier(null)", () => reader.TryGetNodeIdByIdentifier(null!, out _), nullArgument: true);
                Read("GetEdgesBySource(null)", () => reader.GetEdgesBySource(null!), nullArgument: true);
                Read("GetEdgesByTarget(null)", () => reader.GetEdgesByTarget(null!), nullArgument: true);
                Read("FindNodesByName(null, exact)", () => reader.FindNodesByName(null!, true), nullArgument: true);
                Read("FindNodesByName(null, substring)", () => reader.FindNodesByName(null!, false), nullArgument: true);
                Read("FindNodesByPath(null)", () => reader.FindNodesByPath(null!), nullArgument: true);
            }
        }
        return true;
    }
}
