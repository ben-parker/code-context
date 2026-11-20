using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Memory;
using Microsoft.Extensions.Configuration;
using CodeContext.Core.Services;
using NSubstitute;
using CodeContext.Core.Tests.Snapshots;
using Xunit;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// The trigger rules of <see cref="QuietTimeTrim"/> on fake time, with a fake allocation counter and a trim that
/// only counts: once per burst, after the quiet period, never with work in flight, never under steady traffic,
/// spaced by the minimum interval, and only after enough allocation.
/// </summary>
public class QuietTimeTrimTests
{
    private const long MB = 1024 * 1024;

    private static readonly QuietTimeTrimSettings Settings = new()
    {
        QuietPeriod = TimeSpan.FromSeconds(15),
        MinInterval = TimeSpan.FromSeconds(30),
        MinAllocatedBytes = 32 * MB,
    };

    private sealed class Harness : IDisposable
    {
        public ManualTimeProvider Time { get; } = new();
        public long Allocated { get; set; }
        public int Collections { get; private set; }
        public Action? DuringTrim { get; set; }
        public QuietTimeTrim Trim { get; }

        public Harness(QuietTimeTrimSettings? settings = null)
        {
            Trim = new QuietTimeTrim(settings ?? Settings, Time, () => Allocated, () =>
            {
                Collections++;
                DuringTrim?.Invoke();
                return new QuietTimeTrimResult(200 * MB, 100 * MB, TimeSpan.FromMilliseconds(100));
            });
        }

        /// <summary>One piece of work that allocates <paramref name="bytes"/> and takes <paramref name="duration"/>.</summary>
        public void Work(long bytes, TimeSpan duration = default)
        {
            using (Trim.BeginActivity())
            {
                Allocated += bytes;
                if (duration > TimeSpan.Zero) Time.Advance(duration);
            }
        }

        public void Advance(double seconds) => Time.Advance(TimeSpan.FromSeconds(seconds));

        public void Dispose() => Trim.Dispose();
    }

    [Fact]
    public void ABurst_IsTrimmedOnce_AfterTheQuietPeriod()
    {
        using var h = new Harness();
        h.Work(100 * MB, TimeSpan.FromSeconds(3));

        h.Advance(14.9);
        Assert.Equal(0, h.Collections);
        h.Advance(0.1);
        Assert.Equal(1, h.Collections);

        // Idle afterwards: nothing polls, nothing trims again.
        h.Advance(3600);
        Assert.Equal(1, h.Collections);
        Assert.Equal(1, h.Trim.Trims);
    }

    [Fact]
    public void WorkInFlight_IsNeverTrimmed_AndItsEndStartsTheQuietPeriod()
    {
        using var h = new Harness();
        h.Work(100 * MB);
        var open = h.Trim.BeginActivity();
        h.Advance(600);
        Assert.Equal(0, h.Collections);

        open.Dispose();
        h.Advance(14);
        Assert.Equal(0, h.Collections);
        h.Advance(1);
        Assert.Equal(1, h.Collections);
    }

    [Fact]
    public void SteadyTraffic_NeverTrims_UntilItStops()
    {
        using var h = new Harness();
        // A query every 5 s for ten minutes, each allocating 2 MB: 240 MB in all.
        for (var i = 0; i < 120; i++)
        {
            h.Work(2 * MB, TimeSpan.FromMilliseconds(200));
            h.Advance(4.8);
        }
        Assert.Equal(0, h.Collections);

        h.Advance(15);
        Assert.Equal(1, h.Collections);
    }

    [Fact]
    public void LittleAllocation_IsNotWorthAPause()
    {
        using var h = new Harness();
        h.Work(10 * MB);
        h.Advance(60);
        Assert.Equal(0, h.Collections);

        // Allocation adds up across bursts; the burst that crosses the threshold is trimmed.
        h.Work(30 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        // Counted again from the trim.
        h.Work(20 * MB);
        h.Advance(120);
        Assert.Equal(1, h.Collections);
    }

    [Fact]
    public void ASecondBurst_WaitsForTheMinimumInterval()
    {
        using var h = new Harness();
        h.Work(100 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        // Ends 1 s after the trim: quiet at +16 s, but the interval runs to +30 s.
        h.Advance(1);
        h.Work(100 * MB);
        h.Advance(28.9);
        Assert.Equal(1, h.Collections);
        h.Advance(0.1);
        Assert.Equal(2, h.Collections);
    }

    [Fact]
    public void WorkEndingDuringATrim_IsLookedAtAgain()
    {
        using var h = new Harness();
        h.DuringTrim = () =>
        {
            h.DuringTrim = null;
            h.Work(100 * MB);
        };
        h.Work(100 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        // The work counted before the trim ended, so it did not reach the threshold; more work then does.
        h.Work(40 * MB);
        h.Advance(30);
        Assert.Equal(2, h.Collections);
    }

    [Fact]
    public void ATrimThatThrows_StillCounts_AndTheNextBurstTriesAgain()
    {
        using var h = new Harness();
        h.DuringTrim = () => throw new InvalidOperationException("boom");
        h.Work(100 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        h.DuringTrim = null;
        h.Work(100 * MB);
        h.Advance(30);
        Assert.Equal(2, h.Collections);
    }

    [Fact]
    public void Disabled_CreatesNoTimer_AndNeverTrims()
    {
        using var h = new Harness(QuietTimeTrimSettings.Disabled);
        Assert.False(h.Trim.Enabled);
        Assert.Equal(0, h.Time.TimersCreated);
        h.Work(1000 * MB);
        h.Advance(3600);
        Assert.Equal(0, h.Collections);
    }

    [Fact]
    public void AScopeDisposedTwice_EndsOnce()
    {
        using var h = new Harness();
        var first = h.Trim.BeginActivity();
        var second = h.Trim.BeginActivity();
        first.Dispose();
        first.Dispose();
        Assert.Equal(1, h.Trim.Active);
        second.Dispose();
        Assert.Equal(0, h.Trim.Active);
    }

    [Fact]
    public void AfterDispose_NothingTrims()
    {
        var h = new Harness();
        h.Work(100 * MB);
        h.Dispose();
        h.Advance(60);
        Assert.Equal(0, h.Collections);
        h.Trim.BeginActivity().Dispose();
        Assert.Equal(0, h.Trim.Active);
    }

    [Theory]
    [InlineData(null, 15)]
    [InlineData("", 15)]
    [InlineData("0", 0)]
    [InlineData("5", 5)]
    [InlineData(" 20 ", 20)]
    [InlineData("99999999999", 86_400)]
    public void FromSeconds_ReadsTheQuietPeriod(string? text, int seconds)
    {
        var settings = QuietTimeTrimSettings.FromSeconds(text);
        Assert.NotNull(settings);
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings!.QuietPeriod);
        Assert.Equal(seconds > 0, settings.Enabled);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public void FromSeconds_RefusesAnythingElse(string text)
        => Assert.Null(QuietTimeTrimSettings.FromSeconds(text));

    [Fact]
    public void ARelease_TrimsAfterTheQuietPeriod_EvenBelowTheAllocationThreshold()
    {
        using var h = new Harness();
        h.Work(100 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        // A worker process stopped: little managed allocation, but native memory was let go.
        using (h.Trim.BeginActivity())
        {
            h.Allocated += 1 * MB;
            h.Trim.RequestRelease();
        }
        h.Advance(60);
        Assert.Equal(2, h.Collections);

        // Once: the next small burst is left alone again.
        h.Work(1 * MB);
        h.Advance(60);
        Assert.Equal(2, h.Collections);
    }

    [Fact]
    public void ARelease_OutsideAnActivity_ArmsTheTimer_AndKeepsTheMinimumInterval()
    {
        using var h = new Harness();
        h.Work(100 * MB);
        h.Advance(15);
        Assert.Equal(1, h.Collections);

        h.Trim.RequestRelease();
        h.Advance(14.9);
        Assert.Equal(1, h.Collections);
        // Quiet period passed, but the minimum interval since the last trim (30 s) has not.
        h.Advance(0.2);
        Assert.Equal(1, h.Collections);
        h.Advance(15);
        Assert.Equal(2, h.Collections);
    }

    [Fact]
    public void CompactAndDecommit_Collects()
    {
        var before = GC.CollectionCount(2);
        var result = QuietTimeTrim.CompactAndDecommit();
        Assert.True(GC.CollectionCount(2) > before);
        Assert.True(result.Pause >= TimeSpan.Zero);
        Assert.True(result.WorkingSetAfter > 0);
    }

    [Fact]
    public void ReturnFreeNativeMemory_RunsOnGlibcLinux_AndIsANoOpElsewhere()
    {
        // glibc names itself libc.so.6; musl and other systems have no malloc_trim to call.
        var glibc = OperatingSystem.IsLinux()
            && !System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal);
        Assert.Equal(glibc, QuietTimeTrim.ReturnFreeNativeMemory());
        Assert.Equal(glibc, QuietTimeTrim.ReturnFreeNativeMemory());
    }
}

/// <summary>The coordinator marks its startup burst and every command as work for the quiet-time trim.</summary>
public class IndexCoordinatorMemoryTrimTests
{
    [Fact]
    public async Task TheStartupScanAndARefresh_AreActivity_AndNothingIsLeftOpen()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var scan = new TaskCompletionSource();
            var refresh = new TaskCompletionSource();
            var graphUpdate = Substitute.For<IGraphUpdateService>();
            graphUpdate.PerformResumableScanAsync(Arg.Any<string>(), Arg.Any<IScanProgressReporter>(), Arg.Any<CancellationToken>())
                .Returns(_ => scan.Task);
            graphUpdate.ProcessFileChangesAsync(Arg.Any<IReadOnlyList<FileChange>>(), Arg.Any<CancellationToken>())
                .Returns(_ => refresh.Task);
            using var trim = new QuietTimeTrim(new QuietTimeTrimSettings(), new ManualTimeProvider(), () => 0, () => default);
            var scanState = new ScanStateService();
            using var coordinator = new IndexCoordinator(graphUpdate, scanState,
                Microsoft.Extensions.Options.Options.Create(new CodeContextOptions { RootPath = root }),
                Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, memoryTrim: trim);

            await coordinator.StartAsync(CancellationToken.None);
            await WaitFor(() => trim.Active == 1);
            scan.SetResult();
            await WaitFor(() => scanState.Phase == ScanPhase.Ready && trim.Active == 0);

            var refreshed = coordinator.RefreshFileAsync(System.IO.Path.Combine(root, "A.cs"));
            await WaitFor(() => trim.Active == 1);
            refresh.SetResult();
            await refreshed;
            await WaitFor(() => trim.Active == 0);

            await coordinator.StopAsync(CancellationToken.None);
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The test assembly turns the trim off for every host it composes (TestAssemblyGuards): a host built
    /// from the environment, as the product's builders build it, reads a quiet period of zero.
    /// </summary>
    [Fact]
    public void HostsTheTestsCompose_HaveTheTrimOff()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        Assert.Equal("0", configuration[QuietTimeTrimSettings.ConfigurationKey]);
        Assert.Equal(TimeSpan.Zero, MemoryTrimServiceCollectionExtensions.Read(configuration).QuietPeriod);
    }
}
