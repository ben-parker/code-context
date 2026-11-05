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
using System.Globalization;
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
/// Binding is nearly all the cost of analysis, so an edit binds as little as it can. The
/// facts of every file are kept. An edit no other file can see re-walks that file alone; any
/// other edit re-walks its project and what depends on it (see <see cref="EditClassifier"/>).
///
/// Emission is delta-native: facts are bucketed per file and each bucket carries a
/// deterministic content hash. A full index seeds the hash map and replaces the whole
/// workspace; an incremental apply diffs the buckets against the stored hashes and emits only
/// the DIRTY buckets plus the paths REMOVED since the last emission, scoping the host's
/// replacement to exactly those files.
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

    private const int MaxCommonUnresolvedNames = 5;

    /// <summary>Binding parallelizes well to here; past it, threads mostly wait on each other.</summary>
    private const int MaxWalkThreads = 8;

    private const int MinWalkBatch = 32;

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
    private DateTime _builtAt;

    /// <summary>Every unit, dependencies before dependents, the fallback last.</summary>
    private List<ProjectCompilation> _units;

    private ProjectCompilation _fallback;
    private readonly Dictionary<ProjectInfo, ProjectCompilation> _unitByProject = [];

    /// <summary>The approved set the units were built for.</summary>
    private HashSet<string> _files = new(PathComparer);

    /// <summary>The unit each file's facts come from, and so the prefix of the ids it owns.</summary>
    private readonly Dictionary<string, ProjectCompilation> _owners = new(PathComparer);

    /// <summary>What each file's last walk found, before ownership between files is settled.</summary>
    private readonly Dictionary<string, WalkedFile> _walked = new(PathComparer);

    /// <summary>The facts held under each file, ownership settled. Rebuilt only when they can differ.</summary>
    private readonly Dictionary<string, FileFacts> _buckets = new(PathComparer);

    /// <summary>Files changed since the last walk in a way only they can see.</summary>
    private readonly HashSet<string> _changedFiles = new(PathComparer);

    /// <summary>Units whose files, and whose dependents' files, must be walked again.</summary>
    private readonly HashSet<ProjectCompilation> _changedUnits = [];

    /// <summary>Ids emitted by more than one file, with those files, as of the last analysis.</summary>
    private Dictionary<string, List<string>> _sharedIds = new(StringComparer.Ordinal);

    /// <summary>Identifiers more than one symbol would carry, as of the last analysis.</summary>
    private HashSet<string> _contestedIdentifiers = new(StringComparer.Ordinal);

    /// <summary>Id strings by value, so the many edges naming a symbol share one string.</summary>
    private readonly ConcurrentDictionary<string, string> _idStrings = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<(string Id, string Assembly), ProtocolNode> _stubs = new();

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
    /// worker self-heals its state here. A changed project input rebuilds what it affects,
    /// and the fact diff then re-emits whatever now binds differently.
    /// </summary>
    public void SyncApprovedFiles(IReadOnlyList<string> approvedFiles, CancellationToken ct)
    {
        if (_rootPath is not null && (_model is null || _model.InputsChanged()))
        {
            Rebuild(approvedFiles, reuse: true, ct);
            return;
        }
        if (_files.SetEquals(approvedFiles)) return;

        var approved = new HashSet<string>(approvedFiles, PathComparer);
        var added = approvedFiles.Where(path => !_files.Contains(path)).ToList();

        // A file no known project compiles may belong to a project that had none until now.
        if (_model is not null && added.Exists(path => _model.ProjectsCompiling(path).Count == 0 || _model.ReachesIdleProject(path)))
        {
            Rebuild(approvedFiles, reuse: true, ct);
            return;
        }

        foreach (var stale in _files.Where(path => !approved.Contains(path)).ToList())
        {
            DropFile(stale);
        }
        foreach (var path in added)
        {
            ct.ThrowIfCancellationRequested();
            LoadFile(path);
        }
    }

    /// <summary>How many times the projects were read and the files parsed.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Replaces the workspace file set entirely and reparses everything.</summary>
    public void ReplaceFiles(IReadOnlyList<string> files, CancellationToken ct)
    {
        if (IsFreshBuildOf(files)) return;
        Rebuild(files, reuse: false, ct);
    }

    /// <summary>
    /// The host opens a workspace and then indexes it, so on a first scan the build the open
    /// just made is the one the index asked for, unless something moved in between.
    /// </summary>
    private bool IsFreshBuildOf(IReadOnlyList<string> files)
        => _model is not null
            && _walked.Count == 0
            && _files.SetEquals(files)
            && !_model.InputsChanged()
            && files.All(file => File.GetLastWriteTimeUtc(file) < _builtAt);

    /// <summary>
    /// Reads the projects again. A project that reads as it did keeps its unit and so its
    /// facts, unless something it references changed; any other gets a new unit, keeping
    /// each tree whose file is still parsed the same way.
    /// </summary>
    private void Rebuild(IReadOnlyList<string> files, bool reuse, CancellationToken ct)
    {
        BuildCount++;
        _builtAt = DateTime.UtcNow;
        var previous = reuse ? _units : [];
        if (!reuse)
        {
            _walked.Clear();
            _buckets.Clear();
            _idStrings.Clear();
        }

        var previousOwners = new Dictionary<string, ProjectCompilation>(_owners, PathComparer);
        _model = _rootPath is null ? null : ProjectModel.Load(_rootPath, files, Toolchain.Value, ct);
        _files = new HashSet<string>(PathComparer);
        _owners.Clear();
        _unitByProject.Clear();

        var filesByProject = new Dictionary<ProjectInfo, List<string>>();
        foreach (var path in files)
        {
            foreach (var project in _model?.ProjectsCompiling(path) ?? [])
            {
                if (!filesByProject.TryGetValue(project, out var list)) filesByProject[project] = list = [];
                list.Add(path);
            }
        }

        var units = new List<ProjectCompilation>();
        var kept = new HashSet<ProjectCompilation>();
        foreach (var project in _model?.Projects ?? [])
        {
            var signature = Signature(project, filesByProject.GetValueOrDefault(project) ?? []);
            var references = project.ProjectReferences.Select(reference => _unitByProject[reference]).ToList();
            var existing = previous.Find(unit => unit.Project?.Key == project.Key);
            ProjectCompilation unit;
            if (existing?.Signature == signature && references.TrueForAll(kept.Contains))
            {
                unit = existing;
                unit.Project = project;
                kept.Add(unit);
            }
            else
            {
                unit = new ProjectCompilation(
                    project,
                    _workspaceIdPrefix + project.Key + ":",
                    ProjectCompilation.ParseOptionsFor(project),
                    ReferencesFor(project)) { Signature = signature };
                _changedUnits.Add(unit);
            }
            unit.References.Clear();
            unit.References.AddRange(references);
            _unitByProject[project] = unit;
            units.Add(unit);
        }

        var fallback = CreateFallback(_model?.Projects ?? []);
        fallback.References.AddRange(units);
        var previousFallback = previous.Find(unit => unit.Project is null);
        if (previousFallback is not null && units.TrueForAll(kept.Contains) && units.Count == previous.Count - 1
            && previousFallback.Trees.Keys.All(path => _model?.ProjectsCompiling(path).Count is null or 0)
            && files.All(path => _model?.ProjectsCompiling(path).Count > 0 || previousFallback.Trees.ContainsKey(path)))
        {
            fallback = previousFallback;
        }
        else
        {
            _changedUnits.Add(fallback);
        }
        _fallback = fallback;
        units.Add(fallback);
        _units = units;
        _changedUnits.RemoveWhere(unit => !units.Contains(unit));

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var compiling = UnitsCompiling(path);
            SourceText? text = null;
            foreach (var unit in compiling)
            {
                if (unit.Trees.ContainsKey(path)) continue;
                var reusable = previous.Find(old => old.Trees.ContainsKey(path)
                    && old.ParseOptions.Equals(unit.ParseOptions));
                if (reusable is not null)
                {
                    unit.Adopt(path, reusable.Trees[path]);
                }
                else if ((text ??= TryRead(path)) is not null)
                {
                    unit.SetText(path, text);

                    // New to this unit, so what binds to it there has changed.
                    _changedUnits.Add(unit);
                }
            }
            if (!compiling[0].Trees.ContainsKey(path)) continue;

            _owners[path] = compiling[0];
            _files.Add(path);

            // A file's ids belong to its owner, so a new owner moves every symbol in it, and
            // everything binding to them has to be walked again to reach the new ids.
            if (previousOwners.TryGetValue(path, out var previousOwner) && previousOwner != compiling[0])
            {
                _changedUnits.UnionWith(compiling);
                if (units.Find(unit => unit.IdPrefix == previousOwner.IdPrefix) is { } formerOwner)
                {
                    _changedUnits.Add(formerOwner);
                }
            }
        }
    }

    /// <summary>Everything about a project that decides how its files bind.</summary>
    private static string Signature(ProjectInfo project, List<string> files)
    {
        var text = new StringBuilder()
            .AppendJoin('\n', project.PreprocessorSymbols).Append('\0')
            .Append(project.LanguageVersion).Append('\0')
            .Append(project.AssemblyName).Append('\0')
            .Append(project.Name).Append('\0')
            .Append(project.GlobalUsings).Append('\0')
            .AppendJoin('\n', project.InternalsVisibleTo).Append('\0')
            .AppendJoin('\n', project.ProjectReferences.Select(reference => reference.Key)).Append('\0');
        foreach (var path in project.MetadataReferences)
        {
            text.Append(path).Append('|').Append(File.GetLastWriteTimeUtc(path).Ticks).Append('\n');
        }
        text.Append('\0').AppendJoin('\n', files.Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
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
    /// other change kinds re-read the file from disk. An empty batch means a project input
    /// changed, which may be a project nothing has read yet, so projects are read again.</summary>
    public void ApplyChanges(IReadOnlyList<FileChangeDto> changes, CancellationToken ct)
    {
        if (changes.Count == 0 && _rootPath is not null)
        {
            Rebuild(_files.ToList(), reuse: true, ct);
            return;
        }

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
        foreach (var unit in _units)
        {
            if (unit.Remove(path)) _changedUnits.Add(unit);
        }
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
        var visibleElsewhere = false;
        foreach (var unit in compiling)
        {
            visibleElsewhere |= unit.SetText(path, text);
        }
        _owners[path] = compiling[0];
        _files.Add(path);

        if (visibleElsewhere) _changedUnits.UnionWith(compiling);
        else _changedFiles.Add(path);
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
        foreach (var unit in _units)
        {
            unit.RefreshFriends();
            unit.RefreshReferences();
        }
        foreach (var unit in _units) unit.ClearChanged();

        var pending = FilesToWalk();
        var requested = progressFiles ?? pending.ToList();
        var total = requested.Count;
        if (total == 0 && progress is not null)
        {
            await progress(0, 0, null);
        }

        // Progress is defined over the approved request, including files that disappeared
        // or could not be read: those produce no facts, but still count as attempted.
        var batchSize = Math.Max(MinWalkBatch, (total + 99) / 100);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, MaxWalkThreads),
            CancellationToken = ct,
        };
        var walkedNow = new HashSet<string>(PathComparer);
        for (var start = 0; start < total; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = requested.Skip(start).Take(batchSize).Where(pending.Contains).ToList();
            var walks = new WalkedFile[batch.Count];
            Parallel.For(0, batch.Count, options, index => walks[index] = WalkFile(batch[index], ct));
            for (var index = 0; index < batch.Count; index++)
            {
                _walked[batch[index]] = walks[index];
                walkedNow.Add(batch[index]);
            }

            for (var processed = start + 1; progress is not null && processed <= Math.Min(start + batchSize, total); processed++)
            {
                if (ShouldReportProgress(processed, total))
                {
                    await progress(processed, total, requested[processed - 1]);
                }
            }
        }

        var removed = _walked.Keys.Where(path => !_owners.ContainsKey(path)).ToList();
        foreach (var path in removed) _walked.Remove(path);
        _changedFiles.Clear();
        _changedUnits.Clear();

        var buckets = SettleOwnership(walkedNow, removed);
        var diagnostics = _pendingDiagnostics.ToList();
        _pendingDiagnostics.Clear();
        return new AnalysisResult(diagnostics, buckets) { Coverage = BuildCoverage() };
    }

    private static bool ShouldReportProgress(int processed, int total)
    {
        if (processed == total) return true;
        var interval = Math.Max(1, (total + 99) / 100);
        return processed % interval == 0;
    }

    /// <summary>
    /// What must be walked: files edited on their own, every file of a changed unit and of
    /// what depends on it, and anything not walked yet. A unit lists its references
    /// transitively, so one pass finds every dependent.
    /// </summary>
    private HashSet<string> FilesToWalk()
    {
        var affected = _units
            .Where(unit => _changedUnits.Contains(unit) || unit.References.Exists(_changedUnits.Contains))
            .ToHashSet();
        var files = new HashSet<string>(_changedFiles.Where(_owners.ContainsKey), PathComparer);
        foreach (var (path, unit) in _owners)
        {
            if (affected.Contains(unit) || !_walked.TryGetValue(path, out var walked) || walked.Unit != unit)
            {
                files.Add(path);
            }
        }
        return files;
    }

    private WalkedFile WalkFile(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var unit = _owners[path];
        var tree = unit.Trees[path];
        var nodes = new List<ProtocolNode>();
        var edges = new List<EdgeFact>();
        var externals = new ExternalSymbols(_workspaceIdPrefix, _stubs, Share);
        var walker = new GraphWalker(
            unit.Compilation.GetSemanticModel(tree), tree.FilePath, unit.IdPrefix, _owners, externals, Share, nodes, edges);
        walker.Visit(tree.GetRoot(ct));
        nodes.TrimExcess();
        edges.TrimExcess();
        return new WalkedFile(unit, nodes, edges, externals, walker.UnresolvedNames);
    }

    private string Share(string id) => _idStrings.GetOrAdd(id, id);

    /// <summary>
    /// Settles which file holds each fact, a pure function of the file SET and never of edit
    /// order, or an incremental graph would diverge from a fresh scan. A node or edge id can
    /// be emitted by more than one file (partial types; base lists repeated on partial
    /// declarations). The ordinally smallest emitting path owns a node, and an edge belongs
    /// to the file owning its source node, because the host removes an edge along with that
    /// node: a bucket must hold exactly what the host replaces as one unit.
    ///
    /// Only files that could have changed are rebuilt: those just walked, those sharing an
    /// id with them, and those whose identifier gained or lost a rival.
    /// </summary>
    private List<FileFacts> SettleOwnership(HashSet<string> walkedNow, List<string> removed)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, walked) in _walked)
        {
            foreach (var node in walked.Nodes)
            {
                if (!owners.TryGetValue(node.Id, out var current))
                {
                    owners[node.Id] = path;
                }
                else if (current != path)
                {
                    if (!shared.TryGetValue(node.Id, out var files)) shared[node.Id] = files = [current];
                    if (!files.Contains(path)) files.Add(path);
                    if (string.CompareOrdinal(path, current) < 0) owners[node.Id] = path;
                }
            }
        }

        var stubBuckets = ExternalSymbols.ToBuckets(_walked.Values.Select(walked => walked.Externals)).ToList();

        var holders = new Dictionary<string, string>(StringComparer.Ordinal);
        var contested = new HashSet<string>(StringComparer.Ordinal);
        var filesByIdentifier = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Hold(ProtocolNode node, string? file)
        {
            if (!holders.TryAdd(node.Identifier, node.Id) && holders[node.Identifier] != node.Id)
            {
                contested.Add(node.Identifier);
            }
            if (file is null) return;
            if (!filesByIdentifier.TryGetValue(node.Identifier, out var files)) filesByIdentifier[node.Identifier] = files = [];
            if (!files.Contains(file)) files.Add(file);
        }
        foreach (var (path, walked) in _walked)
        {
            foreach (var node in walked.Nodes) Hold(node, path);
        }
        foreach (var bucket in stubBuckets)
        {
            foreach (var node in bucket.Nodes) Hold(node, null);
        }

        var rebuild = new HashSet<string>(walkedNow, PathComparer);
        var touched = new HashSet<string>(walkedNow, PathComparer);
        touched.UnionWith(removed);
        foreach (var files in _sharedIds.Values.Concat(shared.Values))
        {
            if (files.Exists(touched.Contains)) rebuild.UnionWith(files);
        }
        foreach (var identifier in contested.Where(item => !_contestedIdentifiers.Contains(item))
                     .Concat(_contestedIdentifiers.Where(item => !contested.Contains(item))))
        {
            if (filesByIdentifier.TryGetValue(identifier, out var files)) rebuild.UnionWith(files);
        }

        foreach (var path in removed) _buckets.Remove(path);
        foreach (var path in rebuild)
        {
            if (_walked.ContainsKey(path)) _buckets[path] = BuildBucket(path, owners, shared, contested);
        }
        _sharedIds = shared;
        _contestedIdentifiers = contested;

        var result = new List<FileFacts>(_buckets.Count + stubBuckets.Count);
        result.AddRange(_buckets.Values);
        result.AddRange(stubBuckets.Select(bucket => contested.Count == 0 ? bucket : bucket with { Nodes = Qualify(bucket.Nodes, contested) }));
        return result;
    }

    private FileFacts BuildBucket(
        string path,
        Dictionary<string, string> owners,
        Dictionary<string, List<string>> shared,
        HashSet<string> contested)
    {
        var walked = _walked[path];

        // Among within-file duplicates of an id, the last occurrence wins.
        var last = new Dictionary<string, int>(walked.Nodes.Count, StringComparer.Ordinal);
        for (var index = 0; index < walked.Nodes.Count; index++) last[walked.Nodes[index].Id] = index;
        var nodes = new List<ProtocolNode>(last.Count);
        for (var index = 0; index < walked.Nodes.Count; index++)
        {
            var node = walked.Nodes[index];
            if (last[node.Id] == index && owners[node.Id] == path) nodes.Add(node);
        }

        var edges = new HashSet<EdgeFact>();
        foreach (var edge in walked.Edges)
        {
            if (!owners.TryGetValue(edge.SourceId, out var owner) || owner == path) edges.Add(edge);
        }
        foreach (var node in nodes)
        {
            if (!shared.TryGetValue(node.Id, out var files)) continue;
            foreach (var file in files)
            {
                if (file == path) continue;
                foreach (var edge in _walked[file].Edges)
                {
                    if (edge.SourceId == node.Id) edges.Add(edge);
                }
            }
        }

        return new FileFacts(path, Qualify(nodes, contested), edges.ToList());
    }

    private List<ProjectCoverage> BuildCoverage()
    {
        var files = _owners.Values.GroupBy(unit => unit).ToDictionary(group => group.Key, group => group.Count());
        var unresolved = new Dictionary<ProjectCompilation, Dictionary<string, int>>();
        foreach (var walked in _walked.Values)
        {
            if (walked.UnresolvedNames is not { } own) continue;
            if (!unresolved.TryGetValue(walked.Unit, out var names))
            {
                unresolved[walked.Unit] = names = new Dictionary<string, int>(StringComparer.Ordinal);
            }
            foreach (var (name, count) in own) names[name] = names.GetValueOrDefault(name) + count;
        }

        var coverage = new List<ProjectCoverage>();
        foreach (var unit in _units)
        {
            var count = files.GetValueOrDefault(unit);
            var names = unresolved.GetValueOrDefault(unit);
            var total = names?.Values.Sum() ?? 0;
            var common = names?.OrderByDescending(name => name.Value)
                .ThenBy(name => name.Key, StringComparer.Ordinal)
                .Take(MaxCommonUnresolvedNames)
                .Select(name => name.Key)
                .ToList();

            if (unit.Project is not { } project)
            {
                if (count == 0) continue;
                var notes = new List<string>
                {
                    _model is { Projects.Count: > 0 }
                        ? "These files are compiled by no project, so they bind against the worker's runtime and the workspace's projects."
                        : "No project compiles these files, so they bind against the worker's runtime with no constants or packages.",
                };
                notes.AddRange((_model?.Failures ?? []).Select(failure => $"{failure.RelativePath} could not be read: {failure.Reason}"));
                coverage.Add(new ProjectCoverage(
                    unit.Qualifier, null, ProjectCoverageStates.Fallback, count,
                    UnresolvedNames: total, CommonUnresolvedNames: common, Notes: notes));
                continue;
            }

            var projectNotes = project.Notes.ToList();
            var viewedOnly = FilesSeenDifferentlyByOwner(unit);
            if (viewedOnly > 0)
            {
                projectNotes.Add($"{viewedOnly} file(s) this project shares with another use different constants here, "
                    + "and only the owning project's view of them is indexed.");
            }

            coverage.Add(new ProjectCoverage(
                project.Name,
                project.RelativePath,
                project.State switch
                {
                    ProjectCoverageState.Complete => ProjectCoverageStates.Complete,
                    ProjectCoverageState.Partial => ProjectCoverageStates.Partial,
                    _ => ProjectCoverageStates.Fallback,
                },
                count,
                project.TargetFramework,
                NullIfEmpty(project.InactiveTargetFrameworks),
                NullIfEmpty(project.MissingReferences),
                NullIfEmpty(project.AnalyzerPackages),
                total,
                common,
                NullIfEmpty(projectNotes)));
        }
        return coverage;
    }

    /// <summary>
    /// A file two projects compile is walked once. Where they differ on constants, code only
    /// the other project compiles has no nodes, which a reader of status should be told.
    /// </summary>
    private int FilesSeenDifferentlyByOwner(ProjectCompilation unit)
        => unit.Trees.Count(entry => _owners.TryGetValue(entry.Key, out var owner)
            && owner != unit
            && !owner.ParseOptions.Equals(unit.ParseOptions)
            && entry.Value.GetRoot().ContainsDirectives);

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> items) => items.Count == 0 ? null : items;

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

    /// <summary>
    /// One edge as a value. The wire form carries a long id and a metadata dictionary, which
    /// across a large workspace cost more than everything else this process holds; both
    /// derive from these fields, so they exist only while being sent.
    /// </summary>
    public readonly record struct EdgeFact(string SourceId, string TargetId, string Kind, int Line = -1, int Column = -1)
    {
        public string Id => $"{SourceId}=[{Kind}]=>{TargetId}" + (Line >= 0 ? $"@{Line}:{Column}" : string.Empty);

        public ProtocolEdge ToProtocol() => new(
            Id, SourceId, TargetId, Kind,
            Line >= 0
                ? new Dictionary<string, string>
                {
                    ["line"] = Line.ToString(CultureInfo.InvariantCulture),
                    ["column"] = Column.ToString(CultureInfo.InvariantCulture),
                }
                : null);
    }

    /// <summary>What one walk of one file found, kept so an untouched file is not walked again.</summary>
    private sealed record WalkedFile(
        ProjectCompilation Unit,
        List<ProtocolNode> Nodes,
        List<EdgeFact> Edges,
        ExternalSymbols Externals,
        Dictionary<string, int>? UnresolvedNames);

    /// <summary>The facts the host holds under one file: the nodes it owns and the edges
    /// sourced at those nodes, wherever they were found. <see cref="FilePath"/> is the
    /// verbatim <c>tree.FilePath</c> string; the bucket's node <c>FilePath</c>s and the
    /// <c>ReplacesFiles</c> entries derived from it are all this same string.</summary>
    public sealed record FileFacts(
        string FilePath,
        List<ProtocolNode> Nodes,
        List<EdgeFact> Edges)
    {
        /// <summary>Set once computed: a bucket is rebuilt, never changed.</summary>
        internal byte[]? Hash { get; set; }
    }

    public sealed class AnalysisResult(List<ProtocolDiagnostic> diagnostics, IReadOnlyList<FileFacts> buckets)
    {
        private List<ProtocolNode>? _nodes;
        private List<ProtocolEdge>? _edges;
        private HashSet<string>? _nodeIds;

        public List<ProtocolDiagnostic> Diagnostics { get; } = diagnostics;
        public IReadOnlyList<FileFacts> Buckets { get; } = buckets;

        /// <summary>What each project could and could not see.</summary>
        public IReadOnlyList<ProjectCoverage> Coverage { get; init; } = [];

        public List<ProtocolNode> Nodes => _nodes ??= Buckets.SelectMany(bucket => bucket.Nodes).ToList();

        /// <summary>The wire form of every edge. Sending walks <see cref="Buckets"/> instead.</summary>
        public List<ProtocolEdge> Edges
            => _edges ??= Buckets.SelectMany(bucket => bucket.Edges).Select(edge => edge.ToProtocol()).ToList();

        public IReadOnlySet<string> NodeIds
            => _nodeIds ??= Buckets.SelectMany(bucket => bucket.Nodes).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
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
            List<EdgeFact> edgeFacts,
            List<string> replacesFiles,
            Dictionary<string, byte[]> pendingHashes,
            List<string> removedPaths)
        {
            Nodes = nodes;
            EdgeFacts = edgeFacts;
            ReplacesFiles = replacesFiles;
            _pendingHashes = pendingHashes;
            _removedPaths = removedPaths;
        }

        public List<ProtocolNode> Nodes { get; }
        public List<EdgeFact> EdgeFacts { get; }
        public List<ProtocolEdge> Edges => EdgeFacts.ConvertAll(edge => edge.ToProtocol());

        /// <summary>Dirty ∪ removed, verbatim paths; repeated on every emitted chunk.</summary>
        public List<string> ReplacesFiles { get; }

        internal Dictionary<string, byte[]> PendingHashes => _pendingHashes;
        internal List<string> RemovedPaths => _removedPaths;

        private readonly Dictionary<string, byte[]> _pendingHashes;
        private readonly List<string> _removedPaths;
    }

    /// <summary>
    /// False for a worker that has told the host nothing, as after a restart. It then cannot
    /// know what the host holds, so it must replace all of it rather than send a difference.
    /// </summary>
    public bool HasFactBaseline => _factHashes.Count > 0;

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
        var edges = new List<EdgeFact>();
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
        if (bucket.Hash is { } known) return known;

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
        foreach (var edge in bucket.Edges
                     .OrderBy(e => e.SourceId, StringComparer.Ordinal)
                     .ThenBy(e => e.TargetId, StringComparer.Ordinal)
                     .ThenBy(e => e.Kind, StringComparer.Ordinal)
                     .ThenBy(e => e.Line)
                     .ThenBy(e => e.Column))
        {
            hasher.Str(edge.SourceId);
            hasher.Str(edge.TargetId);
            hasher.Str(edge.Kind);
            hasher.Int(edge.Line);
            hasher.Int(edge.Column);
            hasher.Int(nodeIds.Contains(edge.TargetId) ? 1 : 0);
        }
        return bucket.Hash = hasher.GetHashAndReset();
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
        private readonly Func<string, string> _share;
        private Dictionary<string, int>? _unresolved;
        private readonly List<ProtocolNode> _nodes;
        private readonly List<EdgeFact> _edges;
        private bool _hasTopLevelEntry;

        public GraphWalker(
            SemanticModel semanticModel, string filePath, string idPrefix,
            IReadOnlyDictionary<string, ProjectCompilation> owners,
            ExternalSymbols externals,
            Func<string, string> share,
            List<ProtocolNode> nodes, List<EdgeFact> edges)
        {
            _semanticModel = semanticModel;
            _filePath = filePath;
            _idPrefix = idPrefix;
            _owners = owners;
            _externals = externals;
            _share = share;
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
                    return _share((_owners.TryGetValue(tree.FilePath, out var owner) ? owner.IdPrefix : _idPrefix) + DisplayName(symbol));
                }
            }
            return ExternalSymbols.IsExternal(symbol) ? _externals.Add(symbol) : _share(_idPrefix + DisplayName(symbol));
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

        private void AddEdge(string sourceId, string targetId, string kind)
            => _edges.Add(new EdgeFact(sourceId, targetId, kind));

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
            if (symbol is null) NoteIfUnresolved(node);
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
        /// A name that binds to nothing is what a project with something missing looks like
        /// from inside, including one that looks complete: code a source generator would
        /// have written, or a reference that was never declared.
        /// </summary>
        private void NoteIfUnresolved(SimpleNameSyntax node)
        {
            var info = _semanticModel.GetSymbolInfo(node);
            if (info.CandidateSymbols.Length > 0 || node.IsVar || node.Parent is NameColonSyntax or NameEqualsSyntax) return;
            if (node.Identifier.ValueText is "nameof" or "unmanaged" or "notnull") return;
            if (_semanticModel.GetTypeInfo(node).Type is { TypeKind: not TypeKind.Error }) return;
            if (_semanticModel.GetConstantValue(node).HasValue) return;

            var name = node.Identifier.ValueText;
            _unresolved ??= new Dictionary<string, int>(StringComparer.Ordinal);
            _unresolved[name] = _unresolved.GetValueOrDefault(name) + 1;
        }

        public Dictionary<string, int>? UnresolvedNames => _unresolved;

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
            _edges.Add(new EdgeFact(sourceId, targetId, kind, position.Line, position.Character));
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
