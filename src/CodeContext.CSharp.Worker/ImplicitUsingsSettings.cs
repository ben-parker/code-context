using System.Xml;
using System.Xml.Linq;

namespace CodeContext.CSharp.Worker;

/// <summary>
/// Answers whether a file's project enables <c>ImplicitUsings</c>, by reading the project
/// files in the order MSBuild does: the nearest <c>Directory.Build.props</c>, the nearest
/// <c>.csproj</c> above the file, then the nearest <c>Directory.Build.targets</c>, each in
/// document order and following imports. Nothing outside the workspace root counts, so a
/// file beyond the repository cannot change what the repository means.
///
/// This reads structure; it evaluates nothing. Where that leaves doubt the answer leans on,
/// because the mistakes are not equal: wrongly off loses a whole project's collections and
/// LINQ, wrongly on costs only a name clash, which still records the name. So a condition
/// that enables the setting counts, since a multi-targeted project's sources must compile
/// under the target that has it, while one that disables it does not, for the same reason.
/// A value naming a property counts as on, and so does a file under no project once any
/// project has it, since such a file is usually linked in by a <c>.csproj</c> elsewhere.
/// An import whose path depends on a property other than the file's own directory cannot
/// be followed.
/// </summary>
internal sealed class ImplicitUsingsSettings
{
    private const string Props = "Directory.Build.props";
    private const string Targets = "Directory.Build.targets";
    private const string ThisFileDirectory = "$(MSBuildThisFileDirectory)";

    private static readonly bool IgnoreCase = OperatingSystem.IsWindows();

    private static readonly StringComparison PathComparison =
        IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly XmlReaderSettings ReaderSettings = new() { DtdProcessing = DtdProcessing.Prohibit };

    /// <summary>The workspace root with a trailing separator, or null when it is unknown.</summary>
    private readonly string? _rootPrefix;

    /// <summary>What each directory's project says; null for a directory under no project.</summary>
    private readonly Dictionary<string, bool?> _byDirectory = new(PathComparer);

    /// <summary>The files being read right now, so that an import cycle ends.</summary>
    private readonly HashSet<string> _reading = new(PathComparer);

    private bool _anyProjectEnables;

    public ImplicitUsingsSettings(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        var root = Path.GetFullPath(rootPath);
        _rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Reads the project files as they are now, for a workspace of these source files.
    /// All of them up front, so that the answer for a file under no project does not depend
    /// on which file was asked about first.
    /// </summary>
    public void Refresh(IEnumerable<string> files)
    {
        _byDirectory.Clear();
        _anyProjectEnables = false;
        foreach (var file in files)
        {
            _anyProjectEnables |= ForDirectoryOf(file) == true;
        }
    }

    public bool AppliesTo(string file) => ForDirectoryOf(file) ?? _anyProjectEnables;

    private bool? ForDirectoryOf(string file)
    {
        var directory = Path.GetDirectoryName(file);
        if (string.IsNullOrEmpty(directory)) return null;
        if (!_byDirectory.TryGetValue(directory, out var enabled))
        {
            enabled = ReadForDirectory(directory);
            _byDirectory[directory] = enabled;
        }
        return enabled;
    }

    private bool? ReadForDirectory(string directory)
    {
        for (var current = directory; current is not null; current = Parent(current))
        {
            var projects = ProjectsIn(current);
            if (projects.Length > 0)
            {
                var inherited = ReadNearest(Props, current, value: null);
                return projects.Any(project => ReadNearest(Targets, current, ReadFile(project, inherited)) == true);
            }
        }
        return ReadNearest(Props, directory, value: null) == true ? true : null;
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
    private bool? ReadNearest(string fileName, string directory, bool? value)
    {
        for (string? current = directory; current is not null; current = Parent(current))
        {
            var file = Path.Combine(current, fileName);
            if (File.Exists(file)) return ReadFile(file, value);
        }
        return value;
    }

    /// <summary>The value after this file. An unreadable file changes nothing.</summary>
    private bool? ReadFile(string file, bool? value)
    {
        if (!_reading.Add(file)) return value;
        try
        {
            using var reader = XmlReader.Create(file, ReaderSettings);
            var project = XDocument.Load(reader).Root;
            return project is null ? value : Read(project, file, value, conditional: false, unlessSet: false);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return value;
        }
        finally
        {
            _reading.Remove(file);
        }
    }

    /// <param name="parent">The element whose children to apply, in document order.</param>
    /// <param name="file">The file being read, which relative imports start from.</param>
    /// <param name="value">The value so far; null while nothing has set it.</param>
    /// <param name="conditional">Whether a condition encloses these children.</param>
    /// <param name="unlessSet">Whether that condition tests the setting itself.</param>
    private bool? Read(XElement parent, string file, bool? value, bool conditional, bool unlessSet)
    {
        foreach (var element in parent.Elements())
        {
            var condition = element.Attribute("Condition")?.Value;
            var isConditional = conditional || condition is not null;
            var isUnlessSet = unlessSet
                || condition?.Contains("$(ImplicitUsings)", StringComparison.OrdinalIgnoreCase) == true;

            switch (element.Name.LocalName)
            {
                case "PropertyGroup" or "ImportGroup":
                    value = Read(element, file, value, isConditional, isUnlessSet);
                    break;
                case "Choose" or "When" or "Otherwise":
                    value = Read(element, file, value, conditional: true, isUnlessSet);
                    break;
                case "Import":
                    value = ReadImport(element, file, value);
                    break;
                case "ImplicitUsings" when parent.Name.LocalName == "PropertyGroup":
                    var on = IsOn(element.Value);
                    if (isUnlessSet ? value is null : on || !isConditional) value = on;
                    break;
            }
        }
        return value;
    }

    /// <summary>
    /// The SDK compares the raw value, ignoring case, so padding turns the setting off.
    /// </summary>
    private static bool IsOn(string value)
        => value.Equals("enable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Contains("$(", StringComparison.Ordinal);

    private bool? ReadImport(XElement import, string file, bool? value)
    {
        var project = import.Attribute("Project")?.Value.Trim();
        if (string.IsNullOrEmpty(project)) return value;

        var directory = Path.GetDirectoryName(file)!;
        foreach (var chained in (ReadOnlySpan<string>)[Props, Targets])
        {
            if (project.Contains(chained, StringComparison.OrdinalIgnoreCase))
                return Parent(directory) is { } parent ? ReadNearest(chained, parent, value) : value;
        }

        var path = project.Replace(ThisFileDirectory, directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (path.Contains("$(", StringComparison.Ordinal)) return value;

        path = Path.GetFullPath(path, directory);
        return IsInsideRoot(path) && File.Exists(path) ? ReadFile(path, value) : value;
    }

    /// <summary>The next directory to look in, or null at the workspace root.</summary>
    private string? Parent(string directory)
    {
        var parent = Path.GetDirectoryName(directory);
        if (parent is null) return null;
        return IsInsideRoot(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar)
            ? parent
            : null;
    }

    private bool IsInsideRoot(string path) => _rootPrefix is not null && path.StartsWith(_rootPrefix, PathComparison);
}
