using System.Globalization;
using CodeContext.Core.Serialization;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Services;

/// <summary>
/// What the persistent cache tells readers (plan section 3.10): the <c>indexing.cache</c> object of
/// status, and the <c>cache</c> notice of context responses while answers come from a snapshot that
/// has not been checked against the files yet.
/// </summary>
public interface ICacheStateProvider
{
    /// <summary>The <c>indexing.cache</c> object, or null when there is none: the cache is off, or nothing was loaded and no scan has committed yet.</summary>
    CacheStatusDto? GetStatus();

    /// <summary>The notice a context response carries, or null unless answers come from a snapshot not yet verified.</summary>
    CacheNotice? GetNotice();
}

/// <summary>How the start-up load step of plan 3.5 ended.</summary>
internal enum CacheLoadKind
{
    /// <summary>The cache is off for this run (the option, or a reason such as a link in the cache path).</summary>
    Disabled,

    /// <summary>The cache is on and there was no snapshot to try.</summary>
    NothingFound,

    /// <summary>Every snapshot tried was refused; nothing was published.</summary>
    Ignored,

    /// <summary>A snapshot was authenticated, materialized and published into the empty store.</summary>
    Loaded,
}

/// <summary>The load step's result: what was published, or why nothing was.</summary>
internal sealed record CacheLoadOutcome(
    CacheLoadKind Kind,
    string? Reason = null,
    long? SnapshotCreatedUtcTicks = null,
    ulong? SnapshotSequence = null,
    long? SizeBytes = null,
    IReadOnlyList<string>? Parsers = null)
{
    public static CacheLoadOutcome Disabled(string reason) => new(CacheLoadKind.Disabled, reason);
}

/// <summary>
/// The cache state of plan 3.10, kept for one process. The coordinator runs the load step through
/// <see cref="RunLoadStep"/> before its first scan and reports how every scan ended; the snapshot
/// writer's status supplies the write fields. States: absent (off, or nothing loaded and no scan
/// committed), <c>ignored</c> (a snapshot was found and refused), <c>verifying</c> (answers come from a
/// loaded snapshot), <c>unverified</c> (the scan that checks it failed), <c>verified</c> (a scan committed
/// in this process). Only <c>verifying</c> and <c>unverified</c> serve cached answers.
///
/// "Serving a loaded snapshot" is recorded before the loaded shards are published, and a committed scan
/// is reported after the store has published it. A reader takes the notice before and after its store
/// reads and labels when either is present, so it is labelled whenever any of its facts can come from
/// the snapshot (at a transition it may be labelled needlessly, never the reverse).
/// </summary>
public sealed class CacheState : ICacheStateProvider
{
    public const string Ignored = "ignored";
    public const string Verifying = "verifying";
    public const string Unverified = "unverified";
    public const string Verified = "verified";

    public const string Pending = "pending";
    public const string Replaced = "replaced";

    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly Func<Action<CacheLoadOutcome>, CacheLoadOutcome>? _load;
    private Func<SnapshotServiceStatus?>? _writer;
    private Func<ScanPhase>? _scanPhase;
    private CacheLoadOutcome? _outcome;
    private bool _scanCommitted;
    private bool _verificationFailed;

    internal CacheState(bool enabled, Func<CacheLoadOutcome>? load = null)
        : this(enabled, load is null ? null : _ => load())
    {
    }

    /// <param name="load">The load step; it calls the action it is given just before loaded facts become visible.</param>
    internal CacheState(bool enabled, Func<Action<CacheLoadOutcome>, CacheLoadOutcome>? load)
    {
        _enabled = enabled;
        _load = load;
    }

    /// <summary>
    /// Runs the load step once, on the coordinator before its first scan; true when shards were
    /// published. "Serving a loaded snapshot" is recorded just before the shards are published (readers
    /// run during the step), and the step's final outcome then replaces it, so a publish that failed
    /// leaves no label. The step itself never throws; should it, the cache counts as off for the run
    /// (unless the step had already announced a load: over-labelling is harmless, the reverse is not) and
    /// the exception reaches the caller, which logs it and scans.
    /// </summary>
    internal bool RunLoadStep()
    {
        var outcome = CacheLoadOutcome.Disabled("the cache is turned off");
        try
        {
            if (_enabled && _load is not null)
                outcome = _load(loading => { lock (_gate) _outcome = loading; });
        }
        catch
        {
            lock (_gate) _outcome ??= outcome;
            throw;
        }
        lock (_gate) _outcome = outcome;
        return outcome.Kind == CacheLoadKind.Loaded;
    }

    /// <summary>Where the write fields come from: the snapshot writer's status.</summary>
    internal void AttachWriter(Func<SnapshotServiceStatus?> writer)
    {
        lock (_gate) _writer = writer;
    }

    /// <summary>
    /// Where the scan phase comes from. With it, a served snapshot reads <c>unverified</c> exactly while the
    /// phase is <see cref="ScanPhase.Error"/> (a worker failed, the scan failed, or a later refresh did not
    /// run) and <c>verifying</c> while any scan runs, so a refresh after a failure reads <c>verifying</c> again.
    /// </summary>
    internal void AttachScanPhase(Func<ScanPhase> phase)
    {
        lock (_gate) _scanPhase = phase;
    }

    /// <summary>A scan committed: from now on nothing served comes from an unchecked snapshot.</summary>
    internal void OnScanCommitted()
    {
        lock (_gate) _scanCommitted = true;
    }

    /// <summary>A scan failed and rolled back. Before any scan committed, a loaded snapshot stays served, unverified.</summary>
    internal void OnScanFailed()
    {
        lock (_gate)
        {
            if (!_scanCommitted) _verificationFailed = true;
        }
    }

    public CacheStatusDto? GetStatus()
    {
        CacheLoadOutcome? outcome;
        bool committed, failed;
        Func<SnapshotServiceStatus?>? writerSource;
        lock (_gate)
        {
            outcome = _outcome;
            committed = _scanCommitted;
            failed = Failed();
            writerSource = _writer;
        }
        if (StateOf(outcome, committed, failed) is not { } state) return null;

        var serving = state is Verifying or Unverified;
        var writer = writerSource?.Invoke();
        var loaded = outcome!.Kind == CacheLoadKind.Loaded ? outcome : null;
        // While cached answers are served, the snapshot they come from; otherwise the newest on disk,
        // which is the loaded one until this process writes its own.
        var fromWriter = !serving && writer?.LastSequence is not null;
        return new CacheStatusDto(
            State: state,
            ServingCached: serving,
            Reason: state == Ignored ? outcome.Reason : null,
            SnapshotAt: fromWriter ? FormatTime(writer!.LastWriteAt) : FormatTicks(loaded?.SnapshotCreatedUtcTicks),
            SnapshotSequence: fromWriter ? (long?)writer!.LastSequence : (long?)loaded?.SnapshotSequence,
            SizeBytes: fromWriter ? writer!.LastSizeBytes : loaded?.SizeBytes,
            LastWriteMs: writer?.LastWriteMilliseconds,
            LastCaptureMs: writer?.LastCaptureMilliseconds is { } capture ? (long)Math.Round(capture, MidpointRounding.AwayFromZero) : null,
            LastWriteError: writer?.LastWriteError,
            Verification: loaded?.Parsers is { } parsers
                ? parsers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .ToDictionary(parser => parser, _ => committed ? Replaced : Pending, StringComparer.Ordinal)
                : null);
    }

    public CacheNotice? GetNotice()
    {
        CacheLoadOutcome? outcome;
        bool committed, failed;
        lock (_gate)
        {
            outcome = _outcome;
            committed = _scanCommitted;
            failed = Failed();
        }
        return StateOf(outcome, committed, failed) is { } state and (Verifying or Unverified)
            ? new CacheNotice { State = state, SnapshotAt = FormatTicks(outcome!.SnapshotCreatedUtcTicks) }
            : null;
    }

    /// <summary>Whether a served snapshot reads unverified: from the scan phase when attached, else from the last scan's outcome.</summary>
    private bool Failed() => _scanPhase is { } phase ? phase() == ScanPhase.Error : _verificationFailed;

    private string? StateOf(CacheLoadOutcome? outcome, bool committed, bool failed)
    {
        if (!_enabled || outcome is null || outcome.Kind == CacheLoadKind.Disabled) return null;
        if (committed) return Verified;
        return outcome.Kind switch
        {
            CacheLoadKind.Loaded => failed ? Unverified : Verifying,
            CacheLoadKind.Ignored => Ignored,
            _ => null,
        };
    }

    private static string? FormatTicks(long? utcTicks)
        => utcTicks is { } ticks && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
            : null;

    private static string? FormatTime(DateTimeOffset? time)
        => time?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
