using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The write side of the snapshot lifecycle (plan 3.5, phase 3): when snapshots are written, how, and
/// what the cache directory may contain. Fake time throughout; the only real waits are for the
/// coordinator loop and the background write to finish.
/// </summary>
public partial class SnapshotServiceTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Commits_AreCoalescedIntoOneWrite()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();

        await harness.CommitAsync();
        await harness.AdvanceAsync(3 * Second);
        await harness.CommitAsync();
        await harness.AdvanceAsync(3 * Second);
        await harness.CommitAsync();

        await harness.AdvanceAsync(Quiet - Second);
        Assert.Empty(harness.Snapshots());
        await harness.AdvanceAsync(Second);

        Assert.Equal(1, harness.Service.Status.WritesCompleted);
        var name = Assert.Single(harness.Snapshots());
        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, name), harness.Key(), harness.Expectations());
        Assert.True(loaded.Loaded, loaded.Reason);
        // The newest commit is the one written.
        Assert.Equal(new[] { "N3_0", "N3_1", "N3_2" }, loaded.Shards.Single().Shard.GetAllNodes().Select(n => n.Name));
    }

    [Fact]
    public async Task MaxDelay_ForcesAWriteUnderContinuousCommits()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();

        for (var elapsed = 0; elapsed < 125; elapsed += 5)
        {
            await harness.CommitAsync();
            await harness.AdvanceAsync(5 * Second);
            if (elapsed + 5 < 120) Assert.Equal(0, harness.Service.Status.WritesCompleted);
        }
        Assert.Equal(1, harness.Service.Status.WritesCompleted);
    }

    [Fact]
    public async Task OnlyOneWriteRunsAtATime()
    {
        var active = 0;
        var maximum = 0;
        var calls = 0;
        using var release = new ManualResetEventSlim();
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            BeforeRename = _ =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maximum, now);
                if (Interlocked.Increment(ref calls) == 1) release.Wait(TimeSpan.FromSeconds(20));
                Interlocked.Decrement(ref active);
            },
        });
        await harness.StartAsync();

        await harness.CommitAsync();
        harness.Time.Advance(Quiet);
        await SnapshotServiceHarness.WaitUntilAsync(() => Volatile.Read(ref calls) == 1, "the first write to reach its rename");

        // Dirty again while the first write is held: the timer fires, but no second write starts.
        await harness.CommitAsync();
        harness.Time.Advance(Quiet);
        await harness.CommitAsync();
        harness.Time.Advance(Quiet);
        // Deterministic: any capture the timer requested ran on the coordinator before this barrier, and
        // its result was handled (a second write would have started then) once none is pending.
        await harness.Coordinator.RequestSnapshotCaptureAsync(() => null);
        await SnapshotServiceHarness.WaitUntilAsync(() => !harness.Service.IsCaptureRequested, "the capture to be handled");
        Assert.Equal(1, harness.Service.WritesInProgress);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Single(harness.TempFiles());

        release.Set();
        await harness.IdleAsync();
        // The store stayed dirty: the next write follows at once, from the newest state.
        await harness.AdvanceAsync(TimeSpan.Zero);
        Assert.Equal(2, harness.Service.Status.WritesCompleted);
        Assert.Equal(1, maximum);
        var name = Assert.Single(harness.Snapshots());
        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, name), harness.Key(), harness.Expectations());
        Assert.Equal("N3_0", loaded.Shards.Single().Shard.GetAllNodes()[0].Name);
    }

    [Fact]
    public async Task CleanStore_WritesNothing()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();

        // An empty store after the startup scan is clean.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Snapshots());

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(1, harness.Service.Status.WritesCompleted);

        // Commits that change nothing advance the generation and keep every shard reference.
        await harness.CommitAsync(() => harness.CommitNothing());
        await harness.CommitAsync(() => { });
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, harness.Service.Status.WritesCompleted);
    }

    [Fact]
    public async Task EmptiedShard_IsAChange_ButAnEmptyKeyAloneIsNot()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        // A key whose facts are all gone holds no facts: not a change from an empty store.
        await harness.CommitAsync(() => harness.CommitFacts(1, count: 0));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(0, harness.Service.Status.WritesCompleted);

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        await harness.CommitAsync(() => harness.CommitFacts(9, count: 0));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, harness.Service.Status.WritesCompleted);
        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single()), harness.Key(), harness.Expectations());
        Assert.True(loaded.Loaded, loaded.Reason);
        Assert.Empty(loaded.Shards);
    }

    [Fact]
    public async Task CrashBeforeRename_LeavesThePreviousSnapshotValid()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        try
        {
            string? leftover = null;
            var crash = false;
            string first;
            await using (var harness = new SnapshotServiceHarness(basePath, settings => settings with
            {
                BeforeRename = temp =>
                {
                    if (!Volatile.Read(ref crash)) return;
                    // The process dies here: the flushed temp file stays behind, unrenamed.
                    leftover = temp + ".copy";
                    File.Copy(temp, leftover);
                    throw new IOException("simulated crash between flush and rename");
                },
            }))
            {
                await harness.StartAsync();
                await harness.CommitAsync();
                await harness.AdvanceAsync(Quiet);
                first = harness.Snapshots().Single();

                Volatile.Write(ref crash, true);
                await harness.CommitAsync();
                await harness.AdvanceAsync(Quiet);

                Assert.Equal(new[] { first }, harness.Snapshots());
                var previous = SnapshotFile.Open(Path.Combine(harness.WorkspacePath, first), harness.Key(), harness.Expectations());
                using (previous.File) Assert.NotNull(previous.File);
                File.Move(leftover!, leftover![..^".copy".Length]);
                Assert.Single(harness.TempFiles());
            }

            // The next start deletes the temp file and keeps the snapshot.
            await using (var restarted = new SnapshotServiceHarness(basePath))
            {
                await restarted.StartAsync();
                Assert.Empty(restarted.TempFiles());
                Assert.Equal(new[] { first }, restarted.Snapshots());
            }
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task Startup_DeletesTempFilesAndOlderSnapshots()
    {
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var older = harness.PlantSnapshot(3);
        var newest = harness.PlantSnapshot(5);
        var temp = Path.Combine(harness.WorkspacePath, CacheDirectory.TempNameFor(SnapshotFile.FormatName(6, 0x12345678)));
        File.WriteAllBytes(temp, [1, 2, 3]);
        // Planted with a higher sequence but not authentic: refused, so not kept.
        var planted = Path.Combine(harness.WorkspacePath, SnapshotFile.FormatName(9, 0xdeadbeef));
        File.WriteAllBytes(planted, new byte[512]);
        // Names this code never writes are never deleted.
        var foreign = new[] { "notes.txt", "graph.1.ccsnap", "graph.0000000002.ZZZZZZZZ.ccsnap", "worker-CSharp.0000000007.5d0c9a1e.state",
                "worker-csharp.5.5d0c9a1e.state", "worker-..0000000005.5d0c9a1e.state" }
            .Select(name => Path.Combine(harness.WorkspacePath, name)).ToList();
        foreach (var path in foreign) File.WriteAllText(path, "x");
        // A worker state file (and a save in progress) that the kept snapshot does not reference is this code's: deleted.
        var unreferencedState = Path.Combine(harness.WorkspacePath, "worker-csharp.0000000005.5d0c9a1e.state");
        File.WriteAllText(unreferencedState, "x");
        File.WriteAllText(unreferencedState + ".tmp", "x");

        await harness.StartAsync();

        Assert.True(File.Exists(newest));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(planted));
        Assert.False(File.Exists(unreferencedState));
        Assert.False(File.Exists(unreferencedState + ".tmp"));
        Assert.All(foreign, path => Assert.True(File.Exists(path), path));

        // Sequences continue above the kept snapshot; the deleted names (6, 9) do not raise the counter.
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        var written = Assert.Single(harness.Snapshots());
        Assert.True(SnapshotFile.TryParseName(written, out var sequence));
        Assert.Equal(6UL, sequence);
        Assert.False(File.Exists(newest));
    }

    [Fact]
    public async Task Sequences_IncreaseAcrossRestarts()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sequences = new List<ulong>();
            for (var run = 0; run < 3; run++)
            {
                await using var harness = new SnapshotServiceHarness(basePath);
                await harness.StartAsync();
                await harness.CommitAsync();
                await harness.AdvanceAsync(Quiet);
                Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
                sequences.Add(sequence);
            }
            Assert.Equal(new ulong[] { 1, 2, 3 }, sequences);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task PlantedNameNearTheTenDigitLimit_NeitherExhaustsTheCounterNorHidesTheNewest()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        try
        {
            string newest;
            await using (var harness = new SnapshotServiceHarness(basePath))
            {
                Directory.CreateDirectory(harness.WorkspacePath);
                var planted = Path.Combine(harness.WorkspacePath, "graph.9999999998.00000000.ccsnap");
                File.WriteAllBytes(planted, new byte[512]);
                await harness.StartAsync();
                Assert.False(File.Exists(planted));

                for (var write = 1; write <= 3; write++)
                {
                    await harness.CommitAsync();
                    await harness.AdvanceAsync(Quiet);
                    Assert.Equal(write, harness.Service.Status.WritesCompleted);
                    Assert.Null(harness.Service.Status.LastWriteError);
                }
                newest = harness.Snapshots().Single();
                Assert.True(SnapshotFile.TryParseName(newest, out var sequence));
                Assert.Equal(3UL, sequence);

                // Planted again while running: the next retire removes it too.
                File.WriteAllBytes(planted, new byte[512]);
                await harness.CommitAsync();
                await harness.AdvanceAsync(Quiet);
                Assert.False(File.Exists(planted));
                newest = harness.Snapshots().Single();
            }

            await using var restarted = new SnapshotServiceHarness(basePath);
            await restarted.StartAsync();
            Assert.Equal(new[] { newest }, restarted.Snapshots());
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task JunkRefusedBeforeHashing_AboveAValidSnapshot_DoesNotHideIt()
    {
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var valid = harness.PlantSnapshot(5);
        // Four, one more than the cap on costly refusals: refusals before hashing are not counted.
        var junk = Enumerable.Range(6, 4).Select(sequence =>
        {
            var path = Path.Combine(harness.WorkspacePath, SnapshotFile.FormatName((ulong)sequence, 0xdeadbeef));
            File.WriteAllBytes(path, new byte[512]);   // fails at the header magic
            return path;
        }).ToList();

        await harness.StartAsync();
        Assert.True(File.Exists(valid));
        Assert.All(junk, path => Assert.False(File.Exists(path)));

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
        Assert.Equal(6UL, sequence);
    }

    [Fact]
    public async Task FourAuthenticationFailures_AboveAValidSnapshot_StopTheSearch()
    {
        // The cap bounds the start-up cost (each such refusal reads and hashes a whole file). Someone who
        // can plant files can make the cache start cold this way, deny it, but never make it keep a file
        // that does not authenticate: a planter can deny the cache but not make it wrong.
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var valid = harness.PlantSnapshot(5);
        var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SnapshotKey.Length);
        var forged = Enumerable.Range(6, 4).Select(sequence => harness.PlantSnapshot((ulong)sequence, 0xdeadbeef, otherKey)).ToList();

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Writer, harness.Service.Status.Mode);
        Assert.False(File.Exists(valid));
        Assert.All(forged, path => Assert.False(File.Exists(path)));
        Assert.Equal(SnapshotService.MaxCostlyRefusals, harness.Logger.Entries.Count(entry =>
            entry.Message.StartsWith("Ignoring snapshot", StringComparison.Ordinal) && entry.Message.Contains("Authentication")));

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
        Assert.Equal(1UL, sequence);
    }

    [Fact]
    public async Task UndeletableRefusedFile_DoesNotRaiseTheCounter_AndALaterStartKeepsTheNewest()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        try
        {
            string newest;
            string held;
            await using (var harness = new SnapshotServiceHarness(basePath))
            {
                harness.CommitFacts(1);
                var valid = harness.PlantSnapshot(5);
                held = Path.Combine(harness.WorkspacePath, SnapshotFile.FormatName(7, 0xdeadbeef));
                File.WriteAllBytes(held, new byte[512]);

                // Held open without delete sharing: Windows refuses the delete; Unix deletes an open file.
                using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await harness.StartAsync();
                    Assert.True(File.Exists(valid));
                    Assert.Equal(OperatingSystem.IsWindows(), File.Exists(held));

                    // Refused at start, so it can never be kept and does not outrank the next write.
                    await harness.CommitAsync();
                    await harness.AdvanceAsync(Quiet);
                    newest = harness.Snapshots().Single(name => name != Path.GetFileName(held));
                    Assert.True(SnapshotFile.TryParseName(newest, out var sequence));
                    Assert.Equal(6UL, sequence);
                }
            }

            // Still above the newest real snapshot, refused again, and deleted now that it is free.
            await using var restarted = new SnapshotServiceHarness(basePath);
            await restarted.StartAsync();
            Assert.Equal(new[] { newest }, restarted.Snapshots());
            Assert.False(File.Exists(held));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task UndeletableUntriedFile_StillRaisesTheCounter()
    {
        // Three costly refusals end the search, so the file below them is never tried; it could still be
        // valid, so while it cannot be deleted a new write must not take a sequence below it.
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var untried = harness.PlantSnapshot(5);
        var otherKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SnapshotKey.Length);
        foreach (var sequence in new ulong[] { 6, 7, 8 }) harness.PlantSnapshot(sequence, 0xdeadbeef, otherKey);

        using (new FileStream(untried, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.StartAsync();
            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(untried));
            await harness.CommitAsync();
            await harness.AdvanceAsync(Quiet);
        }
        var written = harness.Snapshots().Single(name => name != Path.GetFileName(untried));
        Assert.True(SnapshotFile.TryParseName(written, out var writtenSequence));
        Assert.Equal(OperatingSystem.IsWindows() ? 6UL : 1UL, writtenSequence);
    }

    /// <summary>
    /// Snapshots 5 (facts N1) and 9 (facts N2), both valid; 9 is held open with no sharing (an antivirus, a
    /// backup), so it is refused for I/O and cannot be deleted, and 5 is loaded. An I/O refusal proves
    /// nothing about the file, so 9 still raises the counter. Then a write of facts N3. Returns the base.
    /// </summary>
    private static async Task<(string BasePath, string Written)> BusyNewerSnapshotRunAsync()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        await using var harness = new SnapshotServiceHarness(basePath);
        harness.CommitFacts(1);
        harness.PlantSnapshot(5);
        harness.CommitFacts(2);
        var busy = harness.PlantSnapshot(9);
        harness.Database.Reset();

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await harness.StartAsync();
            if (OperatingSystem.IsWindows())
            {
                Assert.True(File.Exists(busy));
                Assert.Contains(harness.Logger.Entries, entry => entry.Message.StartsWith("Ignoring snapshot", StringComparison.Ordinal)
                    && entry.Message.Contains(Path.GetFileName(busy)) && entry.Message.Contains(nameof(SnapshotRejection.Io)));
                Assert.Equal(new[] { "N1_0", "N1_1", "N1_2" }, harness.Database.EnumerateNodes().Select(node => node.Name));
            }
            await harness.CommitAsync(() => harness.CommitFacts(3));
            await harness.AdvanceAsync(Quiet);
            Assert.Null(harness.Service.Status.LastWriteError);
        }
        return (basePath, harness.Snapshots().Single(name => name != Path.GetFileName(busy)));
    }

    [Fact]
    public async Task IoRefusedBusyFile_StillRaisesTheCounter()
    {
        var (basePath, written) = await BusyNewerSnapshotRunAsync();
        try
        {
            Assert.True(SnapshotFile.TryParseName(written, out var sequence));
            // Windows: above the busy 9. Unix deletes an open file, so 9 is gone and the write follows 5.
            Assert.Equal(OperatingSystem.IsWindows() ? 10UL : 6UL, sequence);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task IoRefusedBusyFile_TheNextStartLoadsTheNewestFacts()
    {
        var (basePath, written) = await BusyNewerSnapshotRunAsync();
        try
        {
            await using var restarted = new SnapshotServiceHarness(basePath);
            await restarted.StartAsync();
            Assert.Equal(new[] { "N3_0", "N3_1", "N3_2" }, restarted.Database.EnumerateNodes().Select(node => node.Name));
            Assert.Equal(new[] { written }, restarted.Snapshots());
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task ReadOnlyPlantedFile_IsDeleted_AndWritesContinue()
    {
        await using var harness = new SnapshotServiceHarness();
        Directory.CreateDirectory(harness.WorkspacePath);
        var planted = Path.Combine(harness.WorkspacePath, "graph.0999999999.deadbeef.ccsnap");
        File.WriteAllBytes(planted, new byte[512]);
        File.SetAttributes(planted, FileAttributes.ReadOnly);

        await harness.StartAsync();
        Assert.False(File.Exists(planted));
        for (var write = 1; write <= 3; write++)
        {
            await harness.CommitAsync();
            await harness.AdvanceAsync(Quiet);
            Assert.Equal(write, harness.Service.Status.WritesCompleted);
            Assert.Null(harness.Service.Status.LastWriteError);
        }
        Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
        Assert.Equal(3UL, sequence);
    }

    [Fact]
    public async Task NameAboveTheUsableRange_IsIgnoredAndRemoved()
    {
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var valid = harness.PlantSnapshot(4);
        // Authentic, but its sequence is outside 1..999,999,999: never a candidate.
        var high = harness.PlantSnapshot(SnapshotService.MaxSequence + 1);
        var highTemp = Path.Combine(harness.WorkspacePath, CacheDirectory.TempNameFor(SnapshotFile.FormatName(5_000_000_000, 0x12345678)));
        File.WriteAllBytes(highTemp, [1]);

        await harness.StartAsync();
        Assert.True(File.Exists(valid));
        Assert.False(File.Exists(high));
        Assert.False(File.Exists(highTemp));

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
        Assert.Equal(5UL, sequence);
    }

    [Fact]
    public async Task ExhaustedCounter_FailsTheWriteWithAClearError()
    {
        await using var harness = new SnapshotServiceHarness();
        harness.CommitFacts(1);
        var last = harness.PlantSnapshot(SnapshotService.MaxSequence);

        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Contains("999,999,999", harness.Service.Status.LastWriteError);
        Assert.Equal(new[] { Path.GetFileName(last) }, harness.Snapshots());
    }

    [Fact]
    public async Task SecondInstance_IsReadOnly()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var writer = new SnapshotServiceHarness(basePath))
            {
                await writer.StartAsync();
                Assert.Equal(SnapshotCacheMode.Writer, writer.Service.Status.Mode);

                var temp = Path.Combine(writer.WorkspacePath, CacheDirectory.TempNameFor(SnapshotFile.FormatName(77, 0x12345678)));
                File.WriteAllBytes(temp, [1]);
                var stale = writer.PlantSnapshot(1);

                await using (var reader = new SnapshotServiceHarness(basePath))
                {
                    await reader.StartAsync();
                    Assert.Equal(SnapshotCacheMode.ReadOnly, reader.Service.Status.Mode);
                    Assert.Contains("writer.lock", reader.Service.Status.Reason);

                    await reader.CommitAsync();
                    await reader.AdvanceAsync(TimeSpan.FromMinutes(5));
                    await reader.StopAsync();
                    Assert.Equal(0, reader.Service.Status.WritesCompleted);
                    Assert.Equal(0, reader.Time.TimersCreated);
                    Assert.True(File.Exists(temp));
                    Assert.True(File.Exists(stale));
                }

                // The writer is unaffected and still writes.
                await writer.CommitAsync();
                await writer.AdvanceAsync(Quiet);
                Assert.Equal(1, writer.Service.Status.WritesCompleted);
            }

            // A stopped writer releases the lock: the next instance writes.
            await using var next = new SnapshotServiceHarness(basePath);
            await next.StartAsync();
            Assert.Equal(SnapshotCacheMode.Writer, next.Service.Status.Mode);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public async Task BusyFile_IsRetiredLater()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        var first = Path.Combine(harness.WorkspacePath, harness.Snapshots().Single());

        // Another process has the snapshot open as a reader does (deny-write, no delete sharing).
        using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await harness.CommitAsync();
            await harness.AdvanceAsync(Quiet);
            Assert.Equal(2, harness.Service.Status.WritesCompleted);
            Assert.Null(harness.Service.Status.LastWriteError);
            Assert.Equal(0, harness.Logger.Count(LogLevel.Warning) + harness.Logger.Count(LogLevel.Error));
            // Windows refuses the delete; Unix deletes an open file.
            Assert.Equal(OperatingSystem.IsWindows(), File.Exists(first));
        }

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.False(File.Exists(first));
        Assert.Single(harness.Snapshots());
    }

    [Fact]
    public async Task Shutdown_FlushesADirtyStore()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync();
        Assert.Empty(harness.Snapshots());

        await harness.Service.StopAsync(CancellationToken.None);

        Assert.Equal(1, harness.Service.Status.WritesCompleted);
        Assert.Single(harness.Snapshots());
        Assert.Equal(SnapshotCacheMode.Stopped, harness.Service.Status.Mode);
    }

    [Fact]
    public async Task Shutdown_WithAStoppedCoordinator_GivesUpWithinTheFlushTimeout()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.Coordinator.StopAsync(CancellationToken.None);

        // The coordinator's queue is closed: the request completes at once with no capture.
        var stop = harness.Service.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(harness.Snapshots());
    }

    [Fact]
    public async Task Shutdown_DuringAScan_DoesNotWait()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync();
        Assert.True(harness.ScanState.TryBeginScan());

        await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(harness.Snapshots());
        harness.ScanState.CompleteScan();
    }

    [Fact]
    public async Task Shutdown_FlushIsBoundedByTheTimeout()
    {
        using var release = new ManualResetEventSlim();
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            BeforeRename = _ => release.Wait(TimeSpan.FromSeconds(20)),
        });
        await harness.StartAsync();
        await harness.CommitAsync();

        var stop = harness.Service.StopAsync(CancellationToken.None);
        await SnapshotServiceHarness.WaitUntilAsync(() => harness.TempFiles().Count == 1, "the final write to start");
        Assert.False(stop.IsCompleted);
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("not written within"));
        release.Set();
        await harness.Service.CurrentWrite;
    }

    [Fact]
    public async Task LinkedCacheDirectory_IsRefused()
    {
        foreach (var level in new[] { ".codecontext", Path.Combine(".codecontext", "cache"), Path.Combine(".codecontext", "cache", "default") })
        {
            await using var harness = new SnapshotServiceHarness();
            var target = Path.Combine(harness.BasePath, "elsewhere");
            Directory.CreateDirectory(target);
            var link = Path.Combine(harness.RootPath, level);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            if (!TryCreateDirectoryLink(link, target)) return;   // no way to make a link here

            await harness.StartAsync();
            Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
            Assert.Contains("link", harness.Service.Status.Reason);
            await harness.CommitAsync();
            await harness.Service.StopAsync(CancellationToken.None);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task CacheDirectoryThatIsAFile_IsRefused()
    {
        await using var harness = new SnapshotServiceHarness();
        File.WriteAllText(Path.Combine(harness.RootPath, ".codecontext"), "not a directory");

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.Contains("not a directory", harness.Service.Status.Reason);
        Assert.Equal("not a directory", File.ReadAllText(Path.Combine(harness.RootPath, ".codecontext")));
    }

    [Fact]
    public async Task LinkedLockFile_IsRefused()
    {
        await using var harness = new SnapshotServiceHarness();
        Directory.CreateDirectory(harness.WorkspacePath);
        // A dangling link: opening the lock through it would create the target, which only the check
        // before the open prevents (the check after the open would refuse, but too late).
        var target = Path.Combine(harness.BasePath, "victim.txt");
        try
        {
            File.CreateSymbolicLink(Path.Combine(harness.WorkspacePath, "writer.lock"), target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;   // symbolic links need a privilege this machine does not grant: not run here
        }

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task ChainSwappedForALinkBeforeTheLock_IsRefused_AndTheLockReleased()
    {
        string? lockOutside = null;
        var swapped = false;
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            BeforeLock = directory =>
            {
                // Between the level check and the lock: .codecontext is moved away and replaced by a link
                // to a look-alike tree outside the repository.
                var elsewhere = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory.CodecontextPath)!)!, "elsewhere");
                Directory.CreateDirectory(Path.Combine(elsewhere, "cache", "default"));
                Directory.Move(directory.CodecontextPath, directory.CodecontextPath + "-moved");
                swapped = TryCreateDirectoryLink(directory.CodecontextPath, elsewhere);
                lockOutside = Path.Combine(elsewhere, "cache", "default", CacheDirectory.LockFileName);
            },
        });
        await harness.StartAsync();
        var link = Path.Combine(harness.RootPath, ".codecontext");
        try
        {
            if (!swapped) return;   // no way to make a link here
            Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
            Assert.Contains("link", harness.Service.Status.Reason);
            // The lock was opened through the link (unavoidable), then released; nothing else was created there.
            using (new FileStream(lockOutside!, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Equal(new[] { "default" }, Directory.GetFileSystemEntries(Path.Combine(harness.BasePath, "elsewhere", "cache")).Select(Path.GetFileName));
            await harness.CommitAsync();
            await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
            Assert.Equal(new[] { CacheDirectory.LockFileName },
                Directory.GetFileSystemEntries(Path.Combine(harness.BasePath, "elsewhere", "cache", "default")).Select(Path.GetFileName));
        }
        finally
        {
            if (swapped) Directory.Delete(link);
        }
    }

    [Fact]
    public async Task PrepareFailingAfterTheLock_ReleasesIt()
    {
        await using var harness = new SnapshotServiceHarness();
        // Everything up to and including the cleanup succeeds; creating the timer throws.
        harness.Time.FailTimerCreation = true;

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.Contains("could not be prepared", harness.Service.Status.Reason);

        // A second instance becomes the writer instead of being read-only for the rest of the run.
        await using var second = new SnapshotServiceHarness(harness.BasePath);
        await second.StartAsync();
        Assert.Equal(SnapshotCacheMode.Writer, second.Service.Status.Mode);
    }

    [Fact]
    public async Task DisabledAfterTheLockWasTaken_ReleasesIt()
    {
        await using var harness = new SnapshotServiceHarness();
        // A directory where the .gitignore goes: refused after the lock is taken.
        Directory.CreateDirectory(Path.Combine(harness.RootPath, ".codecontext", "cache", ".gitignore", "x"));

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        using (new FileStream(Path.Combine(harness.WorkspacePath, CacheDirectory.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        // A second instance is told why it cannot write, not that another instance holds the lock.
        await using var second = new SnapshotServiceHarness(harness.BasePath);
        await second.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, second.Service.Status.Mode);
        Assert.DoesNotContain("another instance", second.Service.Status.Reason);
    }

    [Fact]
    public void LockContention_IsOnlyASharingOrLockViolation()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(CacheDirectory.IsHeldByAnother(new IOException("sharing", unchecked((int)0x80070020))));
            Assert.True(CacheDirectory.IsHeldByAnother(new IOException("lock", unchecked((int)0x80070021))));
            Assert.False(CacheDirectory.IsHeldByAnother(new IOException("disk full", unchecked((int)0x80070070))));
            Assert.False(CacheDirectory.IsHeldByAnother(new IOException("plain")));
        }

        // A real contention is recognized as such on every platform, and nothing else is.
        var directory = Path.Combine(Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "writer.lock");
            using var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var exception = Assert.ThrowsAny<IOException>(() => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            Assert.True(CacheDirectory.IsHeldByAnother(exception), $"{exception.GetType().Name} {exception.HResult}: {exception.Message}");
            var missing = Assert.ThrowsAny<IOException>(() => new FileStream(Path.Combine(directory, "no", "such"), FileMode.Open));
            Assert.False(CacheDirectory.IsHeldByAnother(missing));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LockThatCannotBeOpenedForAnotherReason_IsRefusedWithItsMessage()
    {
        await using var harness = new SnapshotServiceHarness();
        // A directory where the lock file goes: not contention, so not read-only.
        Directory.CreateDirectory(Path.Combine(harness.WorkspacePath, CacheDirectory.LockFileName));
        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.DoesNotContain("another instance", harness.Service.Status.Reason);
    }

    [Fact]
    public async Task Unix_DirectoriesAre0700_LockAndGitignore0600()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only: does not run on Windows
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Writer, harness.Service.Status.Mode);
        const UnixFileMode Directory0700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode File0600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        foreach (var level in new[] { ".codecontext", Path.Combine(".codecontext", "cache"), Path.Combine(".codecontext", "cache", "default") })
            Assert.Equal(Directory0700, File.GetUnixFileMode(Path.Combine(harness.RootPath, level)));
        Assert.Equal(File0600, File.GetUnixFileMode(Path.Combine(harness.WorkspacePath, CacheDirectory.LockFileName)));
        Assert.Equal(File0600, File.GetUnixFileMode(Path.Combine(harness.RootPath, ".codecontext", "cache", ".gitignore")));
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(File0600, File.GetUnixFileMode(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single())));
    }

    [Fact]
    public async Task Unix_KeyWithGroupAccess_RunsWithoutACache()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only: does not run on Windows
        await using var harness = new SnapshotServiceHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.KeyPath)!);
        SnapshotKey.LoadOrCreate(harness.KeyPath);
        File.SetUnixFileMode(harness.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.Contains("group or others", harness.Service.Status.Reason);
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, ".codecontext")));
    }

    [Fact]
    public async Task Unix_ChainSwappedForALinkWhileRunning_StopsWriting()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only: Windows refuses the rename while the lock is held
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        var codecontext = Path.Combine(harness.RootPath, ".codecontext");
        var elsewhere = Path.Combine(harness.BasePath, "elsewhere");
        Directory.Move(codecontext, elsewhere);
        Directory.CreateSymbolicLink(codecontext, elsewhere);
        try
        {
            await harness.CommitAsync();
            await harness.AdvanceAsync(Quiet);
            Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
            Assert.Contains("link", harness.Service.Status.Reason);
            Assert.Empty(Directory.GetFiles(Path.Combine(elsewhere, "cache", "default"), "graph.*"));
            // The lock was released: another opener gets it.
            using (new FileStream(Path.Combine(elsewhere, "cache", "default", CacheDirectory.LockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally
        {
            File.Delete(codecontext);
        }
    }

    [Fact]
    public async Task KeyProblem_DisablesTheCache_AndLeavesTheKeyAndTheRepositoryAlone()
    {
        await using var harness = new SnapshotServiceHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(harness.KeyPath)!);
        File.WriteAllBytes(harness.KeyPath, [1, 2, 3]);

        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.Contains("key", harness.Service.Status.Reason);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(harness.KeyPath));
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, ".codecontext")));
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Message.Contains(Convert.ToHexString(new byte[] { 1, 2, 3 })));
    }

    [Fact]
    public async Task CacheDisabled_CreatesNothing()
    {
        await using var harness = new SnapshotServiceHarness(enabled: false);
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        await harness.Service.StopAsync(CancellationToken.None);

        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.False(Directory.Exists(Path.Combine(harness.RootPath, ".codecontext")));
        Assert.False(File.Exists(harness.KeyPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(harness.KeyPath)));
        Assert.Equal(0, harness.Time.TimersCreated);
        Assert.Empty(harness.Logger.Entries);
        Assert.Equal(new[] { "repo" }, Directory.GetFileSystemEntries(harness.BasePath).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFileSystemEntries(harness.RootPath));
    }

    [Fact]
    public async Task WriteFailure_IsRetriedAfterTheNextCommit()
    {
        var failing = true;
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            WrapStream = stream => Volatile.Read(ref failing) ? new FailingStream() : stream,
        });
        await harness.StartAsync();

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Contains("not enough space", harness.Service.Status.LastWriteError);
        Assert.Empty(harness.Snapshots());
        Assert.Empty(harness.TempFiles());

        // Not retried in a loop: only after the next change.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, harness.Service.Status.WritesFailed);
        await harness.CommitAsync(() => harness.CommitNothing());
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, harness.Service.Status.WritesFailed);

        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, harness.Service.Status.WritesFailed);
        // Logged once per cause.
        Assert.Equal(1, harness.Logger.Count(LogLevel.Warning));

        Volatile.Write(ref failing, false);
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Null(harness.Service.Status.LastWriteError);
        Assert.Single(harness.Snapshots());
        Assert.Equal(0, harness.Logger.Count(LogLevel.Error));
    }

    [Fact]
    public async Task OversizeCapture_IsNotWritten_AndThePreviousSnapshotStays()
    {
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with { MaxFileLength = 1 << 20 });
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        var first = harness.Snapshots().Single();

        await harness.CommitAsync(() => harness.CommitFacts(2, count: 4, signature: new string('s', 400_000)));
        await harness.AdvanceAsync(Quiet);
        Assert.Contains("limit", harness.Service.Status.LastWriteError);
        Assert.Equal(new[] { first }, harness.Snapshots());
        Assert.Empty(harness.TempFiles());
    }

    [Fact]
    public async Task EveryShardSkipped_IsAWriteFailure()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        await harness.CommitAsync(() => harness.CommitFacts(1, count: 1, signature: "lone \uD800 surrogate"));
        await harness.AdvanceAsync(Quiet);

        Assert.Contains("every shard was skipped", harness.Service.Status.LastWriteError);
        Assert.Empty(harness.Snapshots());
        Assert.Empty(harness.TempFiles());
        Assert.Contains(harness.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("not persisted"));
    }

    [Fact]
    public async Task LegacyInPlaceWrite_DuringSerialization_DiscardsTheFile()
    {
        SnapshotServiceHarness? self = null;
        var injected = 0;
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            WrapStream = stream =>
            {
                if (Interlocked.Increment(ref injected) == 1) self!.Database.UpsertNode(SnapshotServiceHarness.Node("Legacy"));
                return stream;
            },
        });
        self = harness;
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(0, harness.Service.Status.WritesCompleted);
        Assert.Empty(harness.Snapshots());

        // The shard changed in place, so it is still dirty and is written again with the change, after
        // the quiet period as for any other change, not at once.
        await harness.AdvanceAsync(TimeSpan.Zero);
        Assert.Equal(0, harness.Service.Status.WritesCompleted);
        await harness.AdvanceAsync(Quiet - Second);
        Assert.Equal(0, harness.Service.Status.WritesCompleted);
        await harness.AdvanceAsync(Second);
        Assert.Equal(1, harness.Service.Status.WritesCompleted);
        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single()), harness.Key(), harness.Expectations());
        Assert.Contains(loaded.Shards.Single().Shard.GetAllNodes(), node => node.Name == "Legacy");
    }

    [Fact]
    public async Task WrittenSnapshot_HoldsTheCaptureWithItsParserVersionCoverageAndGeneration()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        var coverage = new List<ProjectCoverage> { new("App", "src/App.fakeproj", ProjectCoverageStates.Complete, 3) };
        await harness.CommitAsync(() => harness.CommitFacts(1, annotation: new ShardAnnotation("fake-1.2.3", coverage)));
        await harness.AdvanceAsync(Quiet);

        var loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single()), harness.Key(), harness.Expectations());
        Assert.True(loaded.Loaded, loaded.Reason);
        Assert.Equal((ulong)harness.Database.LastCommittedGeneration, loaded.Header!.StoreGeneration);
        var shard = loaded.Shards.Single();
        Assert.Equal(SnapshotServiceHarness.Shard, shard.Key);
        Assert.Equal("fake-1.2.3", shard.Info.ParserVersion);
        var restored = System.Text.Json.JsonSerializer.Deserialize(shard.Info.Coverage!, ParserProtocolJsonContext.Default.ListProjectCoverage);
        Assert.Equal("App", Assert.Single(restored!).Name);

        // A commit that carries the same annotation and no fact change leaves the store clean.
        await harness.CommitAsync(() => harness.CommitNothing(new ShardAnnotation("fake-1.2.3", null)));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(1, harness.Service.Status.WritesCompleted);

        // An equal coverage report in a new list (as every delta brings) leaves it clean too.
        await harness.CommitAsync(() => harness.CommitNothing(new ShardAnnotation("fake-1.2.3", [.. coverage])));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(1, harness.Service.Status.WritesCompleted);

        // A different coverage report alone, with no fact change, makes the store dirty.
        await harness.CommitAsync(() => harness.CommitNothing(new ShardAnnotation("fake-1.2.3", [coverage[0] with { Files = 4 }])));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, harness.Service.Status.WritesCompleted);
        // So does a new parser version alone.
        await harness.CommitAsync(() => harness.CommitNothing(new ShardAnnotation("fake-1.2.4", null)));
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(3, harness.Service.Status.WritesCompleted);
        loaded = SnapshotLoader.Load(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single()), harness.Key(), harness.Expectations());
        Assert.Equal("fake-1.2.4", loaded.Shards.Single().Info.ParserVersion);
        Assert.NotNull(loaded.Shards.Single().Info.Coverage);
    }

    [Fact]
    public async Task CaptureDuringAReconciliation_LatchesNothing()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        harness.CommitFacts(1);
        harness.Database.BeginReconciliation();
        try
        {
            Assert.Null(harness.Service.Capture());
        }
        finally
        {
            harness.Database.CommitReconciliation();
        }
        Assert.NotNull(harness.Service.Capture());
    }

    [Fact]
    public async Task MaintenanceCommands_DoNotExtendTheIdleLease()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        Assert.False(harness.Coordinator.IsBusy);

        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = harness.Coordinator.RequestSnapshotCaptureAsync(() =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });
        Assert.True(inside.Wait(TimeSpan.FromSeconds(10)));
        // One maintenance command running and one queued behind it: neither is indexing work.
        var queued = harness.Coordinator.RequestSnapshotCaptureAsync(() => null);
        Assert.False(harness.Coordinator.IsBusy);
        Assert.Equal(ScanPhase.Ready, harness.ScanState.Phase);

        // Real work queued behind them still counts.
        var refresh = harness.Coordinator.RefreshFileAsync(Path.Combine(harness.RootPath, "A.fake"));
        Assert.True(harness.Coordinator.IsBusy);
        release.Set();
        await Task.WhenAll(running, queued, refresh);
        // IdleShutdownService renews the lease only while IsBusy is true.
        await SnapshotServiceHarness.WaitUntilAsync(() => !harness.Coordinator.IsBusy, "the coordinator to go idle");
    }

    [Fact]
    public async Task CaptureRequestedDuringABatch_RunsAfterTheBatchIsApplied()
    {
        await using var harness = new SnapshotServiceHarness();
        await harness.StartAsync();
        var applied = 0;
        harness.Mutation = () => Interlocked.Increment(ref applied);

        await harness.Coordinator.NotifyFileChangedAsync(Path.Combine(harness.RootPath, "B.fake"), FileChangeType.Changed);
        var seen = -1;
        var capture = harness.Coordinator.RequestSnapshotCaptureAsync(() =>
        {
            seen = Volatile.Read(ref applied);
            return null;
        });
        await capture.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task CacheGitignore_DoesNotTriggerARescan()
    {
        await using var harness = new SnapshotServiceHarness();
        await using var watcher = await WatchedRootAsync(harness.RootPath);

        await harness.StartAsync();
        var gitignore = Path.Combine(harness.RootPath, ".codecontext", "cache", ".gitignore");
        Assert.Equal("*\n", File.ReadAllText(gitignore));
        await watcher.FlushAsync();

        Assert.DoesNotContain(watcher.Forwarded, path => path.Contains(".codecontext", StringComparison.OrdinalIgnoreCase));
        await watcher.Coordinator.DidNotReceive().TryRequestFullRescanAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SnapshotWrite_RaisesNoWatcherError()
    {
        await using var harness = new SnapshotServiceHarness();
        await using var watcher = await WatchedRootAsync(harness.RootPath);
        await harness.StartAsync();

        // 64 nodes with 1 MiB signatures: a snapshot of about 64 MiB, written in 1 MiB chunks.
        var big = Enumerable.Range(0, 64).Select(i => SnapshotServiceHarness.Node($"Big{i}", new string((char)('a' + i % 26), 1 << 20) + i)).ToList();
        await harness.CommitAsync(() => Assert.True(harness.Database.TryCommitGeneration(
            harness.Database.LastCommittedGeneration + 1, big, [], new CommitScope("fake", "default", null))));
        var stopwatch = Stopwatch.StartNew();
        await harness.AdvanceAsync(Quiet);
        Assert.True(new FileInfo(Path.Combine(harness.WorkspacePath, harness.Snapshots().Single())).Length > 64L << 20);
        await watcher.FlushAsync();

        Assert.Equal(0, watcher.Errors);
        Assert.DoesNotContain(watcher.Forwarded, path => path.Contains(".codecontext", StringComparison.OrdinalIgnoreCase));
        await watcher.Coordinator.DidNotReceive().TryRequestFullRescanAsync(Arg.Any<CancellationToken>());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Configuration_TurnsTheCacheOff_AndDefaultsToOn()
    {
        var options = new CodeContextOptions();
        SnapshotServiceCollectionExtensions.ApplyConfiguration(options, null);
        Assert.True(options.CacheEnabled);

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeContext:CacheEnabled"] = "false",
                ["CodeContext:SnapshotQuietSeconds"] = "3",
                ["CodeContext:SnapshotMaxDelaySeconds"] = "-1",
            })
            .Build();
        var logger = new ListLogger<SnapshotService>();
        SnapshotServiceCollectionExtensions.ApplyConfiguration(options, configuration, logger);
        Assert.False(options.CacheEnabled);
        Assert.Equal(3, options.SnapshotQuietSeconds);
        // Negative: the default stays, with one warning.
        Assert.Equal(120, options.SnapshotMaxDelaySeconds);
        Assert.Equal(1, logger.Count(LogLevel.Warning));
        Assert.Contains("SnapshotMaxDelaySeconds", logger.Entries.Single().Message);

        var settings = SnapshotServiceSettings.From(options);
        Assert.Equal(TimeSpan.FromSeconds(3), settings.QuietPeriod);
        Assert.Equal(TimeSpan.FromSeconds(120), settings.MaxDelay);
        // Off: no key path is resolved at all.
        Assert.Null(settings.KeyPath);
        options.CacheEnabled = true;
        var home = Path.Combine(Path.GetTempPath(), "home");
        Assert.Equal(Path.Combine(home, ".codecontext", "cache.key"), SnapshotServiceSettings.From(options, home).KeyPath);
    }

    [Theory]
    [InlineData("0", "86401", 1, 86_400, 0)]           // clamped to 1 s .. 1 day, no warning
    [InlineData("+7", " 9 ", 7, 9, 0)]                  // NumberStyles.Integer: sign and surrounding white space
    [InlineData("1,000", "1e3", 10, 120, 2)]            // no group separators, no exponent: defaults, two warnings
    [InlineData("١٢", "abc", 10, 120, 2)]     // not invariant digits
    [InlineData("99999999999", "-5", 86_400, 120, 1)]   // beyond int: clamped; negative: default and a warning
    public void Configuration_Delays_AreParsedInvariantly_AndClamped(string quiet, string maxDelay, int expectedQuiet, int expectedMax, int warnings)
    {
        var options = new CodeContextOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeContext:SnapshotQuietSeconds"] = quiet,
                ["CodeContext:SnapshotMaxDelaySeconds"] = maxDelay,
            })
            .Build();
        var logger = new ListLogger<SnapshotService>();
        SnapshotServiceCollectionExtensions.ApplyConfiguration(options, configuration, logger);
        Assert.Equal(expectedQuiet, options.SnapshotQuietSeconds);
        Assert.Equal(expectedMax, options.SnapshotMaxDelaySeconds);
        Assert.Equal(warnings, logger.Count(LogLevel.Warning));
    }

    [Fact]
    public void Configuration_UnparsableSwitch_KeepsTheDefault()
    {
        var options = new CodeContextOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["CodeContext:CacheEnabled"] = "yes" })
            .Build();
        var logger = new ListLogger<SnapshotService>();
        SnapshotServiceCollectionExtensions.ApplyConfiguration(options, configuration, logger);
        Assert.True(options.CacheEnabled);
        Assert.Equal(1, logger.Count(LogLevel.Warning));
    }

    [Fact]
    public void Settings_CapEveryDelayAtOneDay_AndAllowZeroInCode()
    {
        var settings = new SnapshotServiceSettings
        {
            RootPath = "r", KeyPath = "k", HostVersion = "v",
            QuietPeriod = TimeSpan.FromDays(400), MaxDelay = TimeSpan.FromSeconds(-5), ShutdownFlushTimeout = TimeSpan.MaxValue,
        };
        Assert.Equal(TimeSpan.FromDays(1), settings.QuietPeriod);
        Assert.Equal(TimeSpan.Zero, settings.MaxDelay);
        Assert.Equal(TimeSpan.FromDays(1), settings.ShutdownFlushTimeout);
        Assert.Equal(TimeSpan.Zero, (settings with { QuietPeriod = TimeSpan.Zero }).QuietPeriod);
        var fromOptions = SnapshotServiceSettings.From(new CodeContextOptions { CacheEnabled = false, SnapshotQuietSeconds = int.MaxValue, SnapshotMaxDelaySeconds = -1 });
        Assert.Equal(TimeSpan.FromDays(1), fromOptions.QuietPeriod);
        Assert.Equal(TimeSpan.Zero, fromOptions.MaxDelay);
    }

    [Fact]
    public async Task QuietLongerThanMaximum_TheMaximumWins()
    {
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            QuietPeriod = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromSeconds(5),
        });
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(4 * Second);
        Assert.Equal(0, harness.Service.Status.WritesCompleted);
        await harness.AdvanceAsync(Second);
        Assert.Equal(1, harness.Service.Status.WritesCompleted);
    }

    [Fact]
    public async Task EmptyUserProfile_MeansNoKeyPath_AndNoCache()
    {
        var settings = SnapshotServiceSettings.From(new CodeContextOptions { CacheEnabled = true, RootPath = "." }, userProfile: "");
        Assert.Null(settings.KeyPath);
        Assert.Null(SnapshotServiceSettings.From(new CodeContextOptions(), userProfile: "relative\\home").KeyPath);
        Assert.Null(SnapshotKey.DefaultPath("   "));

        await using var harness = new SnapshotServiceHarness(configure: s => s with { KeyPath = settings.KeyPath });
        await harness.StartAsync();
        Assert.Equal(SnapshotCacheMode.Disabled, harness.Service.Status.Mode);
        Assert.Contains("profile", harness.Service.Status.Reason);
        Assert.Empty(Directory.GetFileSystemEntries(harness.RootPath));
        Assert.Equal(new[] { "repo" }, Directory.GetFileSystemEntries(harness.BasePath).Select(Path.GetFileName));
    }

    [Fact]
    public void TempNames_RoundTrip_AndAreNotSnapshotNames()
    {
        var name = SnapshotFile.FormatName(43, 0x91f0e2aa);
        var temp = CacheDirectory.TempNameFor(name);
        Assert.Equal("graph.0000000043.91f0e2aa.tmp", temp);
        Assert.True(CacheDirectory.TryParseTempName(temp, out var sequence));
        Assert.Equal(43UL, sequence);
        Assert.False(SnapshotFile.TryParseName(temp, out _));
        Assert.False(CacheDirectory.TryParseTempName(name, out _));
        Assert.False(CacheDirectory.TryParseTempName("graph.0000000043.91F0E2AA.tmp", out _));
        Assert.False(CacheDirectory.TryParseTempName(".gitignore.tmp", out _));
    }

    [Fact]
    public async Task RetireFailure_LeavesTheWriteCompleted_AndIsRetriedAtTheNextWrite()
    {
        var failRetire = true;
        await using var harness = new SnapshotServiceHarness(configure: settings => settings with
        {
            BeforeRetire = () =>
            {
                if (Volatile.Read(ref failRetire)) throw new IOException("the directory cannot be listed");
            },
        });
        await harness.StartAsync();
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(2, harness.Service.Status.WritesCompleted);
        Assert.Equal(0, harness.Service.Status.WritesFailed);
        Assert.Null(harness.Service.Status.LastWriteError);
        Assert.Equal(2, harness.Snapshots().Count);   // nothing retired

        Volatile.Write(ref failRetire, false);
        await harness.CommitAsync();
        await harness.AdvanceAsync(Quiet);
        Assert.Equal(3, harness.Service.Status.WritesCompleted);
        Assert.True(SnapshotFile.TryParseName(harness.Snapshots().Single(), out var sequence));
        Assert.Equal(3UL, sequence);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    /// <summary>A directory symbolic link, or a junction where symbolic links need a privilege. False when neither can be made.</summary>
    internal static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static async Task<WatchedRoot> WatchedRootAsync(string root)
    {
        var watched = new WatchedRoot(root);
        await watched.Service.StartAsync(CancellationToken.None);
        return watched;
    }

    /// <summary>The real <see cref="FileWatcherService"/> on a root, with a coordinator that records what it is sent.</summary>
    private sealed class WatchedRoot : IAsyncDisposable
    {
        private readonly string _root;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _forwarded = new();
        private int _errors;

        public WatchedRoot(string root)
        {
            _root = root;
            Coordinator.NotifyFileChangedAsync(Arg.Any<string>(), Arg.Any<FileChangeType>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    _forwarded.Enqueue(call.ArgAt<string>(0));
                    return ValueTask.CompletedTask;
                });
            Coordinator.TryRequestFullRescanAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref _errors);
                return Task.FromResult<long?>(1);
            });
            var workers = Substitute.For<ILanguageWorkerService>();
            workers.OwnedExtensions.Returns([".cs"]);
            Service = new FileWatcherService(
                Options.Create(new CodeContextOptions { RootPath = root }), NullLogger<FileWatcherService>.Instance,
                Coordinator, new ScanStateService(), workers);
        }

        public IIndexCoordinator Coordinator { get; } = Substitute.For<IIndexCoordinator>();

        public FileWatcherService Service { get; }

        public IReadOnlyList<string> Forwarded => _forwarded.ToList();

        /// <summary>A watcher error schedules a full rescan; this counts those.</summary>
        public int Errors => Volatile.Read(ref _errors);

        /// <summary>Events arrive in order: once a sentinel written now is forwarded, everything before it was handled.</summary>
        public async Task FlushAsync()
        {
            var sentinel = Path.Combine(_root, $"sentinel-{Guid.NewGuid():N}.cs");
            await File.WriteAllTextAsync(sentinel, "class Sentinel { }");
            await SnapshotServiceHarness.WaitUntilAsync(
                () => _forwarded.Contains(sentinel) || Errors > 0, "the watcher to forward the sentinel", 30_000);
            File.Delete(sentinel);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("There is not enough space on the disk.");
    }
}
