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
/// How <c>CodeContext:WorkerHibernateSeconds</c> is read (absent means 900 s, 0 off, above a day is a day, anything
/// else refused and the default kept with a warning) and what <see cref="WorkerHibernationServiceCollectionExtensions.AddCodeContextWorkerHibernation"/>
/// registers.
/// </summary>
public sealed class WorkerHibernationSettingsTests
{
    [Theory]
    [InlineData(null, 900)]
    [InlineData("", 900)]
    [InlineData("   ", 900)]
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
    public void TheDefault_IsFifteenMinutes()
    {
        Assert.Equal(900, WorkerHibernationSettings.DefaultSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), new WorkerHibernationSettings().Idle);
        Assert.False(WorkerHibernationSettings.Disabled.Enabled);
    }

    [Fact]
    public void Read_WithoutConfiguration_IsTheDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(900), WorkerHibernationSettings.Read(null).Idle);
        Assert.Equal(TimeSpan.FromSeconds(900), WorkerHibernationSettings.Read(Configuration(null)).Idle);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("soon")]
    public void Read_AnInvalidValue_KeepsTheDefault_AndWarns(string text)
    {
        var logger = new ListLogger<WorkerHibernation>();
        Assert.Equal(TimeSpan.FromSeconds(900), WorkerHibernationSettings.Read(Configuration(text), logger).Idle);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(WorkerHibernationSettings.ConfigurationKey, warning.Message, StringComparison.Ordinal);
        Assert.Contains(text, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AValidValue_LogsNothing()
    {
        var logger = new ListLogger<WorkerHibernation>();
        Assert.Equal(TimeSpan.FromSeconds(30), WorkerHibernationSettings.Read(Configuration("30"), logger).Idle);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void TheEnvironmentVariable_IsTheConfigurationKeysEnvironmentForm()
        => Assert.Equal(WorkerHibernationSettings.ConfigurationKey.Replace(":", "__", StringComparison.Ordinal),
            WorkerHibernationSettings.EnvironmentVariable);

    [Theory]
    [InlineData("30", true)]
    [InlineData("0", false)]
    public async System.Threading.Tasks.Task Registration_IsOneSingleton_AlsoAHostedService_ConfiguredFromTheKey(string text, bool enabled)
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
            Assert.Equal(enabled ? TimeSpan.FromSeconds(30) : TimeSpan.Zero, hibernation.Settings.Idle);
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
