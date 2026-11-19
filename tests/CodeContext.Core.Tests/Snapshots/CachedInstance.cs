using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>One host's worth of production services over <paramref name="root"/>, without the HTTP layer.</summary>
internal sealed class CachedInstance : IAsyncDisposable
{
    private bool _stopped;

    /// <param name="behavior">The fake worker's behaviour, or null for no worker at all.</param>
    /// <param name="instanceId">What status reports as the instance id (a query checks it against the registry).</param>
    public CachedInstance(string root, string keyPath, string? behavior, string? instanceId = null)
    {
        Factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        Factory.InitializeAsync(root).Wait();
        var options = Options.Create(new CodeContextOptions { RootPath = root, CacheKeyPath = keyPath, InstanceId = instanceId ?? string.Empty });
        var metadata = new InMemoryFileMetadataRepository();
        // The host's registry: it reads the coverage recorded with the published shards.
        var coverage = AnalysisCoverageRegistry.ForStore(Factory);
        var store = (IGenerationalGraphStore)Factory.CreateGraphRepository();
        Workers = new LanguageWorkerService(
            behavior is null ? new StaticWorkerCatalog() : new StaticWorkerCatalog(FakeWorker(behavior)),
            new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance, coverage),
            Sessions, options, NullLoggerFactory.Instance, TestWorkerOptions.Patient());
        var graphUpdate = new GraphUpdateService(Factory, options, NullLogger<GraphUpdateService>.Instance, metadata, Workers);

        var settings = SnapshotServiceSettings.From(options.Value);
        Startup = new SnapshotStartup(settings, Factory.Database, coverage, Workers.IsIndexedParser,
            Workers.MarkLoadedFromSnapshot, NullLogger<SnapshotService>.Instance);
        CacheState = new CacheState(settings.Enabled, Startup.Run);
        Coordinator = new IndexCoordinator(graphUpdate, ScanState, options, NullLoggerFactory.Instance, cacheState: CacheState);
        Snapshots = new SnapshotService(Coordinator, Factory.Database, ScanState, settings, Startup, TimeProvider.System,
            NullLogger<SnapshotService>.Instance, CacheState);
        Context = new ContextService(Factory.CreateNodeRepository(), Factory.CreateEdgeRepository(), metadata, options, Workers,
            coverage, CacheState);
        Status = new StatusService(Factory.CreateNodeRepository(), Factory.CreateEdgeRepository(), metadata, Factory, options,
            ScanState, Sessions, new ApiMetrics(), coverage: coverage, cacheState: CacheState);
    }

    public InMemoryRepositoryFactory Factory { get; }
    public InMemoryDatabase Database => Factory.Database;
    public ParserSessionRegistry Sessions { get; } = new();
    public LanguageWorkerService Workers { get; }
    public ScanStateService ScanState { get; } = new();
    public SnapshotStartup Startup { get; }
    public CacheState CacheState { get; }
    public IndexCoordinator Coordinator { get; }
    public SnapshotService Snapshots { get; }
    public ContextService Context { get; }
    public StatusService Status { get; }

    public async Task StartAsync()
    {
        // The host's order: workers, coordinator, snapshots.
        await Coordinator.StartAsync(CancellationToken.None);
        await Snapshots.StartAsync(CancellationToken.None);
    }

    public Task WaitForAsync(Func<bool> condition, string what) => SnapshotServiceHarness.WaitUntilAsync(condition, what, 60_000);

    /// <summary>The host's stop order: snapshots (the final write), coordinator, workers.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        await Snapshots.StopAsync(CancellationToken.None);
        await Coordinator.StopAsync(CancellationToken.None);
        await Workers.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Snapshots.Dispose();
        Startup.Dispose();
        Coordinator.Dispose();
        await Workers.DisposeAsync();
    }

    internal static RegisteredWorker FakeWorker(string behavior)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDirectory,
            OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
        var spec = File.Exists(apphost)
            ? new WorkerLaunchSpec("fake", "Fake Worker", apphost, ["--behavior", behavior])
            : new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
                [Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll"), "--behavior", behavior]);
        var manifest = new WorkerManifest(
            ManifestVersion: 1, ParserId: "fake", DisplayName: "Fake Worker", Version: "1.0.0-test",
            Command: "test", Args: [], MinProtocolVersion: ParserProtocol.Version, MaxProtocolVersion: ParserProtocol.Version,
            Languages: ["fake"], Extensions: [".fake"], ProjectMarkers: ["fake.proj"]);
        return new RegisteredWorker(manifest, spec, "<in-test>");
    }
}
