using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Phase 3 review regression tests: one broken file — or one unavailable parser —
/// must not abort a scan. All other files get processed, pruning and completion
/// still run, the failure is surfaced at the end, and the parser session reports
/// the failure even when a later file succeeded (no last-write-wins masking).
/// </summary>
public class ScanResilienceTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-scan-resilience-").FullName;
    private readonly InMemoryRepositoryFactory _repositoryFactory =
        new(NullLogger<InMemoryRepositoryFactory>.Instance);
    private readonly InMemoryFileMetadataRepository _metadata = new();
    private readonly ParserSessionRegistry _registry = new();

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private GraphUpdateService CreateService(params ILanguageParser[] parsers)
        => new(
            _repositoryFactory,
            parsers,
            Options.Create(new CodeContextOptions { RootPath = _tempDir }),
            NullLogger<GraphUpdateService>.Instance,
            _metadata,
            _registry);

    private string WriteFile(string name, string content = "content")
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task InitialScan_OneBadFile_ProcessesTheRestPrunesAndStillReportsError()
    {
        var service = CreateService(new SelectiveThrowParser());
        var good1 = WriteFile("good1.boom");
        var bad = WriteFile("bad.boom");
        var good2 = WriteFile("good2.boom");

        // A metadata record for a vanished file: pruning must still run.
        var ghost = Path.Combine(_tempDir, "ghost.boom");
        await _metadata.UpsertAsync(new FileMetadata { FilePath = ghost, Status = FileProcessingStatus.Completed });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None));

        var all = await _metadata.GetAllAsync();
        Assert.Equal(FileProcessingStatus.Completed, all.Single(m => m.FilePath == good1).Status);
        Assert.Equal(FileProcessingStatus.Completed, all.Single(m => m.FilePath == good2).Status);
        Assert.Equal(FileProcessingStatus.Failed, all.Single(m => m.FilePath == bad).Status);
        Assert.DoesNotContain(all, m => m.FilePath == ghost); // prune ran despite the failure
    }

    [Fact]
    public async Task InitialScan_FailureWinsOverLaterSuccessInSessionState()
    {
        var service = CreateService(new SelectiveThrowParser());
        WriteFile("aaa-bad.boom");   // enumerated before the good files
        WriteFile("zzz-good.boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None));

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Failed, session.State);
        Assert.Contains("kaboom", session.LastError);
    }

    [Fact]
    public async Task InitialScan_AllGood_SessionReportsReadyOnce()
    {
        var service = CreateService(new SelectiveThrowParser());
        WriteFile("one.boom");
        WriteFile("two.boom");

        await service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Ready, session.State);
    }

    [Fact]
    public async Task InitialScan_UnavailableParser_DoesNotStopOtherParsers()
    {
        var service = CreateService(new SelectiveThrowParser(), new UnavailableParser());
        var good = WriteFile("fine.boom");
        var blocked = WriteFile("blocked.na");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PerformInitialScanAsync(_tempDir, null, CancellationToken.None));

        var all = await _metadata.GetAllAsync();
        Assert.Equal(FileProcessingStatus.Completed, all.Single(m => m.FilePath == good).Status);
        Assert.Equal(FileProcessingStatus.Failed, all.Single(m => m.FilePath == blocked).Status);

        var sessions = _registry.GetSnapshots();
        Assert.Equal(ParserSessionState.Unavailable, sessions.Single(s => s.ParserId == "na").State);
        Assert.Equal(ParserSessionState.Ready, sessions.Single(s => s.ParserId == "boom").State);
    }

    [Fact]
    public async Task ResumableScan_OneBadFile_ProcessesTheRestAndStillReportsError()
    {
        var service = CreateService(new SelectiveThrowParser());
        var good = WriteFile("good.boom");
        var bad = WriteFile("bad.boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PerformResumableScanAsync(_tempDir, null, CancellationToken.None));

        var all = await _metadata.GetAllAsync();
        Assert.Equal(FileProcessingStatus.Completed, all.Single(m => m.FilePath == good).Status);
        Assert.Equal(FileProcessingStatus.Failed, all.Single(m => m.FilePath == bad).Status);
    }

    [Fact]
    public async Task ChangeBatch_OneBadFile_OthersInBatchAreStillProcessed()
    {
        var service = CreateService(new SelectiveThrowParser());
        var bad = WriteFile("bad.boom");
        var good = WriteFile("good.boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProcessFileChangesAsync(
        [
            new FileChange(bad, FileChangeType.Changed),
            new FileChange(good, FileChangeType.Changed),
        ], CancellationToken.None));

        var all = await _metadata.GetAllAsync();
        Assert.Equal(FileProcessingStatus.Completed, all.Single(m => m.FilePath == good).Status);
        Assert.Equal(FileProcessingStatus.Failed, all.Single(m => m.FilePath == bad).Status);
    }

    /// <summary>Parses ".boom" files; throws for any file whose name contains "bad".</summary>
    private sealed class SelectiveThrowParser : ILanguageParser, IParserDiagnostics
    {
        public string[] SupportedExtensions => [".boom"];
        public string DisplayName => "Boom";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public CodeGraph ParseFile(string filePath, string content)
            => Path.GetFileName(filePath).Contains("bad", StringComparison.OrdinalIgnoreCase)
                ? throw new InvalidOperationException("kaboom")
                : new CodeGraph();

        public CodeGraph ParseFiles(Dictionary<string, string> fileContents) => new();
    }

    private sealed class UnavailableParser : ILanguageParser, IParserDiagnostics
    {
        public string[] SupportedExtensions => [".na"];
        public string DisplayName => "NA";
        public bool IsAvailable => false;
        public string? UnavailableReason => "runtime is not installed";
        public CodeGraph ParseFile(string filePath, string content) => new();
        public CodeGraph ParseFiles(Dictionary<string, string> fileContents) => new();
    }
}
