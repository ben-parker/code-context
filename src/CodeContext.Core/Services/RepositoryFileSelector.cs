using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Services;

/// <summary>
/// Package-free implementation of the project-local portions of gitignore semantics.
/// Rules are compiled once per directory and applied root-to-leaf, so matching cost is
/// proportional to path depth rather than ignore-files times repository files.
/// </summary>
public sealed class RepositoryFileSelector : IRepositoryFileSelector
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<IgnoreRule>> _rulesByDirectory = new(PathComparer);
    private readonly HashSet<string> _loadedIgnoreFiles = new(PathComparer);
    private readonly HashSet<string> _mandatoryNames;
    private int _ignoredPathCount;

    public RepositoryFileSelector(IOptions<CodeContextOptions> options)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _mandatoryNames = new HashSet<string>(PathComparer)
        {
            ".git", ".codecontext",
        };

        // Configured host safety exclusions remain non-negatable. Extract their
        // directory component; these defaults intentionally apply at every depth.
        foreach (var pattern in options.Value.IgnorePatterns ?? [])
        {
            var normalized = pattern.Replace('\\', '/').Trim('/');
            var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(name) && !name.ContainsAny('*', '?', '['))
            {
                _mandatoryNames.Add(name);
            }
        }
    }

    public int IgnoreSourceCount { get { lock (_gate) return _loadedIgnoreFiles.Count; } }
    public int IgnoredPathCount => Volatile.Read(ref _ignoredPathCount);
    public IReadOnlyList<string> MandatoryExclusions =>
        _mandatoryNames.Order(StringComparer.OrdinalIgnoreCase).Select(name => $"{name}/").ToList();

    public bool IsIgnoreFile(string path)
        => string.Equals(Path.GetFileName(path), ".gitignore", StringComparison.OrdinalIgnoreCase);

    public void Invalidate()
    {
        lock (_gate)
        {
            _rulesByDirectory.Clear();
            _loadedIgnoreFiles.Clear();
        }
        Volatile.Write(ref _ignoredPathCount, 0);
    }

    public IReadOnlyList<string> EnumerateIncludedFiles(IEnumerable<string> supportedExtensions)
    {
        Invalidate();
        var extensions = supportedExtensions
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(_rootPath);

        while (directories.TryPop(out var directory))
        {
            EnsureRulesLoaded(directory);
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }

                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)
                        || attributes.HasFlag(FileAttributes.System)
                        || !IsIncluded(entry, isDirectory))
                    {
                        Interlocked.Increment(ref _ignoredPathCount);
                        continue;
                    }

                    if (isDirectory)
                    {
                        directories.Push(entry);
                    }
                    else if (extensions.Contains(Path.GetExtension(entry)))
                    {
                        files.Add(entry);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Enumeration may fail after it starts; retain the readable entries.
            }
            catch (IOException)
            {
                // Broken links and disappearing directories do not fail the scan.
            }
        }

        return files;
    }

    public bool IsIncluded(string path, bool isDirectory = false)
    {
        string fullPath;
        if (!OperatingSystem.IsWindows()) path = path.Replace('\\', '/');
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        var relative = Normalize(Path.GetRelativePath(_rootPath, fullPath));
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            return false;
        if (relative == ".")
            return true;

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegmentCount = isDirectory ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < directorySegmentCount; index++)
        {
            if (_mandatoryNames.Contains(segments[index]))
                return false;

            var directoryRelative = string.Join('/', segments.Take(index + 1));
            if (!EvaluateRules(directoryRelative, isDirectory: true))
                return false; // Git cannot re-include a file below an excluded directory.
        }

        if (segments.Any(segment => _mandatoryNames.Contains(segment)))
            return false;
        return EvaluateRules(relative, isDirectory);
    }

    private bool EvaluateRules(string rootRelativePath, bool isDirectory)
    {
        var included = true;
        var parent = isDirectory
            ? Path.GetDirectoryName(Path.Combine(_rootPath, rootRelativePath.Replace('/', Path.DirectorySeparatorChar)))
            : Path.GetDirectoryName(Path.Combine(_rootPath, rootRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        parent ??= _rootPath;

        foreach (var directory in AncestorDirectories(parent))
        {
            EnsureRulesLoaded(directory);
            var relativeToRules = Normalize(Path.GetRelativePath(directory,
                Path.Combine(_rootPath, rootRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            IReadOnlyList<IgnoreRule> rules;
            lock (_gate) rules = _rulesByDirectory.GetValueOrDefault(directory) ?? [];
            foreach (var rule in rules)
            {
                if (rule.Matches(relativeToRules, isDirectory))
                    included = rule.Negated;
            }
        }
        return included;
    }

    private IEnumerable<string> AncestorDirectories(string directory)
    {
        var chain = new Stack<string>();
        var current = Path.GetFullPath(directory);
        while (current.StartsWith(_rootPath, PathComparison))
        {
            chain.Push(current);
            if (PathComparer.Equals(current, _rootPath)) break;
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || PathComparer.Equals(parent, current)) break;
            current = parent;
        }
        return chain;
    }

    private void EnsureRulesLoaded(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (_gate)
        {
            if (_rulesByDirectory.ContainsKey(directory)) return;
            var ignoreFile = Path.Combine(directory, ".gitignore");
            var rules = new List<IgnoreRule>();
            if (File.Exists(ignoreFile))
            {
                try
                {
                    foreach (var line in File.ReadLines(ignoreFile))
                    {
                        if (IgnoreRule.TryParse(line, out var rule)) rules.Add(rule);
                    }
                    _loadedIgnoreFiles.Add(ignoreFile);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            _rulesByDirectory[directory] = rules;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    private sealed record IgnoreRule(bool Negated, bool DirectoryOnly, Regex Pattern)
    {
        public bool Matches(string relativePath, bool isDirectory)
            => (!DirectoryOnly || isDirectory) && Pattern.IsMatch(Normalize(relativePath));

        public static bool TryParse(string rawLine, out IgnoreRule rule)
        {
            rule = null!;
            var line = TrimUnescapedTrailingSpaces(rawLine.TrimEnd('\r'));
            if (line.Length == 0 || line[0] == '#') return false;

            var negated = line[0] == '!';
            if (negated) line = line[1..];
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
                line = line[1..];
            if (line.Length == 0) return false;

            var directoryOnly = EndsWithUnescapedSlash(line);
            if (directoryOnly) line = line[..^1];
            var anchored = line.StartsWith("/", StringComparison.Ordinal);
            if (anchored) line = line[1..];
            var hasSlash = line.Contains('/');

            var prefix = anchored || hasSlash ? "^" : "(?:^|.*/)";
            var regex = prefix + GlobToRegex(line) + "$";
            var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
            rule = new IgnoreRule(negated, directoryOnly, new Regex(regex, options));
            return true;
        }

        private static string TrimUnescapedTrailingSpaces(string value)
        {
            var end = value.Length;
            while (end > 0 && value[end - 1] == ' ')
            {
                var slashes = 0;
                for (var index = end - 2; index >= 0 && value[index] == '\\'; index--) slashes++;
                if ((slashes & 1) == 1) break;
                end--;
            }
            return value[..end];
        }

        private static bool EndsWithUnescapedSlash(string value)
        {
            if (!value.EndsWith("/", StringComparison.Ordinal)) return false;
            var slashes = 0;
            for (var index = value.Length - 2; index >= 0 && value[index] == '\\'; index--) slashes++;
            return (slashes & 1) == 0;
        }

        private static string GlobToRegex(string glob)
        {
            var result = new StringBuilder();
            for (var index = 0; index < glob.Length; index++)
            {
                var character = glob[index];
                if (character == '\\' && index + 1 < glob.Length)
                {
                    result.Append(Regex.Escape(glob[++index].ToString()));
                }
                else if (character == '*')
                {
                    var isDouble = index + 1 < glob.Length && glob[index + 1] == '*';
                    if (!isDouble) result.Append("[^/]*");
                    else
                    {
                        index++;
                        if (index + 1 < glob.Length && glob[index + 1] == '/')
                        {
                            index++;
                            result.Append("(?:.*/)?");
                        }
                        else result.Append(".*");
                    }
                }
                else if (character == '?') result.Append("[^/]");
                else if (character == '[')
                {
                    var close = glob.IndexOf(']', index + 1);
                    if (close < 0) result.Append("\\[");
                    else
                    {
                        var content = glob[(index + 1)..close];
                        if (content.StartsWith('!')) content = "^" + content[1..];
                        result.Append('[').Append(content).Append(']');
                        index = close;
                    }
                }
                else result.Append(Regex.Escape(character.ToString()));
            }
            return result.ToString();
        }
    }
}

internal static class CharacterSearchExtensions
{
    public static bool ContainsAny(this string value, params char[] characters)
        => value.IndexOfAny(characters) >= 0;
}
