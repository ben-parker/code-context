using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class StatusServiceScanStateTests
    {
        private readonly ICodeNodeRepository _nodeRepository = Substitute.For<ICodeNodeRepository>();
        private readonly ICodeEdgeRepository _edgeRepository = Substitute.For<ICodeEdgeRepository>();
        private readonly IFileMetadataRepository _fileMetadataRepository = Substitute.For<IFileMetadataRepository>();
        private readonly IRepositoryFactory _repositoryFactory = Substitute.For<IRepositoryFactory>();
        private readonly IScanStateService _scanState = Substitute.For<IScanStateService>();

        // A stand-in in-process parser named "CSharp": status discovery only reads
        // parser metadata (the real C# parser now runs out-of-process and reports via
        // the session registry instead).
        private readonly List<ILanguageParser> _parsers = new() { new StubCSharpParser() };
        private readonly ParserSessionRegistry _sessionRegistry = new();

        private StatusService CreateService(IApiMetrics? apiMetrics = null)
        {
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode>());
            _edgeRepository.GetAllAsync().Returns(new List<CodeEdge>());
            _fileMetadataRepository.GetAllAsync().Returns(new List<FileMetadata>());
            _fileMetadataRepository.GetCountByStatusAsync(Arg.Any<FileProcessingStatus>()).Returns(0);

            var options = Options.Create(new CodeContextOptions { RootPath = "/tmp/repo" });
            return new StatusService(
                _nodeRepository, _edgeRepository, _fileMetadataRepository,
                _repositoryFactory, _parsers, options, _scanState, _sessionRegistry,
                apiMetrics ?? new ApiMetrics());
        }

        [Fact]
        public async Task GetStatusAsync_ReportsRecordedApiMetrics()
        {
            var metrics = new ApiMetrics();
            metrics.Record(TimeSpan.FromMilliseconds(10));
            metrics.Record(TimeSpan.FromMilliseconds(30));

            var status = await CreateService(metrics).GetStatusAsync();

            Assert.Equal(2, status.Api.RequestCount);
            Assert.Equal("20ms", status.Api.AverageResponseTime);
        }

        [Fact]
        public async Task GetStatusAsync_SerializesInformationalAndContractVersions()
        {
            var status = await CreateService().GetStatusAsync();

            Assert.False(string.IsNullOrWhiteSpace(status.System.Version));
            Assert.False(string.IsNullOrWhiteSpace(status.System.InformationalVersion));
            Assert.Equal(2, status.Api.ContractVersion);

            var json = JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                status.System.InformationalVersion,
                document.RootElement.GetProperty("system").GetProperty("informationalVersion").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("api").GetProperty("contractVersion").GetInt32());
        }

        [Fact]
        public async Task GetStatusAsync_WhileScanning_ReportsScanningWithProgress()
        {
            _scanState.Phase.Returns(ScanPhase.Scanning);
            _scanState.FilesProcessed.Returns(3);
            _scanState.FilesTotal.Returns(10);

            var status = await CreateService().GetStatusAsync();

            Assert.Equal("scanning", status.Indexing.Status);
            Assert.False(status.Indexed);
            Assert.Equal(3, status.Indexing.FilesProcessed);
            Assert.Equal(10, status.Indexing.FilesTotal);
        }

        [Fact]
        public async Task GetStatusAsync_WhenReady_ReportsReadyWithDuration()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _scanState.LastScanDuration.Returns(TimeSpan.FromSeconds(4));

            var status = await CreateService().GetStatusAsync();

            Assert.Equal("ready", status.Indexing.Status);
            Assert.True(status.Indexed);
            Assert.NotNull(status.Indexing.ScanDuration);
        }

        [Fact]
        public async Task GetStatusAsync_OnError_ReportsError()
        {
            _scanState.Phase.Returns(ScanPhase.Error);
            _scanState.LastError.Returns("boom");

            var status = await CreateService().GetStatusAsync();

            Assert.Equal("error", status.Indexing.Status);
            Assert.False(status.Indexed);
        }

        [Fact]
        public async Task GetStatusAsync_WatcherState_ComesFromScanState()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _scanState.WatcherActive.Returns(false);

            var status = await CreateService().GetStatusAsync();

            Assert.False(status.Watchers.Active);
        }

        [Fact]
        public async Task GetStatusAsync_ReportsOperationId()
        {
            _scanState.Phase.Returns(ScanPhase.Scanning);
            _scanState.OperationId.Returns(7L);

            var status = await CreateService().GetStatusAsync();

            Assert.Equal(7, status.Indexing.OperationId);
        }

        [Fact]
        public async Task GetStatusAsync_ParserStatus_ComesFromRegisteredParsers()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);

            var status = await CreateService().GetStatusAsync();

            // Discovered, not hard-coded: only the registered C# parser appears, and
            // the historical phantom "Python" entry is gone.
            Assert.Contains("CSharp", status.Parsers.Available);
            Assert.DoesNotContain("Python", status.Parsers.Available);
            Assert.Equal("active", status.Parsers.Status["CSharp"]);
        }

        [Fact]
        public async Task GetStatusAsync_UnavailableParser_ReportsRemediation()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _parsers.Add(new FakeUnavailableParser());

            var status = await CreateService().GetStatusAsync();

            Assert.Contains("Fake", status.Parsers.Available);
            Assert.DoesNotContain("Fake", status.Parsers.Enabled);
            Assert.StartsWith("unavailable", status.Parsers.Status["Fake"]);
            Assert.Contains("install the fake runtime", status.Parsers.Status["Fake"]);
        }

        [Fact]
        public async Task GetStatusAsync_NoSessionReports_OmitsSessions()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);

            var status = await CreateService().GetStatusAsync();

            Assert.Null(status.Parsers.Sessions);
        }

        [Fact]
        public async Task GetStatusAsync_SessionReport_SurfacesPerParserState()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _sessionRegistry.Report(new ParserSessionSnapshot(
                ParserId: "csharp",
                DisplayName: "CSharp",
                State: ParserSessionState.Failed,
                LastError: "C# batch of 2 change(s) failed: boom"));

            var status = await CreateService().GetStatusAsync();

            var session = Assert.Single(status.Parsers.Sessions!);
            Assert.Equal("csharp", session.ParserId);
            Assert.Equal("failed", session.State);
            Assert.Contains("boom", session.LastError);
            // The session state overrides the coarse "active" default for that parser.
            Assert.StartsWith("failed", status.Parsers.Status["CSharp"]);
            Assert.Contains("boom", status.Parsers.Status["CSharp"]);
        }

        [Fact]
        public async Task GetStatusAsync_ReadyAfterFailure_KeepsLastErrorDetail()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _sessionRegistry.Report(new ParserSessionSnapshot(
                "csharp", "CSharp", ParserSessionState.Failed, LastError: "boom"));
            _sessionRegistry.Report(new ParserSessionSnapshot(
                "csharp", "CSharp", ParserSessionState.Ready));

            var status = await CreateService().GetStatusAsync();

            var session = Assert.Single(status.Parsers.Sessions!);
            Assert.Equal("ready", session.State);
            Assert.Equal("boom", session.LastError);
            Assert.Equal("ready", status.Parsers.Status["CSharp"]);
        }

        [Fact]
        public async Task GetStatusAsync_WorkerSession_AppearsWithoutMatchingInProcessParser()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            _sessionRegistry.Report(new ParserSessionSnapshot(
                "fake", "Fake Worker", ParserSessionState.Indexing,
                ProcessId: 4242, ParserVersion: "1.0.0-test", ProtocolVersion: 1));

            var status = await CreateService().GetStatusAsync();

            var session = Assert.Single(status.Parsers.Sessions!);
            Assert.Equal("indexing", session.State);
            Assert.Equal(4242, session.Pid);
            Assert.Equal(1, session.ProtocolVersion);
            Assert.Equal("indexing", status.Parsers.Status["fake"]);
        }

        private sealed class StubCSharpParser : ILanguageParser, IParserDiagnostics
        {
            public string[] SupportedExtensions => [".cs"];
            public string DisplayName => "CSharp";
            public bool IsAvailable => true;
            public string? UnavailableReason => null;
            public CodeGraph ParseFile(string filePath, string content) => new();
            public CodeGraph ParseFiles(Dictionary<string, string> fileContents) => new();
        }

        private sealed class FakeUnavailableParser : ILanguageParser, IParserDiagnostics
        {
            public string[] SupportedExtensions => [".fake"];
            public string DisplayName => "Fake";
            public bool IsAvailable => false;
            public string? UnavailableReason => "install the fake runtime";
            public CodeGraph ParseFile(string filePath, string content) => new();
            public CodeGraph ParseFiles(Dictionary<string, string> fileContents) => new();
        }
    }
}
