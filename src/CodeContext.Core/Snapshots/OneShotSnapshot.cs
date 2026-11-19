using System.Diagnostics;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
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
        internal static OneShotSnapshotOpen TryOpen(CodeContextOptions options, IWorkerCatalog catalog)
        {
            try
            {
                return TryOpenCore(options, catalog);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new OneShotSnapshotOpen(null, exception.Message);
            }
        }

        private static OneShotSnapshotOpen TryOpenCore(CodeContextOptions options, IWorkerCatalog catalog)
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
            foreach (var candidate in directory.EnumerateOwnFiles()
                         .Where(file => file.Kind == CacheFileKind.Snapshot && SnapshotStartup.IsUsable(file.Sequence))
                         .OrderByDescending(file => file.Sequence).ThenBy(file => file.Name, StringComparer.Ordinal))
            {
                if (costly >= SnapshotService.MaxCostlyRefusals) break;
                var openedAt = Stopwatch.GetTimestamp();
                var opened = SnapshotFile.Open(candidate.Path, key, expectations);
                if (opened.File is not { } file)
                {
                    firstRefusal ??= $"{candidate.Name}: {opened.Rejection}: {opened.Reason}";
                    if (SnapshotStartup.IsCostly(opened.Rejection, opened.PassedHostMeta)) costly++;
                    continue;
                }
                using (file)
                {
                    var open = Stopwatch.GetElapsedTime(openedAt);
                    if (file.Header.CreatedUtcTicks < DateTime.MinValue.Ticks || file.Header.CreatedUtcTicks > DateTime.MaxValue.Ticks)
                        return Unavailable($"{candidate.Name}: the capture time is not a time");
                    var capturedUtc = new DateTime(file.Header.CreatedUtcTicks, DateTimeKind.Utc);
                    // The newest authentic snapshot decides: an older one is never fresher.
                    var freshness = SnapshotFreshness.For(options, workers, catalog)(capturedUtc);
                    if (!freshness.Unchanged)
                        return Unavailable($"'{freshness.FirstMoved}' changed after the saved index of {CacheState.FormatTicks(file.Header.CreatedUtcTicks)}");

                    var materializedAt = Stopwatch.GetTimestamp();
                    var loaded = SnapshotLoader.Materialize(file, shard =>
                        string.Equals(shard.WorkspaceId, settings.WorkspaceId, StringComparison.Ordinal) && workers.IsIndexedParser(shard.ParserId));
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

                    var notice = new CacheNotice { State = CacheState.Snapshot, SnapshotAt = CacheState.FormatTicks(file.Header.CreatedUtcTicks) };
                    var context = new ContextService(
                        factory.CreateNodeRepository(), factory.CreateEdgeRepository(), factory.CreateFileMetadataRepository(),
                        Options.Create(options), workers, coverage, new FixedNotice(notice));
                    return new OneShotSnapshotOpen(
                        new OneShotSnapshot(context, capturedUtc,
                            new OneShotTimings(keyAndDirectory, open, freshness.Elapsed, freshness.Checked, materialize)),
                        null);
                }
            }
            return Unavailable(firstRefusal is null ? "no saved index" : $"no saved index could be used ({firstRefusal})");
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
