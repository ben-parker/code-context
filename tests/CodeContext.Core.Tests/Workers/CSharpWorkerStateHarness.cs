using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Tests.Snapshots;
using CodeContext.Core.Workers;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The C# worker's request handling in this process, without a worker process: a <see cref="WorkerSession"/>
/// whose deltas go through the real delta applier into a real store, so the facts it ends with are the
/// facts a host would hold, comparable field for field (<see cref="GraphFacts"/>) and coverage entry for
/// coverage entry. A restart replaces the session with a new one, as a new worker process would be, while
/// the store, like a host's loaded snapshot, keeps what was committed.
/// </summary>
internal sealed class InProcessCSharpHost : IWorkerOutput
{
    public const string WorkspaceId = "default";

    private readonly string _root;
    private readonly Func<string, string?, CSharpWorkspaceAnalyzer>? _createAnalyzer;
    private readonly InMemoryRepositoryFactory _factory;
    private readonly AnalysisDeltaApplier _applier;
    private long _generation;
    private long _requestId;

    public InProcessCSharpHost(string root, Func<string, string?, CSharpWorkspaceAnalyzer>? createAnalyzer = null)
    {
        _root = root;
        _createAnalyzer = createAnalyzer;
        _factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        _factory.InitializeAsync(root).Wait();
        _applier = new AnalysisDeltaApplier((IGenerationalGraphStore)_factory.CreateGraphRepository(), NullLogger<AnalysisDeltaApplier>.Instance);
        Session = new WorkerSession(this, createAnalyzer);
    }

    public WorkerSession Session { get; private set; }

    public CSharpWorkspaceAnalyzer Analyzer => Session.Workspace(WorkspaceId)!;

    /// <summary>Every chunk of the last request, in order.</summary>
    public List<AnalysisDelta> LastDeltas { get; } = [];

    public List<AnalysisProgress> LastProgress { get; } = [];

    public ConcurrentQueue<string> Diagnostics { get; } = new();

    /// <summary>The last <c>verify:</c> line.</summary>
    public string? LastVerify => Diagnostics.LastOrDefault(line => line.StartsWith("verify:", StringComparison.Ordinal));

    /// <summary>The source files a host would approve: every <c>.cs</c> outside build output, in path order.</summary>
    public List<string> ApprovedFiles()
        => Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or ".codecontext"))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>A new worker process: nothing it knew survives.</summary>
    public void Restart() => Session = new WorkerSession(this, _createAnalyzer);

    /// <summary>Open, then index, as the host does: with <paramref name="baseline"/> when given.</summary>
    public async Task<IndexWorkspaceResult> IndexAsync(IndexBaseline? baseline = null)
    {
        var files = ApprovedFiles();
        await Session.OpenAsync(new OpenWorkspaceParams(WorkspaceId, _root, [".csproj", ".sln"], files), CancellationToken.None);
        Begin();
        return await Session.IndexAsync(++_requestId, new IndexWorkspaceParams(WorkspaceId, ++_generation, files, baseline), CancellationToken.None);
    }

    /// <summary>
    /// A watcher batch, routed as the host routes it: source changes in one apply, then an empty apply
    /// when a project input changed.
    /// </summary>
    public async Task ApplyAsync(IReadOnlyList<FileChange> changes)
    {
        var sources = changes.Where(change => change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(change => new FileChangeDto(change.Path, change.Type == FileChangeType.Deleted || !File.Exists(change.Path)
                ? FileChangeKinds.Deleted
                : FileChangeKinds.Changed))
            .ToList();
        var projects = changes.Any(change => !change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        if (sources.Count > 0) await ApplyChangesAsync(sources);
        if (projects) await ApplyChangesAsync([]);
    }

    public async Task ApplyChangesAsync(IReadOnlyList<FileChangeDto> changes)
    {
        await Session.OpenAsync(new OpenWorkspaceParams(WorkspaceId, _root, [".csproj", ".sln"], ApprovedFiles()), CancellationToken.None);
        Begin();
        await Session.ApplyAsync(++_requestId, new ApplyChangesParams(WorkspaceId, ++_generation, changes), CancellationToken.None);
    }

    /// <summary>
    /// <c>workspace/saveState</c> to a file in <paramref name="directory"/>, then the baseline a host would offer
    /// from it: the state's id and hash and the fingerprints the store recorded with the last commit.
    /// </summary>
    public async Task<IndexBaseline> SaveAsync(string directory)
    {
        var stateId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var path = Path.Combine(directory, $"worker-csharp.{stateId[..8]}.state");
        var result = await Session.SaveStateAsync(new SaveStateParams(WorkspaceId, path, stateId), CancellationToken.None);
        Assert.True(result.Saved, "the worker did not save its state");
        Assert.Equal(new FileInfo(path).Length, result.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), result.Sha256);
        return new IndexBaseline(path, stateId, result.Sha256!, Annotation()?.Fingerprints ?? []);
    }

    public InMemoryDatabase Database => _factory.Database;

    public List<string> Facts() => GraphFacts.Of(Database);

    private ShardAnnotation? Annotation()
        => Database.LatchPublished().Annotations.GetValueOrDefault(new InMemoryDatabase.ShardKey("csharp", WorkspaceId));

    /// <summary>The coverage recorded with the facts, as JSON: every field of every entry.</summary>
    public string Coverage()
        => JsonSerializer.Serialize((Annotation()?.Coverage ?? []).ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage);

    private void Begin()
    {
        LastDeltas.Clear();
        LastProgress.Clear();
    }

    Task IWorkerOutput.ProgressAsync(AnalysisProgress progress, CancellationToken ct)
    {
        LastProgress.Add(progress);
        return Task.CompletedTask;
    }

    async Task IWorkerOutput.DeltaAsync(AnalysisDelta delta, CancellationToken ct)
    {
        LastDeltas.Add(delta);
        Assert.True(await _applier.ApplyAsync(delta, ct), "the host refused a delta");
    }

    void IWorkerOutput.Diagnostic(string line) => Diagnostics.Enqueue(line);
}

/// <summary>The counters of a <c>verify:</c> line.</summary>
internal readonly record struct VerifyLine(int Restored, int Walked, int Parsed, int Compilations)
{
    public static VerifyLine Parse(string? line)
    {
        Assert.NotNull(line);
        var values = line["verify:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('='))
            .ToDictionary(pair => pair[0], pair => int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(["compilations", "parsed", "restored", "walked"], values.Keys.Order(StringComparer.Ordinal));
        return new VerifyLine(values["restored"], values["walked"], values["parsed"], values["compilations"]);
    }
}
