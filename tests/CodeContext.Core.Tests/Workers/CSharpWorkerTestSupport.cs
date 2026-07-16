using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>Catalog stub with a fixed worker list (no on-disk manifest scan).</summary>
public sealed class StaticWorkerCatalog(params RegisteredWorker[] workers) : IWorkerCatalog
{
    public IReadOnlyList<RegisteredWorker> Workers { get; } = workers;
}

/// <summary>
/// Builds the host-side pipeline (GraphUpdateService + LanguageWorkerService +
/// AnalysisDeltaApplier) wired to the real C# worker executable from the test output
/// directory — the protocol-fixture path the Phase 3 exit gate requires.
/// </summary>
public sealed class CSharpWorkerPipeline : IAsyncDisposable
{
    public const string IdPrefix = "csharp:";

    public InMemoryRepositoryFactory RepositoryFactory { get; }
    public InMemoryFileMetadataRepository FileMetadataRepository { get; }
    public ParserSessionRegistry SessionRegistry { get; }
    public LanguageWorkerService WorkerService { get; }
    public GraphUpdateService GraphUpdateService { get; }

    public CSharpWorkerPipeline(
        string rootPath,
        IEnumerable<ILanguageParser>? parsers = null,
        params RegisteredWorker[] additionalWorkers)
    {
        RepositoryFactory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        RepositoryFactory.InitializeAsync(rootPath).Wait();
        FileMetadataRepository = new InMemoryFileMetadataRepository();
        SessionRegistry = new ParserSessionRegistry();

        var options = Options.Create(new CodeContextOptions { RootPath = rootPath });
        var store = (IGenerationalGraphStore)RepositoryFactory.CreateGraphRepository();
        var sink = new AnalysisDeltaApplier(store, NullLogger<AnalysisDeltaApplier>.Instance);

        WorkerService = new LanguageWorkerService(
            new StaticWorkerCatalog([CSharpWorkerRegistration(), .. additionalWorkers]),
            sink,
            SessionRegistry,
            options,
            NullLoggerFactory.Instance);

        GraphUpdateService = new GraphUpdateService(
            RepositoryFactory,
            parsers ?? [],
            options,
            NullLogger<GraphUpdateService>.Instance,
            FileMetadataRepository,
            SessionRegistry,
            WorkerService);
    }

    /// <summary>Node IDs are language-namespaced by the worker; tests translate a
    /// symbol display string into its graph ID with this.</summary>
    public static string Id(string symbolDisplay) => IdPrefix + "default:" + symbolDisplay;

    public static WorkerLaunchSpec CSharpWorkerLaunchSpec()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDirectory,
            OperatingSystem.IsWindows() ? "CodeContext.CSharp.Worker.exe" : "CodeContext.CSharp.Worker");
        if (File.Exists(apphost))
        {
            return new WorkerLaunchSpec("csharp", "CSharp", apphost, []);
        }

        var dll = Path.Combine(baseDirectory, "CodeContext.CSharp.Worker.dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException(
                $"C# worker binary not found in the test output directory ({baseDirectory}). " +
                "Ensure CodeContext.Core.Tests references CodeContext.CSharp.Worker.");
        }
        return new WorkerLaunchSpec("csharp", "CSharp", "dotnet", [dll]);
    }

    public static RegisteredWorker CSharpWorkerRegistration()
    {
        var manifest = new WorkerManifest(
            ManifestVersion: 1,
            ParserId: "csharp",
            DisplayName: "CSharp",
            Version: "1.0.0",
            Command: "test",
            Args: [],
            MinProtocolVersion: ParserProtocol.Version,
            MaxProtocolVersion: ParserProtocol.Version,
            Languages: ["csharp"],
            Extensions: [".cs"],
            ProjectMarkers: [".csproj", ".sln"]);
        return new RegisteredWorker(manifest, CSharpWorkerLaunchSpec(), ManifestPath: "<in-test>");
    }

    public ValueTask DisposeAsync() => WorkerService.DisposeAsync();
}
