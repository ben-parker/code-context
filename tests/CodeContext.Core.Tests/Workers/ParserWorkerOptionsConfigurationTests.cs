using System.Reflection;
using CodeContext.Api;
using CodeContext.Core;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Covers <see cref="ParserWorkerOptions.FromConfiguration"/> parsing and the DI wiring
/// that carries the registered options instance into <see cref="LanguageWorkerService"/>'s
/// optional constructor parameter.
/// </summary>
public class ParserWorkerOptionsConfigurationTests
{
    private static IConfiguration BuildConfiguration(IDictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void FromConfiguration_PopulatedSection_YieldsParserEnvironmentMaps()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["CodeContext:WorkerEnvironment:csharp:DOTNET_gcServer"] = "1",
            ["CodeContext:WorkerEnvironment:csharp:DOTNET_GCDynamicAdaptationMode"] = "1",
            ["CodeContext:WorkerEnvironment:typescript:NODE_OPTIONS"] = "--max-old-space-size=4096",
        });

        var options = ParserWorkerOptions.FromConfiguration(configuration);

        Assert.NotNull(options.WorkerEnvironment);
        Assert.Equal(2, options.WorkerEnvironment!.Count);

        var csharp = options.WorkerEnvironment["csharp"];
        Assert.Equal("1", csharp["DOTNET_gcServer"]);
        Assert.Equal("1", csharp["DOTNET_GCDynamicAdaptationMode"]);

        var typescript = options.WorkerEnvironment["typescript"];
        Assert.Equal("--max-old-space-size=4096", typescript["NODE_OPTIONS"]);
    }

    [Fact]
    public void FromConfiguration_MissingSection_LeavesWorkerEnvironmentNull()
    {
        var options = ParserWorkerOptions.FromConfiguration(
            BuildConfiguration(new Dictionary<string, string?>()));

        Assert.Null(options.WorkerEnvironment);
    }

    [Fact]
    public void FromConfiguration_UnrelatedKeysOnly_LeavesWorkerEnvironmentNull()
    {
        var options = ParserWorkerOptions.FromConfiguration(
            BuildConfiguration(new Dictionary<string, string?> { ["CodeContext:RootPath"] = "/repo" }));

        Assert.Null(options.WorkerEnvironment);
    }

    [Fact]
    public void FromConfiguration_PreservesOtherDefaults()
    {
        var defaults = new ParserWorkerOptions();
        var options = ParserWorkerOptions.FromConfiguration(BuildConfiguration(
            new Dictionary<string, string?> { ["CodeContext:WorkerEnvironment:csharp:X"] = "y" }));

        Assert.Equal(defaults.InitializeTimeout, options.InitializeTimeout);
        Assert.Equal(defaults.ShutdownTimeout, options.ShutdownTimeout);
        Assert.Equal(defaults.ExitAfterEofTimeout, options.ExitAfterEofTimeout);
        Assert.Equal(defaults.MaxRestarts, options.MaxRestarts);
        Assert.Equal(defaults.MinProtocolVersion, options.MinProtocolVersion);
        Assert.Equal(defaults.MaxProtocolVersion, options.MaxProtocolVersion);
    }

    [Fact]
    public async Task RegisteredParserWorkerOptions_FlowsIntoLanguageWorkerServiceConstructor()
    {
        // The ctor's ParserWorkerOptions parameter is optional; the default DI container
        // still resolves it from the container when registered. This proves the runtime
        // instance genuinely arrives rather than silently falling back to a fresh default.
        var expected = new ParserWorkerOptions
        {
            WorkerEnvironment = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["csharp"] = new Dictionary<string, string> { ["DOTNET_gcServer"] = "1" },
            },
        };

        var catalog = Substitute.For<IWorkerCatalog>();
        catalog.Workers.Returns([]);

        var services = new ServiceCollection();
        services.AddSingleton(expected);
        services.AddSingleton(catalog);
        services.AddSingleton(Substitute.For<IAnalysisDeltaSink>());
        services.AddSingleton<IParserSessionRegistry, ParserSessionRegistry>();
        services.AddSingleton(Options.Create(new CodeContextOptions()));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<LanguageWorkerService>();

        // The service is IAsyncDisposable, so the provider must be disposed asynchronously.
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<LanguageWorkerService>();

        var field = typeof(LanguageWorkerService).GetField(
            "_workerOptions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Same(expected, field.GetValue(service));
    }

    [Fact]
    public async Task ConfigureCoreServices_RegistersParserWorkerOptionsFromConfiguration()
    {
        // Guards the real production registration in ProgramHelpers: deleting the
        // AddSingleton(...FromConfiguration...) line must fail here, not pass silently.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(BuildConfiguration(new Dictionary<string, string?>
        {
            ["CodeContext:WorkerEnvironment:csharp:FOO"] = "bar",
        }));
        ProgramHelpers.ConfigureCoreServices(services, Path.GetTempPath(), isProduction: false);

        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ParserWorkerOptions>();

        Assert.NotNull(options.WorkerEnvironment);
        Assert.Equal("bar", options.WorkerEnvironment!["csharp"]["FOO"]);
    }
}
