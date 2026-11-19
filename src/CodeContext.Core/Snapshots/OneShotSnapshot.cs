using System.Diagnostics;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// The outcome of <see cref="OneShotSnapshot.TryOpen(string, IConfiguration?)"/>: a snapshot to answer from, or why
    /// there is none (the caller then starts an instance, which loads, verifies and re-walks as on any start).
    /// </summary>
    public sealed record OneShotSnapshotOpen(OneShotSnapshot? Snapshot, string? Reason);

    /// <summary>
    /// A snapshot opened by a process that is not an instance (<c>codecontext query</c> with nothing running), to answer
    /// from it once and exit. It reads only: no key is created, no cache directory or <c>.gitignore</c> is made, the
    /// writer lock is not taken, no file is deleted or written, no instance is registered and no worker starts (the
    /// worker catalog is read for the extensions and parsers a host would route, nothing more). A concurrent or later
    /// instance is unaffected.
    ///
    /// The snapshot is the newest one that authenticates with the per-user key under the same rules as a host's load
    /// (name, mode, size, header, host version and root, HMAC, structure, at most
    /// <see cref="SnapshotService.MaxCostlyRefusals"/> costly refusals), and only when the cache is on in the
    /// configuration a host would read (<c>CodeContext:CacheEnabled</c>). It is used only when
    /// <see cref="SnapshotFreshness"/> finds nothing a scan reads newer than its capture; then it is materialized as a host
    /// loads it and answered through <see cref="ContextService"/>, with every answer labelled
    /// <see cref="CacheState.Snapshot"/>.
    /// </summary>
    public sealed class OneShotSnapshot
    {
        private OneShotSnapshot(IContextService context, DateTime capturedUtc, OneShotTimings timings)
        {
            Context = context;
            CapturedUtc = capturedUtc;
            Timings = timings;
        }

        /// <summary>Answers from the snapshot, labelled; the same service a host answers with.</summary>
        public IContextService Context { get; }

        /// <summary>When the snapshot was captured.</summary>
        public DateTime CapturedUtc { get; }

        internal OneShotTimings Timings { get; }

        /// <summary>Opens the snapshot of <paramref name="rootPath"/>; never throws for a missing or refused one.</summary>
        /// <param name="configuration">What a host started here would read (<c>CodeContext:CacheEnabled</c>); null for none.</param>
        public static OneShotSnapshotOpen TryOpen(string rootPath, IConfiguration? configuration)
        {
            var options = new CodeContextOptions { RootPath = rootPath };
            SnapshotServiceCollectionExtensions.ApplyConfiguration(options, configuration);
            return TryOpen(options, new WorkerCatalog(NullLogger<WorkerCatalog>.Instance));
        }

        /// <summary><see cref="TryOpen(string, IConfiguration?)"/> with the options and the catalog given (tests).</summary>
        /// <param name="checkAgainst">For tests: the time files are compared with in place of the capture time, standing for a
        /// capture made later (a status change time cannot be set back to age a tree).</param>
        internal static OneShotSnapshotOpen TryOpen(CodeContextOptions options, IWorkerCatalog catalog, DateTime? checkAgainst = null)
        {
            try
            {
                return TryOpenCore(options, catalog, checkAgainst);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new OneShotSnapshotOpen(null, exception.Message);
            }
        }

        private static OneShotSnapshotOpen TryOpenCore(CodeContextOptions options, IWorkerCatalog catalog, DateTime? checkAgainst)
        {
            var started = Stopwatch.GetTimestamp();
            var settings = SnapshotServiceSettings.From(options);
            if (!settings.Enabled) return Unavailable("the cache is turned off");
            if (!BitConverter.IsLittleEndian) return Unavailable("snapshots are read only by little-endian processes");
            if (string.IsNullOrEmpty(settings.KeyPath)) return Unavailable("there is no user profile folder holding a snapshot key");
            var root = Path.GetFullPath(settings.RootPath);
            if (!Directory.Exists(root)) return Unavailable($"the root '{root}' is not a directory");

            // Never created here: without a key no snapshot can authenticate.
            if (!SnapshotKey.TryRead(settings.KeyPath, out var key)) return Unavailable("there is no snapshot key yet");
            var directory = CacheDirectory.OpenExisting(root, settings.WorkspaceId, out var refusal);
            if (directory is null) return Unavailable($"no saved index ({refusal})");
            var keyAndDirectory = Stopwatch.GetElapsedTime(started);

            // The host's routing, from the manifests alone: no supervisor exists until an index, which never runs here.
            var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
            var coverage = AnalysisCoverageRegistry.ForStore(factory);
            var workers = new LanguageWorkerService(
                catalog,
                new AnalysisDeltaApplier((Repositories.IGenerationalGraphStore)factory.CreateGraphRepository(),
                    NullLogger<AnalysisDeltaApplier>.Instance, coverage),
                new ParserSessionRegistry(), Options.Create(options), NullLoggerFactory.Instance);

            var expectations = new SnapshotExpectations(settings.HostVersion, root, OperatingSystem.IsWindows());
            var costly = 0;
            string? firstRefusal = null;
            // The newest authentic snapshot decides: an older one is never fresher.
            var (_, opened) = SnapshotStartup.TryNewestFirst<OneShotSnapshotOpen>(
                directory.EnumerateOwnFiles(), key, expectations, ref costly,
                (candidate, file, open) => (true, Answer(candidate, file, open), SnapshotRejection.None, null),
                (candidate, rejection, reason) => firstRefusal ??= $"{candidate.Name}: {rejection}: {reason}");
            return opened ?? Unavailable(firstRefusal is null ? "no saved index" : $"no saved index could be used ({firstRefusal})");

            OneShotSnapshotOpen Answer(CacheFile candidate, SnapshotFile file, TimeSpan open)
            {
                if (file.Header.Unverified)
                    return Unavailable($"{candidate.Name} was written while the index was unverified");
                if (file.Header.CreatedUtcTicks < DateTime.MinValue.Ticks || file.Header.CreatedUtcTicks > DateTime.MaxValue.Ticks)
                    return Unavailable($"{candidate.Name}: the capture time is not a time");
                // Which files are indexed, and by what, is the worker set's: a worker installed, removed or replaced since
                // the snapshot would index the tree differently, and only an instance finds out how.
                if (!file.HostMeta.TryGetValue(SnapshotFormat.WorkerSetKey, out var writtenBy))
                    return Unavailable($"{candidate.Name} records no set of workers");
                if (!string.Equals(writtenBy, workers.WorkerSetFingerprint(), StringComparison.Ordinal))
                    return Unavailable($"{candidate.Name} was written with another set of workers");
                var capturedUtc = new DateTime(file.Header.CreatedUtcTicks, DateTimeKind.Utc);
                var snapshotAt = CacheState.FormatTicks(file.Header.CreatedUtcTicks);
                bool Accept(InMemoryDatabase.ShardKey shard)
                    => string.Equals(shard.WorkspaceId, settings.WorkspaceId, StringComparison.Ordinal) && workers.IsIndexedParser(shard.ParserId);

                // What every shard read besides its files, as its worker reported it: without that report nothing says
                // whether the shard is current, and only an instance (whose worker re-reads) may answer from it.
                var inputs = new List<WorkerInputs>();
                foreach (var shard in file.Shards.Where(shard => Accept(shard.Key)))
                {
                    if (ReadInputs(shard.ReadInfo()) is not { } reported)
                        return Unavailable($"{candidate.Name}: the worker of shard {shard.Key} reported no inputs for it");
                    inputs.Add(reported);
                }
                var freshness = SnapshotFreshness.For(options, workers, checkAgainst ?? capturedUtc, inputs, file.Header.RootTimeTicks);
                if (!freshness.Unchanged) return Unavailable(freshness.Describe(snapshotAt ?? "an unknown time"));

                var materializedAt = Stopwatch.GetTimestamp();
                var loaded = SnapshotLoader.Materialize(file, Accept);
                if (!loaded.Loaded) return Unavailable($"{candidate.Name}: {loaded.Rejection}: {loaded.Reason}");
                if (!SnapshotStartup.TryAnnotations(loaded, directory, out var annotations, out _, out var failure))
                    return Unavailable($"{candidate.Name}: {failure}");
                // A worker that keeps saved state (it sends fingerprints) has its state bound to every shard a
                // verified store wrote. One without it was written after a verification failed, a save failed or the
                // pairing broke: facts no worker confirmed, which only an instance may serve, labelled.
                if (annotations.FirstOrDefault(pair => pair.Value is { Fingerprints.Count: > 0, State: null }) is { Key: var unpaired, Value: not null })
                    return Unavailable($"{candidate.Name}: shard {unpaired} was saved without its worker's state");
                factory.Database.PublishLoadedShards(loaded.ForPublish(), annotations);
                var materialize = Stopwatch.GetElapsedTime(materializedAt);

                var notice = new CacheNotice { State = CacheState.Snapshot, SnapshotAt = snapshotAt };
                var context = new ContextService(
                    factory.CreateNodeRepository(), factory.CreateEdgeRepository(), factory.CreateFileMetadataRepository(),
                    Options.Create(options), workers, coverage, new FixedNotice(notice));
                return new OneShotSnapshotOpen(
                    new OneShotSnapshot(context, capturedUtc,
                        new OneShotTimings(keyAndDirectory, open, freshness.Elapsed, freshness.Checked, materialize)),
                    null);
            }
        }

        /// <summary>A shard's reported inputs, or null when its worker sent none or they do not read as two lists of paths.</summary>
        private static WorkerInputs? ReadInputs(SnapshotShardInfo info)
        {
            if (info.Inputs is not { Length: > 0 } bytes) return null;
            try
            {
                var inputs = System.Text.Json.JsonSerializer.Deserialize(bytes, ParserProtocolJsonContext.Default.WorkerInputs);
                return inputs?.Files is null || inputs.Absent is null || inputs.Files.Contains(null!) || inputs.Absent.Contains(null!) ? null : inputs;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private static OneShotSnapshotOpen Unavailable(string reason) => new(null, reason);

        /// <summary>Every answer carries the same notice: it comes from the snapshot, and nothing here ever verifies it.</summary>
        private sealed class FixedNotice(CacheNotice notice) : ICacheStateProvider
        {
            public CacheStatusDto? GetStatus() => null;

            public CacheNotice? GetNotice() => new() { State = notice.State, SnapshotAt = notice.SnapshotAt };
        }
    }

    /// <summary>Where a one-shot open spent its time (measurements).</summary>
    internal sealed record OneShotTimings(
        TimeSpan KeyAndDirectory, TimeSpan Authenticate, TimeSpan Freshness, int FreshnessEntries, TimeSpan Materialize);
}
