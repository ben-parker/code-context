using System.Xml;
using System.Xml.Linq;

namespace CodeContext.CSharp.Worker;

/// <summary>
/// Answers whether a file's project enables <c>ImplicitUsings</c>, by reading the project
/// files the way MSBuild orders them: the nearest <c>Directory.Build.props</c> first, then
/// the nearest <c>.csproj</c> above the file. Nothing above the workspace root counts, so a
/// file outside the repository cannot change what the repository means.
///
/// This reads structure, not evaluation. Conditions are not evaluated: one that enables the
/// setting counts, because a multi-targeted project's sources must compile under the target
/// that has it, and one that disables it does not, for the same reason. An <c>Import</c> is
/// followed only when it chains to the next <c>Directory.Build.props</c> up.
/// </summary>
internal sealed class ImplicitUsingsSettings
{
    private const string DirectoryBuildProps = "Directory.Build.props";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Prohibit };

    /// <summary>The workspace root with a trailing separator, or null when it is unknown.</summary>
    private readonly string? _rootPrefix;

    private readonly Dictionary<string, bool> _byDirectory =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public ImplicitUsingsSettings(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        var root = Path.GetFullPath(rootPath);
        _rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    /// <summary>Forgets what was read, so the next answer sees the project files as they are now.</summary>
    public void Reset() => _byDirectory.Clear();

    public bool AppliesTo(string file)
    {
        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(directory)) return false;
        if (!_byDirectory.TryGetValue(directory, out var enabled))
        {
            enabled = ReadForDirectory(directory);
            _byDirectory[directory] = enabled;
        }
        return enabled;
    }

    private bool ReadForDirectory(string directory)
    {
        for (var current = directory; current is not null; current = Parent(current))
        {
            var projects = ProjectsIn(current);
            if (projects.Length > 0)
            {
                var inherited = ReadDirectoryBuildProps(current, inherited: false);
                return projects.Any(project => ReadFile(project, inherited));
            }
        }
        return ReadDirectoryBuildProps(directory, inherited: false);
    }

    private static string[] ProjectsIn(string directory)
    {
        try
        {
            return Directory.GetFiles(directory, "*.csproj");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>MSBuild imports only the nearest one; it reaches further by chaining.</summary>
    private bool ReadDirectoryBuildProps(string directory, bool inherited)
    {
        for (string? current = directory; current is not null; current = Parent(current))
        {
            var props = Path.Combine(current, DirectoryBuildProps);
            if (File.Exists(props)) return ReadFile(props, inherited);
        }
        return inherited;
    }

    /// <summary>The value after this file, in document order. An unreadable file changes nothing.</summary>
    private bool ReadFile(string file, bool inherited)
    {
        XElement? project;
        try
        {
            using var reader = XmlReader.Create(file, ReaderSettings);
            project = XDocument.Load(reader).Root;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return inherited;
        }

        var enabled = inherited;
        foreach (var element in project?.Elements() ?? [])
        {
            if (element.Name.LocalName == "Import")
            {
                if (ChainsUpwards(element) && Parent(Path.GetDirectoryName(file)!) is { } parent)
                    enabled = ReadDirectoryBuildProps(parent, enabled);
            }
            else if (element.Name.LocalName == "PropertyGroup")
            {
                foreach (var setting in element.Elements().Where(child => child.Name.LocalName == "ImplicitUsings"))
                {
                    var value = setting.Value.Trim();
                    var on = value.Equals("enable", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    var conditional = element.Attribute("Condition") is not null || setting.Attribute("Condition") is not null;
                    if (on || !conditional) enabled = on;
                }
            }
        }
        return enabled;
    }

    private static bool ChainsUpwards(XElement import)
        => import.Attribute("Project")?.Value.Contains(DirectoryBuildProps, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The next directory to look in, or null at the workspace root.</summary>
    private string? Parent(string directory)
    {
        if (_rootPrefix is null) return null;
        var parent = Path.GetDirectoryName(directory);
        if (parent is null) return null;
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return prefix.StartsWith(_rootPrefix, PathComparison) ? parent : null;
    }
}
