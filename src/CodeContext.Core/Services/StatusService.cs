using CodeContext.Core.Models;
using CodeContext.Core.Repositories;
using CodeContext.Core.Serialization;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Reflection;

namespace CodeContext.Core.Services;

public class StatusService : IStatusService
{
    private readonly ICodeNodeRepository _nodeRepository;
    private readonly ICodeEdgeRepository _edgeRepository;
    private readonly IFileMetadataRepository _fileMetadataRepository;
    private readonly IRepositoryFactory _repositoryFactory;
    private readonly IEnumerable<ILanguageParser> _parsers;
    private readonly CodeContextOptions _options;
    private readonly IScanStateService _scanState;
    private readonly IParserSessionRegistry _sessionRegistry;
    private readonly IApiMetrics _apiMetrics;
    private readonly IRepositoryFileSelector? _fileSelector;
    private readonly DateTimeOffset _startTime;

    public StatusService(
        ICodeNodeRepository nodeRepository,
        ICodeEdgeRepository edgeRepository,
        IFileMetadataRepository fileMetadataRepository,
        IRepositoryFactory repositoryFactory,
        IEnumerable<ILanguageParser> parsers,
        IOptions<CodeContextOptions> options,
        IScanStateService scanState,
        IParserSessionRegistry sessionRegistry,
        IApiMetrics apiMetrics,
        IRepositoryFileSelector? fileSelector = null,
        ApplicationStartTime? applicationStartTime = null)
    {
        _nodeRepository = nodeRepository;
        _edgeRepository = edgeRepository;
        _fileMetadataRepository = fileMetadataRepository;
        _repositoryFactory = repositoryFactory;
        _parsers = parsers;
        _options = options.Value;
        _scanState = scanState;
        _sessionRegistry = sessionRegistry;
        _apiMetrics = apiMetrics;
        _fileSelector = fileSelector;
        _startTime = (applicationStartTime ?? new ApplicationStartTime(DateTimeOffset.UtcNow)).Value;
    }

    public async Task<StatusResponseDto> GetStatusAsync()
    {
        // One pass over file metadata covers counts, per-status buckets, language
        // breakdown, and the most recent scan timestamp. Graph counts come from the
        // store's maintained statistics when available so status polling never
        // materializes nodes/edges (the agent skill polls this every 1-2s during
        // indexing).
        var fileAggregates = await AggregateFileMetadataAsync();
        var graphStatistics = await GetGraphStatisticsAsync();

        var indexed = _scanState.Phase == ScanPhase.Ready;

        var systemStatus = GetSystemStatus();
        var indexingStatus = GetIndexingStatus(fileAggregates, indexed);
        var databaseStatus = GetDatabaseStatus(fileAggregates, graphStatistics);
        var watcherStatus = GetWatcherStatus();
        var parserStatus = GetParserStatus();
        var apiStatus = GetApiStatus();

        return new StatusResponseDto(
            System: systemStatus,
            Indexing: indexingStatus,
            Database: databaseStatus,
            Watchers: watcherStatus,
            Parsers: parserStatus,
            Api: apiStatus,
            Indexed: indexed,
            FileCount: fileAggregates.FileCount,
            NodeCount: graphStatistics.NodeCount
        );
    }

    private sealed record FileMetadataAggregates(
        int FileCount,
        Dictionary<string, int> FilesByStatus,
        Dictionary<string, int> LanguageBreakdown,
        DateTime? LastScanAt);

    private async Task<FileMetadataAggregates> AggregateFileMetadataAsync()
    {
        if (_fileMetadataRepository is IFileMetadataStatisticsProvider statisticsProvider)
        {
            var statistics = statisticsProvider.GetStatistics();
            var maintainedStatuses = Enum.GetValues<FileProcessingStatus>()
                .ToDictionary(status => status.ToString(), status =>
                    statistics.FilesByStatus.GetValueOrDefault(status));
            var maintainedLanguages = new Dictionary<string, int>();
            foreach (var (extension, count) in statistics.FilesByExtension)
            {
                var language = GetLanguageFromExtension(extension);
                if (!string.IsNullOrEmpty(language))
                {
                    maintainedLanguages[language] = maintainedLanguages.GetValueOrDefault(language) + count;
                }
            }
            return new FileMetadataAggregates(
                statistics.FileCount, maintainedStatuses, maintainedLanguages, statistics.LastScanAt);
        }

        var filesByStatus = new Dictionary<string, int>();
        foreach (var status in Enum.GetValues<FileProcessingStatus>())
        {
            filesByStatus[status.ToString()] = 0;
        }

        var languageBreakdown = new Dictionary<string, int>();
        DateTime? lastScanAt = null;
        var fileCount = 0;

        foreach (var file in await _fileMetadataRepository.GetAllAsync())
        {
            fileCount++;
            var statusKey = file.Status.ToString();
            filesByStatus[statusKey] = filesByStatus.GetValueOrDefault(statusKey) + 1;

            if (!string.IsNullOrEmpty(file.FilePath))
            {
                var language = GetLanguageFromExtension(Path.GetExtension(file.FilePath));
                if (!string.IsNullOrEmpty(language))
                {
                    languageBreakdown[language] = languageBreakdown.GetValueOrDefault(language) + 1;
                }
            }

            if (file.LastScanned != default && (lastScanAt is null || file.LastScanned > lastScanAt))
            {
                lastScanAt = file.LastScanned;
            }
        }

        return new FileMetadataAggregates(fileCount, filesByStatus, languageBreakdown, lastScanAt);
    }

    private async Task<GraphStatistics> GetGraphStatisticsAsync()
    {
        if (_repositoryFactory.CreateGraphRepository() is IGenerationalGraphStore store)
        {
            return store.GetStatistics();
        }

        // Fallback for repository implementations without maintained counters.
        var allNodes = await _nodeRepository.GetAllAsync();
        var allEdges = await _edgeRepository.GetAllAsync();

        var nodesByType = allNodes
            .Where(n => !string.IsNullOrEmpty(n.Type))
            .GroupBy(n => n.Type)
            .ToDictionary(g => g.Key!, g => g.Count());
        var edgesByType = allEdges
            .Where(e => !string.IsNullOrEmpty(e.Type))
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key!, g => g.Count());

        return new GraphStatistics(allNodes.Count, allEdges.Count, nodesByType, edgesByType);
    }

    private SystemStatusDto GetSystemStatus()
    {
        var uptime = DateTimeOffset.UtcNow - _startTime;
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? version;
        var memoryUsage = GC.GetTotalMemory(false);
        var memoryMB = (memoryUsage / 1024.0 / 1024.0).ToString("F1");

        return new SystemStatusDto(
            Version: version,
            InformationalVersion: informationalVersion,
            Uptime: FormatTimeSpan(uptime),
            StartedAt: _startTime.ToString("O"),
            MemoryUsage: $"{memoryMB}MB",
            ApiHealth: "healthy",
            InstanceId: _options.InstanceId
        );
    }

    private IndexingStatusDto GetIndexingStatus(FileMetadataAggregates files, bool indexed)
    {
        var indexingStatus = _scanState.Phase switch
        {
            ScanPhase.Ready => "ready",
            ScanPhase.Error => "error",
            _ => "scanning",
        };

        return new IndexingStatusDto(
            Indexed: indexed,
            Status: indexingStatus,
            LastScanAt: files.LastScanAt?.ToString("O"),
            ScanDuration: _scanState.LastScanDuration is { } duration ? FormatTimeSpan(duration) : null,
            RootPath: _options.RootPath,
            FilesByStatus: files.FilesByStatus,
            FilesProcessed: _scanState.FilesProcessed,
            FilesTotal: _scanState.FilesTotal,
            LastError: _scanState.Phase == ScanPhase.Error ? _scanState.LastError : null,
            OperationId: _scanState.OperationId
        );
    }

    private DatabaseStatusDto GetDatabaseStatus(FileMetadataAggregates files, GraphStatistics graph)
    {
        var repositoryType = _repositoryFactory is Repositories.InMemory.InMemoryRepositoryFactory
            ? "InMemory"
            : "Unknown";

        return new DatabaseStatusDto(
            FileCount: files.FileCount,
            NodeCount: graph.NodeCount,
            EdgeCount: graph.EdgeCount,
            NodeTypes: new Dictionary<string, int>(graph.NodesByType),
            LanguageBreakdown: files.LanguageBreakdown,
            RepositoryType: repositoryType,
            EdgeTypes: new Dictionary<string, int>(graph.EdgesByType)
        );
    }

    private WatcherStatusDto GetWatcherStatus()
    {
        return new WatcherStatusDto(
            Active: _scanState.WatcherActive,
            WatchedPaths: [_options.RootPath],
            IgnoredPatterns: _options.IgnorePatterns.ToList(),
            PendingChanges: 0, // TODO: Get actual pending changes count
            IgnoreSourceCount: _fileSelector?.IgnoreSourceCount ?? 0,
            IgnoredPathCount: _fileSelector?.IgnoredPathCount ?? 0,
            MandatoryExclusions: _fileSelector?.MandatoryExclusions
        );
    }

    private ParserStatusDto GetParserStatus()
    {
        // Discovered from the registered parsers, never hard-coded. A parser whose
        // external tooling is missing reports "unavailable" with a remediation message
        // so clients (the agent skill) can distinguish "no references" from "not parsed".
        var enabled = new List<string>();
        var available = new List<string>();
        var status = new Dictionary<string, string>();

        foreach (var parser in _parsers)
        {
            var diagnostics = parser as IParserDiagnostics;
            var name = diagnostics?.DisplayName ?? DeriveParserName(parser);
            available.Add(name);

            if (diagnostics is { IsAvailable: false })
            {
                status[name] = diagnostics.UnavailableReason is { } reason
                    ? $"unavailable: {reason}"
                    : "unavailable";
            }
            else
            {
                enabled.Add(name);
                status[name] = "active";
            }
        }

        // Session reports (worker supervisors and in-process parse outcomes) are the
        // richer source of truth: where one exists it overrides the coarse
        // active/unavailable default above. Out-of-process workers appear only here,
        // so their sessions also feed the available/enabled lists.
        var snapshots = _sessionRegistry.GetSnapshots();
        var sessions = new List<ParserSessionDto>(snapshots.Count);
        foreach (var session in snapshots)
        {
            if (!available.Contains(session.DisplayName))
            {
                available.Add(session.DisplayName);
                if (session.State is not (ParserSessionState.Unavailable or ParserSessionState.Stopped))
                {
                    enabled.Add(session.DisplayName);
                }
            }
            var state = ToCamelCase(session.State.ToString());
            sessions.Add(new ParserSessionDto(
                ParserId: session.ParserId,
                DisplayName: session.DisplayName,
                State: state,
                Message: session.Message,
                LastError: session.LastError,
                Pid: session.ProcessId,
                RestartCount: session.RestartCount,
                ParserVersion: session.ParserVersion,
                ProtocolVersion: session.ProtocolVersion,
                UpdatedAt: session.UpdatedAtUtc.ToString("O")));

            var key = status.ContainsKey(session.DisplayName) ? session.DisplayName : session.ParserId;
            status[key] = session.State == ParserSessionState.Failed && session.LastError is { } error
                ? $"{state}: {error}"
                : state;
        }

        return new ParserStatusDto(
            Enabled: enabled,
            Available: available,
            Status: status,
            Sessions: sessions.Count > 0 ? sessions : null
        );
    }

    private static string ToCamelCase(string value)
        => value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string DeriveParserName(ILanguageParser parser)
    {
        var name = parser.GetType().Name;
        return name.EndsWith("Parser", StringComparison.Ordinal) ? name[..^"Parser".Length] : name;
    }

    private ApiStatusDto GetApiStatus()
    {
        var metrics = _apiMetrics.GetSnapshot();
        var endpoints = new List<string>
        {
            "/api/status",
            "/api/context/complete",
            "/api/context/multi",
            "/api/syntax-tree",
            "/api/index/refresh",
            "/api/schema",
            "/api/shutdown",
            "/healthz"
        };

        return new ApiStatusDto(
            Endpoints: endpoints,
            RequestCount: checked((int)Math.Min(metrics.RequestCount, int.MaxValue)),
            AverageResponseTime: metrics.AverageResponseTime.TotalMilliseconds
                .ToString("0.##", CultureInfo.InvariantCulture) + "ms",
            ContractVersion: 1
        );
    }

    private static string FormatTimeSpan(TimeSpan timeSpan)
    {
        if (timeSpan.TotalDays >= 1)
            return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h {timeSpan.Minutes}m";
        if (timeSpan.TotalHours >= 1)
            return $"{timeSpan.Hours}h {timeSpan.Minutes}m {timeSpan.Seconds}s";
        if (timeSpan.TotalMinutes >= 1)
            return $"{timeSpan.Minutes}m {timeSpan.Seconds}s";

        return $"{timeSpan.Seconds}s";
    }

    private static string GetLanguageFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".cs" => "C#",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            ".jsx" => "JavaScript",
            ".tsx" => "TypeScript",
            ".py" => "Python",
            ".pyw" => "Python",
            _ => ""
        };
    }
}
