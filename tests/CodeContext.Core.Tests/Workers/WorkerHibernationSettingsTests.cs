using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeContext.Core.Memory;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// How <c>CodeContext:WorkerHibernateSeconds</c> is read (absent means the default policy: 120 s for a worker whose
/// last index from its files took under 5 s, else 900 s; a value applies to every worker, 0 off, above a day is a
/// day, anything else refused and the default kept with a warning) and what <see cref="WorkerHibernationServiceCollectionExtensions.AddCodeContextWorkerHibernation"/>
/// registers.
/// </summary>
public sealed class WorkerHibernationSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromSeconds_Absent_IsTheDefaultPolicy(string? text)
        => Assert.Equal(new WorkerHibernationSettings(), WorkerHibernationSettings.FromSeconds(text));

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData(" 120 ", 120)]
    [InlineData("86400", 86_400)]
    [InlineData("99999999999", 86_400)]
    public void FromSeconds_ReadsTheIdleTime(string? text, int seconds)
    {
        var settings = WorkerHibernationSettings.FromSeconds(text);
        Assert.NotNull(settings);
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings!.Idle);
        Assert.Equal(seconds > 0, settings.Enabled);
        // A configured value is one idle time for every worker, whatever its wake costs.
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings.QuickIdle);
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings.IdleFor(null));
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings.IdleFor(TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(seconds), settings.IdleFor(TimeSpan.FromMinutes(1)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("15m")]
    [InlineData("1e3")]
    public void FromSeconds_RefusesAnythingElse(string text)
        => Assert.Null(WorkerHibernationSettings.FromSeconds(text));

    [Fact]
    public void TheDefault_IsTwoMinutesForAWorkerThatReadsItsFilesInUnderFiveSeconds_ElseFifteen()
    {
        var settings = new WorkerHibernationSettings();
        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.Idle);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.QuickIdle);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.QuickRead);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.IdleFor(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromMinutes(2), settings.IdleFor(TimeSpan.FromSeconds(4.9)));
        Assert.Equal(TimeSpan.FromMinutes(15), settings.IdleFor(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMinutes(15), settings.IdleFor(TimeSpan.FromSeconds(35)));
        // No read yet: a worker that has only restored saved state, whose next edit pays the warm-up the restore skipped.
        Assert.Equal(TimeSpan.FromMinutes(15), settings.IdleFor(null));
    }

    [Fact]
    public void Disabled_IsOffForEveryWorker()
    {
        Assert.False(WorkerHibernationSettings.Disabled.Enabled);
        Assert.Equal(TimeSpan.Zero, WorkerHibernationSettings.Disabled.IdleFor(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, WorkerHibernationSettings.Disabled.IdleFor(null));
        Assert.Equal(WorkerHibernationSettings.Disabled, WorkerHibernationSettings.FromSeconds("0"));
    }

    [Fact]
    public void Read_WithoutConfiguration_IsTheDefault()
    {
        Assert.Equal(new WorkerHibernationSettings(), WorkerHibernationSettings.Read(null));
        Assert.Equal(new WorkerHibernationSettings(), WorkerHibernationSettings.Read(Configuration(null)));
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    public void Read_AnInvalidValue_KeepsTheDefault_AndWarns(string text)
    {
        var logger = new ListLogger<WorkerHibernation>();
        Assert.Equal(new WorkerHibernationSettings(), WorkerHibernationSettings.Read(Configuration(text), logger));
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(WorkerHibernationSettings.ConfigurationKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(text, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AValidValue_LogsNothing()
    {
        var logger = new ListLogger<WorkerHibernation>();
        Assert.Equal(WorkerHibernationSettings.Fixed(TimeSpan.FromSeconds(30)), WorkerHibernationSettings.Read(Configuration("30"), logger));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TheEnvironmentVariable_IsTheConfigurationKeysEnvironmentForm()
        => Assert.Equal(WorkerHibernationSettings.EnvironmentVariable,
            WorkerHibernationSettings.ConfigurationKey.Replace(":", "__", StringComparison.Ordinal));

    [Theory]
    [InlineData(null, true)]
    [InlineData("30", true)]
    [InlineData("0", false)]
    public async System.Threading.Tasks.Task Registration_IsOneSingleton_AlsoAHostedService_ConfiguredFromTheKey(string? text, bool enabled)
    {
        var root = Directory.CreateTempSubdirectory("cc-hibernate-di-").FullName;
        try
        {
            await using var instance = new PersistentStateTests.Instance(Path.Combine(root, "repo"), Path.Combine(root, "cache.key"),
                ["--behavior", "strict"], null, cache: true);
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(Configuration(text));
            services.AddSingleton(instance.Workers);
            services.AddSingleton(instance.Coordinator);
            services.AddSingleton(instance.Snapshots);
            services.AddCodeContextWorkerHibernation();
            await using var provider = services.BuildServiceProvider();

            var hibernation = provider.GetRequiredService<WorkerHibernation>();
            Assert.Same(hibernation, provider.GetRequiredService<WorkerHibernation>());
            Assert.Contains(provider.GetServices<IHostedService>(), service => ReferenceEquals(service, hibernation));
            Assert.Equal(enabled, hibernation.Enabled);
            Assert.Equal(WorkerHibernationSettings.FromSeconds(text), hibernation.Settings);
            // Enabled, it takes over the worker service's hooks; off, it leaves them alone.
            Assert.Equal(enabled, instance.Workers.Waker is not null);
            Assert.Equal(enabled, instance.Workers.SnapshotWritten is not null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IConfiguration Configuration(string? seconds)
        => new ConfigurationBuilder().AddInMemoryCollection(seconds is null
            ? []
            : new Dictionary<string, string?> { [WorkerHibernationSettings.ConfigurationKey] = seconds }).Build();
}
