using CodeContext.Core.Models;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Workers;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Multi-worker failure isolation for <see cref="GraphUpdateService"/>: one broken
/// worker must not stop the host from attempting every other worker, recording the
/// failure, pruning, and surfacing the first exception. These drive the real C# worker
/// (the healthy language) alongside the protocol-conformant fake worker in its
/// <c>crash-on-index</c> mode (the failing language), so they exercise the actual
/// process/protocol path — not a mock. Like the other worker fixtures
/// (<see cref="ParserProcessSupervisorTests"/>, <see cref="CSharpWorkerProtocolFixtureTests"/>)
/// these spawn plain dotnet child processes and carry no ExternalTooling trait.
///
/// Reconciliation note: <see cref="GraphUpdateService.PerformInitialScanAsync"/> stages the
/// whole rescan behind the live snapshot and is atomic — when any worker fails it rethrows,
/// which rolls the staged generation back, so a full scan's healthy graph facts do NOT
/// survive a sibling failure (the previous complete graph is preserved instead). The
/// "healthy results survive a sibling failure" property lives on the change-batch path
/// (<see cref="GraphUpdateService.ProcessFileChangesAsync"/>), which commits per worker and
/// is not reconciliation-wrapped; that is what
/// <see cref="ChangeBatch_HealthyWorkerCommits_WhenASiblingWorkerFails"/> proves.
/// </summary>
public sealed class WorkerScanResilienceTests : IAsyncLifetime
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-worker-resilience-").FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>The real fake worker (owns <c>.fake</c>) wired to crash the instant it is
    /// asked to index or apply changes — the failing language in these tests.</summary>
    private static RegisteredWorker CrashingFakeRegistration()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var apphost = Path.Combine(baseDirectory,
            OperatingSystem.IsWindows() ? "CodeContext.FakeWorker.exe" : "CodeContext.FakeWorker");
        var spec = File.Exists(apphost)
            ? new WorkerLaunchSpec("fake", "Fake Worker", apphost, ["--behavior", "crash-on-index"])
            : new WorkerLaunchSpec("fake", "Fake Worker", "dotnet",
                [Path.Combine(baseDirectory, "CodeContext.FakeWorker.dll"), "--behavior", "crash-on-index"]);

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
    public async Task FullScan_OneWorkerFails_SurfacesFailureAndStillAttemptsEveryWorker()
    {
        var healthy = await WriteFileAsync("Healthy.cs", "namespace Fixture { public class Healthy { } }");
        var broken = await WriteFileAsync("broken.fake", "fake");

        await using var pipeline = new CSharpWorkerPipeline(_tempDir, CrashingFakeRegistration());

        // The failing worker's crash is collected and rethrown at the end of the scan
        // (not fail-fast at the first worker).
        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None));

        // The failing worker was attempted and its file recorded a per-file failure.
        var brokenMeta = await pipeline.FileMetadataRepository.GetByFilePathAsync(broken);
        Assert.NotNull(brokenMeta);
        Assert.Equal(FileProcessingStatus.Failed, brokenMeta!.Status);
        Assert.False(string.IsNullOrEmpty(brokenMeta.ErrorMessage));

        // The healthy worker was also attempted and did not fail. (Its staged Completed
        // status was reset to Pending when the atomic reconciliation rolled back — the
        // whole scan is all-or-nothing, so the healthy graph facts do not commit either.)
        var healthyMeta = await pipeline.FileMetadataRepository.GetByFilePathAsync(healthy);
        Assert.NotNull(healthyMeta);
        Assert.NotEqual(FileProcessingStatus.Failed, healthyMeta!.Status);
    }

    [Fact]
    public async Task FullScan_PrunesMissingFiles_EvenWhenAWorkerFails()
    {
        await WriteFileAsync("Healthy.cs", "namespace Fixture { public class Healthy { } }");
        await WriteFileAsync("broken.fake", "fake");

        await using var pipeline = new CSharpWorkerPipeline(_tempDir, CrashingFakeRegistration());

        // A file that used to exist but is no longer on disk: its stale metadata must be
        // pruned by the scan's unconditional PruneMissingFilesAsync even though a worker
        // fails afterwards (metadata deletes are not part of the rolled-back generation).
        var stalePath = Path.Combine(_tempDir, "Gone.cs");
        await pipeline.FileMetadataRepository.UpsertAsync(new FileMetadata
        {
            FilePath = stalePath,
            Status = FileProcessingStatus.Completed,
            LastModified = DateTime.UtcNow,
            LastScanned = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None));

        // Pruning ran despite the failure: the stale entry is gone.
        var stale = await pipeline.FileMetadataRepository.GetByFilePathAsync(stalePath);
        Assert.Null(stale);
    }

    [Fact]
    public async Task ChangeBatch_HealthyWorkerCommits_WhenASiblingWorkerFails()
    {
        var healthy = await WriteFileAsync("Healthy.cs", "namespace Fixture { public class Healthy { } }");
        var broken = await WriteFileAsync("broken.fake", "fake");

        await using var pipeline = new CSharpWorkerPipeline(_tempDir, CrashingFakeRegistration());

        // The failing worker's batch is listed first: a fail-fast regression would abort
        // before the C# batch commits, which is exactly the isolation this test guards.
        var changes = new[]
        {
            new FileChange(broken, FileChangeType.Created),
            new FileChange(healthy, FileChangeType.Created),
        };

        await Assert.ThrowsAsync<ParserWorkerFailedException>(
            () => pipeline.GraphUpdateService.ProcessFileChangesAsync(changes, CancellationToken.None));

        // The healthy worker's batch committed and is queryable despite the sibling crash.
        var nodes = await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        Assert.Contains(nodes, n => n.Name == "Healthy" && n.Type == "Class");

        // The healthy file reached Completed; the failed batch's file reached Failed.
        var healthyMeta = await pipeline.FileMetadataRepository.GetByFilePathAsync(healthy);
        Assert.NotNull(healthyMeta);
        Assert.Equal(FileProcessingStatus.Completed, healthyMeta!.Status);

        var brokenMeta = await pipeline.FileMetadataRepository.GetByFilePathAsync(broken);
        Assert.NotNull(brokenMeta);
        Assert.Equal(FileProcessingStatus.Failed, brokenMeta!.Status);
        Assert.False(string.IsNullOrEmpty(brokenMeta.ErrorMessage));
    }
}
