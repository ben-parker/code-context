using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>Host-side routing/lifecycle tests for <see cref="LanguageWorkerService"/>
/// against the protocol-conformant fake worker.</summary>
public class LanguageWorkerServiceTests : IAsyncLifetime
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-worker-service-").FullName;
    private readonly ParserSessionRegistry _registry = new();
    private InMemoryRepositoryFactory _repositoryFactory = null!;
    private IGenerationalGraphStore _store = null!;
    private LanguageWorkerService _service = null!;

    public Task InitializeAsync()
    {
        _repositoryFactory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        _store = (IGenerationalGraphStore)_repositoryFactory.CreateGraphRepository();
        _service = new LanguageWorkerService(
            new StaticWorkerCatalog(FakeWorkerRegistration()),
            new AnalysisDeltaApplier(_store, NullLogger<AnalysisDeltaApplier>.Instance),
            _registry,
            Options.Create(new CodeContextOptions { RootPath = _tempDir }),
            NullLoggerFactory.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        Directory.Delete(_tempDir, recursive: true);
    }

    private static RegisteredWorker FakeWorkerRegistration()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDirectory,
            OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
        var spec = File.Exists(apphost)
            ? new WorkerLaunchSpec("fake", "Fake Worker", apphost, [])
            : new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
                [Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll")]);

        var manifest = new WorkerManifest(
            ManifestVersion: 1,
            ParserId: "fake",
            DisplayName: "Fake Worker",
            Version: "1.0.0-test",
            Command: "test",
            Args: [],
            MinProtocolVersion: ParserProtocol.Version,
            MaxProtocolVersion: ParserProtocol.Version,
            Languages: ["fake"],
            Extensions: [".fake"],
            ProjectMarkers: ["fake.proj"]);
        return new RegisteredWorker(manifest, spec, "<in-test>");
    }

    [Fact]
    public void Extensions_MapToTheOwningWorker()
    {
        Assert.Contains(".fake", _service.OwnedExtensions);
        Assert.True(_service.TryGetParserForExtension(".fake", out var parserId));
        Assert.Equal("fake", parserId);
        Assert.False(_service.TryGetParserForExtension(".unknown", out _));
    }

    [Fact]
    public void BeforeFirstUse_SessionReportsNotNeeded()
    {
        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.NotNeeded, session.State);
        Assert.Equal("fake", session.ParserId);
    }

    [Fact]
    public async Task IndexWorkspace_WithNoFilesAndNoWorker_DoesNotSpawnAProcess()
    {
        await _service.IndexWorkspaceAsync("fake", [], CancellationToken.None);

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.NotNeeded, session.State);
        Assert.Null(session.ProcessId);
    }

    [Fact]
    public async Task IndexWorkspace_CommitsWorkerFactsToTheStore()
    {
        var file = Path.Combine(_tempDir, "thing.fake");
        await File.WriteAllTextAsync(file, "fake");

        await _service.IndexWorkspaceAsync("fake", [file], CancellationToken.None);

        var nodes = await _repositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "thingClass");
        Assert.All(nodes, n => Assert.Equal("fake", n.Metadata?["parserId"]));

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Ready, session.State);
    }

    [Fact]
    public async Task ApplyChanges_ReplacesOnlyTheTouchedFilesFacts()
    {
        var keep = Path.Combine(_tempDir, "keep.fake");
        var drop = Path.Combine(_tempDir, "drop.fake");
        await File.WriteAllTextAsync(keep, "fake");
        await File.WriteAllTextAsync(drop, "fake");
        await _service.IndexWorkspaceAsync("fake", [keep, drop], CancellationToken.None);

        File.Delete(drop);
        await _service.ApplyChangesAsync("fake",
            [new FileChange(drop, FileChangeType.Deleted)],
            approvedFiles: [keep],
            CancellationToken.None);

        var nodes = await _repositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "keepClass");
        Assert.DoesNotContain(nodes, n => n.Name == "dropClass");
    }

    [Fact]
    public async Task UnknownParser_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.IndexWorkspaceAsync("nope", [], CancellationToken.None));
    }

    [Fact]
    public async Task MissingWorkerBinary_SurfacesAsWorkerFailure()
    {
        var manifest = new WorkerManifest(
            ManifestVersion: 1, ParserId: "ghost", DisplayName: "Ghost", Version: "0",
            Command: "test", Args: [],
            MinProtocolVersion: ParserProtocol.Version, MaxProtocolVersion: ParserProtocol.Version,
            Extensions: [".ghost"]);
        var registration = new RegisteredWorker(
            manifest,
            new WorkerLaunchSpec("ghost", "Ghost", Path.Combine(_tempDir, "no-such-worker.exe"), []),
            "<in-test>");
        await using var service = new LanguageWorkerService(
            new StaticWorkerCatalog(registration),
            new AnalysisDeltaApplier(_store, NullLogger<AnalysisDeltaApplier>.Instance),
            _registry,
            Options.Create(new CodeContextOptions { RootPath = _tempDir }),
            NullLoggerFactory.Instance);

        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => service.IndexWorkspaceAsync("ghost", [Path.Combine(_tempDir, "x.ghost")], CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentForAliasedDependencyInjectionRegistrations()
    {
        await _service.DisposeAsync();
        await _service.DisposeAsync();
    }
}
