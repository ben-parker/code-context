using System;
using System.IO;
using System.Threading.Tasks;
using CodeContext.Api;
using CodeContext.Core.Memory;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Tests;

/// <summary>
/// The one host the tests compose as a user runs it. <see cref="TestAssemblyGuards"/> turns the quiet-time trim and
/// worker hibernation off for every other host, so nothing else would notice the product's composition losing
/// either, or losing the wiring between them, the worker service and the snapshot writer. The host here stops
/// within a second, long before a real timer of either could fire.
/// </summary>
public sealed class ComposedHostDefaultsTests
{
    [Fact]
    public async Task WithNothingConfigured_TheHostTrimsAndHibernatesByItsDefaults_AndTheyAreWired()
    {
        var home = Directory.CreateTempSubdirectory("cc-defaults-").FullName;
        var root = Path.Combine(home, "repo");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.fake"), "a");
        try
        {
            var builder = Host.CreateApplicationBuilder();
            // The guards set both through the environment; an empty value reads as not configured.
            builder.Configuration[QuietTimeTrimSettings.ConfigurationKey] = "";
            builder.Configuration[WorkerHibernationSettings.ConfigurationKey] = "";
            ProgramHelpers.ConfigureCoreServices(builder.Services, root, isProduction: false);
            builder.Services.AddSingleton<IWorkerCatalog>(
                new StaticWorkerCatalog(PersistentStateTests.Instance.FakeWorker(["--behavior", "strict"])));
            builder.Services.PostConfigure<CodeContextOptions>(options => options.CacheKeyPath = Path.Combine(home, "cache.key"));
            builder.Logging.ClearProviders();
            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var trim = host.Services.GetRequiredService<QuietTimeTrim>();
                var hibernation = host.Services.GetRequiredService<WorkerHibernation>();
                var workers = host.Services.GetRequiredService<LanguageWorkerService>();

                Assert.True(trim.Enabled);
                Assert.True(hibernation.Enabled);
                Assert.Equal(new WorkerHibernationSettings(), hibernation.Settings);
                // What hibernation needs from the rest: the wake path, the end of each operation, the snapshot's state.
                Assert.NotNull(workers.Waker);
                Assert.NotNull(workers.ActivityEnded);
                Assert.NotNull(workers.SnapshotWritten);

                // The start-up scan is the coordinator's work on the trim, and leaves none open.
                var scan = host.Services.GetRequiredService<IScanStateService>();
                await SnapshotServiceHarness.WaitUntilAsync(() => scan.Phase == ScanPhase.Ready && trim.Active == 0,
                    "the first scan, with no activity left open", 30_000);
                Assert.Equal(0, trim.Trims);
                // The fake worker read its one file at once: the quick worker's idle time.
                Assert.Equal(TimeSpan.FromSeconds(WorkerHibernationSettings.QuickIdleSeconds), hibernation.IdleFor("fake"));
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }
}
