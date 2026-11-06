using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The convergence gate of plan phase 4: a host restarted from its snapshot must end, after its
/// verification scan, with the graph a fresh scan produces. The edit walk of
/// <see cref="CSharpWorkerEditWalkConvergenceTests"/> runs through the real C# worker; at every 8th
/// step the host's store is written to a snapshot by the production capture and write path (the
/// snapshot service's final flush on stop) and thrown away with its worker, the step's edits reach
/// the disk while it is down, and a new store and a new worker process start from the snapshot
/// through the production load step, then run the full-scan verification the coordinator runs after
/// a load.
///
/// Before the verification replaces every shard, the loaded store is compared with the store that
/// wrote it: every field of every node and edge (metadata included), every shard's annotation, the
/// coverage a registry is restored to, and compact context answers. The graph is compared with a
/// fresh scan, by the same fact comparison, at every step.
/// </summary>
public sealed class SnapshotReloadConvergenceTests : IAsyncLifetime
{
    private const int Steps = 120;
    private const int ReloadEvery = 8;

    private readonly string _base = Directory.CreateTempSubdirectory("cc-reload-walk-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private CSharpWorkerPipeline _incremental = null!;
    private CSharpWorkerPipeline _fresh = null!;
    private SnapshotServiceSettings? _settings;
    private ulong _sequence;

    private List<string>? _factsBefore;
    private List<string>? _annotationsBefore;
    private string? _coverageBefore;
    private List<(string Identifier, string Answer)>? _answersBefore;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        _incremental = new CSharpWorkerPipeline(Root);
        _fresh = new CSharpWorkerPipeline(Root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _incremental.DisposeAsync();
        await _fresh.DisposeAsync();
        Directory.Delete(_base, recursive: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20261104)]
    public async Task EveryStepMatchesAFreshScan(int seed)
    {
        var walk = new CSharpWorkerEditWalkConvergenceTests.Walk(Root, seed);
        walk.WriteAll();
        await _incremental.GraphUpdateService.PerformInitialScanAsync(Root, null, CancellationToken.None);
        await AssertConverged(walk, seed, "initial scan");

        var reloads = 0;
        for (var step = 1; step <= Steps; step++)
        {
            if (step % ReloadEvery == 0)
            {
                await WriteSnapshotAndStopAsync(seed, step);
                // Edited while the host is down: no event reaches anyone.
                walk.TakeStep();
                await ReloadAndVerifyAsync(seed, step);
                reloads++;
            }
            else
            {
                var changes = walk.TakeStep();
                await _incremental.GraphUpdateService.ProcessFileChangesAsync(changes, CancellationToken.None);
            }
            await AssertConverged(walk, seed, $"step {step}");
        }

        Assert.Equal(Steps / ReloadEvery, reloads);
        Assert.True(walk.BodyEdits >= Steps / 4, $"Only {walk.BodyEdits} body edits.");
        Assert.True(walk.TopLevelEdits >= Steps / 4, $"Only {walk.TopLevelEdits} top-level edits.");
    }

    /// <summary>
    /// The production write: a snapshot service over the running store, whose stop flushes the dirty store
    /// through a capture on a real coordinator and the background write (temp file, rename, re-open).
    /// </summary>
    private async Task WriteSnapshotAndStopAsync(int seed, int step)
    {
        var database = _incremental.RepositoryFactory.Database;
        await using (var writer = new SnapshotServiceHarness(_base, database: database))
        {
            _settings = writer.Settings;
            await writer.StartAsync();
            Assert.Equal(SnapshotCacheMode.Writer, writer.Service.Status.Mode);
            await writer.StopAsync();
            Assert.True(writer.Service.Status.WritesCompleted == 1,
                $"Seed {seed}, step {step}: no snapshot written ({writer.Service.Status.LastWriteError}).");
            Assert.Equal(_sequence + 1, (ulong)writer.Service.Status.LastSequence!.Value);
            _sequence = (ulong)writer.Service.Status.LastSequence!.Value;
        }

        _factsBefore = await Facts(_incremental);
        _annotationsBefore = Annotations(database);
        // A walk whose snapshot carries no coverage could not tell a loader that drops it.
        Assert.Contains(_annotationsBefore, line => line.Contains("\"state\"", StringComparison.Ordinal));
        _coverageBefore = CoverageJson(AnalysisCoverageRegistry.ForStore(_incremental.RepositoryFactory).GetProjects());
        _answersBefore = await AnswersAsync(_incremental, cacheState: null, await QueriesAsync(_incremental));
        Assert.True(_answersBefore.Count >= 3, "The walk's store has too few symbols to query.");
        await _incremental.DisposeAsync();
    }

    private async Task ReloadAndVerifyAsync(int seed, int step)
    {
        _incremental = new CSharpWorkerPipeline(Root);
        var coverage = new AnalysisCoverageRegistry();
        using var startup = new SnapshotStartup(
            _settings!, _incremental.RepositoryFactory.Database, coverage, _incremental.WorkerService.IsIndexedParser,
            _incremental.WorkerService.MarkLoadedFromSnapshot, NullLogger<SnapshotService>.Instance);
        var cacheState = new CacheState(_settings!.Enabled, startup.Run);
        Assert.True(cacheState.RunLoadStep(), $"Seed {seed}, step {step}: {startup.Result?.Outcome.Kind} {startup.Result?.Outcome.Reason}");
        Assert.Equal(_sequence, startup.Result!.Outcome.SnapshotSequence);

        // The loaded store is the store that wrote it: facts, annotations, restored coverage, answers.
        AssertSame(_factsBefore!, await Facts(_incremental), $"Seed {seed}, step {step}: loaded facts");
        AssertSame(_annotationsBefore!, Annotations(_incremental.RepositoryFactory.Database), $"Seed {seed}, step {step}: loaded annotations");
        Assert.Equal(_coverageBefore, CoverageJson(coverage.GetProjects()));
        var answers = await AnswersAsync(_incremental, cacheState, _answersBefore!.Select(answer => answer.Identifier).ToList());
        Assert.Equal(_answersBefore, answers);

        // The coordinator's startup scan after a load: the full-scan flow, one reconciliation.
        await _incremental.GraphUpdateService.PerformInitialScanAsync(Root, null, CancellationToken.None);
    }

    private async Task AssertConverged(CSharpWorkerEditWalkConvergenceTests.Walk walk, int seed, string step)
    {
        await _fresh.GraphUpdateService.PerformInitialScanAsync(Root, null, CancellationToken.None);

        var expected = await Facts(_fresh);
        var actual = await Facts(_incremental);
        if (expected.SequenceEqual(actual)) return;

        var message = new StringBuilder($"Seed {seed} diverged at {step}.\n");
        message.AppendLine("Recent edits:").AppendJoin('\n', walk.History.TakeLast(6)).AppendLine();
        message.AppendLine("Missing:").AppendJoin('\n', expected.Except(actual).Take(10)).AppendLine();
        message.AppendLine("Stale:").AppendJoin('\n', actual.Except(expected).Take(10));
        Assert.Fail(message.ToString());
    }

    private static void AssertSame(List<string> expected, List<string> actual, string what)
    {
        if (expected.SequenceEqual(actual)) return;
        Assert.Fail($"{what} differ.\nMissing:\n{string.Join('\n', expected.Except(actual).Take(10))}\nExtra:\n{string.Join('\n', actual.Except(expected).Take(10))}");
    }

    /// <summary>
    /// Every field of every node and edge, metadata pairs in key order, in canonical order. The one fact
    /// comparison of this gate: the loaded store against the store that wrote it, and every step against
    /// a fresh scan. No field is left out of either.
    /// </summary>
    private static async Task<List<string>> Facts(CSharpWorkerPipeline pipeline)
    {
        var nodes = await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync();
        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        return nodes.Select(node => string.Join('|',
                "N", F(node.Id), F(node.Identifier), F(node.Name), F(node.Type), F(node.FilePath),
                I(node.StartLine), I(node.EndLine), I(node.StartCol), I(node.EndCol),
                F(node.Namespace), F(node.Visibility), F(node.Signature), F(node.Language), F(node.ReturnType),
                F(node.Parameters), F(node.Modifiers), F(node.Metrics), M(node.Metadata)))
            .Concat(edges.Select(edge => string.Join('|',
                "E", F(edge.Id), F(edge.SourceId), F(edge.TargetId), F(edge.Type), M(edge.Metadata))))
            .Order(StringComparer.Ordinal)
            .ToList();

        static string F(string? value) => value is null
            ? "<null>"
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("|", "\\p").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
        static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
        static string M(IReadOnlyDictionary<string, string>? metadata) => metadata is null
            ? "<null>"
            : "{" + string.Join(',', metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => F(pair.Key) + ":" + F(pair.Value))) + "}";
    }

    /// <summary>Each published shard's annotation: parser version and coverage, by shard.</summary>
    private static List<string> Annotations(InMemoryDatabase database)
        => database.LatchPublished().Annotations
            .Select(annotation => $"{annotation.Key}|{annotation.Value.ParserVersion}|{CoverageJson(annotation.Value.Coverage)}")
            .ToList();

    private static string CoverageJson(IReadOnlyList<ProjectCoverage>? coverage)
        => coverage is null ? "<null>" : JsonSerializer.Serialize(coverage.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage);

    /// <summary>A few identifiers the store holds (a type, a method, a short name), and one it does not.</summary>
    private static async Task<List<string>> QueriesAsync(CSharpWorkerPipeline pipeline)
    {
        var nodes = (await pipeline.RepositoryFactory.CreateNodeRepository().GetAllAsync())
            .Where(node => node.Identifier.Length > 0 && node.Name is { Length: > 0 })
            .OrderBy(node => node.Identifier, StringComparer.Ordinal)
            .ToList();
        var queries = new List<string>();
        if (nodes.Find(node => node.Type is "Class" or "class") is { } type) queries.Add(type.Identifier);
        if (nodes.Find(node => node.Type is "Method" or "method") is { } method) queries.Add(method.Identifier);
        if (nodes.Count > 0) queries.Add(nodes[nodes.Count / 2].Name!);
        queries.Add("NoSuchSymbolAnywhere");
        return queries;
    }

    /// <summary>Compact context answers, the cache notice aside (present only on the loaded store, which is verifying).</summary>
    private async Task<List<(string Identifier, string Answer)>> AnswersAsync(
        CSharpWorkerPipeline pipeline, CacheState? cacheState, List<string> identifiers)
    {
        var context = new ContextService(
            pipeline.RepositoryFactory.CreateNodeRepository(), pipeline.RepositoryFactory.CreateEdgeRepository(),
            pipeline.FileMetadataRepository, Options.Create(new CodeContextOptions { RootPath = Root }), pipeline.WorkerService,
            AnalysisCoverageRegistry.ForStore(pipeline.RepositoryFactory), cacheState);
        var answers = new List<(string, string)>();
        foreach (var identifier in identifiers)
        {
            var answer = await context.GetCompactContextAsync(identifier, includeTests: true);
            if (cacheState is not null) Assert.Equal(CacheState.Verifying, answer.Cache?.State);
            answer.Cache = null;
            answers.Add((identifier, JsonSerializer.Serialize(answer, CodeContextJsonContext.Default.CompactContextResponse)));
        }
        return answers;
    }
}
