using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Api;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The snapshot service inside the real composition (<see cref="ProgramHelpers.ConfigureCoreServices"/>):
/// its place in the hosted-service order, the configuration switch, and the final write on a graceful
/// host stop, which needs the coordinator loop still running when the service stops.
/// </summary>
public class SnapshotHostTests
{
    [Fact]
    public void SnapshotService_StopsBeforeTheCoordinatorAndTheWorkers_AndAfterTheWatcher()
    {
        using var temp = new SnapshotTempDirectory();
        using var host = BuildHost(temp.Path, cacheEnabled: false, keyPath: temp.File("cache.key"));

        var order = host.Services.GetServices<IHostedService>().Select(service => service.GetType()).ToList();
        var workers = order.IndexOf(typeof(LanguageWorkerService));
        var coordinator = order.IndexOf(typeof(IndexCoordinator));
        var snapshots = order.IndexOf(typeof(SnapshotService));
        var watcher = order.IndexOf(typeof(FileWatcherService));
        // Hosted services stop in reverse registration order.
        Assert.True(workers >= 0 && workers < coordinator && coordinator < snapshots && snapshots < watcher, string.Join(", ", order));
    }

    [Fact]
    public async Task GracefulHostStop_WritesTheFinalSnapshot()
    {
        using var temp = new SnapshotTempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        using var host = BuildHost(root, cacheEnabled: true, keyPath: temp.File("cache.key"));
        await host.StartAsync();
        var scanState = host.Services.GetRequiredService<IScanStateService>();
        await SnapshotServiceHarness.WaitUntilAsync(() => scanState.Phase == ScanPhase.Ready, "the startup scan");
        var service = host.Services.GetRequiredService<SnapshotService>();
        Assert.Equal(SnapshotCacheMode.Writer, service.Status.Mode);

        // A commit through the coordinator's ordered path; the quiet period (10 s) has not passed.
        var database = ((InMemoryRepositoryFactory)host.Services.GetRequiredService<IRepositoryFactory>()).Database;
        var graphUpdate = host.Services.GetRequiredService<IIndexCoordinator>();
        database.TryCommitGeneration(database.LastCommittedGeneration + 1,
            [SnapshotServiceHarness.Node("Final")], [], new CommitScope("fake", "default", null));
        await graphUpdate.RefreshFileAsync(Path.Combine(root, "notes.txt"));
        var workspace = Path.Combine(root, ".codecontext", "cache", "default");
        Assert.Empty(Directory.GetFiles(workspace, "*.ccsnap"));

        await host.StopAsync();

        var snapshot = Assert.Single(Directory.GetFiles(workspace, "*.ccsnap"));
        var loaded = SnapshotLoader.Load(snapshot, File.ReadAllBytes(temp.File("cache.key")),
            new SnapshotExpectations(SnapshotServiceSettings.From(new CodeContextOptions()).HostVersion, root, OperatingSystem.IsWindows()));
        Assert.True(loaded.Loaded, loaded.Reason);
        Assert.Equal("Final", loaded.Shards.Single().Shard.GetAllNodes().Single().Name);
    }

    [Fact]
    public async Task CacheOff_ByDefault_CreatesNothing()
    {
        using var temp = new SnapshotTempDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var key = temp.File("cache.key");
        using var host = BuildHost(root, cacheEnabled: null, keyPath: key);
        await host.StartAsync();
        var scanState = host.Services.GetRequiredService<IScanStateService>();
        await SnapshotServiceHarness.WaitUntilAsync(() => scanState.Phase == ScanPhase.Ready, "the startup scan");
        var database = ((InMemoryRepositoryFactory)host.Services.GetRequiredService<IRepositoryFactory>()).Database;
        database.TryCommitGeneration(database.LastCommittedGeneration + 1,
            [SnapshotServiceHarness.Node("Final")], [], new CommitScope("fake", "default", null));
        await host.Services.GetRequiredService<IIndexCoordinator>().RefreshFileAsync(Path.Combine(root, "notes.txt"));
        await host.StopAsync();

        Assert.Equal(SnapshotCacheMode.Disabled, host.Services.GetRequiredService<SnapshotService>().Status.Mode);
        Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.False(File.Exists(key));
    }

    private static IHost BuildHost(string root, bool? cacheEnabled, string keyPath)
    {
        var builder = Host.CreateApplicationBuilder();
        if (cacheEnabled is { } enabled) builder.Configuration["CodeContext:CacheEnabled"] = enabled ? "true" : "false";
        ProgramHelpers.ConfigureCoreServices(builder.Services, root, isProduction: false);
        // No workers: the startup scan has nothing to start, and the test commits facts itself.
        builder.Services.AddSingleton<IWorkerCatalog>(new StaticWorkerCatalog());
        builder.Services.PostConfigure<CodeContextOptions>(options => options.CacheKeyPath = keyPath);
        builder.Logging.ClearProviders();
        return builder.Build();
    }
}
