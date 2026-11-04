using System.Text.Json;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>What a restore recorded for one target framework of a project.</summary>
public sealed record RestoreTarget(
    string Alias,
    IReadOnlyList<string> FrameworkReferences,
    IReadOnlyDictionary<string, string> PackDownloads,
    string? DotnetRoot,
    IReadOnlyList<string> CompileAssets,
    IReadOnlyList<string> MissingPackages,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> AnalyzerPackages);

/// <summary>
/// A project's <c>project.assets.json</c>. The file names the project it belongs to, so it
/// can be matched wherever a build wrote it. Everything here is data recorded by a restore,
/// including the transitive packages and project references a csproj never mentions.
/// </summary>
public sealed class RestoreData
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private RestoreData(string assetsPath, string? projectPath, IReadOnlyList<string> packageFolders, Dictionary<string, RestoreTarget> targets)
    {
        AssetsPath = assetsPath;
        ProjectPath = projectPath;
        PackageFolders = packageFolders;
        Targets = targets;
    }

    public string AssetsPath { get; }

    /// <summary>The project the restore was for, from the file itself.</summary>
    public string? ProjectPath { get; }

    public IReadOnlyList<string> PackageFolders { get; }

    public IReadOnlyDictionary<string, RestoreTarget> Targets { get; }

    public RestoreTarget? TargetFor(string? moniker)
    {
        if (moniker is null) return Targets.Values.FirstOrDefault();
        if (Targets.TryGetValue(moniker, out var exact)) return exact;
        if (!TargetFrameworkName.TryParse(moniker, out var wanted)) return null;
        foreach (var (alias, target) in Targets)
        {
            if (TargetFrameworkName.TryParse(alias, out var candidate)
                && candidate.Identifier == wanted.Identifier
                && candidate.Version == wanted.Version
                && candidate.Platform == wanted.Platform)
            {
                return target;
            }
        }
        return null;
    }

    /// <summary>Reads an assets file, or returns null when it is not readable as one.</summary>
    public static RestoreData? TryRead(string assetsPath, string? projectPathForMovedRepository = null)
    {
        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var document = JsonDocument.Parse(stream);
            return Read(assetsPath, document.RootElement, projectPathForMovedRepository);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RestoreData Read(string assetsPath, JsonElement root, string? projectPathOverride)
    {
        var packageFolders = new List<string>();
        if (root.TryGetProperty("packageFolders", out var folders) && folders.ValueKind == JsonValueKind.Object)
        {
            packageFolders.AddRange(folders.EnumerateObject().Select(folder => folder.Name));
        }

        string? projectPath = null;
        var hasProject = root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object;
        if (hasProject && project.TryGetProperty("restore", out var restore)
            && restore.TryGetProperty("projectPath", out var path) && path.ValueKind == JsonValueKind.String)
        {
            projectPath = path.GetString();
        }

        var projectDirectory = Path.GetDirectoryName(projectPathOverride ?? projectPath ?? assetsPath)!;
        var libraries = root.TryGetProperty("libraries", out var librariesElement) ? librariesElement : default;
        var targets = new Dictionary<string, RestoreTarget>(StringComparer.OrdinalIgnoreCase);
        if (hasProject && project.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("targets", out var targetsElement) && targetsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                var libraryList = FindTarget(targetsElement, framework.Name);
                targets[framework.Name] = ReadTarget(framework, libraryList, libraries, packageFolders, projectDirectory);
            }
        }
        return new RestoreData(assetsPath, projectPath, packageFolders, targets);
    }

    private static JsonElement? FindTarget(JsonElement targets, string alias)
    {
        if (targets.TryGetProperty(alias, out var exact)) return exact;
        if (!TargetFrameworkName.TryParse(alias, out var wanted)) return null;
        foreach (var target in targets.EnumerateObject())
        {
            // A key with a slash is a runtime-specific graph, which adds nothing to compile against.
            if (!target.Name.Contains('/')
                && TargetFrameworkName.TryParse(target.Name, out var candidate)
                && candidate.Identifier == wanted.Identifier
                && candidate.Version == wanted.Version)
            {
                return target.Value;
            }
        }
        return null;
    }

    private static RestoreTarget ReadTarget(
        JsonProperty framework,
        JsonElement? libraryList,
        JsonElement libraries,
        List<string> packageFolders,
        string projectDirectory)
    {
        var frameworkReferences = new List<string>();
        if (framework.Value.TryGetProperty("frameworkReferences", out var references) && references.ValueKind == JsonValueKind.Object)
        {
            frameworkReferences.AddRange(references.EnumerateObject().Select(reference => reference.Name));
        }

        var downloads = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (framework.Value.TryGetProperty("downloadDependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependencies.EnumerateArray())
            {
                if (dependency.TryGetProperty("name", out var name) && dependency.TryGetProperty("version", out var version)
                    && name.GetString() is { } packName && version.GetString() is { } range)
                {
                    downloads[packName] = range.Trim('[', ']', '(', ')').Split(',')[0].Trim();
                }
            }
        }

        string? dotnetRoot = null;
        if (framework.Value.TryGetProperty("runtimeIdentifierGraphPath", out var graph) && graph.GetString() is { Length: > 0 } graphPath)
        {
            // <root>/sdk/<version>/PortableRuntimeIdentifierGraph.json
            dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(graphPath)));
        }

        var compile = new List<string>();
        var missing = new List<string>();
        var projects = new List<string>();
        var analyzers = new List<string>();
        if (libraryList is { ValueKind: JsonValueKind.Object } list)
        {
            foreach (var library in list.EnumerateObject())
            {
                var kind = library.Value.TryGetProperty("type", out var type) ? type.GetString() : null;
                var entry = libraries.ValueKind == JsonValueKind.Object && libraries.TryGetProperty(library.Name, out var found)
                    ? found
                    : default;
                if (kind == "project")
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && (entry.TryGetProperty("msbuildProject", out var relative) || entry.TryGetProperty("path", out relative))
                        && relative.GetString() is { Length: > 0 } relativePath)
                    {
                        projects.Add(Path.GetFullPath(MsBuildExpander.ToNative(relativePath), projectDirectory));
                    }
                }
                else if (kind == "package")
                {
                    ReadPackage(library, entry, packageFolders, compile, missing, analyzers);
                }
            }
        }

        return new RestoreTarget(framework.Name, frameworkReferences, downloads, dotnetRoot, compile, missing, projects, analyzers);
    }

    private static void ReadPackage(
        JsonProperty library,
        JsonElement entry,
        List<string> packageFolders,
        List<string> compile,
        List<string> missing,
        List<string> analyzers)
    {
        var packagePath = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("path", out var path)
            ? path.GetString()
            : library.Name.ToLowerInvariant();
        if (packagePath is null) return;

        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("files", out var files)
            && files.ValueKind == JsonValueKind.Array
            && files.EnumerateArray().Any(file => file.GetString() is { } text
                && text.StartsWith("analyzers/", StringComparison.OrdinalIgnoreCase)
                && text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            analyzers.Add(library.Name);
        }

        if (!library.Value.TryGetProperty("compile", out var assets) || assets.ValueKind != JsonValueKind.Object) return;
        foreach (var asset in assets.EnumerateObject())
        {
            // "_._" marks a package with nothing to compile against for this framework.
            if (asset.Name.EndsWith("/_._", StringComparison.Ordinal) || asset.Name == "_._") continue;

            var relative = MsBuildExpander.ToNative(asset.Name);
            var located = packageFolders
                .Select(folder => Path.Combine(folder, MsBuildExpander.ToNative(packagePath), relative))
                .FirstOrDefault(File.Exists);
            if (located is not null)
            {
                compile.Add(Path.GetFullPath(located));
            }
            else if (!missing.Contains(library.Name, PathComparer))
            {
                missing.Add(library.Name);
            }
        }
    }
}
