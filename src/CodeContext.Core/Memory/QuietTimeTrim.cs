// One source file, compiled into the host (CodeContext.Core) and linked into the C# worker, which
// must not reference Core. Each copy gets its own namespace so a test assembly that sees both
// can name either.
using System.Diagnostics;
using System.Runtime;

#if CODECONTEXT_CSHARP_WORKER
namespace CodeContext.CSharp.Worker.Memory
#else
namespace CodeContext.Core.Memory
#endif
{
    /// <summary>When <see cref="QuietTimeTrim"/> runs; immutable. The defaults are the process defaults.</summary>
    public sealed record QuietTimeTrimSettings
    {
        /// <summary>The configuration key (host) and, in its environment form, the variable the C# worker reads.</summary>
        public const string ConfigurationKey = "CodeContext:MemoryTrimSeconds";

        /// <summary><see cref="ConfigurationKey"/> as an environment variable.</summary>
        public const string EnvironmentVariable = "CodeContext__MemoryTrimSeconds";

        public const int DefaultQuietSeconds = 15;

        /// <summary>The largest quiet period accepted from configuration: one day.</summary>
        public const int MaxQuietSeconds = 86_400;

        /// <summary>Time without any activity, after the last one ended, before a trim runs. Zero turns trimming off.</summary>
        public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromSeconds(DefaultQuietSeconds);

        /// <summary>The shortest time from the end of one trim to the start of the next.</summary>
        public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Bytes the process must have allocated since the last trim (or since start) for a trim to be worth its pause.</summary>
        public long MinAllocatedBytes { get; init; } = 32L * 1024 * 1024;

        public bool Enabled => QuietPeriod > TimeSpan.Zero;

        public static readonly QuietTimeTrimSettings Disabled = new() { QuietPeriod = TimeSpan.Zero };

        /// <summary>
        /// The settings for a configured quiet period in whole seconds: absent means the default, 0 turns trimming
        /// off, a value above one day is one day. Anything else (negative, not an integer) is refused: null.
        /// </summary>
        public static QuietTimeTrimSettings? FromSeconds(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new QuietTimeTrimSettings();
            if (!long.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
                return null;
            return new QuietTimeTrimSettings { QuietPeriod = TimeSpan.FromSeconds(Math.Min(seconds, MaxQuietSeconds)) };
        }
    }

    /// <summary>What one trim did, for the log line.</summary>
    public readonly record struct QuietTimeTrimResult(long WorkingSetBefore, long WorkingSetAfter, TimeSpan Pause);

    /// <summary>
    /// Gives memory back to the operating system once a burst of work is over.
    ///
    /// The GC does not return the garbage of a burst (a snapshot load or write, a scan, a run of queries, a
    /// worker's index) while the process is idle: an idle process allocates nothing, so no collection runs and
    /// the dead objects and the space they held stay resident. Only an aggressive, blocking, compacting
    /// collection with large-object compaction decommits that space (<see cref="CompactAndDecommit"/>; a
    /// forced collection frees the objects but keeps the memory committed).
    ///
    /// Every piece of work runs inside <see cref="BeginActivity"/>. A trim runs on a timer thread when all of
    /// these hold: no activity is in flight; <see cref="QuietTimeTrimSettings.QuietPeriod"/> has passed since
    /// the last activity ended; <see cref="QuietTimeTrimSettings.MinInterval"/> has passed since the last trim;
    /// and the process has allocated <see cref="QuietTimeTrimSettings.MinAllocatedBytes"/> since the last trim (or
    /// <see cref="RequestRelease"/> was called since).
    /// Each activity's end re-arms the timer, so steady traffic keeps pushing the trim back and never runs it,
    /// and a quiet process that allocated little is left alone: it is looked at once per activity, not polled.
    /// An activity that starts while a trim runs waits for the collection like any other GC pause.
    ///
    /// The decision is separate from the collection (<c>trim</c>), and time and the allocation counter are
    /// injected, so the rules are tested on fake time without collecting anything.
    /// </summary>
    public sealed class QuietTimeTrim : IDisposable
    {
        private readonly QuietTimeTrimSettings _settings;
        private readonly TimeProvider _time;
        private readonly Func<long> _allocatedBytes;
        private readonly Func<QuietTimeTrimResult> _trim;
        private readonly Action<QuietTimeTrimResult>? _trimmed;
        private readonly object _gate = new();
        private readonly ITimer? _timer;

        // Guarded by _gate.
        private int _active;
        private bool _trimming;
        private bool _disposed;
        private DateTimeOffset _lastActivityEnd;
        private DateTimeOffset? _lastTrimEnd;
        private long _allocatedAtLastTrim;
        private int _trims;
        private bool _releaseRequested;

        /// <param name="trim">The collection; <see cref="CompactAndDecommit"/> outside tests.</param>
        /// <param name="trimmed">Called after each trim, outside the lock (a log line). An exception from it is swallowed.</param>
        public QuietTimeTrim(
            QuietTimeTrimSettings settings,
            TimeProvider time,
            Func<long>? allocatedBytes = null,
            Func<QuietTimeTrimResult>? trim = null,
            Action<QuietTimeTrimResult>? trimmed = null)
        {
            _settings = settings;
            _time = time;
            _allocatedBytes = allocatedBytes ?? (() => GC.GetTotalAllocatedBytes(precise: false));
            _trim = trim ?? CompactAndDecommit;
            _trimmed = trimmed;
            _lastActivityEnd = time.GetUtcNow();
            if (settings.Enabled)
                _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public bool Enabled => _timer is not null;

        /// <summary>Trims run so far.</summary>
        public int Trims
        {
            get { lock (_gate) return _trims; }
        }

        /// <summary>Activities in flight.</summary>
        internal int Active
        {
            get { lock (_gate) return _active; }
        }

        /// <summary>
        /// Marks a piece of work in flight until the returned scope is disposed. No trim starts while any scope is
        /// open; the end of the last one starts the quiet period again. Disposing a scope twice counts once.
        /// </summary>
        public IDisposable BeginActivity()
        {
            if (_timer is null) return NoActivity.Instance;
            lock (_gate)
            {
                if (_disposed) return NoActivity.Instance;
                _active++;
            }
            return new Activity(this);
        }

        /// <summary>
        /// Says that memory was let go that the allocation counter does not see: a worker process stopped (the
        /// pipes, threads and buffers that served it) after work that allocated little. The next trim then runs
        /// after the quiet period whatever was allocated, once; the quiet period and the minimum interval still
        /// apply, and it counts as the end of an activity.
        /// </summary>
        public void RequestRelease()
        {
            if (_timer is null) return;
            lock (_gate)
            {
                if (_disposed) return;
                _releaseRequested = true;
                _lastActivityEnd = _time.GetUtcNow();
                if (_active == 0 && !_trimming) ArmLocked(_settings.QuietPeriod);
            }
        }

        private void EndActivity()
        {
            lock (_gate)
            {
                if (_active > 0) _active--;
                _lastActivityEnd = _time.GetUtcNow();
                if (_active == 0 && !_disposed) ArmLocked(_settings.QuietPeriod);
            }
        }

        private void ArmLocked(TimeSpan delay)
            => _timer?.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        private void OnTimer()
        {
            DateTimeOffset started;
            lock (_gate)
            {
                // Busy: the activity's end re-arms. Trimming: the trim's end re-arms when needed.
                if (_disposed || _active > 0 || _trimming) return;
                started = _time.GetUtcNow();
                var due = _lastActivityEnd + _settings.QuietPeriod;
                if (_lastTrimEnd is { } last && last + _settings.MinInterval > due) due = last + _settings.MinInterval;
                if (started < due)
                {
                    ArmLocked(due - started);
                    return;
                }
                // Not worth a pause: the next activity's end looks again.
                if (!_releaseRequested && _allocatedBytes() - _allocatedAtLastTrim < _settings.MinAllocatedBytes) return;
                _releaseRequested = false;
                _trimming = true;
            }

            QuietTimeTrimResult? result = null;
            try
            {
                result = _trim();
            }
            catch
            {
                // A collection does not throw; a test's trim may. Either way the next burst tries again.
            }
            finally
            {
                lock (_gate)
                {
                    _trimming = false;
                    _trims++;
                    _lastTrimEnd = _time.GetUtcNow();
                    _allocatedAtLastTrim = _allocatedBytes();
                    // Work that ended while the trim ran is a burst of its own; its re-arm found the trim running.
                    if (!_disposed && _active == 0 && _lastActivityEnd >= started) ArmLocked(_settings.QuietPeriod);
                }
            }
            if (result is { } done)
            {
                try
                {
                    _trimmed?.Invoke(done);
                }
                catch
                {
                    // A log line must not take the timer down.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
            }
            _timer?.Dispose();
        }

        /// <summary>
        /// One aggressive, blocking, compacting collection of every generation, the large object heap included, which
        /// also decommits the space it frees. Then the free native heap is returned too (<see cref="ReturnFreeNativeMemory"/>). Measured pause: 80 to 240 ms on this repository's host and C# worker.
        /// </summary>
        public static QuietTimeTrimResult CompactAndDecommit()
        {
            var before = Environment.WorkingSet;
            var started = Stopwatch.GetTimestamp();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            ReturnFreeNativeMemory();
            var pause = Stopwatch.GetElapsedTime(started);
            return new QuietTimeTrimResult(before, Environment.WorkingSet, pause);
        }

#if !CODECONTEXT_CSHARP_WORKER
        private static volatile bool s_nativeTrimUnavailable;
#endif

        /// <summary>
        /// On Linux with glibc, gives the free memory of the native heap back to the operating system
        /// (<c>malloc_trim(0)</c>); elsewhere, and in the C# worker's copy, does nothing. The runtime's own native allocations (the JIT compiling
        /// a code path for the first time, thread start-up, interop buffers) land in glibc's per-thread arenas, which
        /// glibc does not shrink on its own: measured on this repository's host, about 13 MB stayed resident after a
        /// first edit, a hibernation and the collection above. Returns whether the call was made; an unavailable
        /// <c>libc.so.6</c> or entry point (musl, another OS) turns it off for the process.
        /// </summary>
        public static bool ReturnFreeNativeMemory()
        {
#if CODECONTEXT_CSHARP_WORKER
            // The worker reaches the system only through its disk guard, and its banned-symbol list forbids
            // platform invoke; it keeps the managed trim alone.
            return false;
#else
            if (!OperatingSystem.IsLinux() || s_nativeTrimUnavailable) return false;
            try
            {
                MallocTrim(0);
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                s_nativeTrimUnavailable = true;
                return false;
            }
#endif
        }

#if !CODECONTEXT_CSHARP_WORKER
        [System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "malloc_trim")]
        private static extern int MallocTrim(nuint pad);
#endif

        private sealed class Activity(QuietTimeTrim owner) : IDisposable
        {
            private int _ended;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _ended, 1) == 0) owner.EndActivity();
            }
        }

        private sealed class NoActivity : IDisposable
        {
            public static readonly NoActivity Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
