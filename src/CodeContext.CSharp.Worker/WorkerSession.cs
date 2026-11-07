using System.Reflection;
using CodeContext.CSharp.Worker.State;
using CodeContext.Parser.Protocol;

namespace CodeContext.CSharp.Worker;

/// <summary>Where a session's notifications and diagnostics go: the connection in the worker process, a recorder in tests.</summary>
internal interface IWorkerOutput
{
    Task ProgressAsync(AnalysisProgress progress, CancellationToken ct);

    Task DeltaAsync(AnalysisDelta delta, CancellationToken ct);

    /// <summary>One line for the host's log (stderr in the worker process).</summary>
    void Diagnostic(string line);
}

/// <summary>
/// What the worker does with each request, apart from reading and writing JSON. Every request takes one
/// lock, so requests run one at a time in the order they take it: a <c>workspace/saveState</c> therefore
/// runs only between requests, and the state it writes describes what the last completed request emitted.
/// </summary>
internal sealed class WorkerSession(IWorkerOutput output, Func<string, string?, CSharpWorkspaceAnalyzer>? createAnalyzer = null)
{
    public const string ParserId = "csharp";

    public static readonly string ParserVersion = GetInformationalVersion(typeof(WorkerSession).Assembly);

    /// <summary>Upper bound on nodes/edges per analysis/delta message so a huge
    /// workspace streams in bounded frames instead of one giant payload.</summary>
    private const int MaxItemsPerDelta = 2000;

    private readonly Func<string, string?, CSharpWorkspaceAnalyzer> _createAnalyzer =
        createAnalyzer ?? ((workspaceId, rootPath) => new CSharpWorkspaceAnalyzer(workspaceId, rootPath));

    private readonly Dictionary<string, CSharpWorkspaceAnalyzer> _workspaces = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Mutating requests this process has received: a baseline is offered only with the first.</summary>
    private int _mutations;

    // Mirror of CodeContext.Core.AssemblyVersionInfo.GetInformationalVersion. The worker
    // deliberately references only Parser.Protocol + Roslyn and must not take a dependency
    // on Core (which drags in Microsoft.Extensions.Hosting/Logging), so this tiny reader is
    // duplicated here rather than shared.
    private static string GetInformationalVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>The analyzer of a workspace, for tests.</summary>
    internal CSharpWorkspaceAnalyzer? Workspace(string workspaceId) => _workspaces.GetValueOrDefault(workspaceId);

    public static InitializeResult Initialize() => new(
        ParserId: ParserId,
        ParserVersion: ParserVersion,
        DisplayName: "CSharp",
        ProtocolVersion: ParserProtocol.Version,
        Languages: ["csharp"],
        Extensions: [".cs"],
        ProjectMarkers: [".csproj", ".sln"],
        Capabilities: new WorkerCapabilities(
            WorkspaceAnalysis: true,
            IncrementalUpdates: true,
            SemanticAnalysis: true,
            NativeSyntaxTree: true,
            PersistentState: true),
        // Roslyn line/column positions are zero-based; the end position is the
        // location immediately after the declaration's last character (exclusive).
        SpanSemantics: new SpanSemantics(LineBase: 0, ColumnBase: 0, EndIsInclusive: false));

    public async Task<OpenWorkspaceResult> OpenAsync(OpenWorkspaceParams open, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_workspaces.TryGetValue(open.WorkspaceId, out var workspace))
            {
                workspace = _createAnalyzer(open.WorkspaceId, open.RootPath);
                _workspaces[open.WorkspaceId] = workspace;
            }
            // Non-destructive sync: the host re-opens before every mutation with the
            // current approved file list, which lets a freshly (re)started worker
            // rebuild its tree cache without a full reindex round-trip. It changes no
            // record of what was emitted: only a mutating request does that.
            workspace.SyncApprovedFiles(open.ApprovedFiles, ct);
        }
        finally
        {
            _lock.Release();
        }
        return new OpenWorkspaceResult(open.WorkspaceId, Opened: true);
    }

    /// <summary>
    /// A whole-workspace index. With a baseline the worker can restore, it sends only what differs from
    /// what the baseline holds (file-scoped, possibly empty); otherwise it replaces the workspace, exactly
    /// as without one.
    /// </summary>
    public async Task<IndexWorkspaceResult> IndexAsync(long requestId, IndexWorkspaceParams index, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var first = _mutations++ == 0;
            var workspace = GetWorkspace(index.WorkspaceId);
            workspace.ReplaceFiles(index.Files, ct);

            if (index.Baseline is not { } baseline)
            {
                var replaced = await PublishFullAsync(workspace, index.WorkspaceId, index.Generation, requestId, index.Files, ct);
                return new IndexWorkspaceResult(index.WorkspaceId, index.Generation, replaced, Complete: true);
            }

            var reason = first ? workspace.TryRestore(baseline) : "a baseline is taken only with the first mutating request";
            int emitted;
            if (reason is null)
            {
                var analysis = await workspace.AnalyzeAsync(Progress(index.WorkspaceId, index.Generation, requestId, ct), ct, index.Files);
                var emission = workspace.BuildIncrementalEmission(analysis);
                emitted = await PublishAsync(
                    index.WorkspaceId, index.Generation, requestId,
                    emission.Nodes, emission.EdgeFacts,
                    replacesWorkspace: false, replacesFiles: emission.ReplacesFiles,
                    analysis.Diagnostics, analysis.Coverage, analysis.Fingerprints, ct);
                workspace.CommitIncremental(emission);
            }
            else
            {
                emitted = await PublishFullAsync(workspace, index.WorkspaceId, index.Generation, requestId, index.Files, ct);
            }

            // One line per baseline, read by tests and the measurement harness: keep its form.
            output.Diagnostic(
                $"verify: restored={(reason is null ? workspace.RestoredCount : 0)} walked={workspace.LastWalkCount} " +
                $"parsed={workspace.ParseCount} compilations={workspace.CompilationCount}");
            return new IndexWorkspaceResult(
                index.WorkspaceId, index.Generation, emitted, Complete: true,
                Baseline: reason is null ? BaselineOutcomes.Accepted : BaselineOutcomes.Rejected,
                BaselineReason: reason);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApplyChangesResult> ApplyAsync(long requestId, ApplyChangesParams apply, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _mutations++;
            var workspace = GetWorkspace(apply.WorkspaceId);
            workspace.ApplyChanges(apply.Changes, ct);
            var emitted = await PublishIncrementalAsync(workspace, apply.WorkspaceId, apply.Generation, requestId, ct);
            return new ApplyChangesResult(apply.WorkspaceId, apply.Generation, emitted, Complete: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<NativeSyntaxTreeResult> SyntaxTreeAsync(NativeSyntaxTreeParams request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var workspace = GetWorkspace(request.WorkspaceId);
            var native = workspace.GetNativeSyntaxTree(
                request.FilePath, request.Start, request.Length, request.MaxDepth, ct);
            return new NativeSyntaxTreeResult(
                ParserId, ParserVersion, request.WorkspaceId, workspace.FullPath(request.FilePath),
                "roslyn-csharp-syntax-v1", native.Tree, native.Truncated);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Saves what the last completed request emitted to the path the host named. Answers <c>saved:false</c>
    /// when there is nothing to describe (no emission yet, or an analysis that did not finish emitting) or
    /// the file could not be written; a cancelled save leaves no file behind and answers nothing.
    /// </summary>
    public async Task<SaveStateResult> SaveStateAsync(SaveStateParams save, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_workspaces.TryGetValue(save.WorkspaceId, out var workspace)
                || workspace.CaptureState(save.StateId) is not { } state)
            {
                return new SaveStateResult(save.WorkspaceId, save.StateId, Saved: false);
            }
            try
            {
                var (length, sha256) = WorkerStateFile.Save(workspace.Disk, save.StatePath, state, ct);
                return new SaveStateResult(save.WorkspaceId, save.StateId, Saved: true, length, Convert.ToHexStringLower(sha256));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                output.Diagnostic($"save: state not written: {ex.Message}");
                return new SaveStateResult(save.WorkspaceId, save.StateId, Saved: false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private CSharpWorkspaceAnalyzer GetWorkspace(string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var workspace))
        {
            // Tolerate a mutation without a prior open (e.g. a host that skips the
            // re-open after restart): start from an empty workspace.
            workspace = _createAnalyzer(workspaceId, null);
            _workspaces[workspaceId] = workspace;
        }
        return workspace;
    }

    private Func<int, int, string?, ValueTask> Progress(string workspaceId, long generation, long requestId, CancellationToken ct)
        => (processed, total, currentFile) => new ValueTask(output.ProgressAsync(
            new AnalysisProgress(ParserId, ParserVersion, workspaceId, generation, requestId, processed, total, currentFile), ct));

    /// <summary>
    /// Whole-workspace (index/reset) path: walks everything, streams the complete
    /// workspace replacement as one or more analysis/delta notifications (all flagged
    /// <c>replacesWorkspace</c>), then seeds the per-file hash baseline.
    /// </summary>
    private async Task<int> PublishFullAsync(
        CSharpWorkspaceAnalyzer workspace, string workspaceId, long generation, long requestId,
        IReadOnlyList<string> approvedFiles, CancellationToken ct)
    {
        var analysis = await workspace.AnalyzeAsync(Progress(workspaceId, generation, requestId, ct), ct, approvedFiles);
        var emitted = await PublishAsync(
            workspaceId, generation, requestId,
            analysis.Nodes, analysis.Buckets.SelectMany(bucket => bucket.Edges).ToList(),
            replacesWorkspace: true, replacesFiles: [],
            analysis.Diagnostics, analysis.Coverage, analysis.Fingerprints, ct);
        // Seed only after every chunk was sent, mirroring the incremental path's
        // commit-after-send rule: a cancelled mid-index emission must not leave the
        // hash baseline claiming facts the host never received.
        workspace.SeedFactHashes(analysis);
        return emitted;
    }

    /// <summary>
    /// Incremental (applyChanges) path: walks what the changes could affect, then emits
    /// only the dirty buckets' facts scoped to the dirty ∪ removed file set (every chunk flagged <c>replacesWorkspace:false</c> and
    /// carrying the full <c>replacesFiles</c> list). The per-file hash map is committed
    /// only after every chunk has been sent, so a failed emission cannot desync it.
    /// </summary>
    private async Task<int> PublishIncrementalAsync(
        CSharpWorkspaceAnalyzer workspace, string workspaceId, long generation, long requestId, CancellationToken ct)
    {
        var analysis = workspace.Analyze(ct);
        if (!workspace.HasFactBaseline)
        {
            var replaced = await PublishAsync(
                workspaceId, generation, requestId,
                analysis.Nodes, analysis.Buckets.SelectMany(bucket => bucket.Edges).ToList(),
                replacesWorkspace: true, replacesFiles: [],
                analysis.Diagnostics, analysis.Coverage, analysis.Fingerprints, ct);
            workspace.SeedFactHashes(analysis);
            return replaced;
        }

        var emission = workspace.BuildIncrementalEmission(analysis);
        var emitted = await PublishAsync(
            workspaceId, generation, requestId,
            emission.Nodes, emission.EdgeFacts,
            replacesWorkspace: false, replacesFiles: emission.ReplacesFiles,
            analysis.Diagnostics, analysis.Coverage, analysis.Fingerprints, ct);
        workspace.CommitIncremental(emission);
        return emitted;
    }

    /// <summary>
    /// Streams a node/edge set as one or more <c>analysis/delta</c> notifications, chunked
    /// at <see cref="MaxItemsPerDelta"/>. Always emits at least one delta (the supervisor
    /// requires ≥1 per mutation), so an empty incremental batch still sends a single
    /// terminal delta with no facts. Diagnostics, coverage and fingerprints ride on the first chunk only.
    /// </summary>
    private async Task<int> PublishAsync(
        string workspaceId, long generation, long requestId,
        List<ProtocolNode> allNodes, List<CSharpWorkspaceAnalyzer.EdgeFact> allEdges,
        bool replacesWorkspace, IReadOnlyList<string> replacesFiles,
        List<ProtocolDiagnostic> diagnostics, IReadOnlyList<ProjectCoverage> coverage,
        IReadOnlyList<UnitFingerprint> fingerprints, CancellationToken ct)
    {
        var nodeChunks = Chunk(allNodes, MaxItemsPerDelta);
        var edgeChunks = Chunk(allEdges, MaxItemsPerDelta);
        var totalChunks = Math.Max(1, nodeChunks.Count + edgeChunks.Count);

        var emitted = 0;
        for (var i = 0; i < totalChunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<ProtocolNode> nodes = i < nodeChunks.Count ? nodeChunks[i] : [];
            var edgeIndex = i - nodeChunks.Count;
            IReadOnlyList<ProtocolEdge> edges = edgeIndex >= 0 && edgeIndex < edgeChunks.Count
                ? edgeChunks[edgeIndex].Select(edge => edge.ToProtocol()).ToList()
                : [];

            var delta = new AnalysisDelta(
                ParserId: ParserId,
                ParserVersion: ParserVersion,
                WorkspaceId: workspaceId,
                Generation: generation,
                RequestId: requestId,
                ReplacesWorkspace: replacesWorkspace,
                ReplacesFiles: replacesFiles,
                Nodes: nodes,
                Edges: edges,
                IsLastForRequest: i == totalChunks - 1,
                Diagnostics: i == 0 && diagnostics.Count > 0 ? diagnostics : null,
                Coverage: i == 0 ? coverage : null,
                Fingerprints: i == 0 ? fingerprints : null);

            await output.DeltaAsync(delta, ct);
            emitted++;
        }
        return emitted;
    }

    private static List<IReadOnlyList<T>> Chunk<T>(List<T> items, int size)
    {
        var chunks = new List<IReadOnlyList<T>>();
        for (var offset = 0; offset < items.Count; offset += size)
        {
            chunks.Add(items.GetRange(offset, Math.Min(size, items.Count - offset)));
        }
        return chunks;
    }
}
