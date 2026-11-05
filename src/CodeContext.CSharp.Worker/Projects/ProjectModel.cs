using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>How much of a project's declared context was found.</summary>
public enum ProjectCoverageState
{
    /// <summary>Evaluated, restore data read where needed, every reference found.</summary>
    Complete,

    /// <summary>Compiled as its own project, but some references are missing.</summary>
    Partial,

    /// <summary>The project could not be resolved. Its code binds as if it had no project.</summary>
    Fallback,
}

/// <summary>Everything needed to compile one project, as read from files.</summary>
public sealed class ProjectInfo
{
    public required string Key { get; init; }

    /// <summary>Unique among the workspace's projects, and what an identifier is qualified with.</summary>
    public required string Name { get; init; }

    public required string ProjectPath { get; init; }
    public required string RelativePath { get; init; }
    public required string AssemblyName { get; init; }
    public required string? TargetFramework { get; init; }
    public required IReadOnlyList<string> InactiveTargetFrameworks { get; init; }
    public required string Configuration { get; init; }
    public required IReadOnlyList<string> PreprocessorSymbols { get; init; }
    public required string? LanguageVersion { get; init; }

    /// <summary>The text of a <c>global using</c> file for this project, or empty.</summary>
    public required string GlobalUsings { get; init; }

    /// <summary>Assemblies given access by project items rather than by source.</summary>
    public required IReadOnlyList<string> InternalsVisibleTo { get; init; }

    public IReadOnlyList<string> MetadataReferences { get; internal set; } = [];

    /// <summary>Referenced projects in the model, transitively, as a build passes them.</summary>
    public IReadOnlyList<ProjectInfo> ProjectReferences { get; internal set; } = [];

    public IReadOnlyList<string> MissingReferences { get; internal set; } = [];
    public required IReadOnlyList<string> AnalyzerPackages { get; init; }
    public required bool HasRestoreData { get; init; }
    public required CompileItemSet CompileItems { get; init; }
    public required IReadOnlyList<string> Inputs { get; init; }

    /// <summary>True when no framework assemblies were found for this project.</summary>
    internal bool NoFramework { get; init; }

    /// <summary>Shared frameworks this project declares that reach what references it.</summary>
    internal IReadOnlyList<string> FlowingFrameworks { get; init; } = [];

    /// <summary>Why this project's context is incomplete, beyond its missing references.</summary>
    internal List<string> Gaps { get; init; } = [];

    /// <summary>Things worth saying that leave the context complete.</summary>
    internal List<string> Remarks { get; init; } = [];

    public IEnumerable<string> Notes => Gaps.Concat(Remarks);

    public ProjectCoverageState State => NoFramework ? ProjectCoverageState.Fallback
        : MissingReferences.Count > 0 || Gaps.Count > 0 ? ProjectCoverageState.Partial
        : ProjectCoverageState.Complete;

    public string Directory => Path.GetDirectoryName(ProjectPath)!;

    public override string ToString() => Name;
}

/// <summary>
/// The projects of a workspace, read from files alone. Nothing here runs a build, a task,
/// or any code a repository supplies.
///
/// A project is only in the model if it owns an approved file. A file no project compiles
/// is reported as unassigned rather than dropped, because the graph must still cover it.
/// </summary>
public sealed class ProjectModel
{
    private const string AssetsFileName = "project.assets.json";
    private const string DefaultConfiguration = "Debug";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".venv", ".codecontext", "bin", "node_modules",
    };

    private readonly Dictionary<string, List<ProjectInfo>> _compiledBy;
    private readonly Dictionary<string, DateTime> _inputStamps;
    private readonly List<CompileItemSet> _idleCompileItems;

    private ProjectModel(
        IReadOnlyList<ProjectInfo> projects,
        Dictionary<string, List<ProjectInfo>> compiledBy,
        IReadOnlyList<string> unassignedFiles,
        IReadOnlyList<ProjectLoadFailure> failures,
        Dictionary<string, DateTime> inputStamps,
        List<CompileItemSet> idleCompileItems)
    {
        Projects = projects;
        _compiledBy = compiledBy;
        UnassignedFiles = unassignedFiles;
        Failures = failures;
        _inputStamps = inputStamps;
        _idleCompileItems = idleCompileItems;
    }

    /// <summary>In dependency order: every project follows the projects it references.</summary>
    public IReadOnlyList<ProjectInfo> Projects { get; }

    /// <summary>Approved files no project compiles.</summary>
    public IReadOnlyList<string> UnassignedFiles { get; }

    /// <summary>Project files over approved files that could not be read.</summary>
    public IReadOnlyList<ProjectLoadFailure> Failures { get; }

    /// <summary>The projects compiling a file, fact owner first.</summary>
    public IReadOnlyList<ProjectInfo> ProjectsCompiling(string file)
    {
        if (_compiledBy.TryGetValue(file, out var known)) return known;

        // A file created since the model was read: the same operations decide.
        var compiling = OwnerOrder(file, Projects.Where(project => project.CompileItems.Contains(file)), project => project.ProjectPath);
        if (compiling.Count > 0) _compiledBy[file] = compiling;
        return compiling;
    }

    /// <summary>
    /// The owner of a shared file is the deepest project whose directory holds it, since
    /// that is where it was written; a project linking it from elsewhere only borrows it.
    /// </summary>
    private static List<T> OwnerOrder<T>(string file, IEnumerable<T> projects, Func<T, string> path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int Depth(T project)
        {
            var directory = Path.GetDirectoryName(path(project))! + Path.DirectorySeparatorChar;
            return file.StartsWith(directory, comparison) ? directory.Length : 0;
        }
        return projects.OrderByDescending(Depth).ThenBy(path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether a project with no files until now would compile this one.</summary>
    public bool ReachesIdleProject(string file) => _idleCompileItems.Exists(items => items.Contains(file));

    /// <summary>The project whose facts include this file, or null when no project compiles it.</summary>
    public ProjectInfo? OwnerOf(string file) => ProjectsCompiling(file).FirstOrDefault();

    /// <summary>Whether any file this model was read from has changed since.</summary>
    public bool InputsChanged() => _inputStamps.Any(input => Stamp(input.Key) != input.Value);

    public static ProjectModel Load(
        string rootPath,
        IReadOnlyList<string> approvedFiles,
        ToolchainLocations? toolchain = null,
        CancellationToken ct = default)
        => new Loader(Path.GetFullPath(rootPath), approvedFiles, toolchain ?? ToolchainLocations.Detect(), ct).Run();

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>The stable key of a project: its location within the workspace.</summary>
    internal static string KeyFor(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? normalized.ToLowerInvariant() : normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 4));
    }

    public sealed record ProjectLoadFailure(string ProjectPath, string RelativePath, string Reason);

    private sealed record Discovery(List<string> Projects, List<string> Assets, List<string> Solutions);

    /// <summary>A project read from disk, before references between projects are known.</summary>
    private sealed record Candidate(
        EvaluatedProject Evaluated,
        string? TargetFramework,
        IReadOnlyList<string> InactiveTargetFrameworks,
        RestoreData? Restore,
        string? ExpectedAssetsPath);

    private sealed class Loader(string root, IReadOnlyList<string> approvedFiles, ToolchainLocations toolchain, CancellationToken ct)
    {
        private readonly string _boundary = FindBoundary(root);
        private readonly Dictionary<string, Candidate> _candidates = new(PathComparer);
        private readonly List<ProjectLoadFailure> _failures = [];
        private readonly Dictionary<string, RestoreData> _restoreByProject = new(PathComparer);
        private List<string> _solutions = [];

        public ProjectModel Run()
        {
            var discovery = Discover();
            _solutions = discovery.Solutions;
            IndexRestoreData(discovery);

            foreach (var project in discovery.Projects)
            {
                ct.ThrowIfCancellationRequested();
                TryLoadCandidate(project);
            }

            var compiling = AssignFiles(out var unassigned);
            var failures = RelevantFailures(unassigned);
            var infos = BuildProjects(compiling);
            var ordered = Order(infos);

            var compiledBy = new Dictionary<string, List<ProjectInfo>>(PathComparer);
            foreach (var (file, owners) in compiling)
            {
                compiledBy[file] = OwnerOrder(file, owners, path => path).Select(path => infos[path]).ToList();
            }

            var inputs = new Dictionary<string, DateTime>(PathComparer);
            foreach (var project in ordered)
            {
                foreach (var input in project.Inputs) inputs[input] = Stamp(input);
            }
            foreach (var failure in failures) inputs[failure.ProjectPath] = Stamp(failure.ProjectPath);

            var idle = _candidates
                .Where(candidate => !infos.ContainsKey(candidate.Key))
                .Select(candidate => candidate.Value.Evaluated.CompileItems)
                .ToList();
            return new ProjectModel(ordered, compiledBy, unassigned, failures, inputs, idle);
        }

        /// <summary>
        /// The top of the repository holding the workspace, so a workspace opened on a
        /// subdirectory still sees the repository's <c>Directory.Build.props</c>. Without a
        /// repository it is the workspace itself, so nothing unrelated above it counts.
        /// </summary>
        private static string FindBoundary(string root)
        {
            for (string? directory = root; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var marker = Path.Combine(directory, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return directory;
            }
            return root;
        }

        private Discovery Discover()
        {
            var discovery = new Discovery([], [], []);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        var name = Path.GetFileName(file);
                        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) discovery.Projects.Add(file);
                        else if (name.Equals(AssetsFileName, StringComparison.OrdinalIgnoreCase)) discovery.Assets.Add(file);
                        else if (name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)) discovery.Solutions.Add(file);
                    }
                    foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
                    {
                        // A link can lead back here, and whatever it reaches is not this tree's.
                        if (!SkippedDirectories.Contains(child.Name) && !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            pending.Push(child.FullName);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable directory holds nothing this can use.
                }
            }
            discovery.Projects.Sort(StringComparer.Ordinal);
            discovery.Assets.Sort(StringComparer.Ordinal);
            return discovery;
        }

        private void IndexRestoreData(Discovery discovery)
        {
            var known = new HashSet<string>(discovery.Projects, PathComparer);
            foreach (var assets in discovery.Assets)
            {
                ct.ThrowIfCancellationRequested();
                if (RestoreData.TryRead(assets, boundary: _boundary) is not { ProjectPath: { } path } restore) continue;
                var full = Path.GetFullPath(MsBuildExpander.ToNative(path));
                if (!known.Contains(full)) continue;

                // Two restores of one project: the newer one reflects the project as it is.
                if (!_restoreByProject.TryGetValue(full, out var existing)
                    || File.GetLastWriteTimeUtc(assets) > File.GetLastWriteTimeUtc(existing.AssetsPath))
                {
                    _restoreByProject[full] = restore;
                }
            }
        }

        private void TryLoadCandidate(string project)
        {
            if (_candidates.ContainsKey(project)) return;
            try
            {
                _candidates[project] = LoadCandidate(project);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                _failures.Add(new ProjectLoadFailure(project, Relative(project), ex.Message));
            }
        }

        private Candidate LoadCandidate(string project)
        {
            _restoreByProject.TryGetValue(project, out var restore);
            var folders = PackageFolders(restore);
            var solution = NearestSolution(project);

            var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = DefaultConfiguration,
            };
            var evaluated = ProjectEvaluator.Evaluate(project, _boundary, folders, globals, solution);

            // The IDE's active configuration: Debug, or the first a project declares without it.
            var configurations = Split(evaluated.Property("Configurations"));
            if (configurations.Count > 0 && !configurations.Contains(DefaultConfiguration, StringComparer.OrdinalIgnoreCase))
            {
                globals["Configuration"] = configurations[0];
                evaluated = ProjectEvaluator.Evaluate(project, _boundary, folders, globals, solution);
            }

            var frameworks = Split(evaluated.Property("TargetFrameworks"));
            var inactive = new List<string>();
            var framework = evaluated.Property("TargetFramework").Trim();
            if (framework.Length == 0 && frameworks.Count > 0)
            {
                // A multi-targeted project is evaluated as its first target's inner build.
                framework = frameworks[0];
                inactive = frameworks.Skip(1).ToList();
                globals["TargetFramework"] = framework;
                evaluated = ProjectEvaluator.Evaluate(project, _boundary, folders, globals, solution);
            }
            if (framework.Length == 0)
            {
                framework = TargetFrameworkName.FromLegacyVersion(evaluated.Property("TargetFrameworkVersion")) ?? string.Empty;
            }

            var expected = ExpectedAssetsPath(evaluated);
            if (expected is not null && MsBuildExpander.IsForeignPath(expected, _boundary)) expected = null;
            if (restore is null && expected is not null && File.Exists(expected))
            {
                // Beside the project but naming another path: the repository moved.
                restore = RestoreData.TryRead(expected, project, _boundary);
            }

            return new Candidate(evaluated, framework.Length > 0 ? framework : null, inactive, restore, expected);
        }

        private static string? ExpectedAssetsPath(EvaluatedProject evaluated)
        {
            var directory = Path.GetDirectoryName(evaluated.ProjectPath)!;
            var relative = evaluated.Property("MSBuildProjectExtensionsPath");
            if (relative.Length == 0) relative = evaluated.Property("BaseIntermediateOutputPath");
            if (relative.Length == 0) relative = "obj";
            return Path.GetFullPath(Path.Combine(MsBuildExpander.ToNative(relative), AssetsFileName), directory);
        }

        private List<string> PackageFolders(RestoreData? restore)
            => (restore?.PackageFolders ?? []).Concat(toolchain.PackageFolders)
                .Distinct(PathComparer)
                .ToList();

        private string? NearestSolution(string project)
        {
            if (_solutions.Count == 0) return null;
            for (var directory = Path.GetDirectoryName(project); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (_solutions.Any(solution => MsBuildExpander.SamePath(Path.GetDirectoryName(solution)!, directory)))
                {
                    return directory;
                }
                if (MsBuildExpander.SamePath(directory, _boundary)) break;
            }
            return null;
        }

        private static List<string> Split(string text)
            => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        private string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        /// <summary>Which projects compile each approved file, by running each project's item operations.</summary>
        private Dictionary<string, List<string>> AssignFiles(out List<string> unassigned)
        {
            var byDirectory = _candidates.Keys
                .GroupBy(path => Path.GetDirectoryName(path)!, PathComparer)
                .ToDictionary(group => group.Key, group => group.ToList(), PathComparer);
            var reachingOutside = _candidates.Values
                .Where(candidate => candidate.Evaluated.CompileItems.IncludesOutside(Path.GetDirectoryName(candidate.Evaluated.ProjectPath)!))
                .Select(candidate => candidate.Evaluated.ProjectPath)
                .ToList();

            var result = new Dictionary<string, List<string>>(PathComparer);
            unassigned = [];
            foreach (var file in approvedFiles)
            {
                ct.ThrowIfCancellationRequested();
                var compiling = new List<string>();
                for (var directory = Path.GetDirectoryName(file); directory is not null; directory = Path.GetDirectoryName(directory))
                {
                    if (byDirectory.TryGetValue(directory, out var projects))
                    {
                        compiling.AddRange(projects.Where(project => _candidates[project].Evaluated.CompileItems.Contains(file)));
                    }
                    if (MsBuildExpander.SamePath(directory, root)) break;
                }
                foreach (var project in reachingOutside)
                {
                    if (!compiling.Contains(project, PathComparer) && _candidates[project].Evaluated.CompileItems.Contains(file))
                    {
                        compiling.Add(project);
                    }
                }

                if (compiling.Count == 0) unassigned.Add(file);
                else result[file] = compiling;
            }
            return result;
        }

        /// <summary>A broken project matters only if it might have owned something.</summary>
        private List<ProjectLoadFailure> RelevantFailures(List<string> unassigned)
            => _failures.Where(failure =>
            {
                var directory = Path.GetDirectoryName(failure.ProjectPath)! + Path.DirectorySeparatorChar;
                return unassigned.Any(file => file.StartsWith(directory, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
            }).ToList();

        private Dictionary<string, ProjectInfo> BuildProjects(Dictionary<string, List<string>> compiling)
        {
            var owning = compiling.Values.SelectMany(projects => projects).ToHashSet(PathComparer);
            var names = owning
                .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var infos = new Dictionary<string, ProjectInfo>(PathComparer);
            foreach (var path in owning.Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var relative = Relative(path);
                var unique = names[name] == 1 ? name : relative[..^Path.GetExtension(relative).Length];
                infos[path] = ResolveProject(_candidates[path], unique, relative);
            }

            LinkProjects(infos);
            return infos;
        }

        private ProjectInfo ResolveProject(Candidate candidate, string name, string relative)
        {
            var evaluated = candidate.Evaluated;
            var target = candidate.Restore?.TargetFor(candidate.TargetFramework);
            var folders = PackageFolders(candidate.Restore);
            var references = new List<string>();
            var missing = new List<string>();
            var gaps = new List<string>();
            var remarks = new List<string>();
            var noFramework = false;

            if (ResolveFramework(candidate, target, folders) is { } framework)
            {
                references.AddRange(framework.Assemblies);
                missing.AddRange(framework.Missing);
                gaps.AddRange(framework.Notes);
                if (!framework.Found)
                {
                    gaps.Add("No framework assemblies were found, so the worker's own runtime stands in.");
                    noFramework = true;
                }
            }

            if (target is not null)
            {
                references.AddRange(target.CompileAssets);
                missing.AddRange(target.MissingPackages);
            }
            else if (evaluated.ItemsOf("PackageReference") is { Count: > 0 } packages)
            {
                gaps.Add(candidate.Restore is null
                    ? "No restore data: packages were looked up by declared version and their dependencies are not known."
                    : $"Restore data has no target for {candidate.TargetFramework}: packages were looked up by declared version.");
                ResolvePackagesByName(candidate, packages, folders, references, missing);
            }

            ResolveAssemblyReferences(evaluated, _boundary, references, missing);

            gaps.AddRange(evaluated.MissingSdks.Select(sdk => $"SDK {sdk} was not found, so its defaults are unknown."));
            gaps.AddRange(evaluated.MissingImports.Select(import => $"Import {import} could not be read."));
            if (evaluated.Uncertainties.Count > 0)
            {
                remarks.Add($"{evaluated.Uncertainties.Count} project expression(s) could not be read; first: {evaluated.Uncertainties[0]}");
            }

            var inputs = evaluated.Inputs.ToList();
            if (candidate.Restore is not null) inputs.Add(candidate.Restore.AssetsPath);
            else if (candidate.ExpectedAssetsPath is not null) inputs.Add(candidate.ExpectedAssetsPath);

            return new ProjectInfo
            {
                Key = KeyFor(relative),
                Name = name,
                ProjectPath = evaluated.ProjectPath,
                RelativePath = relative,
                AssemblyName = NonEmpty(evaluated.Property("AssemblyName"), Path.GetFileNameWithoutExtension(evaluated.ProjectPath)),
                TargetFramework = candidate.TargetFramework,
                InactiveTargetFrameworks = candidate.InactiveTargetFrameworks,
                Configuration = NonEmpty(evaluated.Property("Configuration"), DefaultConfiguration),
                PreprocessorSymbols = Symbols(evaluated.Property("DefineConstants")),
                LanguageVersion = evaluated.Property("LangVersion") is { Length: > 0 } version
                    ? version
                    : TargetFrameworkName.TryParse(candidate.TargetFramework, out var target2) ? target2.DefaultLanguageVersion : null,
                GlobalUsings = ReadGlobalUsings(candidate),
                InternalsVisibleTo = Friends(evaluated),
                MetadataReferences = ReferenceConflicts.KeepNewest(references.Distinct(PathComparer)),
                MissingReferences = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                AnalyzerPackages = target?.AnalyzerPackages ?? [],
                NoFramework = noFramework,
                FlowingFrameworks = ImpliedFrameworkReferences(evaluated, includePrivate: false),
                Gaps = gaps,
                Remarks = remarks,
                HasRestoreData = candidate.Restore is not null,
                CompileItems = evaluated.CompileItems,
                Inputs = inputs,
            };
        }

        private static string NonEmpty(string value, string fallback) => value.Length > 0 ? value : fallback;

        /// <summary>The SDK writes both of these item forms into a generated attribute.</summary>
        private static List<string> Friends(EvaluatedProject evaluated)
            => evaluated.ItemsOf("InternalsVisibleTo").Select(item => item.Include)
                .Concat(evaluated.ItemsOf("AssemblyAttribute")
                    .Where(item => item.Include.Split('.')[^1] is "InternalsVisibleTo" or "InternalsVisibleToAttribute")
                    .Select(item => item.Get("_Parameter1"))
                    .OfType<string>()
                    .Select(friend => friend.Split(',')[0].Trim()))
                .ToList();

        private FrameworkResolution? ResolveFramework(
            Candidate candidate, RestoreTarget? target, List<string> folders, IEnumerable<string>? inherited = null)
        {
            var evaluated = candidate.Evaluated;

            // Unity-style projects bring their whole framework as hint paths.
            if (evaluated.IsTrue("NoStdLib") || evaluated.IsTrue("NoCompilerStandardLib")) return null;

            if (!TargetFrameworkName.TryParse(candidate.TargetFramework, out var framework))
            {
                return new FrameworkResolution([], false, [], ["The project declares no readable target framework."]);
            }

            var roots = toolchain.DotnetRoots.ToList();
            if (target?.DotnetRoot is { } root && Directory.Exists(root) && !roots.Contains(root, PathComparer))
            {
                roots.Insert(0, root);
            }

            return FrameworkReferenceResolver.Resolve(
                framework,
                target?.FrameworkReferences ?? [.. ImpliedFrameworkReferences(evaluated, includePrivate: true), .. inherited ?? []],
                target?.PackDownloads ?? new Dictionary<string, string>(),
                folders,
                toolchain,
                roots);
        }

        /// <summary>What a restore would have recorded, from what the project and its SDK declare.</summary>
        private static List<string> ImpliedFrameworkReferences(EvaluatedProject evaluated, bool includePrivate)
        {
            var references = evaluated.ItemsOf("FrameworkReference")
                .Where(item => includePrivate || !string.Equals(item.Get("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Include)
                .ToList();
            if (evaluated.SdkNames.Any(sdk => sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)
                    || sdk.Equals("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase)))
            {
                references.Add("Microsoft.AspNetCore.App");
            }
            if (evaluated.IsTrue("UseWPF") || evaluated.IsTrue("UseWindowsForms"))
            {
                references.Add("Microsoft.WindowsDesktop.App");
            }
            return references;
        }

        private static void ResolvePackagesByName(
            Candidate candidate,
            IReadOnlyList<ProjectItem> packages,
            List<string> folders,
            List<string> references,
            List<string> missing)
        {
            var evaluated = candidate.Evaluated;
            TargetFrameworkName.TryParse(candidate.TargetFramework, out var framework);
            var versions = evaluated.ItemsOf("PackageVersion")
                .GroupBy(item => item.Include, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last().Get("Version"), StringComparer.OrdinalIgnoreCase);

            foreach (var package in packages)
            {
                var version = package.Get("VersionOverride") ?? package.Get("Version") ?? versions.GetValueOrDefault(package.Include);
                if (PackageAssemblies(package.Include, version, folders, framework) is { } assemblies)
                {
                    references.AddRange(assemblies);
                }
                else
                {
                    missing.Add(package.Include);
                }
            }
        }

        /// <summary>
        /// The package's assemblies for this framework, or null when the package is not on
        /// this machine. A package with nothing to compile against, such as a build-only
        /// tool, is found and contributes nothing.
        /// </summary>
        private static List<string>? PackageAssemblies(string id, string? version, List<string> folders, TargetFrameworkName? framework)
        {
            foreach (var folder in folders)
            {
                var package = Path.Combine(folder, id.ToLowerInvariant());
                if (!Directory.Exists(package)) continue;

                var directories = Directory.GetDirectories(package)
                    .OrderByDescending(directory => MsBuildExpander.TryVersion(Path.GetFileName(directory), out var parsed) ? parsed : new Version(0, 0))
                    .ToList();
                var chosen = version is null || version.Contains('*') || version.Contains('[') || version.Contains('(')
                    ? directories.FirstOrDefault()
                    : directories.FirstOrDefault(directory => IsSameVersion(Path.GetFileName(directory), version));
                if (chosen is null) continue;

                foreach (var assets in new[] { "ref", "lib" })
                {
                    if (BestAssetDirectory(Path.Combine(chosen, assets), framework) is { } best)
                    {
                        return Directory.GetFiles(best, "*.dll").Order(StringComparer.Ordinal).ToList();
                    }
                }
                return [];
            }
            return null;
        }

        /// <summary>NuGet stores <c>1.0</c> as <c>1.0.0</c>, so the written form may not be the folder's.</summary>
        private static bool IsSameVersion(string folder, string declared)
        {
            if (folder.Equals(declared, StringComparison.OrdinalIgnoreCase)) return true;
            if (folder.Contains('-') || declared.Contains('-')) return false;
            return MsBuildExpander.TryVersion(folder, out var left) && MsBuildExpander.TryVersion(declared, out var right)
                && Normalized(left) == Normalized(right);
        }

        private static Version Normalized(Version version)
            => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

        private static string? BestAssetDirectory(string assets, TargetFrameworkName? framework)
        {
            if (!Directory.Exists(assets)) return null;
            var ranked = new List<(string Directory, TargetFrameworkName Asset)>();
            foreach (var directory in Directory.GetDirectories(assets))
            {
                if (TargetFrameworkName.TryParse(Path.GetFileName(directory), out var asset)
                    && (framework is null || framework.CanConsume(asset)))
                {
                    ranked.Add((directory, asset));
                }
            }
            return ranked
                .OrderBy(entry => entry.Asset.Identifier == framework?.Identifier ? 0 : 1)
                .ThenByDescending(entry => entry.Asset.Version)
                .Select(entry => entry.Directory)
                .FirstOrDefault();
        }

        private static void ResolveAssemblyReferences(
            EvaluatedProject evaluated, string boundary, List<string> references, List<string> missing)
        {
            var directory = Path.GetDirectoryName(evaluated.ProjectPath)!;
            var available = references.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in evaluated.ItemsOf("Reference"))
            {
                var assembly = reference.Include.Split(',')[0].Trim();
                if (reference.Get("HintPath") is { Length: > 0 } hint)
                {
                    var path = Path.GetFullPath(MsBuildExpander.ToNative(hint), directory);
                    if (!MsBuildExpander.IsForeignPath(path, boundary) && File.Exists(path)) references.Add(path);
                    else missing.Add(assembly);
                }
                else if (!available.Contains(assembly))
                {
                    // A bare name is resolved by the framework or by a project in the model;
                    // the second is checked once all projects are known.
                    missing.Add(assembly);
                }
            }
        }

        private static IReadOnlyList<string> Symbols(string constants)
            => constants.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ProjectEvaluator.Unescape)
                .Where(symbol => symbol.Length > 0
                    && (char.IsLetter(symbol[0]) || symbol[0] == '_')
                    && symbol.All(character => char.IsLetterOrDigit(character) || character == '_'))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// The build's own generated file when it is at least as new as the project, since it
        /// is exact, including what the SDK added. Otherwise what the project declares.
        /// </summary>
        private static string ReadGlobalUsings(Candidate candidate)
        {
            var evaluated = candidate.Evaluated;
            var usings = evaluated.ItemsOf("Using");
            if (usings.Count == 0) return string.Empty;

            if (FindGeneratedGlobalUsings(candidate) is { } generated
                && File.GetLastWriteTimeUtc(generated) >= File.GetLastWriteTimeUtc(evaluated.ProjectPath))
            {
                try
                {
                    return File.ReadAllText(generated);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Fall through to what the project declares.
                }
            }

            var text = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in usings)
            {
                var line = item.Get("Alias") is { Length: > 0 } alias
                    ? $"global using {alias} = global::{item.Include};"
                    : item.Get("Static")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
                        ? $"global using static global::{item.Include};"
                        : $"global using global::{item.Include};";
                if (seen.Add(line)) text.AppendLine(line);
            }
            return text.ToString();
        }

        private static string? FindGeneratedGlobalUsings(Candidate candidate)
        {
            var assets = candidate.Restore?.AssetsPath ?? candidate.ExpectedAssetsPath;
            var intermediate = assets is null ? null : Path.GetDirectoryName(assets);
            if (intermediate is null) return null;

            var configuration = candidate.Evaluated.Property("Configuration");
            var directories = new List<string> { Path.Combine(intermediate, configuration) };
            if (candidate.TargetFramework is { } framework)
            {
                directories.Insert(0, Path.Combine(intermediate, configuration, framework));
            }
            return directories
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.GetFiles(directory, "*.GlobalUsings.g.cs"))
                .FirstOrDefault();
        }

        private void LinkProjects(Dictionary<string, ProjectInfo> infos)
        {
            var byAssembly = infos.Values
                .GroupBy(info => info.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var direct = new Dictionary<ProjectInfo, List<ProjectInfo>>();
            foreach (var (path, info) in infos)
            {
                var candidate = _candidates[path];
                var projects = new List<ProjectInfo>();
                var assemblies = info.MetadataReferences.ToList();
                var missing = info.MissingReferences.ToList();

                foreach (var target in DeclaredProjectReferences(candidate))
                {
                    if (infos.TryGetValue(target, out var project))
                    {
                        if (project != info) projects.Add(project);
                    }
                    else if (OutputAssembly(target) is { } output)
                    {
                        assemblies.Add(output);
                    }
                    else
                    {
                        missing.Add(Path.GetFileNameWithoutExtension(target));
                    }
                }

                // Unity and hand-kept solutions refer to a sibling's build output by path.
                foreach (var reference in candidate.Evaluated.ItemsOf("Reference"))
                {
                    var assembly = reference.Include.Split(',')[0].Trim();
                    if (!byAssembly.TryGetValue(assembly, out var project) || project == info) continue;
                    projects.Add(project);
                    assemblies.RemoveAll(file => Path.GetFileNameWithoutExtension(file).Equals(assembly, StringComparison.OrdinalIgnoreCase));
                    missing.RemoveAll(name => name.Equals(assembly, StringComparison.OrdinalIgnoreCase));
                }

                info.MetadataReferences = assemblies.Distinct(PathComparer).ToList();
                info.MissingReferences = missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                direct[info] = projects.Distinct().ToList();
            }

            foreach (var info in infos.Values)
            {
                info.ProjectReferences = Closure(info, direct);
            }
            AddTransitivePackages(infos);
            AddTransitiveFrameworks(infos);
        }

        private IEnumerable<string> DeclaredProjectReferences(Candidate candidate)
        {
            var directory = Path.GetDirectoryName(candidate.Evaluated.ProjectPath)!;
            var items = candidate.Evaluated.ItemsOf("ProjectReference")
                .Select(item => (Item: item, Path: Path.GetFullPath(MsBuildExpander.ToNative(item.Include), directory)))
                .ToList();

            // A reference kept for build order, or to run a generator, adds nothing to compile against.
            var buildOnly = items
                .Where(entry => entry.Item.Get("ReferenceOutputAssembly")?.Equals("false", StringComparison.OrdinalIgnoreCase) == true)
                .Select(entry => entry.Path)
                .ToHashSet(PathComparer);
            var restored = candidate.Restore?.TargetFor(candidate.TargetFramework)?.ProjectReferences ?? [];
            return items.Select(entry => entry.Path).Concat(restored)
                .Where(path => !buildOnly.Contains(path))
                .Distinct(PathComparer);
        }

        /// <summary>A referenced project with no indexed files can still be bound through its build output.</summary>
        private string? OutputAssembly(string project)
        {
            if (MsBuildExpander.IsForeignPath(project, _boundary) || !File.Exists(project)) return null;
            TryLoadCandidate(project);
            if (!_candidates.TryGetValue(project, out var candidate)) return null;

            var evaluated = candidate.Evaluated;
            var directory = Path.GetDirectoryName(project)!;
            var name = NonEmpty(evaluated.Property("AssemblyName"), Path.GetFileNameWithoutExtension(project)) + ".dll";
            var output = NonEmpty(evaluated.Property("BaseOutputPath"), "bin");
            var configuration = evaluated.Property("Configuration");
            var candidates = new List<string>
            {
                Path.Combine(MsBuildExpander.ToNative(output), configuration, candidate.TargetFramework ?? string.Empty, name),
                Path.Combine(MsBuildExpander.ToNative(output), configuration, name),
            };
            return candidates.Select(path => Path.GetFullPath(path, directory))
                .FirstOrDefault(path => !MsBuildExpander.IsForeignPath(path, _boundary) && File.Exists(path));
        }

        private static List<ProjectInfo> Closure(ProjectInfo start, Dictionary<ProjectInfo, List<ProjectInfo>> direct)
        {
            var seen = new HashSet<ProjectInfo> { start };
            var result = new List<ProjectInfo>();
            var pending = new Stack<ProjectInfo>(direct[start]);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (!seen.Add(next)) continue;
                result.Add(next);
                foreach (var reference in direct[next]) pending.Push(reference);
            }
            return result.OrderBy(project => project.ProjectPath, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// With restore data the assets file already lists what arrives through a project
        /// reference. Without it, a project still sees the packages of what it references.
        /// </summary>
        private static void AddTransitivePackages(Dictionary<string, ProjectInfo> infos)
        {
            foreach (var info in infos.Values.Where(info => !info.HasRestoreData))
            {
                var own = info.MetadataReferences.ToList();
                own.AddRange(info.ProjectReferences.SelectMany(project => project.MetadataReferences)
                    .Where(path => path.Contains(".nuget", StringComparison.OrdinalIgnoreCase)));
                info.MetadataReferences = ReferenceConflicts.KeepNewest(own.Distinct(PathComparer));

                // What a reference lacks would have reached this project too.
                var lacking = info.ProjectReferences.Where(project => project.MissingReferences.Count > 0).ToList();
                if (lacking.Count > 0)
                {
                    info.Gaps.Add($"No restore data, and {string.Join(", ", lacking.Select(project => project.Name))} "
                        + "has references that are missing here too.");
                }
            }
        }

        /// <summary>
        /// A shared framework flows through project references as packages do. Each is found
        /// for this project's own target, since the referenced one may target something else.
        /// </summary>
        private void AddTransitiveFrameworks(Dictionary<string, ProjectInfo> infos)
        {
            foreach (var (path, info) in infos.Where(entry => !entry.Value.HasRestoreData))
            {
                var inherited = info.ProjectReferences.SelectMany(project => project.FlowingFrameworks)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (inherited.Count == 0) continue;

                var candidate = _candidates[path];
                if (ResolveFramework(candidate, null, PackageFolders(candidate.Restore), inherited) is not { } framework) continue;

                info.MetadataReferences = ReferenceConflicts.KeepNewest(framework.Assemblies.Concat(info.MetadataReferences).Distinct(PathComparer));
                info.MissingReferences = info.MissingReferences.Union(framework.Missing, StringComparer.OrdinalIgnoreCase).ToList();
                info.Gaps.AddRange(framework.Notes.Except(info.Gaps, StringComparer.Ordinal));
            }
        }

        /// <summary>
        /// Dependencies first. A cycle cannot compile, so the edge closing one is dropped and
        /// reported rather than left to fail further down.
        /// </summary>
        private static List<ProjectInfo> Order(Dictionary<string, ProjectInfo> infos)
        {
            var ordered = new List<ProjectInfo>(infos.Count);
            var finished = new HashSet<ProjectInfo>();
            var active = new HashSet<ProjectInfo>();

            void Visit(ProjectInfo project)
            {
                if (finished.Contains(project)) return;
                active.Add(project);
                var kept = new List<ProjectInfo>(project.ProjectReferences.Count);
                foreach (var reference in project.ProjectReferences)
                {
                    if (active.Contains(reference))
                    {
                        project.Gaps.Add($"Reference to {reference.Name} forms a cycle and was dropped.");
                        continue;
                    }
                    Visit(reference);
                    kept.Add(reference);
                }
                project.ProjectReferences = kept;
                active.Remove(project);
                finished.Add(project);
                ordered.Add(project);
            }

            foreach (var project in infos.Values.OrderBy(info => info.ProjectPath, StringComparer.Ordinal))
            {
                Visit(project);
            }
            return ordered;
        }
    }
}
