using CodeContext.Api.Commands;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// These tests change the process's current directory, so they run alone.
/// </summary>
[CollectionDefinition(nameof(CurrentDirectoryCollection), DisableParallelization = true)]
public sealed class CurrentDirectoryCollection;

/// <summary>
/// A detached host starts inside the repository it indexes, and a foreground one is
/// usually started from there. Nothing in that directory may configure the host: its
/// configuration reaches the workers' process environment, where a startup hook is code.
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class HostContentRootTests : IDisposable
{
    private const string StartupHook = "CodeContext:WorkerEnvironment:csharp:DOTNET_STARTUP_HOOKS";

    // What a hostile or merely unrelated repository could carry: a worker environment
    // entry, an endpoint override and a logging override.
    private const string RepositorySettings = """
        {
          "CodeContext": {
            "WorkerEnvironment": {
              "csharp": { "DOTNET_STARTUP_HOOKS": "C:/does-not-exist/hook.dll" },
              "typescript": { "NODE_OPTIONS": "--require /does-not-exist/hook.js" }
            }
          },
          "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:1" } } },
          "Logging": { "LogLevel": { "Default": "None" } }
        }
        """;

    private readonly string _originalDirectory = Directory.GetCurrentDirectory();
    private readonly string _repository = Directory.CreateTempSubdirectory("cc-repo-settings-").FullName;

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        try { Directory.Delete(_repository, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Hosts => new() { "rest", "mcp" };

    private static (IConfiguration Configuration, IHostEnvironment Environment) Build(string host)
    {
        if (host == "rest")
        {
            var rest = StartCommandHandler.CreateRestBuilder();
            return (rest.Configuration, rest.Environment);
        }

        var mcp = StartCommandHandler.CreateMcpBuilder();
        return (mcp.Configuration, mcp.Environment);
    }

    private void StartInsideRepositoryWith(string appsettings, string fileName = "appsettings.json")
    {
        File.WriteAllText(Path.Combine(_repository, fileName), appsettings);
        Directory.SetCurrentDirectory(_repository);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ARepositoryAppsettings_ConfiguresNothing(string host)
    {
        StartInsideRepositoryWith(RepositorySettings);

        var (configuration, _) = Build(host);

        Assert.Null(configuration[StartupHook]);
        Assert.Null(configuration["CodeContext:WorkerEnvironment:typescript:NODE_OPTIONS"]);
        Assert.Null(configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.NotEqual("None", configuration["Logging:LogLevel:Default"]);
        Assert.Null(ParserWorkerOptions.FromConfiguration(configuration).WorkerEnvironment);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void ARepositoryAppsettingsForTheEnvironment_ConfiguresNothing(string host)
    {
        // appsettings.<Environment>.json is read from the same place as appsettings.json.
        var environmentName = Build(host).Environment.EnvironmentName;
        StartInsideRepositoryWith(RepositorySettings, $"appsettings.{environmentName}.json");

        var (configuration, _) = Build(host);

        Assert.Null(configuration[StartupHook]);
        Assert.Null(ParserWorkerOptions.FromConfiguration(configuration).WorkerEnvironment);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void AMalformedRepositoryAppsettings_DoesNotStopTheHost(string host)
    {
        StartInsideRepositoryWith("{ \"Path\": \"C:\\does-not-parse\" ");

        var failure = Record.Exception(() => Build(host));

        Assert.Null(failure);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void TheContentRoot_IsTheInstallDirectory_NotTheCurrentDirectory(string host)
    {
        StartInsideRepositoryWith(RepositorySettings);

        var (_, environment) = Build(host);

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath)));
        Assert.NotEqual(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_repository)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.ContentRootPath)));
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public void EnvironmentVariables_StillConfigureTheWorkers(string host)
    {
        // The documented way to set a worker's environment is the host's own environment.
        const string variable = "CodeContext__WorkerEnvironment__csharp__CODECONTEXT_TEST_PROBE";
        StartInsideRepositoryWith(RepositorySettings);
        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            var (configuration, _) = Build(host);

            var workers = ParserWorkerOptions.FromConfiguration(configuration).WorkerEnvironment;
            Assert.NotNull(workers);
            Assert.Equal("1", workers!["csharp"]["CODECONTEXT_TEST_PROBE"]);
            Assert.False(workers["csharp"].ContainsKey("DOTNET_STARTUP_HOOKS"));
            Assert.False(workers.ContainsKey("typescript"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
