using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// What the start-up step handed over to <see cref="SnapshotService"/>: the mode the cache runs in, and
    /// for a writer the directory, key, host meta, next sequence and the view of what was loaded (so a
    /// start does not rewrite the snapshot it has just loaded). The writer lock itself is taken from
    /// <see cref="SnapshotStartup.TakeLock"/>.
    /// </summary>
    internal sealed record SnapshotStartupResult(
        SnapshotCacheMode Mode,
        string? Reason,
        CacheLoadOutcome Outcome,
        CacheDirectory? Directory = null,
        byte[]? Key = null,
        SnapshotHostMeta? Host = null,
        ulong NextSequence = 1,
        SnapshotView? LoadedView = null);

    /// <summary>
    /// The start-up step of plan 3.5, run once as the first step of <see cref="IndexCoordinator"/> before
    /// its first scan: the key, the cache directory, the writer lock, then (as the lock holder only) the
    /// <c>.gitignore</c> and the clean-up of temp files and older snapshots; then the newest snapshot that
    /// authenticates and materializes is published into the empty store with its annotations, and its
    /// coverage is restored. A second instance (no lock) loads the same way and deletes nothing.
    ///
    /// Candidates are tried newest first. Refusals before hashing (name, length, header, host meta, Unix
    /// claim, size cap) are free; at most <see cref="SnapshotService.MaxCostlyRefusals"/> refusals at or
    /// after authentication (an I/O failure after the host meta checks included) are tried. Shards whose
    /// parser no registered worker indexes, or whose workspace is not the host's, are dropped.
    ///
    /// Never throws: any failure means no cache for this run, with the reason, and the caller scans as
    /// it would without one. A snapshot is only data: authenticated graph facts, parser version strings
    /// and coverage. Nothing in it is used as a path, a command or a setting.
    /// </summary>
    internal sealed class SnapshotStartup : IDisposable
    {
        private readonly SnapshotServiceSettings _settings;
        private readonly InMemoryDatabase? _database;
        private readonly IAnalysisCoverageRegistry? _coverage;
        private readonly Func<string, bool>? _isIndexedParser;
        private readonly Action<IReadOnlyCollection<string>>? _onLoaded;
        private readonly ILogger _logger;

        private readonly object _gate = new();
        private readonly List<Action<SnapshotStartupResult>> _waiters = [];
        private SnapshotStartupResult? _result;
        private FileStream? _lock;
        private CacheLoadOutcome? _published;
        private bool _disposed;

        /// <param name="isIndexedParser">Whether a registered worker indexes this parser id; null accepts every parser.</param>
        /// <param name="onLoaded">Told which parsers have published facts, before the first scan.</param>
        public SnapshotStartup(
            SnapshotServiceSettings settings,
            InMemoryDatabase? database,
            IAnalysisCoverageRegistry? coverage,
            Func<string, bool>? isIndexedParser,
            Action<IReadOnlyCollection<string>>? onLoaded,
            ILogger logger)
        {
            _settings = settings;
            _database = database;
            _coverage = coverage;
            _isIndexedParser = isIndexedParser;
            _onLoaded = onLoaded;
            _logger = logger;
        }

        public bool Enabled => _settings.Enabled;

        /// <summary>The step's result once it has run, else null.</summary>
        public SnapshotStartupResult? Result
        {
            get { lock (_gate) return _result; }
        }

        /// <summary>Runs the step (once; later calls return the first outcome). Never throws.</summary>
        /// <param name="beforePublish">Told the loaded outcome just before the loaded shards become visible.</param>
        public CacheLoadOutcome Run(Action<CacheLoadOutcome>? beforePublish = null)
        {
            lock (_gate)
            {
                if (_result is not null) return _result.Outcome;
            }

            SnapshotStartupResult result;
            try
            {
                result = RunCore(beforePublish);
            }
            catch (Exception exception)
            {
                // Published shards stay published and labelled; only the writer side is given up.
                result = Disable($"the cache could not be prepared: {exception.Message}");
                if (_published is { } published) result = result with { Outcome = published };
            }

            List<Action<SnapshotStartupResult>> waiters;
            lock (_gate)
            {
                _result = result;
                waiters = [.. _waiters];
                _waiters.Clear();
                if (_disposed) ReleaseLockLocked();
            }
            foreach (var waiter in waiters) Notify(waiter, result);
            return result.Outcome;
        }

        /// <summary>Calls <paramref name="callback"/> with the result: at once when the step has run, else when it does.</summary>
        public void OnCompleted(Action<SnapshotStartupResult> callback)
        {
            SnapshotStartupResult? result;
            lock (_gate)
            {
                result = _result;
                if (result is null) _waiters.Add(callback);
            }
            if (result is not null) Notify(callback, result);
        }

        /// <summary>Hands the writer lock to the caller, who holds it from now on; null when there is none.</summary>
        public FileStream? TakeLock()
        {
            lock (_gate)
            {
                var stream = _lock;
                _lock = null;
                return stream;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                ReleaseLockLocked();
            }
        }

        private void Notify(Action<SnapshotStartupResult> callback, SnapshotStartupResult result)
        {
            try
            {
                callback(result);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Snapshot start-up hand-over failed.");
            }
        }

        private SnapshotStartupResult RunCore(Action<CacheLoadOutcome>? beforePublish)
        {
            if (!_settings.Enabled) return new SnapshotStartupResult(SnapshotCacheMode.Disabled, "the cache is turned off",
                CacheLoadOutcome.Disabled("the cache is turned off"));
            lock (_gate)
            {
                if (_disposed) return Disable("the host is stopping");
            }
            if (_database is null) return Disable("the graph store is not the in-memory store");
            if (string.IsNullOrEmpty(_settings.RootPath) || !Directory.Exists(_settings.RootPath))
                return Disable($"the root '{_settings.RootPath}' is not a directory");
            if (!BitConverter.IsLittleEndian) return Disable("snapshots are written only by little-endian processes");
            if (string.IsNullOrEmpty(_settings.KeyPath)) return Disable("there is no user profile folder to keep the snapshot key in");

            // The key first: a key problem must leave the repository untouched.
            byte[] key;
            try
            {
                key = SnapshotKey.LoadOrCreate(_settings.KeyPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Disable($"the snapshot key at '{_settings.KeyPath}' cannot be used: {exception.Message}");
            }

            var directory = CacheDirectory.Open(_settings.RootPath, _settings.WorkspaceId, out var refusal);
            if (directory is null) return Disable(refusal!);

            _settings.BeforeLock?.Invoke(directory);
            var lockResult = directory.TryAcquireWriterLock(out var lockStream, out var lockReason);
            if (lockResult == WriterLockResult.Refused) return Disable(lockReason!);
            var writer = lockResult == WriterLockResult.Acquired;
            if (writer)
            {
                lock (_gate) _lock = lockStream;
                if (directory.EnsureGitignore() is { } gitignoreRefusal) return Disable(gitignoreRefusal);
            }
            else
            {
                _logger.LogInformation(
                    "Snapshot cache is read-only for this instance: another instance writes snapshots for {Root}.",
                    _settings.RootPath);
            }

            var host = new SnapshotHostMeta(
                _settings.HostVersion, Path.GetFullPath(_settings.RootPath), RuntimeInformation.RuntimeIdentifier,
                OperatingSystem.IsWindows());
            // Checked again before anything is deleted (see CacheDirectory.RecheckLevels).
            if (writer && directory.RecheckLevels() is { } chainRefusal) return Disable(chainRefusal);

            var (outcome, view, nextSequence) = SelectAndLoad(directory, key, host, writer, beforePublish);
            return writer
                ? new SnapshotStartupResult(SnapshotCacheMode.Writer, null, outcome, directory, key, host, nextSequence, view)
                : new SnapshotStartupResult(SnapshotCacheMode.ReadOnly, lockReason, outcome, Host: host, Key: key);
        }

        private sealed record Refusal(CacheFile File, SnapshotRejection Rejection, string? Reason);

        private static bool IsUsable(ulong sequence) => sequence is >= 1 and <= SnapshotService.MaxSequence;

        /// <summary>A refusal at or after authentication, which costs a full read and hash of the file.</summary>
        internal static bool IsCostly(SnapshotRejection rejection, bool passedHostMeta)
            => rejection is SnapshotRejection.Authentication or SnapshotRejection.Structure or SnapshotRejection.Unexpected
                || (rejection == SnapshotRejection.Io && passedHostMeta);

        /// <summary>
        /// Whether a refusal proves the file can never be kept: every check of the file's own bytes, name, host
        /// meta, size or (Unix) mode. An <see cref="SnapshotRejection.Io"/> refusal (a sharing violation from an
        /// antivirus, a backup or another reader) or an <see cref="SnapshotRejection.Unexpected"/> one proves
        /// nothing about the file, which a later start may load.
        /// </summary>
        internal static bool ProvesInvalid(SnapshotRejection rejection)
            => rejection is not (SnapshotRejection.None or SnapshotRejection.Io or SnapshotRejection.Unexpected);

        /// <summary>
        /// Plan 3.5, rule 11, and the load. Tries the snapshots with a usable sequence, newest first, until one
        /// loads; publishes it when the store is empty. As the lock holder, deletes every temp file and every
        /// snapshot file but the kept one, tried or not, and returns the next sequence: one above the larger of
        /// the kept file's and the highest usable sequence among files that could not be deleted, except those
        /// whose refusal in this start proves them invalid (<see cref="ProvesInvalid"/>). An untried file, or
        /// one refused for I/O, may still be valid and must not outrank a new write; a proven-invalid one can
        /// never be kept.
        /// </summary>
        private (CacheLoadOutcome Outcome, SnapshotView? View, ulong NextSequence) SelectAndLoad(
            CacheDirectory directory, byte[] key, SnapshotHostMeta host, bool writer, Action<CacheLoadOutcome>? beforePublish)
        {
            var started = Stopwatch.GetTimestamp();
            var expectations = new SnapshotExpectations(host.HostVersion, host.RootPath, host.PathsCaseInsensitive);
            // Phase-3 behaviour for a store that already holds facts (never so in a host): only open, never publish.
            var load = _database!.NodeCount == 0 && _database.EdgeCount == 0;

            var refusals = new List<Refusal>();
            var costly = 0;
            var files = directory.EnumerateOwnFiles();
            var (kept, loaded) = TryCandidates(files, key, expectations, load, refusals, ref costly);
            if (kept is null && !writer && refusals.Exists(r => r.Rejection == SnapshotRejection.Io && !File.Exists(r.File.Path)))
            {
                // A file vanished between selection and open (the writer retired it): select once more.
                files = directory.EnumerateOwnFiles();
                (kept, loaded) = TryCandidates(
                    files.Where(file => !refusals.Exists(r => string.Equals(r.File.Path, file.Path, StringComparison.Ordinal))).ToList(),
                    key, expectations, load, refusals, ref costly);
            }

            ulong nextSequence = 1;
            if (writer)
            {
                ulong highestLeft = 0;
                var busy = 0;
                foreach (var file in files)
                {
                    if (kept is not null && string.Equals(file.Path, kept.Path, StringComparison.Ordinal)) continue;
                    if (CacheDirectory.TryDelete(file)) continue;
                    busy++;
                    var provenInvalid = refusals.Exists(r =>
                        string.Equals(r.File.Path, file.Path, StringComparison.Ordinal) && ProvesInvalid(r.Rejection));
                    if (!provenInvalid && IsUsable(file.Sequence) && file.Sequence > highestLeft) highestLeft = file.Sequence;
                }
                if (busy > 0) _logger.LogDebug("{Count} older snapshot file(s) are in use and will be deleted later.", busy);
                nextSequence = Math.Max(kept?.Sequence ?? 0, highestLeft) + 1;
            }

            if (kept is null)
            {
                var outcome = refusals.Count == 0
                    ? new CacheLoadOutcome(CacheLoadKind.NothingFound)
                    : new CacheLoadOutcome(CacheLoadKind.Ignored,
                        $"{refusals[0].File.Name}: {refusals[0].Rejection}: {refusals[0].Reason}");
                if (refusals.Count > 0)
                    _logger.LogInformation("No snapshot was used ({Reason}); scanning without one.", outcome.Reason);
                return (outcome, null, nextSequence);
            }
            if (loaded is null)
            {
                return (new CacheLoadOutcome(CacheLoadKind.Ignored, "the graph store already held facts"), null, nextSequence);
            }

            var (result, annotations, coverage) = loaded.Value;
            var header = result.Header!;
            var parsers = result.Shards.Select(shard => shard.Key.ParserId).Distinct(StringComparer.Ordinal).ToList();
            var published = new CacheLoadOutcome(
                CacheLoadKind.Loaded, null, header.CreatedUtcTicks, header.Sequence, header.FileLength, parsers);
            // Readers run while this step does: the state says "serving a loaded snapshot" before any loaded
            // fact is visible, so no answer from it goes unlabelled. A failed publish corrects it (RunLoadStep).
            beforePublish?.Invoke(published);
            // The store generation stays where it is (0): it does not resume from the header. Generations are
            // allocated per process, as the last committed one plus one, and no worker survives a restart, so a
            // fresh one rejects no legitimate delta and can receive no stale one.
            _database.PublishLoadedShards(result.ForPublish(), annotations);
            _published = published;
            // At once, so a failure below cannot leave loaded facts that the "nothing to index" shortcut skips.
            _onLoaded?.Invoke(parsers);

            foreach (var (shardKey, projects) in coverage) _coverage?.Report(shardKey.ParserId, shardKey.WorkspaceId, projects);
            var (shards, publishedAnnotations) = _database.LatchPublished();
            var view = SnapshotView.Of(shards, publishedAnnotations);

            foreach (var dropped in result.Dropped)
                _logger.LogInformation("Snapshot shard {Shard} was not loaded: no registered worker indexes it for this host.", dropped);
            _logger.LogInformation(
                "Loaded snapshot {Name} ({Bytes} bytes, {Shards} shards) in {Milliseconds} ms; answering from it while the files are checked.",
                kept.Name, header.FileLength, result.Shards.Count, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return (published, view, nextSequence);
        }

        private (CacheFile? Kept, (SnapshotLoadResult Result, List<KeyValuePair<ShardKey, ShardAnnotation>> Annotations,
            List<(ShardKey Key, IReadOnlyList<ProjectCoverage> Projects)> Coverage)? Loaded) TryCandidates(
            IReadOnlyList<CacheFile> files, byte[] key, SnapshotExpectations expectations, bool load, List<Refusal> refusals,
            ref int costly)
        {
            foreach (var candidate in files
                         .Where(file => file.Kind == CacheFileKind.Snapshot && IsUsable(file.Sequence))
                         .OrderByDescending(file => file.Sequence).ThenBy(file => file.Name, StringComparer.Ordinal))
            {
                if (costly >= SnapshotService.MaxCostlyRefusals) break;

                SnapshotRejection rejection;
                string? reason;
                bool passedHostMeta;
                if (!load)
                {
                    var opened = SnapshotFile.Open(candidate.Path, key, expectations);
                    using (opened.File)
                    {
                        if (opened.File is not null) return (candidate, null);
                    }
                    (rejection, reason, passedHostMeta) = (opened.Rejection, opened.Reason, opened.PassedHostMeta);
                }
                else
                {
                    var result = SnapshotLoader.Load(candidate.Path, key, expectations, Accept);
                    if (!result.Loaded)
                    {
                        (rejection, reason, passedHostMeta) = (result.Rejection, result.Reason, result.PassedHostMeta);
                    }
                    else if (TryAnnotations(result, out var annotations, out var coverage, out var coverageFailure))
                    {
                        return (candidate, (result, annotations, coverage));
                    }
                    else
                    {
                        (rejection, reason, passedHostMeta) = (SnapshotRejection.Structure, coverageFailure, true);
                    }
                }

                refusals.Add(new Refusal(candidate, rejection, reason));
                if (IsCostly(rejection, passedHostMeta)) costly++;
                _logger.LogDebug("Ignoring snapshot {Name}: {Rejection} {Reason}.", candidate.Name, rejection, reason);
            }
            return (null, null);
        }

        private bool Accept(ShardKey key)
            => string.Equals(key.WorkspaceId, _settings.WorkspaceId, StringComparison.Ordinal)
                && (_isIndexedParser?.Invoke(key.ParserId) ?? true);

        /// <summary>
        /// Each loaded shard's annotation (parser version, coverage) as the store recorded it at capture, and
        /// the coverage to restore. Coverage that is not a JSON list of projects refuses the file: an
        /// authenticated file that fails a check is never answered from.
        /// </summary>
        private static bool TryAnnotations(
            SnapshotLoadResult result,
            out List<KeyValuePair<ShardKey, ShardAnnotation>> annotations,
            out List<(ShardKey Key, IReadOnlyList<ProjectCoverage> Projects)> coverage,
            out string? failure)
        {
            annotations = [];
            coverage = [];
            failure = null;
            foreach (var shard in result.Shards)
            {
                List<ProjectCoverage>? projects = null;
                if (shard.Info.Coverage is { Length: > 0 } bytes)
                {
                    try
                    {
                        projects = JsonSerializer.Deserialize(bytes, ParserProtocolJsonContext.Default.ListProjectCoverage);
                    }
                    catch (JsonException exception)
                    {
                        failure = $"the coverage of shard {shard.Key} does not parse: {exception.Message}";
                        return false;
                    }
                    if (projects is null || projects.Contains(null!))
                    {
                        failure = $"the coverage of shard {shard.Key} is not a list of projects";
                        return false;
                    }
                }
                if (shard.Info.ParserVersion is null && projects is null) continue;
                annotations.Add(KeyValuePair.Create(shard.Key, new ShardAnnotation(shard.Info.ParserVersion, projects)));
                if (projects is not null) coverage.Add((shard.Key, projects));
            }
            return true;
        }

        private SnapshotStartupResult Disable(string reason)
        {
            lock (_gate) ReleaseLockLocked();
            _logger.LogWarning("Snapshot cache disabled for this run: {Reason}.", reason);
            return new SnapshotStartupResult(SnapshotCacheMode.Disabled, reason, CacheLoadOutcome.Disabled(reason));
        }

        private void ReleaseLockLocked()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }
}
