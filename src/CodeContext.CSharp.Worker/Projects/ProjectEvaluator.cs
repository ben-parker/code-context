using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>One item from a project file, with its metadata.</summary>
public sealed record ProjectItem(string Include, IReadOnlyDictionary<string, string> Metadata)
{
    public string? Get(string name) => Metadata.GetValueOrDefault(name);
}

/// <summary>What reading a project file produced.</summary>
public sealed class EvaluatedProject
{
    public required string ProjectPath { get; init; }
    public required IReadOnlyDictionary<string, string> Properties { get; init; }
    public required IReadOnlyDictionary<string, List<ProjectItem>> Items { get; init; }
    public required CompileItemSet CompileItems { get; init; }

    /// <summary>Every file read, so a change to any of them can be noticed.</summary>
    public required IReadOnlyList<string> Inputs { get; init; }

    public required IReadOnlyList<string> SdkNames { get; init; }
    public required bool UsesDotNetSdk { get; init; }
    public required IReadOnlyList<string> MissingSdks { get; init; }
    public required IReadOnlyList<string> MissingImports { get; init; }

    /// <summary>Expressions that could not be read but change nothing the compiler is given.</summary>
    public required IReadOnlyList<string> Uncertainties { get; init; }

    /// <summary>
    /// Conditions, and expressions deciding what the compiler is given, that could not be read.
    /// The project's context is a guess wherever one of these applies.
    /// </summary>
    public required IReadOnlyList<string> CompilerUncertainties { get; init; }

    public string Property(string name) => Properties.GetValueOrDefault(name, string.Empty);

    public IReadOnlyList<ProjectItem> ItemsOf(string type)
        => Items.TryGetValue(type, out var items) ? items : [];

    public bool IsTrue(string name) => Property(name).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public bool IsFalse(string name) => Property(name).Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads a project file in MSBuild's order: properties and imports top to bottom, then items
/// with the final property values. It runs no tasks or targets and loads no assembly.
///
/// The Microsoft SDK is built in rather than read, because its files are logic for a real
/// evaluator: its effects here are the defaults a project can see. Any other SDK is plain
/// data in a package folder, so its <c>Sdk.props</c> and <c>Sdk.targets</c> go through this
/// same reader, and whatever constants or paths they declare arrive without knowing the SDK.
/// </summary>
public sealed class ProjectEvaluator
{
    private const int MaxImportDepth = 32;
    private const string DirectoryBuildProps = "Directory.Build.props";
    private const string DirectoryBuildTargets = "Directory.Build.targets";
    private const string DirectoryPackagesProps = "Directory.Packages.props";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] BaseImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
        "System.Threading", "System.Threading.Tasks",
    ];

    /// <summary>The usings an SDK adds to the base set. Read from the SDK's own defaults.</summary>
    private static readonly Dictionary<string, string[]> SdkImplicitUsings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.NET.Sdk.Web"] =
        [
            "System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Http", "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
        ],
        ["Microsoft.NET.Sdk.Worker"] =
        [
            "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging",
        ],
    };

    private readonly Disk _disk;
    private readonly string _projectPath;
    private readonly string _projectDirectory;
    private readonly string _boundary;
    private readonly IReadOnlyList<string> _packageFolders;
    private readonly IReadOnlyDictionary<string, string> _globals;
    private readonly string? _solutionDirectory;

    private readonly Dictionary<string, string> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ProjectItem>> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly CompileItemSet _compileItems = new();
    private readonly List<Action> _itemSteps = [];
    private readonly HashSet<string> _imported = new(PathComparer);
    private readonly List<string> _inputs = [];
    private readonly List<string> _sdkNames = [];
    private readonly List<string> _missingSdks = [];
    private readonly List<string> _missingImports = [];
    private static readonly HashSet<string> CompilerProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "AssemblyName", "DefineConstants", "EnableDefaultCompileItems", "ImplicitUsings",
        "LangVersion", "TargetFramework", "TargetFrameworks",
    };

    private static readonly HashSet<string> CompilerItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Compile", "FrameworkReference", "PackageReference", "PackageVersion", "ProjectReference", "Reference", "Using",
    };

    private readonly List<string> _uncertainties = [];
    private readonly List<string> _compilerUncertainties = [];

    private bool _dotNetSdkPropsDone;
    private bool _dotNetSdkTargetsDone;
    private int _depth;
    private Dictionary<string, string>? _globalJsonSdks;

    private ProjectEvaluator(
        Disk disk,
        string projectPath,
        string boundary,
        IReadOnlyList<string> packageFolders,
        IReadOnlyDictionary<string, string> globals,
        string? solutionDirectory)
    {
        _disk = disk;
        _projectPath = projectPath;
        _projectDirectory = Path.GetDirectoryName(projectPath)!;
        _boundary = boundary;
        _packageFolders = packageFolders;
        _globals = globals;
        _solutionDirectory = solutionDirectory;
    }

    /// <param name="disk">The way to the file system.</param>
    /// <param name="projectPath">Full path of the project file.</param>
    /// <param name="boundary">The highest directory searched for <c>Directory.Build.*</c>.</param>
    /// <param name="packageFolders">Where a third-party SDK may be found.</param>
    /// <param name="globals">Properties a project cannot override, as a build's command line sets.</param>
    /// <param name="solutionDirectory">Directory of the solution, when there is one.</param>
    /// <exception cref="XmlException">The project file is not well formed.</exception>
    /// <exception cref="IOException">The project file cannot be read.</exception>
    public static EvaluatedProject Evaluate(
        Disk disk,
        string projectPath,
        string boundary,
        IReadOnlyList<string> packageFolders,
        IReadOnlyDictionary<string, string> globals,
        string? solutionDirectory = null)
        => new ProjectEvaluator(disk, projectPath, boundary, packageFolders, globals, solutionDirectory).Run();

    private EvaluatedProject Run()
    {
        var root = Load(_projectPath);
        _imported.Add(_projectPath);

        var sdks = ReadProjectSdks(root);
        foreach (var (name, version) in sdks)
        {
            ImportSdk(name, version, "Sdk.props", _projectPath);
        }
        Process(root, _projectPath);
        foreach (var (name, version) in Enumerable.Reverse(sdks))
        {
            ImportSdk(name, version, "Sdk.targets", _projectPath);
        }

        foreach (var step in _itemSteps)
        {
            step();
        }
        foreach (var (name, value) in _globals)
        {
            _properties[name] = value;
        }

        return new EvaluatedProject
        {
            ProjectPath = _projectPath,
            Properties = _properties,
            Items = _items,
            CompileItems = _compileItems,
            Inputs = _inputs,
            SdkNames = _sdkNames,
            UsesDotNetSdk = _dotNetSdkPropsDone,
            MissingSdks = _missingSdks,
            MissingImports = _missingImports,
            Uncertainties = _uncertainties,
            CompilerUncertainties = _compilerUncertainties,
        };
    }

    private XElement Load(string file)
    {
        if (!_inputs.Contains(file, PathComparer)) _inputs.Add(file);
        return _disk.LoadXml(file).Root
            ?? throw new XmlException($"'{file}' has no root element.");
    }

    private static List<(string Name, string? Version)> ReadProjectSdks(XElement root)
    {
        var sdks = new List<(string, string?)>();
        foreach (var part in (root.Attribute("Sdk")?.Value ?? string.Empty)
                     .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            sdks.Add(slash < 0 ? (part, null) : (part[..slash], part[(slash + 1)..]));
        }
        foreach (var element in root.Elements().Where(child => child.Name.LocalName == "Sdk"))
        {
            if (element.Attribute("Name")?.Value is { Length: > 0 } name)
            {
                sdks.Add((name, element.Attribute("Version")?.Value));
            }
        }
        return sdks;
    }

    private void Process(XElement parent, string file)
    {
        foreach (var element in parent.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    if (Passes(element, file)) ProcessProperties(element, file);
                    break;
                case "ItemGroup":
                    QueueItems(element, file);
                    break;
                case "Import":
                    ProcessImport(element, file);
                    break;
                case "ImportGroup":
                    if (Passes(element, file)) Process(element, file);
                    break;
                case "Choose":
                    ProcessChoose(element, file);
                    break;
            }
        }
    }

    private void ProcessChoose(XElement choose, string file)
    {
        foreach (var branch in choose.Elements())
        {
            if (branch.Name.LocalName == "When" && Passes(branch, file))
            {
                Process(branch, file);
                return;
            }
            if (branch.Name.LocalName == "Otherwise")
            {
                Process(branch, file);
                return;
            }
        }
    }

    private void ProcessProperties(XElement group, string file)
    {
        foreach (var property in group.Elements())
        {
            if (!Passes(property, file)) continue;
            var expander = Expander(file);
            var value = expander.Expand(property.Value);
            if (expander.Uncertain) NoteUncertain(property.Value, CompilerProperties.Contains(property.Name.LocalName));
            SetProperty(property.Name.LocalName, value);
        }
    }

    private void SetProperty(string name, string value)
    {
        if (!_globals.ContainsKey(name)) _properties[name] = value;
    }

    private void SetDefault(string name, string value)
    {
        if (string.IsNullOrEmpty(Get(name))) SetProperty(name, value);
    }

    private string? Get(string name) => Lookup(name, _projectPath);

    private string? Lookup(string name, string file)
    {
        if (_globals.TryGetValue(name, out var global)) return global;
        if (_properties.TryGetValue(name, out var value)) return value;

        var separator = Path.DirectorySeparatorChar;
        switch (name.ToLowerInvariant())
        {
            case "msbuildprojectfullpath": return _projectPath;
            case "msbuildprojectdirectory": return _projectDirectory;
            case "msbuildprojectname": return Path.GetFileNameWithoutExtension(_projectPath);
            case "msbuildprojectfile": return Path.GetFileName(_projectPath);
            case "msbuildprojectextension": return Path.GetExtension(_projectPath);
            case "projectdir": return _projectDirectory + separator;
            case "msbuildthisfile": return Path.GetFileName(file);
            case "msbuildthisfilename": return Path.GetFileNameWithoutExtension(file);
            case "msbuildthisfileextension": return Path.GetExtension(file);
            case "msbuildthisfilefullpath": return file;
            case "msbuildthisfiledirectory": return Path.GetDirectoryName(file) + separator;
            case "solutiondir": return _solutionDirectory is null ? null : _solutionDirectory + separator;
            case "os": return OperatingSystem.IsWindows() ? "Windows_NT" : "Unix";
            case "msbuildruntimetype": return "Core";
            case "msbuildversion": return "18.0.0";
            case "msbuildassemblyversion": return "18.0";
            case "msbuildtoolsversion": return "Current";
            case "nugetpackageroot":
                return _packageFolders.Count > 0 ? _packageFolders[0].TrimEnd(separator) + separator : null;
            default:
                return Environment.GetEnvironmentVariable(name);
        }
    }

    private MsBuildExpander Expander(string file)
        => new(_disk, name => Lookup(name, file), Path.GetDirectoryName(file)!, _boundary);

    private bool Passes(XElement element, string file)
    {
        var condition = element.Attribute("Condition")?.Value;
        if (condition is null) return true;
        if (MsBuildCondition.Evaluate(condition, Expander(file)) is { } result) return result;
        NoteUncertain(condition, reachesCompiler: true);
        return true;
    }

    private void NoteUncertain(string text, bool reachesCompiler)
        => (reachesCompiler ? _compilerUncertainties : _uncertainties).Add(text.Trim());

    private void ProcessImport(XElement import, string file)
    {
        var project = import.Attribute("Project")?.Value.Trim() ?? string.Empty;

        // Old-style projects reach their defaults through these two, conditioned on paths
        // that only a real toolset has, so they are recognized before the condition is read.
        if (project.EndsWith("Microsoft.Common.props", StringComparison.OrdinalIgnoreCase))
        {
            ImportNearest(DirectoryBuildProps);
            return;
        }
        if (project.EndsWith("Microsoft.CSharp.targets", StringComparison.OrdinalIgnoreCase)
            || project.EndsWith("Microsoft.Common.targets", StringComparison.OrdinalIgnoreCase))
        {
            ImportNearest(DirectoryBuildTargets);
            return;
        }

        if (!Passes(import, file)) return;

        if (import.Attribute("Sdk")?.Value is { Length: > 0 } sdk)
        {
            ImportSdk(sdk, import.Attribute("Version")?.Value, project.Length > 0 ? project : "Sdk.props", file);
            return;
        }
        if (project.Length == 0) return;

        var expander = Expander(file);
        var expanded = expander.Expand(project);
        if (expander.Uncertain || expanded.Length == 0)
        {
            NoteMissingImport(project);
            return;
        }

        var path = expander.FullPath(expanded);
        if (path.Contains('*') || path.Contains('?'))
        {
            foreach (var match in ExpandImportWildcard(path)) ImportFile(match);
            return;
        }
        if (_disk.FileExists(path))
        {
            ImportFile(path);
        }
        else if (import.Attribute("Condition") is null)
        {
            NoteMissingImport(project);
        }
    }

    /// <summary>An import from the toolset itself says nothing about the project's references.</summary>
    private void NoteMissingImport(string project)
    {
        if (project.Contains("$(MSBuild", StringComparison.OrdinalIgnoreCase)
            || project.Contains("$(VSToolsPath", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _missingImports.Add(project);
    }

    private IEnumerable<string> ExpandImportWildcard(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null || directory.Contains('*') || directory.Contains('?') || !_disk.DirectoryExists(directory))
        {
            return [];
        }
        return _disk.Files(directory, Path.GetFileName(path)).Order(StringComparer.Ordinal);
    }

    private void ImportFile(string path)
    {
        if (!_imported.Add(path) || _depth >= MaxImportDepth) return;
        _depth++;
        try
        {
            Process(Load(path), path);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            _missingImports.Add(path);
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>
    /// The nearest such file above the project, as MSBuild finds it. The search stops at the
    /// boundary, so nothing outside the repository changes what the repository means.
    /// </summary>
    private void ImportNearest(string fileName)
    {
        if (FindAbove(fileName) is { } found) ImportFile(found);
    }

    private string? FindAbove(string fileName)
    {
        for (string? directory = _projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, fileName);

            // A file created here later changes what this project means.
            _inputs.Add(candidate);
            if (_disk.FileExists(candidate)) return candidate;
            if (MsBuildExpander.SamePath(directory, _boundary)) break;
        }
        return null;
    }

    private void ImportSdk(string name, string? version, string file, string importingFile)
    {
        var slash = name.IndexOf('/');
        if (slash >= 0)
        {
            version ??= name[(slash + 1)..];
            name = name[..slash];
        }
        if (!_sdkNames.Contains(name, StringComparer.OrdinalIgnoreCase)) _sdkNames.Add(name);

        if (IsDotNetSdk(name))
        {
            RunDotNetSdk(file);
            return;
        }

        if (ResolveSdkDirectory(name, version) is not { } directory)
        {
            // Unknown SDK: the conventions of the common one are the best remaining guess,
            // and the project is reported as not fully resolved.
            if (!_missingSdks.Contains(name, StringComparer.OrdinalIgnoreCase)) _missingSdks.Add(name);
            RunDotNetSdk(file);
            return;
        }

        var path = Path.Combine(directory, MsBuildExpander.ToNative(file));
        if (_disk.FileExists(path)) ImportFile(path);
    }

    private static bool IsDotNetSdk(string name)
        => name.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.NET.Sdk.", StringComparison.OrdinalIgnoreCase);

    private string? ResolveSdkDirectory(string name, string? version)
    {
        version ??= GlobalJsonSdks().GetValueOrDefault(name);
        var lower = name.ToLowerInvariant();
        foreach (var folder in _packageFolders)
        {
            var package = Path.Combine(folder, lower);
            if (!_disk.DirectoryExists(package)) continue;
            if (version is not null)
            {
                var exact = Path.Combine(package, version.ToLowerInvariant(), "Sdk");
                if (_disk.DirectoryExists(exact)) return exact;
                continue;
            }
            var newest = _disk.Directories(package)
                .Where(candidate => _disk.DirectoryExists(Path.Combine(candidate, "Sdk")))
                .OrderByDescending(candidate => MsBuildExpander.TryVersion(Path.GetFileName(candidate), out var parsed)
                    ? parsed
                    : new Version(0, 0))
                .FirstOrDefault();
            if (newest is not null) return Path.Combine(newest, "Sdk");
        }
        return null;
    }

    private Dictionary<string, string> GlobalJsonSdks()
    {
        if (_globalJsonSdks is not null) return _globalJsonSdks;
        _globalJsonSdks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (FindAbove("global.json") is not { } file) return _globalJsonSdks;
        try
        {
            _inputs.Add(file);
            using var document = JsonDocument.Parse(
                _disk.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.TryGetProperty("msbuild-sdks", out var sdks) && sdks.ValueKind == JsonValueKind.Object)
            {
                foreach (var sdk in sdks.EnumerateObject())
                {
                    if (sdk.Value.ValueKind == JsonValueKind.String) _globalJsonSdks[sdk.Name] = sdk.Value.GetString()!;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken global.json only costs the version pin.
        }
        return _globalJsonSdks;
    }

    private void RunDotNetSdk(string file)
    {
        if (file.EndsWith("Sdk.props", StringComparison.OrdinalIgnoreCase)) RunDotNetSdkProps();
        else if (file.EndsWith("Sdk.targets", StringComparison.OrdinalIgnoreCase)) RunDotNetSdkTargets();
    }

    private void RunDotNetSdkProps()
    {
        if (_dotNetSdkPropsDone) return;
        _dotNetSdkPropsDone = true;

        ImportNearest(DirectoryBuildProps);
        ImportNearest(DirectoryPackagesProps);

        SetProperty("UsingMicrosoftNETSdk", "true");
        SetDefault("Platform", "AnyCPU");
        SetDefault("BaseOutputPath", "bin" + Path.DirectorySeparatorChar);
        SetDefault("BaseIntermediateOutputPath", "obj" + Path.DirectorySeparatorChar);
        SetDefault("MSBuildProjectExtensionsPath", Get("BaseIntermediateOutputPath") ?? string.Empty);
        SetDefault("DefineConstants", "TRACE");
        _itemSteps.Add(AddDefaultCompileItems);
        _itemSteps.Add(AddImplicitUsings);
    }

    private void RunDotNetSdkTargets()
    {
        if (_dotNetSdkTargetsDone) return;
        _dotNetSdkTargetsDone = true;

        SetDefault("AssemblyName", Path.GetFileNameWithoutExtension(_projectPath));
        SetDefault("RootNamespace", Get("AssemblyName") ?? string.Empty);

        var constants = new List<string>();
        if (Get("DefineConstants") is { Length: > 0 } declared) constants.Add(declared);
        if (!IsSet("DisableImplicitConfigurationDefines") && Get("Configuration") is { Length: > 0 } configuration)
        {
            constants.Add(ConfigurationConstant(configuration));
        }
        if (!IsSet("DisableImplicitFrameworkDefines")
            && TargetFrameworkName.TryParse(Get("TargetFramework"), out var framework))
        {
            SetProperty("TargetFrameworkIdentifier", framework.Identifier);
            SetProperty("TargetFrameworkVersion", "v" + framework.VersionText());
            constants.AddRange(framework.PreprocessorSymbols());
        }
        SetProperty("DefineConstants", string.Join(';', constants));

        ImportNearest(DirectoryBuildTargets);
    }

    private bool IsSet(string name) => (Get(name) ?? string.Empty).Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string ConfigurationConstant(string configuration)
    {
        var text = new StringBuilder(configuration.Length);
        foreach (var character in configuration.ToUpperInvariant())
        {
            text.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        return text.ToString();
    }

    private void AddDefaultCompileItems()
    {
        if ((Get("EnableDefaultItems") ?? "true").Equals("false", StringComparison.OrdinalIgnoreCase)
            || (Get("EnableDefaultCompileItems") ?? "true").Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expander = Expander(_projectPath);
        var excludes = new List<string> { "**/.*/**" };
        foreach (var path in new[] { Get("BaseOutputPath"), Get("BaseIntermediateOutputPath") })
        {
            if (!string.IsNullOrEmpty(path)) excludes.Add(MsBuildExpander.ToNative(path).TrimEnd(Path.DirectorySeparatorChar) + "/**");
        }
        excludes.AddRange(SplitList(Get("DefaultItemExcludes")));
        excludes.AddRange(SplitList(Get("DefaultExcludesInProjectFolder")));

        _compileItems.Include(
            [expander.FullPath("**/*.cs")],
            excludes.Where(IsUsablePath).Select(expander.FullPath));
    }

    private void AddImplicitUsings()
    {
        // The SDK compares the raw value, ignoring case, so padding turns the setting off.
        var value = Get("ImplicitUsings") ?? string.Empty;
        if (!value.Equals("enable", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var implied = BaseImplicitUsings.AsEnumerable();
        foreach (var sdk in _sdkNames)
        {
            if (SdkImplicitUsings.TryGetValue(sdk, out var additions)) implied = implied.Concat(additions);
        }
        var usings = ItemList("Using");
        foreach (var name in implied)
        {
            usings.Add(new ProjectItem(name, new Dictionary<string, string>()));
        }
    }

    private List<ProjectItem> ItemList(string type)
    {
        if (!_items.TryGetValue(type, out var items))
        {
            _items[type] = items = [];
        }
        return items;
    }

    private void QueueItems(XElement group, string file)
    {
        _itemSteps.Add(() =>
        {
            if (!Passes(group, file)) return;
            foreach (var item in group.Elements())
            {
                if (Passes(item, file)) EvaluateItem(item, file);
            }
        });
    }

    private void EvaluateItem(XElement item, string file)
    {
        var expander = Expander(file);
        var type = item.Name.LocalName;
        var include = ExpandList(item.Attribute("Include")?.Value, expander);
        var remove = ExpandList(item.Attribute("Remove")?.Value, expander);
        if (expander.Uncertain) NoteUncertain(item.ToString(), CompilerItems.Contains(type));

        if (type == "Compile")
        {
            // Item paths are relative to the project, wherever the import that wrote them lives.
            var projectExpander = Expander(_projectPath);
            if (remove.Exists(path => path.Equals("@(Compile)", StringComparison.OrdinalIgnoreCase)))
            {
                _compileItems.Clear();
                remove.RemoveAll(path => path.Equals("@(Compile)", StringComparison.OrdinalIgnoreCase));
            }
            if (include.Concat(remove).Any(path => !IsUsablePath(path))) NoteUncertain(item.ToString(), reachesCompiler: true);
            if (include.Count > 0)
            {
                var exclude = ExpandList(item.Attribute("Exclude")?.Value, expander);
                _compileItems.Include(
                    include.Where(IsUsablePath).Select(projectExpander.FullPath),
                    exclude.Where(IsUsablePath).Select(projectExpander.FullPath));
            }
            if (remove.Count > 0) _compileItems.Remove(remove.Where(IsUsablePath).Select(projectExpander.FullPath));
            return;
        }

        if (remove.Count > 0)
        {
            ItemList(type).RemoveAll(existing => remove.Contains(existing.Include, StringComparer.OrdinalIgnoreCase));
        }
        if (include.Count == 0) return;

        var metadata = ReadMetadata(item, file);
        var items = ItemList(type);
        foreach (var value in include)
        {
            items.Add(new ProjectItem(value, metadata));
        }
    }

    private Dictionary<string, string> ReadMetadata(XElement item, string file)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expander = Expander(file);
        foreach (var attribute in item.Attributes())
        {
            if (attribute.Name.LocalName is not ("Include" or "Exclude" or "Remove" or "Update" or "Condition"))
            {
                metadata[attribute.Name.LocalName] = expander.Expand(attribute.Value);
            }
        }
        foreach (var child in item.Elements())
        {
            if (Passes(child, file)) metadata[child.Name.LocalName] = expander.Expand(child.Value.Trim());
        }
        return metadata;
    }

    /// <summary>An item reference such as <c>@(Other)</c> has no value here.</summary>
    private static bool IsUsablePath(string path) => !path.Contains("@(", StringComparison.Ordinal);

    private static List<string> ExpandList(string? text, MsBuildExpander expander)
        => string.IsNullOrWhiteSpace(text) ? [] : SplitList(expander.Expand(text));

    private static List<string> SplitList(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Unescape)
                .ToList();

    /// <summary>Project text escapes reserved characters as <c>%XX</c>.</summary>
    internal static string Unescape(string text)
    {
        if (!text.Contains('%')) return text;
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && i + 2 < text.Length
                && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                result.Append((char)Convert.ToInt32(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                result.Append(text[i]);
            }
        }
        return result.ToString();
    }
}
