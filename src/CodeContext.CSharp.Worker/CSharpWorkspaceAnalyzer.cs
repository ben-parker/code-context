using CodeContext.CSharp.Worker.Projects;
using CodeContext.Parser.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeContext.CSharp.Worker;

/// <summary>
/// Owns the Roslyn state for one logical workspace: one compilation per project, each with
/// the references, constants and usings the project declares, plus one fallback compilation
/// for files no project compiles. A workspace with no projects has only the fallback.
///
/// The whole workspace is always re-walked (cross-file binding correctness requires it),
/// but the emission is delta-native: facts are bucketed per walked file and each bucket
/// carries a deterministic content hash. A full index seeds the hash map and replaces the
/// whole workspace; an incremental apply diffs the freshly walked buckets against the
/// stored hashes and emits only the DIRTY buckets (hash changed) plus the paths REMOVED
/// since the last emission, scoping the host's replacement to exactly those files.
/// Cross-file correctness falls out for free: editing base.cs so a dependent's edge
/// resolves differently changes the dependent's bucket hash, so the dependent re-emits.
/// </summary>
public sealed class CSharpWorkspaceAnalyzer
{
    /// <summary>Prefix shared by all C# facts; a workspace component follows it.</summary>
    public const string IdPrefix = "csharp:";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Runtime metadata references shared process-wide. MetadataReference instances are
    /// immutable and safe to share across compilations and threads, so resolving them
    /// once (best-effort) avoids re-reading the assemblies on every mutation.
    /// </summary>
    private static readonly Lazy<ImmutableArray<MetadataReference>> SharedReferences =
        new(ResolveReferences);

    private static readonly Lazy<ToolchainLocations> Toolchain = new(ToolchainLocations.Detect);

    /// <summary>
    /// Assemblies by path, shared across projects and workspaces. A shared instance is what
    /// lets Roslyn reuse the symbols it read from an assembly across compilations.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Path, DateTime Stamp), MetadataReference?> AssemblyReferences = new();

    private readonly string? _rootPath;
    private readonly List<ProtocolDiagnostic> _pendingDiagnostics = [];
    private readonly string _workspaceIdPrefix;

    /// <summary>The projects as last read, or null when this workspace has no known root.</summary>
    private ProjectModel? _model;

    /// <summary>Every unit, dependencies before dependents, the fallback last.</summary>
    private List<ProjectCompilation> _units;

    private ProjectCompilation _fallback;
    private readonly Dictionary<ProjectInfo, ProjectCompilation> _unitByProject = [];

    /// <summary>The approved set the units were built for.</summary>
    private HashSet<string> _files = new(PathComparer);

    /// <summary>The unit each file's facts come from, and so the prefix of the ids it owns.</summary>
    private readonly Dictionary<string, ProjectCompilation> _owners = new(PathComparer);

    /// <summary>
    /// Per-file content hash of the facts last emitted for that file, keyed by the exact
    /// <c>tree.FilePath</c> string. This is the sole record of "what the host currently
    /// holds for each file": a full index reseeds it wholesale; each incremental apply
    /// reads it to find dirty buckets and commits the new hashes back. Its key set therefore
    /// equals the set of files whose facts are committed downstream, so a path that has an
    /// entry but no current bucket is a removal — no separate drop-tracking bookkeeping is
    /// needed.
    /// </summary>
    private readonly Dictionary<string, byte[]> _factHashes = new(PathComparer);

    /// <param name="workspaceId">Identity of the workspace, part of every fact id.</param>
    /// <param name="rootPath">
    /// The workspace root, where projects are looked for. Without one, every file goes in
    /// the fallback compilation.
    /// </param>
    public CSharpWorkspaceAnalyzer(string workspaceId, string? rootPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceIdPrefix = IdPrefix + Uri.EscapeDataString(workspaceId) + ":";
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? null : Path.GetFullPath(rootPath);
        _fallback = CreateFallback([]);
        _units = [_fallback];
    }

    /// <summary>
    /// A file in no project is usually written for the projects around it, so it binds
    /// against them and sees their global usings. Getting that wrong costs a name clash;
    /// the opposite mistake loses every collection and LINQ call in the file.
    /// </summary>
    private ProjectCompilation CreateFallback(IReadOnlyList<ProjectInfo> projects)
    {
        var usings = projects.SelectMany(project => project.GlobalUsings.Split('\n', StringSplitOptions.TrimEntries))
            .Where(line => line.StartsWith("global using", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);
        return new ProjectCompilation(
            null, _workspaceIdPrefix, ProjectCompilation.ParseOptionsFor(null), SharedReferences.Value,
            string.Join('\n', usings));
    }

    private static ImmutableArray<MetadataReference> ResolveReferences()
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        try
        {
            // Basic runtime references give the semantic model real symbols for core
            // types; when unavailable, syntax-level analysis still works.
            var objectAssembly = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(objectAssembly) && File.Exists(objectAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(objectAssembly));
                foreach (var path in FrameworkAssemblies(objectAssembly))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }
        catch
        {
            // Reference resolution is best-effort.
        }
        return references.ToImmutable();
    }

    /// <summary>
    /// Every managed framework assembly beside CoreLib, for code with no project to say
    /// otherwise. A value whose type comes from a missing assembly has an error type, so
    /// everything reached through it is silently lost: the object
    /// <c>JsonSerializer.Deserialize&lt;Item&gt;(text)</c> returns, or the parameter of a
    /// lambda passed to <c>Select</c>. Which libraries matter depends on the analyzed code,
    /// so none is chosen in advance. Taking all of them also keeps each assembly's own
    /// dependencies present: with System.Linq.Queryable but not System.Linq.Expressions,
    /// chained LINQ goes ambiguous.
    /// Names are matched because a self-contained layout also holds this worker's own
    /// dependencies, which analyzed code must not bind against, and <c>*.Native.dll</c>
    /// files share the directory without being assemblies.
    /// </summary>
    private static IEnumerable<string> FrameworkAssemblies(string coreLibrary)
        => Directory.EnumerateFiles(Path.GetDirectoryName(coreLibrary)!, "*.dll")
            .Where(path => !PathComparer.Equals(path, coreLibrary) && IsFrameworkAssembly(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal);

    private static bool IsFrameworkAssembly(string fileName)
        => !fileName.EndsWith(".Native.dll", StringComparison.OrdinalIgnoreCase)
            && (fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase));

    public int FileCount => _owners.Count;

    /// <summary>The projects as last read, for reporting. Null when there is no known root.</summary>
    public ProjectModel? Projects => _model;

    /// <summary>
    /// Reconciles the cached trees with the approved file list: files no longer approved are
    /// dropped, newly approved files are loaded from disk. Cached trees for still-approved
    /// files are kept as-is (changes arrive via <see cref="ApplyChanges"/>). This runs on
    /// every workspace/open, which the host sends before each mutation, so a restarted
    /// worker self-heals its state here. A changed project input rebuilds the units, and the
    /// fact diff then re-emits whatever now binds differently.
    /// </summary>
    public void SyncApprovedFiles(IReadOnlyList<string> approvedFiles, CancellationToken ct)
    {
        if (_model is not null && !_model.InputsChanged() && _files.SetEquals(approvedFiles)) return;
        if (_model is null && _rootPath is null && _files.SetEquals(approvedFiles)) return;
        Rebuild(approvedFiles, reuseTrees: true, ct);
    }

    /// <summary>Replaces the workspace file set entirely and reparses everything.</summary>
    public void ReplaceFiles(IReadOnlyList<string> files, CancellationToken ct)
        => Rebuild(files, reuseTrees: false, ct);

    /// <summary>
    /// Reads the projects again and rebuilds every unit, keeping a tree when its file is
    /// still compiled the same way so only what changed is parsed.
    /// </summary>
    private void Rebuild(IReadOnlyList<string> files, bool reuseTrees, CancellationToken ct)
    {
        var previous = reuseTrees ? _units : [];
        _model = _rootPath is null ? null : ProjectModel.Load(_rootPath, files, Toolchain.Value, ct);
        _files = new HashSet<string>(files, PathComparer);
        _owners.Clear();
        _unitByProject.Clear();

        var units = new List<ProjectCompilation>();
        foreach (var project in _model?.Projects ?? [])
        {
            var unit = new ProjectCompilation(
                project,
                _workspaceIdPrefix + project.Key + ":",
                ProjectCompilation.ParseOptionsFor(project),
                ReferencesFor(project));
            unit.References.AddRange(project.ProjectReferences.Select(reference => _unitByProject[reference]));
            _unitByProject[project] = unit;
            units.Add(unit);
        }

        _fallback = CreateFallback(_model?.Projects ?? []);
        _fallback.References.AddRange(units);
        units.Add(_fallback);
        _units = units;

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var compiling = UnitsCompiling(path);
            SourceText? text = null;
            foreach (var unit in compiling)
            {
                var reusable = previous.FirstOrDefault(old => old.Trees.ContainsKey(path)
                    && old.ParseOptions.Equals(unit.ParseOptions));
                if (reusable is not null)
                {
                    unit.Adopt(path, reusable.Trees[path]);
                }
                else if ((text ??= TryRead(path)) is not null)
                {
                    unit.SetText(path, text);
                }
            }
            if (compiling[0].Trees.ContainsKey(path)) _owners[path] = compiling[0];
        }
    }

    private List<ProjectCompilation> UnitsCompiling(string path)
    {
        var projects = _model?.ProjectsCompiling(path) ?? [];
        return projects.Count == 0 ? [_fallback] : projects.Select(project => _unitByProject[project]).ToList();
    }

    private ImmutableArray<MetadataReference> ReferencesFor(ProjectInfo project)
    {
        var references = ImmutableArray.CreateBuilder<MetadataReference>();
        if (project.State == ProjectCoverageState.Fallback) references.AddRange(SharedReferences.Value);
        foreach (var path in project.MetadataReferences)
        {
            if (AssemblyReference(path) is { } reference) references.Add(reference);
        }
        return references.ToImmutable();
    }

    private static MetadataReference? AssemblyReference(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        return AssemblyReferences.GetOrAdd((path, stamp), static key =>
        {
            try
            {
                return MetadataReference.CreateFromFile(key.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                return null;
            }
        });
    }

    /// <summary>Applies an ordered change batch: deleted files drop their tree, all
    /// other change kinds re-read the file from disk.</summary>
    public void ApplyChanges(IReadOnlyList<FileChangeDto> changes, CancellationToken ct)
    {
        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();
            if (change.ChangeType == FileChangeKinds.Renamed && change.OldPath is { Length: > 0 } oldPath)
            {
                // Drop the old path; the new one is loaded below. A rename whose OldPath
                // equals Path under the path comparer degenerates to a replace.
                DropFile(oldPath);
            }
            if (change.ChangeType == FileChangeKinds.Deleted)
            {
                DropFile(change.Path);
            }
            else
            {
                LoadFile(change.Path);
            }
        }
    }

    private void DropFile(string path)
    {
        _owners.Remove(path);
        _files.Remove(path);
        foreach (var unit in _units) unit.Remove(path);
    }

    private void LoadFile(string path)
    {
        if (TryRead(path) is not { } text)
        {
            // Treated as absent so its stale facts are replaced by nothing.
            DropFile(path);
            return;
        }

        var compiling = UnitsCompiling(path);
        foreach (var unit in compiling) unit.SetText(path, text);
        _owners[path] = compiling[0];
        _files.Add(path);
    }

    /// <summary>The file's text, or null with a diagnostic telling the host why not.</summary>
    private SourceText? TryRead(string path)
    {
        try
        {
            return SourceText.From(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _pendingDiagnostics.Add(new ProtocolDiagnostic(
                path, "warning", $"File could not be read and was skipped: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// Compiles the current tree set and walks every file into normalized nodes/edges.
    /// The result always represents the complete workspace.
    /// </summary>
    public AnalysisResult Analyze(CancellationToken ct)
        => AnalyzeAsync(progress: null, ct).GetAwaiter().GetResult();

    public async Task<AnalysisResult> AnalyzeAsync(
        Func<int, int, string?, ValueTask>? progress,
        CancellationToken ct,
        IReadOnlyList<string>? progressFiles = null)
    {
        foreach (var unit in _units) unit.RefreshReferences();
        foreach (var unit in _units) unit.ClearChanged();

        // Walk each tree into its own bucket, then concatenate the buckets to build the
        // flat whole-workspace lists, so each file's nodes/edges stay contiguous in walk order.
        var rawBuckets = new List<FileFacts>(_owners.Count);
        var walked = new List<ExternalSymbols>(_owners.Count);
        var total = progressFiles?.Count ?? _owners.Count;
        var processed = 0;
        if (total == 0 && progress is not null)
        {
            await progress(0, 0, null);
        }
        if (progressFiles is null)
        {
            foreach (var path in _owners.Keys)
            {
                rawBuckets.Add(AnalyzeFile(path));
            }
        }
        else
        {
            // Progress is defined over the approved request, including files that
            // disappeared or could not be read. Those paths produce diagnostics and
            // no facts, but still count as completed analysis attempts.
            foreach (var path in progressFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (_owners.ContainsKey(path))
                {
                    rawBuckets.Add(AnalyzeFile(path));
                }
                processed++;
                if (progress is not null && ShouldReportProgress(processed, total))
                {
                    await progress(processed, total, path);
                }
            }
        }

        FileFacts AnalyzeFile(string path)
        {
            ct.ThrowIfCancellationRequested();
            var unit = _owners[path];
            var tree = unit.Trees[path];
            var semanticModel = unit.Compilation.GetSemanticModel(tree);
            var bucketNodes = new List<ProtocolNode>();
            var bucketEdges = new List<ProtocolEdge>();
            var externals = new ExternalSymbols(_workspaceIdPrefix);
            var walker = new GraphWalker(
                semanticModel, tree.FilePath, unit.IdPrefix, _owners, externals, bucketNodes, bucketEdges);
            walker.Visit(tree.GetRoot(ct));
            walked.Add(externals);
            return new FileFacts(tree.FilePath, bucketNodes, bucketEdges);
        }

        rawBuckets.AddRange(ExternalSymbols.ToBuckets(walked));

        // Duplicate-id canonicalization. A node or edge id can be emitted by more than
        // one file (partial types split across files; base lists repeated on partial
        // declarations). The winner must be a pure function of the file SET, never of
        // edit order: incremental emission re-sends only dirty buckets, so a winner that
        // depended on which file was analyzed or edited last would make the incremental
        // graph diverge from a fresh scan of the identical tree. Rule: the bucket with
        // the ordinally smallest FilePath owns the id; within one bucket the last
        // occurrence wins (matching the old per-file flat-walk behavior). Buckets are
        // recomputed from the full walk on every analysis, so deleting the canonical
        // file moves the id into the ordinal-next survivor's bucket, flipping that
        // bucket's hash and re-emitting it — convergence needs no extra bookkeeping.
        var nodeOwner = ComputeOwners(rawBuckets, static b => b.Nodes, static n => n.Id);
        var edgeOwner = ComputeOwners(rawBuckets, static b => b.Edges, static e => e.Id);

        // A bucket must hold exactly what the host replaces as one unit, and the host
        // removes an edge along with its source node. So an edge belongs to the file that
        // owns its source node, not the file whose walk found it: `partial class C` in two
        // files has one node in one file, yet both walks emit edges from it. Bucketing
        // those under the file that found them would let an edit of the node's file lose
        // the other file's edges, and an edit of the other file leave its old edges behind.
        // With this rule any change to such an edge flips the owning file's hash, so both
        // directions re-emit through the ordinary dirty diff.
        var edgesByFile = new Dictionary<string, List<ProtocolEdge>>(StringComparer.Ordinal);
        foreach (var raw in rawBuckets)
        {
            foreach (var edge in FilterOwned(raw.Edges, static e => e.Id, edgeOwner, raw.FilePath))
            {
                var file = nodeOwner.GetValueOrDefault(edge.SourceId, raw.FilePath);
                if (!edgesByFile.TryGetValue(file, out var fileEdges))
                {
                    edgesByFile[file] = fileEdges = [];
                }
                fileEdges.Add(edge);
            }
        }

        var buckets = new List<FileFacts>(rawBuckets.Count);
        var nodes = new List<ProtocolNode>();
        var edges = new List<ProtocolEdge>();
        var contested = ContestedIdentifiers(rawBuckets);
        foreach (var raw in rawBuckets)
        {
            var keptNodes = Qualify(
                FilterOwned(raw.Nodes, static n => n.Id, nodeOwner, raw.FilePath), contested);
            var keptEdges = edgesByFile.GetValueOrDefault(raw.FilePath) ?? [];
            nodes.AddRange(keptNodes);
            edges.AddRange(keptEdges);
            buckets.Add(new FileFacts(raw.FilePath, keptNodes, keptEdges));
        }

        var diagnostics = _pendingDiagnostics.ToList();
        _pendingDiagnostics.Clear();
        return new AnalysisResult(nodes, edges, diagnostics, buckets);
    }

    /// <summary>The public identifiers more than one symbol would carry.</summary>
    private static HashSet<string> ContestedIdentifiers(List<FileFacts> buckets)
    {
        var holders = new Dictionary<string, string>(StringComparer.Ordinal);
        var contested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in buckets.SelectMany(bucket => bucket.Nodes))
        {
            if (!holders.TryAdd(node.Identifier, node.Id) && holders[node.Identifier] != node.Id)
            {
                contested.Add(node.Identifier);
            }
        }
        return contested;
    }

    /// <summary>
    /// Gives each symbol sharing an identifier its project's name. All of them change
    /// together, so a bare identifier never quietly means one of several.
    /// </summary>
    private List<ProtocolNode> Qualify(List<ProtocolNode> nodes, HashSet<string> contested)
    {
        if (contested.Count == 0 || !nodes.Exists(node => contested.Contains(node.Identifier))) return nodes;

        return nodes.ConvertAll(node => contested.Contains(node.Identifier)
            ? node with { Identifier = IdPrefix + QualifierOf(node) + "#" + node.Identifier[IdPrefix.Length..] }
            : node);
    }

    private string QualifierOf(ProtocolNode node)
        => _owners.TryGetValue(node.FilePath!, out var owner)
            ? owner.Qualifier
            : node.Metadata![ExternalSymbols.AssemblyKey];

    /// <summary>
    /// The symbol's semantic identity. A static constructor displays exactly like a
    /// parameterless instance constructor, so it carries a marker or the two would
    /// collapse into a single node.
    /// </summary>
    internal static string DisplayNameOf(ISymbol symbol)
        => symbol is IMethodSymbol { MethodKind: MethodKind.StaticConstructor }
            ? symbol.ToDisplayString() + "[static]"
            : symbol.ToDisplayString();

    private static bool ShouldReportProgress(int processed, int total)
    {
        if (processed == total) return true;
        var interval = Math.Max(1, (total + 99) / 100);
        return processed % interval == 0;
    }

    /// <summary>Maps each fact id to the ordinally smallest FilePath that emits it.</summary>
    private static Dictionary<string, string> ComputeOwners<T>(
        List<FileFacts> buckets,
        Func<FileFacts, List<T>> facts,
        Func<T, string> id)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            foreach (var fact in facts(bucket))
            {
                var key = id(fact);
                if (!owner.TryGetValue(key, out var current)
                    || string.CompareOrdinal(bucket.FilePath, current) < 0)
                {
                    owner[key] = bucket.FilePath;
                }
            }
        }
        return owner;
    }

    /// <summary>
    /// Keeps only the facts this bucket canonically owns; among within-bucket duplicates
    /// of an id, keeps the last occurrence. Returns the original list when nothing was
    /// dropped (the overwhelmingly common no-duplicates case).
    /// </summary>
    private static List<T> FilterOwned<T>(
        List<T> items,
        Func<T, string> id,
        Dictionary<string, string> owner,
        string filePath)
    {
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            lastIndex[id(items[i])] = i;
        }

        var kept = new List<T>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var key = id(items[i]);
            if (lastIndex[key] == i && owner[key] == filePath)
            {
                kept.Add(items[i]);
            }
        }

        return kept.Count == items.Count ? items : kept;
    }

    /// <summary>The facts the host holds under one file: the nodes it owns and the edges
    /// sourced at those nodes, wherever they were found. <see cref="FilePath"/> is the
    /// verbatim <c>tree.FilePath</c> string; the bucket's node <c>FilePath</c>s and the
    /// <c>ReplacesFiles</c> entries derived from it are all this same string.</summary>
    public sealed record FileFacts(
        string FilePath,
        List<ProtocolNode> Nodes,
        List<ProtocolEdge> Edges);

    public sealed record AnalysisResult(
        List<ProtocolNode> Nodes,
        List<ProtocolEdge> Edges,
        List<ProtocolDiagnostic> Diagnostics,
        IReadOnlyList<FileFacts> Buckets)
    {
        public IReadOnlySet<string> NodeIds { get; } = Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The scoped emission for one incremental apply: the facts of the DIRTY buckets only
    /// (concatenated in bucket order) plus the full <see cref="ReplacesFiles"/> list — the
    /// dirty files unioned with the files removed since the last emission — carried
    /// verbatim. <see cref="_pendingHashes"/>/<see cref="_removedPaths"/> are the commit
    /// payload applied by <see cref="CommitIncremental"/> once the host has accepted every
    /// chunk.
    /// </summary>
    public sealed class IncrementalEmission
    {
        internal IncrementalEmission(
            List<ProtocolNode> nodes,
            List<ProtocolEdge> edges,
            List<string> replacesFiles,
            Dictionary<string, byte[]> pendingHashes,
            List<string> removedPaths)
        {
            Nodes = nodes;
            Edges = edges;
            ReplacesFiles = replacesFiles;
            _pendingHashes = pendingHashes;
            _removedPaths = removedPaths;
        }

        public List<ProtocolNode> Nodes { get; }
        public List<ProtocolEdge> Edges { get; }

        /// <summary>Dirty ∪ removed, verbatim paths; repeated on every emitted chunk.</summary>
        public List<string> ReplacesFiles { get; }

        internal Dictionary<string, byte[]> PendingHashes => _pendingHashes;
        internal List<string> RemovedPaths => _removedPaths;

        private readonly Dictionary<string, byte[]> _pendingHashes;
        private readonly List<string> _removedPaths;
    }

    /// <summary>
    /// Seeds the fact-hash map from a full analysis: every walked file's bucket hash
    /// becomes the baseline the next incremental apply diffs against. Called on the
    /// whole-workspace (index/reset) path, which replaces the whole workspace downstream,
    /// so the previous map is discarded wholesale.
    /// </summary>
    public void SeedFactHashes(AnalysisResult analysis)
    {
        _factHashes.Clear();
        foreach (var bucket in analysis.Buckets)
        {
            _factHashes[bucket.FilePath] = ComputeBucketHash(bucket, analysis.NodeIds);
        }
    }

    /// <summary>
    /// Diffs a full analysis against the stored hash map and produces the scoped
    /// incremental emission. Dirty = buckets whose hash differs from (or is absent in) the
    /// stored map; removed = stored keys with no current bucket (deleted files, files
    /// dropped by SyncApprovedFiles, rename-old-paths, unreadable files). A path dropped
    /// then re-added before this call still has a bucket, so it is never removed — it is
    /// dirty only if its facts actually changed (identical re-added content hashes the
    /// same and is correctly absent from the emission).
    /// The hash map is NOT mutated here: call <see cref="CommitIncremental"/> after the
    /// host has accepted every chunk so a failed emission does not desync the map.
    /// </summary>
    public IncrementalEmission BuildIncrementalEmission(AnalysisResult analysis)
    {
        var nodes = new List<ProtocolNode>();
        var edges = new List<ProtocolEdge>();
        var replacesFiles = new List<string>();
        var pendingHashes = new Dictionary<string, byte[]>(PathComparer);
        var currentPaths = new HashSet<string>(PathComparer);

        foreach (var bucket in analysis.Buckets)
        {
            currentPaths.Add(bucket.FilePath);
            var hash = ComputeBucketHash(bucket, analysis.NodeIds);
            pendingHashes[bucket.FilePath] = hash;
            if (!_factHashes.TryGetValue(bucket.FilePath, out var stored)
                || !stored.AsSpan().SequenceEqual(hash))
            {
                nodes.AddRange(bucket.Nodes);
                edges.AddRange(bucket.Edges);
                replacesFiles.Add(bucket.FilePath);
            }
        }

        var removedPaths = _factHashes.Keys.Where(path => !currentPaths.Contains(path)).ToList();
        replacesFiles.AddRange(removedPaths);

        return new IncrementalEmission(nodes, edges, replacesFiles, pendingHashes, removedPaths);
    }

    /// <summary>
    /// Commits an emission's hash changes: stores the (added/changed) buckets' new hashes
    /// and deletes the removed files' entries, so the map again mirrors exactly the facts
    /// the host now holds. Call only after every chunk has been accepted.
    /// </summary>
    public void CommitIncremental(IncrementalEmission emission)
    {
        foreach (var (path, hash) in emission.PendingHashes)
        {
            _factHashes[path] = hash;
        }
        foreach (var path in emission.RemovedPaths)
        {
            _factHashes.Remove(path);
        }
    }

    /// <summary>
    /// Deterministic content hash of one file's facts, stable across processes. Nodes are
    /// sorted by Id and edges by Id first (so the hash is independent of walk order), then
    /// every emission-affecting field is fed to SHA-256 in a fixed order with explicit
    /// length/null framing, so no two distinct fact sets can collide by field-boundary
    /// ambiguity. Whether each edge's target has a node counts as well: the host drops an
    /// edge when its target node goes, yet the walk can keep emitting the same edge, as it
    /// does for an unresolved base type in the global namespace. Re-emitting it is what
    /// keeps it, and what brings it back if the host dropped it.
    /// </summary>
    private static byte[] ComputeBucketHash(FileFacts bucket, IReadOnlySet<string> nodeIds)
    {
        using var hasher = new FactHasher();
        hasher.Int(bucket.Nodes.Count);
        foreach (var node in bucket.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            hasher.Str(node.Id);
            hasher.Str(node.Identifier);
            hasher.Str(node.Name);
            hasher.Str(node.Kind);
            hasher.Str(node.Language);
            hasher.Str(node.FilePath);
            hasher.Int(node.StartLine);
            hasher.Int(node.EndLine);
            hasher.Int(node.StartColumn);
            hasher.Int(node.EndColumn);
            hasher.Str(node.Namespace);
            hasher.Str(node.Visibility);
            hasher.Str(node.Signature);
            hasher.Str(node.ReturnType);
            hasher.Str(node.Parameters);
            hasher.Str(node.Modifiers);
            hasher.Metadata(node.Metadata);
        }
        hasher.Int(bucket.Edges.Count);
        foreach (var edge in bucket.Edges.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            hasher.Str(edge.Id);
            hasher.Str(edge.SourceId);
            hasher.Str(edge.TargetId);
            hasher.Str(edge.Kind);
            hasher.Metadata(edge.Metadata);
            hasher.Int(nodeIds.Contains(edge.TargetId) ? 1 : 0);
        }
        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// Thin length-and-null-framed writer over <see cref="IncrementalHash"/>. Framing
    /// (a null/present marker plus a byte-length prefix on every string, a count prefix on
    /// every collection) makes the byte stream unambiguous, so two different fact sets can
    /// never hash to the same digest by concatenation coincidence.
    /// </summary>
    private sealed class FactHasher : IDisposable
    {
        private static readonly byte[] NullMarker = [0];
        private static readonly byte[] PresentMarker = [1];

        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly byte[] _scratch = new byte[4];

        public void Int(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_scratch, value);
            _hash.AppendData(_scratch);
        }

        public void Str(string? value)
        {
            if (value is null)
            {
                _hash.AppendData(NullMarker);
                return;
            }
            _hash.AppendData(PresentMarker);
            var count = Encoding.UTF8.GetByteCount(value);
            Int(count);
            var buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                var written = Encoding.UTF8.GetBytes(value, buffer);
                _hash.AppendData(buffer.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Metadata(IReadOnlyDictionary<string, string>? metadata)
        {
            if (metadata is null)
            {
                _hash.AppendData(NullMarker);
                return;
            }
            _hash.AppendData(PresentMarker);
            Int(metadata.Count);
            foreach (var pair in metadata.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                Str(pair.Key);
                Str(pair.Value);
            }
        }

        public byte[] GetHashAndReset() => _hash.GetHashAndReset();

        public void Dispose() => _hash.Dispose();
    }

    public sealed record NativeTreeResult(JsonElement Tree, bool Truncated);

    /// <summary>Builds a bounded Roslyn-native node/token tree for one cached file.</summary>
    public NativeTreeResult GetNativeSyntaxTree(
        string filePath, int? start, int? length, int maxDepth, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!_owners.TryGetValue(fullPath, out var owner))
        {
            throw new ArgumentException("The file is not open in this workspace.", nameof(filePath));
        }
        var syntaxTree = owner.Trees[fullPath];
        if ((start is null) != (length is null))
        {
            throw new ArgumentException("start and length must be supplied together.");
        }
        if (maxDepth is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "maxDepth must be between 0 and 32.");
        }

        var root = syntaxTree.GetRoot(ct);
        SyntaxNode selected = root;
        if (start is { } offset && length is { } count)
        {
            if (offset < 0 || count < 0 || (long)offset + count > root.FullSpan.End)
            {
                throw new ArgumentOutOfRangeException(nameof(start), "The requested range is outside the file.");
            }
            selected = root.FindNode(new TextSpan(offset, count), getInnermostNodeForTie: true);
        }

        var state = new NativeTreeBuildState(maxDepth, maxNodes: 10_000);
        var json = SerializeNode(selected, depth: 0, state, ct);
        return new NativeTreeResult(
            JsonSerializer.SerializeToElement(json, DeepTreeJsonOptions), state.Truncated);
    }

    // Every tree level is a node object plus its children array, so a tree cut at the
    // allowed maximum of 32 is already past System.Text.Json's default depth limit of 64.
    private static readonly JsonSerializerOptions DeepTreeJsonOptions =
        new() { MaxDepth = ParserProtocol.MaxJsonDepth };

    private sealed class NativeTreeBuildState(int maxDepth, int maxNodes)
    {
        public int MaxDepth { get; } = maxDepth;
        public int MaxNodes { get; } = maxNodes;
        public int NodesWritten { get; set; }
        public bool Truncated { get; set; }
    }

    private static JsonObject SerializeNode(
        SyntaxNode node, int depth, NativeTreeBuildState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        state.NodesWritten++;
        var result = new JsonObject
        {
            ["kind"] = node.Kind().ToString(),
            ["rawKind"] = node.RawKind,
            ["span"] = SpanJson(node.Span),
            ["fullSpan"] = SpanJson(node.FullSpan),
        };

        var children = node.ChildNodesAndTokens();
        if (children.Count == 0) return result;
        if (depth >= state.MaxDepth || state.NodesWritten >= state.MaxNodes)
        {
            state.Truncated = true;
            result["childrenTruncated"] = true;
            return result;
        }

        var childArray = new JsonArray();
        foreach (var child in children)
        {
            if (state.NodesWritten >= state.MaxNodes)
            {
                state.Truncated = true;
                result["childrenTruncated"] = true;
                break;
            }
            childArray.Add(child.IsNode
                ? SerializeNode(child.AsNode()!, depth + 1, state, ct)
                : SerializeToken(child.AsToken(), state));
        }
        result["children"] = childArray;
        return result;
    }

    private static JsonObject SerializeToken(SyntaxToken token, NativeTreeBuildState state)
    {
        state.NodesWritten++;
        const int maxTokenTextLength = 4096;
        var textTruncated = token.Text.Length > maxTokenTextLength
            || token.ValueText.Length > maxTokenTextLength;
        var result = new JsonObject
        {
            ["kind"] = token.Kind().ToString(),
            ["rawKind"] = token.RawKind,
            ["span"] = SpanJson(token.Span),
            ["fullSpan"] = SpanJson(token.FullSpan),
            ["text"] = token.Text.Length > maxTokenTextLength
                ? token.Text[..maxTokenTextLength]
                : token.Text,
            ["valueText"] = token.ValueText.Length > maxTokenTextLength
                ? token.ValueText[..maxTokenTextLength]
                : token.ValueText,
            ["isMissing"] = token.IsMissing,
        };
        if (textTruncated) result["textTruncated"] = true;
        return result;
    }

    private static JsonObject SpanJson(TextSpan span) => new()
    {
        ["start"] = span.Start,
        ["length"] = span.Length,
        ["end"] = span.End,
    };

    private sealed class GraphWalker : CSharpSyntaxWalker
    {
        private readonly SemanticModel _semanticModel;
        private readonly string _filePath;
        private readonly string _idPrefix;
        private readonly IReadOnlyDictionary<string, ProjectCompilation> _owners;
        private readonly ExternalSymbols _externals;
        private readonly List<ProtocolNode> _nodes;
        private readonly List<ProtocolEdge> _edges;
        private bool _hasTopLevelEntry;

        public GraphWalker(
            SemanticModel semanticModel, string filePath, string idPrefix,
            IReadOnlyDictionary<string, ProjectCompilation> owners,
            ExternalSymbols externals,
            List<ProtocolNode> nodes, List<ProtocolEdge> edges)
        {
            _semanticModel = semanticModel;
            _filePath = filePath;
            _idPrefix = idPrefix;
            _owners = owners;
            _externals = externals;
            _nodes = nodes;
            _edges = edges;
        }

        /// <summary>
        /// A symbol's id belongs to the project owning its declaring file, whichever
        /// compilation bound it, so a file compiled into two projects stays one node. A
        /// library symbol gets a stub, recorded because this file used it. One that failed
        /// to bind stays in this project: it names nothing elsewhere.
        /// </summary>
        private string NodeId(ISymbol symbol)
        {
            foreach (var location in symbol.Locations)
            {
                if (location.SourceTree is { } tree)
                {
                    return (_owners.TryGetValue(tree.FilePath, out var owner) ? owner.IdPrefix : _idPrefix) + DisplayName(symbol);
                }
            }
            return ExternalSymbols.IsExternal(symbol) ? _externals.Add(symbol) : _idPrefix + DisplayName(symbol);
        }

        private static string PublicIdentifier(ISymbol symbol)
            => "csharp:" + DisplayName(symbol);

        private static string DisplayName(ISymbol symbol) => DisplayNameOf(symbol);

        /// <summary>
        /// Identity of the file's synthetic top-level entry point. Each project's
        /// <c>Program.cs</c> compiles to the same <c>Program.&lt;Main&gt;$</c> symbol, so
        /// the file path is what keeps two tools in one workspace apart.
        /// </summary>
        private string TopLevelIdentity => $"<Main>@{_filePath.Replace('\\', '/')}";

        private string TopLevelNodeId => _idPrefix + TopLevelIdentity;

        private ProtocolNode BuildNode(
            ISymbol symbol,
            SyntaxNode syntax,
            string kind,
            string signature,
            IReadOnlyDictionary<string, string>? metadata = null,
            string? name = null)
        {
            var span = syntax.GetLocation().GetLineSpan().Span;
            return new ProtocolNode(
                Id: NodeId(symbol),
                Identifier: PublicIdentifier(symbol),
                Name: name ?? symbol.Name,
                Kind: kind,
                Language: "csharp",
                FilePath: _filePath,
                StartLine: span.Start.Line,
                EndLine: span.End.Line,
                StartColumn: span.Start.Character,
                EndColumn: span.End.Character,
                Namespace: symbol.ContainingNamespace.ToDisplayString(),
                Visibility: symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                Signature: signature,
                Metadata: metadata);
        }

        private void AddEdge(string sourceId, string targetId, string kind, IReadOnlyDictionary<string, string>? metadata = null)
        {
            _edges.Add(new ProtocolEdge(
                Id: $"{sourceId}=[{kind}]=>{targetId}" + (metadata is not null && metadata.TryGetValue("line", out var line)
                    ? $"@{line}:{metadata.GetValueOrDefault("column", "0")}"
                    : string.Empty),
                SourceId: sourceId,
                TargetId: targetId,
                Kind: kind,
                Metadata: metadata));
        }

        /// <summary>
        /// Emits a containment edge (HAS_METHOD / HAS_PROPERTY / HAS_FIELD) from the
        /// member's directly containing type to the member node, mirroring the
        /// TypeScript worker's convention. The edge is only emitted when the containing
        /// type itself produces a node (a type declared in source), so we never point at
        /// a node that was never created. <c>ContainingType</c> is the direct declaring
        /// type, so nested members attach to their own type only.
        /// </summary>
        private void AddContainmentEdge(ISymbol member, string kind)
        {
            if (member.ContainingType is { } containingType && IsIndexedSourceType(containingType))
            {
                AddEdge(NodeId(containingType), NodeId(member), kind);
            }
        }

        public override void VisitCompilationUnit(CompilationUnitSyntax node)
        {
            // Text that is not C# at all parses as a run of broken global statements, so
            // only a statement that parsed cleanly shows the file really has an entry point.
            var statements = node.Members.OfType<GlobalStatementSyntax>().ToList();
            _hasTopLevelEntry = statements.Any(statement => !statement.ContainsDiagnostics);
            if (_hasTopLevelEntry)
            {
                var start = statements[0].GetLocation().GetLineSpan().StartLinePosition;
                var end = statements[^1].GetLocation().GetLineSpan().EndLinePosition;
                _nodes.Add(new ProtocolNode(
                    Id: TopLevelNodeId,
                    Identifier: "csharp:" + TopLevelIdentity,
                    Name: "<Main>",
                    Kind: "Method",
                    Language: "csharp",
                    FilePath: _filePath,
                    StartLine: start.Line,
                    EndLine: end.Line,
                    StartColumn: start.Character,
                    EndColumn: end.Character,
                    Namespace: string.Empty,
                    Visibility: "internal",
                    Signature: "<Main>"));
            }
            base.VisitCompilationUnit(node);
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            AddTypeNode(node, "Class", parameters: null);
            base.VisitClassDeclaration(node);
        }

        public override void VisitExtensionBlockDeclaration(ExtensionBlockDeclarationSyntax node)
        {
            AddTypeNode(node, "Extension", node.ParameterList, name: "extension");
            base.VisitExtensionBlockDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            AddTypeNode(node, "Struct", parameters: null);
            base.VisitStructDeclaration(node);
        }

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            var kind = node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "Struct" : "Record";
            if (AddTypeNode(node, kind, node.ParameterList) is { } symbol && node.ParameterList is { } parameters)
            {
                foreach (var parameter in parameters.Parameters)
                {
                    AddPositionalProperty(symbol, parameter);
                }
            }
            base.VisitRecordDeclaration(node);
        }

        /// <summary>
        /// Emits the property a positional record parameter compiles to. A parameter the
        /// record redeclares as an ordinary property is left to
        /// <see cref="VisitPropertyDeclaration"/>, which owns that declaration.
        /// </summary>
        private void AddPositionalProperty(INamedTypeSymbol record, ParameterSyntax parameter)
        {
            var property = record.GetMembers(parameter.Identifier.ValueText)
                .OfType<IPropertySymbol>()
                .FirstOrDefault(candidate => candidate.DeclaringSyntaxReferences
                    .Any(reference => reference.GetSyntax() == parameter));
            if (property is null) return;

            _nodes.Add(BuildNode(property, parameter, "Property", parameter.Identifier.ToString()));
            AddContainmentEdge(property, "HAS_PROPERTY");
        }

        /// <summary>Adds the node and base/interface edges for a class, struct or record.</summary>
        private INamedTypeSymbol? AddTypeNode(
            TypeDeclarationSyntax node, string kind, ParameterListSyntax? parameters, string? name = null)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is not { } symbol) return null;

            var signature = (name ?? node.Identifier.ToString())
                + (node.TypeParameterList?.ToString() ?? "")
                + (parameters?.ToString() ?? "")
                + (node.BaseList?.ToString() ?? "");
            _nodes.Add(BuildNode(symbol, node, kind, signature, name: name));

            if (symbol.BaseType is { SpecialType: not (SpecialType.System_Object or SpecialType.System_ValueType) } baseType)
            {
                AddEdge(NodeId(symbol), NodeId(baseType.OriginalDefinition), "INHERITS");
            }
            foreach (var interfaceSymbol in symbol.AllInterfaces)
            {
                AddEdge(NodeId(symbol), NodeId(interfaceSymbol.OriginalDefinition), "IMPLEMENTS");
            }
            return symbol;
        }

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Enum", node.Identifier.ToString()));
            }
            base.VisitEnumDeclaration(node);
        }

        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "EnumMember", node.Identifier.ToString()));
                AddContainmentEdge(symbol, "HAS_FIELD");
            }
            base.VisitEnumMemberDeclaration(node);
        }

        public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + node.ParameterList.ToString();
                _nodes.Add(BuildNode(symbol, node, "Delegate", signature));
            }
            base.VisitDelegateDeclaration(node);
        }

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + (node.BaseList?.ToString() ?? "");
                _nodes.Add(BuildNode(symbol, node, "Interface", signature));

                foreach (var baseInterface in symbol.Interfaces)
                {
                    AddEdge(NodeId(symbol), NodeId(baseInterface.OriginalDefinition), "INHERITS");
                }
            }
            base.VisitInterfaceDeclaration(node);
        }

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                var signature = node.Identifier.ToString()
                    + (node.TypeParameterList?.ToString() ?? "")
                    + node.ParameterList.ToString();
                var metadata = IsTestMethod(node)
                    ? new Dictionary<string, string> { ["isTest"] = "true" }
                    : null;
                _nodes.Add(BuildNode(symbol, node, "Method", signature, metadata));
                AddContainmentEdge(symbol, "HAS_METHOD");

                if (symbol.OverriddenMethod is { } overridden)
                    AddEdge(NodeId(symbol), NodeId(NormalizeMethod(overridden)), "OVERRIDES_MEMBER");

                var implementedMembers = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
                foreach (var implemented in symbol.ExplicitInterfaceImplementations)
                    implementedMembers.Add(NormalizeMethod(implemented));

                if (symbol.ContainingType is { } containingType)
                {
                    foreach (var @interface in containingType.AllInterfaces)
                    {
                        foreach (var member in @interface.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (containingType.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation
                                && SymbolEqualityComparer.Default.Equals(
                                    NormalizeMethod(implementation), NormalizeMethod(symbol)))
                            {
                                implementedMembers.Add(NormalizeMethod(member));
                            }
                        }
                    }
                }

                foreach (var implemented in implementedMembers)
                    AddEdge(NodeId(symbol), NodeId(implemented), "IMPLEMENTS_MEMBER");
            }
            base.VisitMethodDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Property", node.Identifier.ToString()));
                AddContainmentEdge(symbol, "HAS_PROPERTY");
            }
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Property", "this" + node.ParameterList));
                AddContainmentEdge(symbol, "HAS_PROPERTY");
            }
            base.VisitIndexerDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            AddVariableNodes(node, "Field");
            base.VisitFieldDeclaration(node);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            AddVariableNodes(node, "Event");
            base.VisitEventFieldDeclaration(node);
        }

        private void AddVariableNodes(BaseFieldDeclarationSyntax node, string kind)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                if (_semanticModel.GetDeclaredSymbol(variable) is { } symbol)
                {
                    _nodes.Add(BuildNode(symbol, variable, kind, variable.Identifier.ToString()));
                    AddContainmentEdge(symbol, "HAS_FIELD");
                }
            }
        }

        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Event", node.Identifier.ToString()));
                AddContainmentEdge(symbol, "HAS_FIELD");
            }
            base.VisitEventDeclaration(node);
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            // Named like the TypeScript worker's constructors, not after the type: an
            // exact query for the type must keep resolving to the type alone.
            AddMethodLikeNode(node, "constructor", node.Identifier + node.ParameterList.ToString());
            base.VisitConstructorDeclaration(node);
        }

        public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
        {
            AddMethodLikeNode(node, "destructor", "~" + node.Identifier + node.ParameterList);
            base.VisitDestructorDeclaration(node);
        }

        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            var name = $"operator {node.OperatorToken}";
            AddMethodLikeNode(node, name, name + node.ParameterList);
            base.VisitOperatorDeclaration(node);
        }

        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            var name = $"{node.ImplicitOrExplicitKeyword} operator {node.Type}";
            AddMethodLikeNode(node, name, name + node.ParameterList);
            base.VisitConversionOperatorDeclaration(node);
        }

        private void AddMethodLikeNode(BaseMethodDeclarationSyntax node, string name, string signature)
        {
            if (_semanticModel.GetDeclaredSymbol(node) is { } symbol)
            {
                _nodes.Add(BuildNode(symbol, node, "Method", signature, name: name));
                AddContainmentEdge(symbol, "HAS_METHOD");
            }
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (ResolveMethodSymbol(node) is { } calleeSymbol && FindEnclosingNodeId(node) is { } callerId)
            {
                var enclosingMethod = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                var kind = enclosingMethod is not null && IsMockOrFluentTestInvocation(node, enclosingMethod)
                    ? "MOCK_CALLS"
                    : "CALLS";
                AddLocatedEdge(callerId, NodeId(calleeSymbol), kind, node);
            }
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddConstructorCall(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            AddConstructorCall(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
        {
            AddConstructorCall(node);
            base.VisitConstructorInitializer(node);
        }

        public override void VisitPrimaryConstructorBaseType(PrimaryConstructorBaseTypeSyntax node)
        {
            AddConstructorCall(node);
            base.VisitPrimaryConstructorBaseType(node);
        }

        /// <summary>
        /// Records construction as a call, like the TypeScript worker: to the declared
        /// constructor when there is one, otherwise to the type itself.
        /// </summary>
        private void AddConstructorCall(SyntaxNode node)
        {
            if (FindEnclosingNodeId(node) is not { } sourceId) return;

            // A failed expression, as an ambiguous type name makes it, chooses no overload
            // even when the name settles on one type. That type is still being constructed.
            var definition = (ResolveSymbol(node) as IMethodSymbol)?.OriginalDefinition;
            var type = definition?.ContainingType ?? NamedType(node);
            if (type is null) return;
            definition ??= OnlyConstructorTaking(type, node);

            if (definition is not null && IsIndexedSourceMember(definition))
            {
                AddLocatedEdge(sourceId, NodeId(definition), "CALLS", node);
                // `new(...)` names no type, so no identifier will reference it.
                if (node is ImplicitObjectCreationExpressionSyntax)
                    AddLocatedEdge(sourceId, NodeId(type), "REFERENCES", node);
            }
            else if ((IsIndexedSourceType(type) || ExternalSymbols.IsExternal(type)) && sourceId != NodeId(type))
            {
                AddLocatedEdge(sourceId, NodeId(type), "CALLS", node);
            }
        }

        private INamedTypeSymbol? NamedType(SyntaxNode node)
            => node is ObjectCreationExpressionSyntax { Type: var name } && ResolveSymbol(name) is INamedTypeSymbol type
                ? type.OriginalDefinition
                : null;

        private static IMethodSymbol? OnlyConstructorTaking(INamedTypeSymbol type, SyntaxNode creation)
        {
            var arguments = (creation as BaseObjectCreationExpressionSyntax)?.ArgumentList?.Arguments.Count ?? 0;
            var matching = type.InstanceConstructors.Where(constructor => constructor.Parameters.Length == arguments).ToList();
            return matching.Count == 1 ? matching[0] : null;
        }

        private IMethodSymbol? ResolveMethodSymbol(InvocationExpressionSyntax invocation)
        {
            var info = _semanticModel.GetSymbolInfo(invocation);
            var method = info.Symbol as IMethodSymbol;
            if (method is null)
            {
                // Roslyn reports candidate symbols for some fluent/mock expressions.
                // Only accept an unambiguous candidate: guessing among overloads would
                // trade a visible unresolved call for a silently wrong edge.
                var candidates = new List<IMethodSymbol>();
                foreach (var candidate in info.CandidateSymbols.OfType<IMethodSymbol>().Select(NormalizeMethod))
                {
                    if (!candidates.Any(existing => SymbolEqualityComparer.Default.Equals(existing, candidate)))
                        candidates.Add(candidate);
                }
                method = candidates.Count == 1 ? candidates[0] : null;
            }
            return method is null ? null : NormalizeMethod(method);
        }

        private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
        {
            // Reduced extension and constructed generic methods must converge on the
            // declaration ID emitted by the walker. OriginalDefinition preserves the
            // containing type and parameter signature, so unrelated overloads remain
            // distinct.
            var normalized = method.ReducedFrom ?? method;
            return normalized.OriginalDefinition;
        }

        private static bool IsMockOrFluentTestInvocation(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax enclosingMethod)
        {
            if (!IsTestMethod(enclosingMethod)) return false;
            return invocation.DescendantNodesAndSelf().Concat(invocation.Ancestors())
                .OfType<InvocationExpressionSyntax>()
                .Select(candidate => candidate.Expression)
                .OfType<MemberAccessExpressionSyntax>()
                .Select(member => member.Name.Identifier.ValueText)
                .Any(name => name is "Received" or "DidNotReceive" or "Returns"
                    or "ReturnsForAnyArgs" or "When" or "WhenForAnyArgs");
        }

        public override void VisitIdentifierName(IdentifierNameSyntax node)
        {
            AddNameReference(node);
            base.VisitIdentifierName(node);
        }

        public override void VisitGenericName(GenericNameSyntax node)
        {
            AddNameReference(node);
            base.VisitGenericName(node);
        }

        public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
        {
            AddIndexerReference(node);
            base.VisitElementAccessExpression(node);
        }

        public override void VisitElementBindingExpression(ElementBindingExpressionSyntax node)
        {
            AddIndexerReference(node);
            base.VisitElementBindingExpression(node);
        }

        public override void VisitImplicitElementAccess(ImplicitElementAccessSyntax node)
        {
            AddIndexerReference(node);
            base.VisitImplicitElementAccess(node);
        }

        private void AddIndexerReference(ExpressionSyntax node)
        {
            if (ResolveSymbol(node) is IPropertySymbol { IsIndexer: true } indexer)
                AddMemberReference(node, indexer.OriginalDefinition);
        }

        private void AddNameReference(SimpleNameSyntax node)
        {
            var symbol = ResolveSymbol(node);
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    AddTypeReference(node, type);
                    break;
                // An attribute name binds to the attribute's constructor, not its class.
                case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
                    when node.Ancestors().Any(ancestor => ancestor is AttributeSyntax):
                    AddTypeReference(node, constructor.ContainingType);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension } method
                    when !IsInvokedName(node):
                    AddMemberReference(node, NormalizeMethod(method));
                    break;
                case IPropertySymbol or IFieldSymbol or IEventSymbol:
                    AddMemberReference(node, symbol.OriginalDefinition);
                    break;
            }
        }

        /// <summary>
        /// The bound symbol, or the only candidate when binding failed. Engine and package
        /// types are unresolved here, so <c>timer.Timeout += OnTimeout</c> fails to bind
        /// yet still names one method; a choice between candidates would be a guess.
        /// </summary>
        private ISymbol? ResolveSymbol(SyntaxNode node)
        {
            var info = _semanticModel.GetSymbolInfo(node);
            return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : OnlySourceCandidate(info));
        }

        /// <summary>
        /// The repository symbol in an ambiguity with library symbols. Code that compiles
        /// in its own project is not ambiguous, so the clash comes from analyzing it here:
        /// against a newer framework than the project targets, which is how a project's own
        /// <c>Range</c> meets one it has never seen, or beside another project's
        /// <c>global using</c>. The rare wrong pick is a spurious user, which costs far less
        /// than the empty answer of refusing. Two repository symbols stay unresolved, since
        /// nothing says which is meant, unless they are one node: a class and a record that
        /// share a full name in two projects. This rescues the name only: the value's type
        /// stays an error type, so its members still do not bind.
        /// </summary>
        private static ISymbol? OnlySourceCandidate(SymbolInfo info)
        {
            if (info.CandidateReason != CandidateReason.Ambiguous) return null;
            var inSource = info.CandidateSymbols
                .Where(candidate => candidate.Locations.Any(location => location.IsInSource))
                .DistinctBy(DisplayName, StringComparer.Ordinal)
                .Take(2)
                .ToList();
            return inSource.Count == 1 ? inSource[0] : null;
        }

        /// <summary>
        /// True when this name is the method an invocation calls, which the CALLS edge
        /// already records. Any other use of a method name is a method group.
        /// </summary>
        private static bool IsInvokedName(SimpleNameSyntax node)
        {
            SyntaxNode invoked = node.Parent switch
            {
                MemberAccessExpressionSyntax access when access.Name == node => access,
                MemberBindingExpressionSyntax binding when binding.Name == node => binding,
                _ => node,
            };
            return invoked.Parent is InvocationExpressionSyntax invocation && invocation.Expression == invoked;
        }

        private void AddTypeReference(SimpleNameSyntax node, INamedTypeSymbol referencedType)
        {
            // A base type already has the more specific INHERITS or IMPLEMENTS edge;
            // emitting REFERENCES as well only duplicates it. Its type arguments do not.
            if (IsBaseTypeName(node) ||
                !IsRecordedType(node, referencedType) ||
                FindEnclosingNodeId(node) is not { } sourceId)
            {
                return;
            }

            var targetId = NodeId(referencedType.OriginalDefinition);
            if (sourceId != targetId)
                AddLocatedEdge(sourceId, targetId, "REFERENCES", node);
        }

        /// <summary>
        /// A library type is recorded where the code names it. <c>var</c> names nothing,
        /// and would otherwise make every local a use of whatever it holds.
        /// </summary>
        private static bool IsRecordedType(SimpleNameSyntax node, INamedTypeSymbol type)
            => IsIndexedSourceType(type)
                || (ExternalSymbols.IsExternal(type) && node is not IdentifierNameSyntax { IsVar: true });

        private static bool IsBaseTypeName(SyntaxNode node)
        {
            while (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
                node = qualified;
            return node.Parent is BaseTypeSyntax;
        }

        private void AddMemberReference(ExpressionSyntax node, ISymbol member)
        {
            if (!(IsIndexedSourceMember(member) || ExternalSymbols.IsExternal(member))
                || FindEnclosingNodeId(node) is not { } sourceId)
            {
                return;
            }

            var targetId = NodeId(member);
            if (sourceId != targetId)
                AddLocatedEdge(sourceId, targetId, "REFERENCES", node);
        }

        private void AddLocatedEdge(string sourceId, string targetId, string kind, SyntaxNode location)
        {
            var position = location.GetLocation().GetLineSpan().StartLinePosition;
            AddEdge(sourceId, targetId, kind, new Dictionary<string, string>
            {
                ["line"] = position.Line.ToString(),
                ["column"] = position.Character.ToString(),
            });
        }

        /// <summary>
        /// The node an edge from this syntax belongs to: the nearest declaration the
        /// walker emits a node for. Accessors resolve through their property, indexer or
        /// event; lambdas and local functions through the member that holds them.
        /// </summary>
        private string? FindEnclosingNodeId(SyntaxNode node)
        {
            foreach (var ancestor in node.Ancestors())
            {
                switch (ancestor)
                {
                    case GlobalStatementSyntax:
                        return _hasTopLevelEntry ? TopLevelNodeId : null;
                    case BaseMethodDeclarationSyntax:
                    case BasePropertyDeclarationSyntax:
                    case EnumMemberDeclarationSyntax:
                    case BaseTypeDeclarationSyntax:
                    case DelegateDeclarationSyntax:
                    case VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }:
                        if (_semanticModel.GetDeclaredSymbol(ancestor) is { } symbol)
                            return NodeId(symbol);
                        break;
                }
            }
            return null;
        }

        private static bool IsIndexedSourceType(INamedTypeSymbol symbol)
            => symbol.Locations.Any(location => location.IsInSource) &&
               symbol.DeclaringSyntaxReferences.Any(reference =>
                   reference.GetSyntax() is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

        /// <summary>
        /// True when the walker emits a node for this member, which excludes anonymous
        /// type and tuple members, compiler-generated backing fields, and implicit or
        /// primary constructors: all of those report source locations but have no node.
        /// </summary>
        private static bool IsIndexedSourceMember(ISymbol symbol)
            => symbol is not IFieldSymbol { AssociatedSymbol: not null } &&
               symbol.ContainingType is { } containingType &&
               IsIndexedSourceType(containingType) &&
               symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax()
                   is BaseMethodDeclarationSyntax
                   or BasePropertyDeclarationSyntax
                   or EnumMemberDeclarationSyntax
                   or VariableDeclaratorSyntax
                   or ParameterSyntax);

        private static bool IsTestMethod(MethodDeclarationSyntax method)
        {
            foreach (var attribute in method.AttributeLists.SelectMany(list => list.Attributes))
            {
                var name = attribute.Name.ToString();
                var simpleName = name.Split('.').Last();
                if (simpleName.EndsWith("Attribute", StringComparison.Ordinal))
                {
                    simpleName = simpleName[..^"Attribute".Length];
                }

                if (simpleName is "Fact" or "Theory" or "Test" or "TestCase" or
                    "TestMethod" or "DataTestMethod")
                {
                    return true;
                }
            }

            return false;
        }
    }
}
