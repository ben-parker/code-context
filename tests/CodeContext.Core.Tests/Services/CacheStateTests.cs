using System;
using System.Collections.Generic;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Services;

/// <summary>The cache states of plan 3.10 and the fields each reports.</summary>
public class CacheStateTests
{
    private static readonly long Ticks = new DateTime(2026, 11, 4, 10, 12, 3, DateTimeKind.Utc).Ticks;

    private static CacheLoadOutcome Loaded(params string[] parsers)
        => new(CacheLoadKind.Loaded, null, Ticks, 42, 26_123_456, parsers);

    private static CacheState Run(CacheLoadOutcome outcome, bool enabled = true)
    {
        var state = new CacheState(enabled, () => outcome);
        state.RunLoadStep();
        return state;
    }

    [Fact]
    public void BeforeTheStep_AndWhenOff_ThereIsNoCacheObject()
    {
        var notRun = new CacheState(enabled: true, () => Loaded("csharp"));
        Assert.Null(notRun.GetStatus());
        Assert.Null(notRun.GetNotice());

        var off = new CacheState(enabled: false, () => throw new InvalidOperationException("never called"));
        Assert.False(off.RunLoadStep());
        off.OnScanCommitted();
        Assert.Null(off.GetStatus());
        Assert.Null(off.GetNotice());

        var disabled = Run(CacheLoadOutcome.Disabled("a link in the cache path"));
        disabled.OnScanCommitted();
        Assert.Null(disabled.GetStatus());
    }

    [Fact]
    public void Loaded_IsVerifying_ThenVerifiedWhenAScanCommits()
    {
        var state = new CacheState(enabled: true, () => Loaded("typescript", "csharp"));
        Assert.True(state.RunLoadStep());

        var status = state.GetStatus()!;
        Assert.Equal(CacheState.Verifying, status.State);
        Assert.True(status.ServingCached);
        Assert.Equal("2026-11-04T10:12:03.0000000Z", status.SnapshotAt);
        Assert.Equal(42, status.SnapshotSequence);
        Assert.Equal(26_123_456, status.SizeBytes);
        Assert.Null(status.WorkerStateBytes);
        Assert.Null(status.Reason);
        Assert.Equal(new Dictionary<string, string> { ["csharp"] = "pending", ["typescript"] = "pending" }, status.Verification);
        Assert.Equal(["csharp", "typescript"], status.Verification!.Keys);
        Assert.Equal("verifying", state.GetNotice()!.State);
        Assert.Equal("2026-11-04T10:12:03.0000000Z", state.GetNotice()!.SnapshotAt);

        state.OnScanCommitted();
        status = state.GetStatus()!;
        Assert.Equal(CacheState.Verified, status.State);
        Assert.False(status.ServingCached);
        Assert.Equal(42, status.SnapshotSequence);
        Assert.Equal(CacheState.Replaced, status.Verification!["csharp"]);
        Assert.Null(state.GetNotice());
    }

    [Fact]
    public void FailedVerification_IsUnverified_UntilALaterScanCommits()
    {
        var state = Run(Loaded("csharp"));
        state.OnScanFailed();
        Assert.Equal(CacheState.Unverified, state.GetStatus()!.State);
        Assert.True(state.GetStatus()!.ServingCached);
        Assert.Equal(CacheState.Unverified, state.GetNotice()!.State);
        Assert.Equal(CacheState.Pending, state.GetStatus()!.Verification!["csharp"]);

        state.OnScanCommitted();
        Assert.Equal(CacheState.Verified, state.GetStatus()!.State);
        // A scan failing after one committed changes nothing: nothing cached is served any more.
        state.OnScanFailed();
        Assert.Equal(CacheState.Verified, state.GetStatus()!.State);
        Assert.Null(state.GetNotice());
    }

    [Fact]
    public void Ignored_ReportsItsReason_ThenVerified()
    {
        var state = Run(new CacheLoadOutcome(CacheLoadKind.Ignored, "graph.0000000042.5d0c9a1e.ccsnap: Authentication: the HMAC does not verify"));
        var status = state.GetStatus()!;
        Assert.Equal(CacheState.Ignored, status.State);
        Assert.False(status.ServingCached);
        Assert.Contains("Authentication", status.Reason);
        Assert.Null(status.SnapshotSequence);
        Assert.Null(status.Verification);
        Assert.Null(state.GetNotice());
        state.OnScanFailed();
        Assert.Equal(CacheState.Ignored, state.GetStatus()!.State);

        state.OnScanCommitted();
        Assert.Equal(CacheState.Verified, state.GetStatus()!.State);
        Assert.Null(state.GetStatus()!.Reason);
    }

    [Fact]
    public void FirstEverStart_HasNoObjectUntilTheScanCommits_AndNoSnapshotFieldsUntilTheFirstWrite()
    {
        var state = Run(new CacheLoadOutcome(CacheLoadKind.NothingFound));
        SnapshotServiceStatus writer = new(SnapshotCacheMode.Writer);
        state.AttachWriter(() => writer);
        Assert.Null(state.GetStatus());

        state.OnScanCommitted();
        var status = state.GetStatus()!;
        Assert.Equal(CacheState.Verified, status.State);
        Assert.Null(status.SnapshotAt);
        Assert.Null(status.SnapshotSequence);
        Assert.Null(status.SizeBytes);

        writer = writer with
        {
            LastSequence = 1, LastSizeBytes = 223_560, LastWriteAt = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero),
            LastWriteMilliseconds = 79, LastCaptureMilliseconds = 0.6,
        };
        status = state.GetStatus()!;
        Assert.Equal(1, status.SnapshotSequence);
        Assert.Equal(223_560, status.SizeBytes);
        Assert.Equal("2026-11-04T11:00:00.0000000Z", status.SnapshotAt);
        Assert.Equal(79, status.LastWriteMs);
        Assert.Equal(1, status.LastCaptureMs);
    }

    [Fact]
    public void WhileServingCached_TheLoadedSnapshotIsReported_ThenTheNewestWrite()
    {
        var state = Run(Loaded("csharp"));
        SnapshotServiceStatus writer = new(SnapshotCacheMode.Writer, LastSequence: 43, LastSizeBytes: 1, LastWriteAt: DateTimeOffset.UnixEpoch,
            LastWriteError: "disk full");
        state.AttachWriter(() => writer);
        state.OnScanFailed();
        var status = state.GetStatus()!;
        Assert.Equal(42, status.SnapshotSequence);
        Assert.Equal("disk full", status.LastWriteError);

        state.OnScanCommitted();
        Assert.Equal(43, state.GetStatus()!.SnapshotSequence);
    }

    [Fact]
    public void WithTheScanPhase_AServedSnapshotIsUnverifiedExactlyWhileTheStatusIsError()
    {
        var phase = ScanPhase.Scanning;
        var state = Run(Loaded("csharp"));
        state.AttachScanPhase(() => phase);
        Assert.Equal(CacheState.Verifying, state.GetStatus()!.State);

        // One worker failed while the others still index: the status is already error.
        phase = ScanPhase.Error;
        Assert.Equal(CacheState.Unverified, state.GetStatus()!.State);
        Assert.Equal(CacheState.Unverified, state.GetNotice()!.State);
        state.OnScanFailed();

        // A refresh re-checks the files: verifying again while it runs, verified when it commits.
        phase = ScanPhase.Scanning;
        Assert.Equal(CacheState.Verifying, state.GetStatus()!.State);
        Assert.Equal(CacheState.Verifying, state.GetNotice()!.State);
        state.OnScanCommitted();
        phase = ScanPhase.Ready;
        Assert.Equal(CacheState.Verified, state.GetStatus()!.State);
        Assert.Null(state.GetNotice());
    }

    [Fact]
    public void LoadIsAnnouncedBeforeThePublish_AndAFailedPublishTakesItBack()
    {
        CacheNotice? duringPublish = null;
        CacheState? state = null;
        state = new CacheState(enabled: true, beforePublish =>
        {
            beforePublish(Loaded("csharp"));
            duringPublish = state!.GetNotice();
            // The publish failed: the step ends with no cache.
            return CacheLoadOutcome.Disabled("the publish failed");
        });
        Assert.False(state.RunLoadStep());
        Assert.Equal(CacheState.Verifying, duringPublish?.State);
        Assert.Null(state.GetNotice());
        Assert.Null(state.GetStatus());
    }

    [Fact]
    public void LoadStepThatThrows_CountsAsOff()
    {
        var calls = 0;
        var state = new CacheState(enabled: true, () =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });
        Assert.Throws<InvalidOperationException>(() => state.RunLoadStep());
        Assert.Null(state.GetStatus());
        Assert.Equal(1, calls);
    }
}
