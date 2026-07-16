using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// In-process parsers must surface parse failures as per-parser session states so
/// /api/status can distinguish "parsing failed" from "no references" (deferred
/// follow-up from the Phase 0/1 review, wired up with the Phase 2 session states).
/// </summary>
public class GraphUpdateSessionReportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cc-session-").FullName;
    private readonly ParserSessionRegistry _registry = new();
    private readonly ThrowSwitchParser _parser = new();
    private readonly GraphUpdateService _service;

    public GraphUpdateSessionReportTests()
    {
        _service = new GraphUpdateService(
            new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance),
            [_parser],
            Options.Create(new CodeContextOptions { RootPath = _directory }),
            NullLogger<GraphUpdateService>.Instance,
            new InMemoryFileMetadataRepository(),
            _registry);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "content");
        return path;
    }

    [Fact]
    public async Task ParserThrows_SessionReportsFailedWithFileDetail()
    {
        _parser.ShouldThrow = true;
        var file = WriteFile("broken.boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.ProcessFileChangeAsync(file, FileChangeType.Changed, CancellationToken.None));

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Failed, session.State);
        Assert.Contains("broken.boom", session.LastError);
        Assert.Contains("kaboom", session.LastError);
    }

    [Fact]
    public async Task ParserRecovers_SessionReturnsToReadyButKeepsLastError()
    {
        _parser.ShouldThrow = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ProcessFileChangeAsync(WriteFile("broken.boom"), FileChangeType.Changed, CancellationToken.None));

        _parser.ShouldThrow = false;
        await _service.ProcessFileChangeAsync(WriteFile("fine.boom"), FileChangeType.Changed, CancellationToken.None);

        var session = Assert.Single(_registry.GetSnapshots());
        Assert.Equal(ParserSessionState.Ready, session.State);
        Assert.Contains("kaboom", session.LastError); // last failure detail is retained
    }

    private sealed class ThrowSwitchParser : ILanguageParser, IParserDiagnostics
    {
        public bool ShouldThrow { get; set; }
        public string[] SupportedExtensions => [".boom"];
        public string DisplayName => "Boom";
        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public CodeGraph ParseFile(string filePath, string content)
            => ShouldThrow ? throw new InvalidOperationException("kaboom") : new CodeGraph();

        public CodeGraph ParseFiles(Dictionary<string, string> fileContents) => new();
    }
}
