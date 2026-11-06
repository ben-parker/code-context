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

        // Parser health is discovered entirely from worker session reports; the real
        // parsers run out-of-process and report through this registry.
        private readonly ParserSessionRegistry _sessionRegistry = new();

        private StatusService CreateService(
            IApiMetrics? apiMetrics = null, ApplicationStartTime? applicationStartTime = null,
            List<FileMetadata>? files = null, IAnalysisCoverageRegistry? coverage = null, ICacheStateProvider? cache = null)
        {
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode>());
            _edgeRepository.GetAllAsync().Returns(new List<CodeEdge>());
            _fileMetadataRepository.GetAllAsync().Returns(files ?? new List<FileMetadata>());
            _fileMetadataRepository.GetCountByStatusAsync(Arg.Any<FileProcessingStatus>()).Returns(0);

            var options = Options.Create(new CodeContextOptions { RootPath = "/tmp/repo" });
            return new StatusService(
                _nodeRepository, _edgeRepository, _fileMetadataRepository,
                _repositoryFactory, options, _scanState, _sessionRegistry,
                apiMetrics ?? new ApiMetrics(), applicationStartTime: applicationStartTime, coverage: coverage, cacheState: cache);
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
            Assert.Equal(1, status.Api.ContractVersion);

            var json = JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(
                status.System.InformationalVersion,
                document.RootElement.GetProperty("system").GetProperty("informationalVersion").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("api").GetProperty("contractVersion").GetInt32());
        }

        [Fact]
        public async Task GetStatusAsync_UsesHostStartTimeBeforeFirstScan()
        {
            var started = DateTimeOffset.UtcNow.AddMinutes(-2);
            var scanned = DateTime.UtcNow.AddMinutes(-1);
            var status = await CreateService(
                applicationStartTime: new ApplicationStartTime(started),
                files: [new FileMetadata { FilePath = "/tmp/repo/A.cs", LastScanned = scanned }])
                .GetStatusAsync();

            Assert.Equal(started, DateTimeOffset.Parse(status.System.StartedAt));
            Assert.True(DateTimeOffset.Parse(status.System.StartedAt) < DateTimeOffset.Parse(status.Indexing.LastScanAt!));
            Assert.StartsWith("2m", status.System.Uptime);
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
            // With no in-process parsers, status is keyed by the worker's parserId.
            Assert.StartsWith("failed", status.Parsers.Status["csharp"]);
            Assert.Contains("boom", status.Parsers.Status["csharp"]);
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
            Assert.Equal("ready", status.Parsers.Status["csharp"]);
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

        [Fact]
        public async Task GetStatusAsync_ReportsTheCacheObject_AndMarksCachedCoverage()
        {
            _scanState.Phase.Returns(ScanPhase.Scanning);
            var coverage = new AnalysisCoverageRegistry();
            coverage.Report("csharp", "default", [new CodeContext.Parser.Protocol.ProjectCoverage("App", "src/App.csproj", "partial", 3)]);
            var cache = new CacheState(enabled: true, () => new CodeContext.Core.Services.CacheLoadOutcome(
                CodeContext.Core.Services.CacheLoadKind.Loaded, null, new DateTime(2026, 11, 4, 10, 12, 3, DateTimeKind.Utc).Ticks, 42, 26_123_456, ["csharp"]));
            cache.RunLoadStep();

            var status = await CreateService(coverage: coverage, cache: cache).GetStatusAsync();
            Assert.Equal("scanning", status.Indexing.Status);
            Assert.Equal("verifying", status.Indexing.Cache!.State);
            Assert.True(status.Indexing.Cache.ServingCached);
            Assert.True(status.Coverage!.Cached);
            var json = JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto);
            Assert.Contains("\"operationId\":0,\"cache\":{\"state\":\"verifying\",\"servingCached\":true,\"snapshotAt\":\"2026-11-04T10:12:03.0000000Z\",\"snapshotSequence\":42,\"sizeBytes\":26123456,\"verification\":{\"csharp\":\"pending\"}}", json);
            Assert.Contains("\"cached\":true", json);

            // Verified: the object stays, nothing is served from the snapshot, coverage is no longer marked.
            cache.OnScanCommitted();
            _scanState.Phase.Returns(ScanPhase.Ready);
            status = await CreateService(coverage: coverage, cache: cache).GetStatusAsync();
            Assert.Equal("ready", status.Indexing.Status);
            Assert.Equal("verified", status.Indexing.Cache!.State);
            Assert.False(status.Indexing.Cache.ServingCached);
            Assert.Null(status.Coverage!.Cached);
            Assert.DoesNotContain("\"cached\"", JsonSerializer.Serialize(status, CodeContextJsonContext.Default.StatusResponseDto));
        }

        [Fact]
        public void ComposeCache_FollowsTheOnePhaseTheResponseReports()
        {
            var verifying = new CacheStatusDto(CacheState.Verifying, true, SnapshotSequence: 42);
            var unverified = verifying with { State = CacheState.Unverified };
            var verified = new CacheStatusDto(CacheState.Verified, false, SnapshotSequence: 43);

            // Ready: only after the commit left verifying, so the later reading; never serving with ready.
            Assert.Same(verified, StatusService.ComposeCache(verifying, verified, ScanPhase.Ready));
            // Loaded or verified during the reads, before ready: the serving reading wins.
            Assert.Same(verifying, StatusService.ComposeCache(verifying, verified, ScanPhase.Scanning));
            Assert.Same(verifying, StatusService.ComposeCache(null, verifying, ScanPhase.Scanning));
            // Error with a served snapshot is always unverified; a running scan is always verifying.
            Assert.Equal(CacheState.Unverified, StatusService.ComposeCache(verifying, verifying, ScanPhase.Error)!.State);
            Assert.Equal(CacheState.Verifying, StatusService.ComposeCache(unverified, unverified, ScanPhase.Scanning)!.State);
            Assert.Same(verified, StatusService.ComposeCache(verified, verified, ScanPhase.Error));
            Assert.Null(StatusService.ComposeCache(null, null, ScanPhase.Scanning));
        }

        [Fact]
        public async Task GetStatusAsync_WithNoCache_HasNoCacheObject()
        {
            _scanState.Phase.Returns(ScanPhase.Ready);
            var json = JsonSerializer.Serialize(await CreateService().GetStatusAsync(), CodeContextJsonContext.Default.StatusResponseDto);
            Assert.DoesNotContain("\"cache\"", json);
        }
    }
}
