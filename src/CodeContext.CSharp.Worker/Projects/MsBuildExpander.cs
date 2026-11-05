using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// Expands <c>$(...)</c> in project file text. Only pure functions are understood: string
/// methods, path arithmetic, version and framework comparisons. Anything else makes the
/// result empty and sets <see cref="Uncertain"/>, because this reads a project and must
/// never run anything a project names.
/// </summary>
public sealed class MsBuildExpander(Func<string, string?> property, string directory, string? boundary = null)
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The directory relative paths resolve against.</summary>
    public string Directory { get; } = directory;

    /// <summary>Set when an expansion met something it could not read. Callers reset it.</summary>
    public bool Uncertain { get; set; }

    public string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!text.Contains("$(", StringComparison.Ordinal)) return text;

        var result = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var start = text.IndexOf("$(", position, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(text, position, text.Length - position);
                break;
            }
            result.Append(text, position, start - position);
            var end = FindClosingParenthesis(text, start + 1);
            if (end < 0)
            {
                result.Append(text, start, text.Length - start);
                break;
            }
            result.Append(Evaluate(text[(start + 2)..end]));
            position = end + 1;
        }
        return result.ToString();
    }

    /// <summary>The index of the parenthesis closing the one at <paramref name="open"/>, or -1.</summary>
    internal static int FindClosingParenthesis(string text, int open)
    {
        var depth = 0;
        char? quote = null;
        for (var i = open; i < text.Length; i++)
        {
            var character = text[i];
            if (quote is not null)
            {
                if (character == quote) quote = null;
            }
            else if (character is '\'' or '"' or '`')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && --depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    private string Evaluate(string body)
    {
        body = body.Trim();
        if (body.Length == 0) return string.Empty;

        string value;
        int position;
        if (body[0] == '[')
        {
            var close = body.IndexOf(']');
            if (close < 0 || !body.AsSpan(close + 1).StartsWith("::")) return Unknown();
            var type = body[1..close].Trim();
            position = close + 3;
            var (member, arguments) = ReadMember(body, ref position);
            if (member is null) return Unknown();
            value = CallStatic(type, member, arguments);
        }
        else
        {
            position = 0;
            while (position < body.Length && (char.IsLetterOrDigit(body[position]) || body[position] is '_' or '-'))
            {
                position++;
            }
            if (position == 0) return Unknown();
            value = property(body[..position]) ?? string.Empty;
        }

        while (position < body.Length)
        {
            if (body[position] != '.') return Unknown();
            position++;
            var (member, arguments) = ReadMember(body, ref position);
            if (member is null) return Unknown();
            value = CallInstance(value, member, arguments);
        }
        return value;
    }

    private (string? Member, List<string> Arguments) ReadMember(string body, ref int position)
    {
        var start = position;
        while (position < body.Length && (char.IsLetterOrDigit(body[position]) || body[position] == '_')) position++;
        if (position == start) return (null, []);
        var member = body[start..position];
        if (position >= body.Length || body[position] != '(') return (member, []);

        var close = FindClosingParenthesis(body, position);
        if (close < 0) return (null, []);
        var arguments = SplitArguments(body[(position + 1)..close]).Select(ReadArgument).ToList();
        position = close + 1;
        return (member, arguments);
    }

    private string ReadArgument(string argument)
    {
        argument = argument.Trim();
        if (argument.Length >= 2 && argument[0] is '\'' or '"' or '`' && argument[^1] == argument[0])
        {
            argument = argument[1..^1];
        }
        return Expand(argument);
    }

    private static IEnumerable<string> SplitArguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (quote is not null)
            {
                if (character == quote) quote = null;
            }
            else if (character is '\'' or '"' or '`')
            {
                quote = character;
            }
            else if (character == '(')
            {
                depth++;
            }
            else if (character == ')')
            {
                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }
        yield return text[start..];
    }

    private string CallInstance(string value, string member, List<string> a)
    {
        var count = a.Count;
        switch (member.ToLowerInvariant())
        {
            case "contains" when count == 1: return Bool(value.Contains(a[0], StringComparison.Ordinal));
            case "startswith" when count >= 1: return Bool(value.StartsWith(a[0], StringComparison.Ordinal));
            case "endswith" when count >= 1: return Bool(value.EndsWith(a[0], StringComparison.Ordinal));
            case "equals" when count == 1: return Bool(value.Equals(a[0], StringComparison.Ordinal));
            case "replace" when count == 2 && a[0].Length > 0: return value.Replace(a[0], a[1], StringComparison.Ordinal);
            case "tolower" or "tolowerinvariant": return value.ToLowerInvariant();
            case "toupper" or "toupperinvariant": return value.ToUpperInvariant();
            case "trim" when count == 0: return value.Trim();
            case "trim" when count == 1: return value.Trim(a[0].ToCharArray());
            case "trimend" when count == 0: return value.TrimEnd();
            case "trimend" when count == 1: return value.TrimEnd(a[0].ToCharArray());
            case "trimstart" when count == 0: return value.TrimStart();
            case "trimstart" when count == 1: return value.TrimStart(a[0].ToCharArray());
            case "length" when count == 0: return value.Length.ToString(CultureInfo.InvariantCulture);
            case "indexof" when count == 1:
                return value.IndexOf(a[0], StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture);
            case "lastindexof" when count == 1:
                return value.LastIndexOf(a[0], StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture);
            case "substring" when count >= 1 && int.TryParse(a[0], out var start) && start >= 0 && start <= value.Length:
                if (count == 1) return value[start..];
                return int.TryParse(a[1], out var length) && length >= 0 && start + length <= value.Length
                    ? value.Substring(start, length)
                    : Unknown();
            default:
                return Unknown();
        }
    }

    private string CallStatic(string type, string member, List<string> a)
    {
        var name = member.ToLowerInvariant();
        return type.ToLowerInvariant() switch
        {
            "msbuild" => CallMsBuild(name, a),
            "system.io.path" => CallPath(name, a),
            "system.string" or "string" => CallString(name, a),
            "system.io.file" when name == "exists" && a.Count == 1 => Bool(!IsForeign(FullPath(a[0])) && File.Exists(FullPath(a[0]))),
            "system.io.directory" when name == "exists" && a.Count == 1 => Bool(!IsForeign(FullPath(a[0])) && System.IO.Directory.Exists(FullPath(a[0]))),
            "system.environment" => CallEnvironment(name, a),
            "system.operatingsystem" => name switch
            {
                "iswindows" => Bool(OperatingSystem.IsWindows()),
                "islinux" => Bool(OperatingSystem.IsLinux()),
                "ismacos" => Bool(OperatingSystem.IsMacOS()),
                "isfreebsd" => Bool(OperatingSystem.IsFreeBSD()),
                _ => Unknown(),
            },
            "system.runtime.interopservices.runtimeinformation" when name == "isosplatform" && a.Count == 1
                => Bool(IsOsPlatform(a[0].Split('.')[^1])),
            _ => Unknown(),
        };
    }

    private string CallMsBuild(string name, List<string> a)
    {
        var count = a.Count;
        switch (name)
        {
            case "isosplatform" when count == 1: return Bool(IsOsPlatform(a[0]));
            case "isosunixlike": return Bool(!OperatingSystem.IsWindows());
            case "ensuretrailingslash" when count == 1: return EnsureTrailingSlash(a[0]);
            case "normalizepath" when count >= 1: return FullPath(Path.Combine(a.Select(ToNative).ToArray()));
            case "normalizedirectory" when count >= 1:
                return EnsureTrailingSlash(FullPath(Path.Combine(a.Select(ToNative).ToArray())));
            case "getpathoffileabove" when count >= 1: return FileAbove(a[0], count > 1 ? a[1] : null) ?? string.Empty;
            case "getdirectorynameoffileabove" when count == 2:
                return FileAbove(a[1], a[0]) is { } found ? Path.GetDirectoryName(found)! : string.Empty;
            case "makerelative" when count == 2: return Path.GetRelativePath(FullPath(a[0]), FullPath(a[1]));
            case "valueordefault" when count == 2: return a[0].Length > 0 ? a[0] : a[1];
            case "converttobase64" when count == 1: return Convert.ToBase64String(Encoding.UTF8.GetBytes(a[0]));
            case "add" or "subtract" or "multiply" or "divide" or "modulo" when count == 2: return Arithmetic(name, a);
            case "versionequals" when count == 2: return CompareVersions(a, order => order == 0);
            case "versionnotequals" when count == 2: return CompareVersions(a, order => order != 0);
            case "versiongreaterthan" when count == 2: return CompareVersions(a, order => order > 0);
            case "versiongreaterthanorequals" when count == 2: return CompareVersions(a, order => order >= 0);
            case "versionlessthan" when count == 2: return CompareVersions(a, order => order < 0);
            case "versionlessthanorequals" when count == 2: return CompareVersions(a, order => order <= 0);
            case "gettargetframeworkidentifier" when count == 1:
                return TargetFrameworkName.TryParse(a[0], out var identified) ? identified.Identifier : Unknown();
            case "gettargetframeworkversion" when count >= 1:
                return TargetFrameworkName.TryParse(a[0], out var versioned)
                    ? versioned.VersionText(count > 1 && int.TryParse(a[1], out var parts) ? parts : 2)
                    : Unknown();
            case "gettargetplatformidentifier" when count == 1:
                return TargetFrameworkName.TryParse(a[0], out var platformed) ? platformed.Platform ?? string.Empty : Unknown();
            case "istargetframeworkcompatible" when count == 2:
                return TargetFrameworkName.TryParse(a[0], out var target) && TargetFrameworkName.TryParse(a[1], out var candidate)
                    ? Bool(target.CanConsume(candidate))
                    : Unknown();
            default:
                return Unknown();
        }
    }

    private string Arithmetic(string name, List<string> a)
    {
        if (!double.TryParse(a[0], CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(a[1], CultureInfo.InvariantCulture, out var right))
        {
            return Unknown();
        }
        var value = name switch
        {
            "add" => left + right,
            "subtract" => left - right,
            "multiply" => left * right,
            "divide" => left / right,
            _ => left % right,
        };
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private string CompareVersions(List<string> a, Func<int, bool> test)
        => TryVersion(a[0], out var left) && TryVersion(a[1], out var right)
            ? Bool(test(left.CompareTo(right)))
            : Unknown();

    internal static bool TryVersion(string text, out Version version)
    {
        text = text.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(['-', '+']);
        if (end >= 0) text = text[..end];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out version!);
    }

    private string CallPath(string name, List<string> a)
    {
        var count = a.Count;
        switch (name)
        {
            case "combine" when count >= 1: return Path.Combine(a.Select(ToNative).ToArray());
            case "getfullpath" when count == 1: return FullPath(a[0]);
            case "getdirectoryname" when count == 1: return Path.GetDirectoryName(ToNative(a[0])) ?? string.Empty;
            case "getfilename" when count == 1: return Path.GetFileName(ToNative(a[0]));
            case "getfilenamewithoutextension" when count == 1: return Path.GetFileNameWithoutExtension(ToNative(a[0]));
            case "getextension" when count == 1: return Path.GetExtension(ToNative(a[0]));
            case "ispathrooted" when count == 1: return Bool(Path.IsPathRooted(ToNative(a[0])));
            case "directoryseparatorchar": return Path.DirectorySeparatorChar.ToString();
            default: return Unknown();
        }
    }

    private string CallString(string name, List<string> a) => name switch
    {
        "isnullorempty" when a.Count == 1 => Bool(a[0].Length == 0),
        "isnullorwhitespace" when a.Count == 1 => Bool(string.IsNullOrWhiteSpace(a[0])),
        "copy" when a.Count == 1 => a[0],
        "concat" when a.Count >= 1 => string.Concat(a),
        "join" when a.Count >= 2 => string.Join(a[0], a.Skip(1)),
        _ => Unknown(),
    };

    private string CallEnvironment(string name, List<string> a)
    {
        switch (name)
        {
            case "getenvironmentvariable" when a.Count == 1:
                return Environment.GetEnvironmentVariable(a[0]) ?? string.Empty;
            case "getfolderpath" when a.Count >= 1
                && Enum.TryParse<Environment.SpecialFolder>(a[0].Split('.')[^1], ignoreCase: true, out var folder):
                return Environment.GetFolderPath(folder);
            case "newline":
                return Environment.NewLine;
            default:
                return Unknown();
        }
    }

    private static bool IsOsPlatform(string platform) => platform.ToUpperInvariant() switch
    {
        "WINDOWS" => OperatingSystem.IsWindows(),
        "LINUX" => OperatingSystem.IsLinux(),
        "OSX" or "MACOS" => OperatingSystem.IsMacOS(),
        "FREEBSD" => OperatingSystem.IsFreeBSD(),
        _ => RuntimeInformation.IsOSPlatform(OSPlatform.Create(platform)),
    };

    /// <summary>
    /// Searches this directory and those above it, as the MSBuild function does, but no
    /// further than the boundary: nothing above a repository changes what it means.
    /// </summary>
    private string? FileAbove(string file, string? from)
    {
        var start = string.IsNullOrEmpty(from) ? Directory : FullPath(from);
        if (IsForeign(start) || (boundary is not null && !IsWithin(start, boundary))) return null;
        for (var current = start; current is not null; current = Path.GetDirectoryName(current))
        {
            var candidate = Path.Combine(current, file);
            if (File.Exists(candidate)) return candidate;
            if (boundary is not null && SamePath(current, boundary)) break;
        }
        return null;
    }

    internal bool IsForeign(string path) => IsForeignPath(path, boundary);

    /// <summary>
    /// True for a network path outside the repository's own share. Reading a project must
    /// not reach out: opening <c>\\host\share</c> makes Windows authenticate to that host,
    /// so a repository could learn who indexed it, and a property in the path could carry an
    /// environment variable with it. A repository on a share may read the rest of that share.
    /// </summary>
    public static bool IsForeignPath(string path, string? root)
    {
        if (!OperatingSystem.IsWindows() || !IsNetworkPath(path)) return false;
        return root is null || !IsNetworkPath(root) || !ShareOf(path).Equals(ShareOf(root), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNetworkPath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static string ShareOf(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[0] + '\\' + parts[1] : path;
    }

    private static bool IsWithin(string path, string directory)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        directory = Path.TrimEndingDirectorySeparator(directory);
        return SamePath(path, directory)
            || path.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string EnsureTrailingSlash(string path)
    {
        path = ToNative(path);
        return path.Length == 0 || Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    internal string FullPath(string path) => Path.GetFullPath(ToNative(path), Directory);

    /// <summary>Project files write backslashes on every platform.</summary>
    internal static string ToNative(string path)
        => Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', Path.DirectorySeparatorChar);

    internal static bool SamePath(string left, string right) => string.Equals(left, right, PathComparison);

    private static string Bool(bool value) => value ? "True" : "False";

    private string Unknown()
    {
        Uncertain = true;
        return string.Empty;
    }
}
