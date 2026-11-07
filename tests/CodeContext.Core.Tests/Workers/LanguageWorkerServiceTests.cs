using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
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
            NullLoggerFactory.Instance,
            TestWorkerOptions.Patient());
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

    // ---- Persistent state (plan phase 5): the offer rule and the save ----

    private static readonly InMemoryDatabase.ShardKey FakeShard = new("fake", LanguageWorkerService.DefaultWorkspaceId);

    private static RegisteredWorker FakeWorkerRegistration(params string[] args)
    {
        var registration = FakeWorkerRegistration();
        var spec = registration.LaunchSpec;
        return registration with { LaunchSpec = spec with { Arguments = [.. spec.Arguments, .. args] } };
    }

    private LanguageWorkerService NewService(InMemoryRepositoryFactory factory, params string[] args)
        => new(
            new StaticWorkerCatalog(FakeWorkerRegistration(args)),
            new AnalysisDeltaApplier((IGenerationalGraphStore)factory.CreateGraphRepository(), NullLogger<AnalysisDeltaApplier>.Instance),
            new ParserSessionRegistry(),
            Options.Create(new CodeContextOptions { RootPath = _tempDir }),
            NullLoggerFactory.Instance,
            TestWorkerOptions.Patient());

    private string WriteFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        System.IO.File.WriteAllText(path, "fake");
        return path;
    }

    /// <summary>
    /// A store as a start would load it: the shard with <paramref name="parserVersion"/> and a recorded state file
    /// (not one the worker wrote), anchored to the loaded shard.
    /// </summary>
    private (InMemoryRepositoryFactory Factory, CacheDirectory Directory) LoadedStore(string parserVersion, IReadOnlyList<string> files)
    {
        var directory = CacheDirectory.Open(_tempDir, LanguageWorkerService.DefaultWorkspaceId, out var refusal)!;
        Assert.Null(refusal);
        var name = WorkerStateFile.FormatName("fake", 1, new string('1', 32))!;
        System.IO.File.WriteAllText(directory.PathOf(name), "not this worker's state");

        var source = new InMemoryDatabase();
        var nodes = files.Select(file => new CodeContext.Core.CodeNode
        {
            Id = $"fake:default:{Path.GetFileNameWithoutExtension(file)}.Class", Identifier = $"fake:{Path.GetFileNameWithoutExtension(file)}.Class",
            Name = "C", Type = "class", FilePath = file,
            Metadata = new Dictionary<string, string> { ["parserId"] = "fake", ["workspaceId"] = "default" },
        }).ToList();
        Assert.True(source.TryCommitGeneration(1, nodes, [], new CommitScope("fake", "default", null)));
        var shard = source.LatchPublished().Shards[FakeShard];

        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        factory.Database.PublishLoadedShards([KeyValuePair.Create(FakeShard, shard)],
        [
            KeyValuePair.Create(FakeShard, new ShardAnnotation(parserVersion, null)
            {
                Fingerprints = [new UnitFingerprint("default", "x")],
                State = new WorkerStateBinding(name, new string('1', 32), new string('2', 64), 23),
                Anchor = ShardAnchor.Of(shard),
            }),
        ]);
        return (factory, directory);
    }

    [Fact]
    public async Task Baseline_IsOfferedOnTheFirstIndex_WhenEveryConditionHolds()
    {
        var file = WriteFile("a.fake");
        var (factory, directory) = LoadedStore("1.0.0-test", [file]);
        await using var service = NewService(factory, "--behavior", "persistent-state");
        service.BaselineSource = new WorkerBaselines(factory.Database, () => directory);

        await service.IndexWorkspaceAsync("fake", [file]);
        // Offered: the worker answered it (this state file is not one it wrote, so it rejected it).
        Assert.False(service.AcceptedBaseline("fake"));
        Assert.True(service.LastBaselineOutcomeFor("fake") == BaselineOutcomes.Rejected);
    }

    [Fact]
    public async Task Baseline_IsNotOfferedWhenTheParserVersionChanged()
    {
        var file = WriteFile("a.fake");
        var (factory, directory) = LoadedStore("0.9.0-older", [file]);
        await using var service = NewService(factory, "--behavior", "persistent-state");
        service.BaselineSource = new WorkerBaselines(factory.Database, () => directory);

        await service.IndexWorkspaceAsync("fake", [file]);
        Assert.Null(service.LastBaselineOutcomeFor("fake"));
    }

    [Fact]
    public async Task Baseline_IsNotOfferedWhenTheShardChangedSinceCapture()
    {
        var file = WriteFile("a.fake");
        var (factory, directory) = LoadedStore("1.0.0-test", [file]);
        // Something changed the shard after its state file was recorded: the file no longer describes it.
        factory.Database.UpsertNode(new CodeContext.Core.CodeNode
        {
            Id = "fake:default:Late", Identifier = "fake:Late", Name = "Late", Type = "class", FilePath = file,
            Metadata = new Dictionary<string, string> { ["parserId"] = "fake", ["workspaceId"] = "default" },
        });
        await using var service = NewService(factory, "--behavior", "persistent-state");
        service.BaselineSource = new WorkerBaselines(factory.Database, () => directory);

        await service.IndexWorkspaceAsync("fake", [file]);
        Assert.Null(service.LastBaselineOutcomeFor("fake"));
    }

    [Fact]
    public async Task Baseline_IsNotOfferedOnASecondIndexOfTheSameProcess()
    {
        var file = WriteFile("a.fake");
        var (factory, directory) = LoadedStore("1.0.0-test", [file]);
        await using (var service = NewService(factory, "--behavior", "persistent-state"))
        {
            service.BaselineSource = new WorkerBaselines(factory.Database, () => directory);
            await service.IndexWorkspaceAsync("fake", [file]);
            Assert.Equal(BaselineOutcomes.Rejected, service.LastBaselineOutcomeFor("fake"));
            await service.IndexWorkspaceAsync("fake", [file]);
            Assert.Null(service.LastBaselineOutcomeFor("fake"));
        }

        // A watcher batch first: the process's first index is no longer its first mutation.
        (factory, directory) = LoadedStore("1.0.0-test", [file]);
        await using var batched = NewService(factory, "--behavior", "persistent-state");
        batched.BaselineSource = new WorkerBaselines(factory.Database, () => directory);
        await batched.ApplyChangesAsync("fake", [new FileChange(file, FileChangeType.Changed)], [file]);
        await batched.IndexWorkspaceAsync("fake", [file]);
        Assert.Null(batched.LastBaselineOutcomeFor("fake"));
    }

    [Fact]
    public async Task Baseline_IsNotOfferedToAWorkerWithoutTheCapability_NorWhenTheStateFileIsGone()
    {
        var file = WriteFile("a.fake");
        var (factory, directory) = LoadedStore("1.0.0-test", [file]);
        await using (var strict = NewService(factory, "--behavior", "strict"))
        {
            strict.BaselineSource = new WorkerBaselines(factory.Database, () => directory);
            await strict.IndexWorkspaceAsync("fake", [file]);
            Assert.Null(strict.LastBaselineOutcomeFor("fake"));
        }

        (factory, directory) = LoadedStore("1.0.0-test", [file]);
        foreach (var state in directory.EnumerateOwnFiles()) System.IO.File.Delete(state.Path);
        await using var service = NewService(factory, "--behavior", "persistent-state");
        service.BaselineSource = new WorkerBaselines(factory.Database, () => directory);
        await service.IndexWorkspaceAsync("fake", [file]);
        Assert.Null(service.LastBaselineOutcomeFor("fake"));
    }

    [Fact]
    public async Task SaveState_IsSentOnlyWhileThePairingHolds()
    {
        var file = WriteFile("a.fake");
        var failMarker = Path.Combine(_tempDir, "fail.marker");
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        await using var service = NewService(factory, "--behavior", "persistent-state", "--fail-marker", failMarker);
        IWorkerStateSaver saver = service;
        await service.IndexWorkspaceAsync("fake", [file]);
        var (shard, annotation) = factory.Database.CurrentShard(FakeShard);
        var lineage = annotation!.LineageFor(shard)!;
        Assert.True(lineage.Establishes);

        var path = Path.Combine(_tempDir, "worker-fake.0000000001.11111111.state");
        var saved = await saver.SaveStateAsync("fake", lineage, new SaveStateParams("default", path, new string('1', 32)), default);
        Assert.True(saved!.Saved);
        // A lineage that names another request of the process (a later one happened, or an earlier one) does not pair.
        Assert.Null(await saver.SaveStateAsync("fake", lineage with { Request = lineage.Request + 1 },
            new SaveStateParams("default", path, new string('1', 32)), default));

        // A request that failed after its delta committed: the published lineage no longer pairs.
        System.IO.File.WriteAllText(failMarker, "fail");
        await Assert.ThrowsAsync<ParserWorkerFailedException>(() =>
            service.ApplyChangesAsync("fake", [new FileChange(file, FileChangeType.Changed)], [file]));
        (shard, annotation) = factory.Database.CurrentShard(FakeShard);
        Assert.NotNull(annotation!.LineageFor(shard));
        Assert.Null(await saver.SaveStateAsync("fake", annotation.LineageFor(shard)!,
            new SaveStateParams("default", path, new string('1', 32)), default));
    }

    /// <summary>Offers what <paramref name="inner"/> offers, then runs <paramref name="afterOffer"/> before the index is sent.</summary>
    private sealed class ChangingBaselineSource(IWorkerBaselineSource inner, Action? afterOffer) : IWorkerBaselineSource
    {
        public IndexBaseline? TryGetBaseline(string parserId, string workspaceId, string parserVersion)
        {
            var baseline = inner.TryGetBaseline(parserId, workspaceId, parserVersion);
            if (baseline is not null) afterOffer?.Invoke();
            return baseline;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedBaseline_StartsALineageOnlyIfTheShardStillCarriesTheOfferedState(bool changedAfterTheOffer)
    {
        var file = WriteFile("a.fake");
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        var directory = CacheDirectory.Open(_tempDir, LanguageWorkerService.DefaultWorkspaceId, out var refusal)!;
        Assert.Null(refusal);

        // A process indexes, saves its state, and the state is recorded with the shard, as a capture does.
        await using (var first = NewService(factory, "--behavior", "persistent-state"))
        {
            await first.IndexWorkspaceAsync("fake", [file]);
            var (shard, annotation) = factory.Database.CurrentShard(FakeShard);
            var stateId = WorkerStateFile.NewStateId();
            var name = WorkerStateFile.FormatName("fake", 1, stateId)!;
            var saved = await ((IWorkerStateSaver)first).SaveStateAsync("fake", annotation!.LineageFor(shard)!,
                new SaveStateParams(LanguageWorkerService.DefaultWorkspaceId, directory.PathOf(name), stateId), default);
            Assert.True(saved!.Saved);
            Assert.True(factory.Database.TryRecordWorkerState(FakeShard, shard!, annotation,
                new WorkerStateBinding(name, stateId, saved.Sha256!, saved.Bytes!.Value)));
        }

        // The next process is offered that state and accepts it. In between, something may change the shard.
        await using var second = NewService(factory, "--behavior", "persistent-state");
        second.BaselineSource = new ChangingBaselineSource(new WorkerBaselines(factory.Database, () => directory),
            changedAfterTheOffer
                ? () => factory.Database.UpsertNode(new CodeContext.Core.CodeNode
                {
                    Id = "fake:default:Late", Identifier = "fake:Late", Name = "Late", Type = "class", FilePath = file,
                    Metadata = new Dictionary<string, string> { ["parserId"] = "fake", ["workspaceId"] = "default" },
                })
                : null);
        await second.IndexWorkspaceAsync("fake", [file]);
        Assert.True(second.AcceptedBaseline("fake"));

        var (after, afterAnnotation) = factory.Database.CurrentShard(FakeShard);
        if (changedAfterTheOffer)
        {
            // The shard holds a fact the worker never emitted: committed as an ordinary file-scoped commit, no lineage.
            Assert.Contains(after!.GetAllNodes(), node => node.Name == "Late");
            Assert.Null(afterAnnotation!.LineageFor(after));
            Assert.Null(afterAnnotation.StateFor(after));
        }
        else
        {
            Assert.True(afterAnnotation!.LineageFor(after)!.Establishes);
            Assert.NotNull(afterAnnotation.StateFor(after));
        }
    }
}
