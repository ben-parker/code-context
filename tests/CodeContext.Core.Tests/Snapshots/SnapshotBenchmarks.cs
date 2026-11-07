using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Repositories.StoreModel;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Opt-in timing of the snapshot on the jellyfin-sized generated shard of
/// <see cref="Repositories.StoreBenchmarks"/> (26,000 nodes, about 296,000 edges). Excluded from the
/// ordinary suite; run with
/// <c>dotnet test -c Release --filter "Category=Benchmark&amp;FullyQualifiedName~SnapshotBenchmarks" --logger "console;verbosity=detailed"</c>.
/// Reports file size, bytes per section kind, and the median of 5 runs of writing the file,
/// loading it (open, authenticate, map, validate, materialize) and the streaming HMAC alone.
/// Set <c>CODECONTEXT_BENCH_OUT</c> to a file path to append the numbers there as well.
/// </summary>
[Trait("Category", "Benchmark")]
public class SnapshotBenchmarks
{
    private const int Runs = 5;

    private readonly ITestOutputHelper _output;

    public SnapshotBenchmarks(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WriteAndLoad_OnAJellyfinSizedShard()
    {
        var shard = GeneratedShard.Create(seed: 20261105);
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, shard.Nodes, shard.Edges, new CommitScope(GeneratedShard.Parser, GeneratedShard.Workspace, null)));
        var shards = SnapshotTestSupport.ShardsOf(database);
        Report($"shard: {database.NodeCount} nodes, {database.EdgeCount} edges");

        using var temp = new SnapshotTempDirectory();
        ulong sequence = 0;
        var writes = new List<double>();
        SnapshotWriteResult? result = null;
        string? path = null;
        for (var run = 0; run <= Runs; run++)
        {
            if (path is not null) File.Delete(path);
            path = temp.Snapshot(++sequence);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var stopwatch = Stopwatch.StartNew();
            result = SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(shards, sequence));
            stopwatch.Stop();
            if (run > 0) writes.Add(stopwatch.Elapsed.TotalMilliseconds);   // run 0 warms the JIT
        }
        Assert.Single(result!.Written);
        Report($"file: {result.Length:N0} bytes ({result.Length / 1048576.0:F1} MiB)");
        foreach (var (kind, bytes) in result.SectionBytes)
            Report($"  {kind,-16} {bytes,12:N0} bytes  {bytes * 100.0 / result.Length,5:F1} %");
        Report($"write: median {Median(writes):F1} ms [{string.Join(", ", writes.Select(ms => ms.ToString("F1")))}]");

        var loads = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var stopwatch = Stopwatch.StartNew();
            var loaded = SnapshotTestSupport.LoadOrFail(path!);
            stopwatch.Stop();
            Assert.Equal(database.NodeCount, loaded.Shards[0].Shard.NodeCount);
            Assert.Equal(database.EdgeCount, loaded.Shards[0].Shard.EdgeCount);
            if (run > 0) loads.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        Report($"load (open, authenticate, map, validate, materialize): median {Median(loads):F1} ms [{string.Join(", ", loads.Select(ms => ms.ToString("F1")))}]");

        var opens = new List<double>();
        var hmacs = new List<double>();
        for (var run = 0; run <= Runs; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            using (var file = SnapshotFile.Open(path!, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!) Assert.Single(file.Shards);
            stopwatch.Stop();
            if (run > 0) opens.Add(stopwatch.Elapsed.TotalMilliseconds);

            stopwatch.Restart();
            StreamingHmac(path!);
            stopwatch.Stop();
            if (run > 0) hmacs.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        Report($"open (authenticate, map, validate; no materialization): median {Median(opens):F1} ms [{string.Join(", ", opens.Select(ms => ms.ToString("F1")))}]");
        Report($"streaming HMAC alone: median {Median(hmacs):F1} ms [{string.Join(", ", hmacs.Select(ms => ms.ToString("F1")))}]");
    }

    // The same read as SnapshotFile's authentication: 1 MiB chunks over one sequential-scan handle.
    private static void StreamingHmac(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, SnapshotTestSupport.Key);
        var buffer = new byte[1 << 20];
        var end = RandomAccess.GetLength(handle) - SnapshotFormat.HmacSize;
        for (long offset = 0; offset < end;)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - offset)), offset);
            hmac.AppendData(buffer.AsSpan(0, read));
            offset += read;
        }
        _ = hmac.GetHashAndReset();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToList();
        return sorted[sorted.Count / 2];
    }

    private void Report(string line)
    {
        _output.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("CODECONTEXT_BENCH_OUT");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
    }
}
