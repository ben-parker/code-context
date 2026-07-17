using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Enumeration;
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

    // Per-(path, kind) include/exclude verdict memoization. IsIncluded runs multiple times per
    // surviving FS event and once per entry during scans; each call would otherwise re-walk every
    // ancestor directory and re-run every compiled regex (O(depth^2) evaluations per leaf). Keyed by
    // "relativePath\0d|f" so directory and file verdicts for the same path never collide (directory-
    // only rules make them genuinely different). Swapped wholesale by Invalidate(), the single point
    // where rule state changes, so verdict staleness is exactly the rules-cache staleness contract.
    private volatile ConcurrentDictionary<string, bool> _verdicts = new(PathComparer);

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
        // Swap in a fresh verdict cache (volatile write). This is the ONLY clearing point, so
        // verdict staleness is exactly a subset of the rules-cache staleness contract.
        // ORDERING INVARIANT: the rules clear above must happen BEFORE this swap. A reader that
        // captures the new cache instance synchronizes-with this volatile write and therefore
        // observes the cleared rules — so a verdict computed from pre-clear rules can never be
        // stored into the post-swap cache. Reordering these two statements breaks that proof.
        _verdicts = new ConcurrentDictionary<string, bool>(PathComparer);
        Volatile.Write(ref _ignoredPathCount, 0);
    }

    public IReadOnlyList<string> EnumerateIncludedFiles(IEnumerable<string> supportedExtensions)
    {
        Invalidate();
        var extensions = supportedExtensions
            .Select(extension => extension.StartsWith('.') ? extension : "." + extension)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var extensionLookup = extensions.GetAlternateLookup<ReadOnlySpan<char>>();
        var files = new List<string>();
        var directories = new Stack<string>();
        directories.Push(_rootPath);

        // AttributesToSkip = 0 so Hidden entries stay visible (only System/ReparsePoint are
        // filtered below, matching the pre-existing contract); attributes are read straight
        // from the directory enumeration, avoiding a per-entry File.GetAttributes syscall.
        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        while (directories.TryPop(out var directory))
        {
            EnsureRulesLoaded(directory);
            var entries = new FileSystemEnumerable<ScannedEntry>(
                directory,
                static (ref FileSystemEntry entry) =>
                    new ScannedEntry(entry.ToFullPath(), entry.IsDirectory, entry.Attributes),
                enumerationOptions);

            try
            {
                foreach (var entry in entries)
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                        || entry.Attributes.HasFlag(FileAttributes.System)
                        || !IsIncluded(entry.Path, entry.IsDirectory))
                    {
                        Interlocked.Increment(ref _ignoredPathCount);
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        directories.Push(entry.Path);
                    }
                    else if (extensionLookup.Contains(Path.GetExtension(entry.Path.AsSpan())))
                    {
                        files.Add(entry.Path);
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

    private readonly record struct ScannedEntry(string Path, bool IsDirectory, FileAttributes Attributes);

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

        // Span-based segment walk: O(depth) prefix slices instead of Split + an O(depth^2)
        // string.Join-per-segment loop. Mandatory-name membership is checked span-first via
        // the set's alternate lookup, so no per-segment string is allocated for the check.
        var mandatory = _mandatoryNames.GetAlternateLookup<ReadOnlySpan<char>>();
        var span = relative.AsSpan();
        var start = 0;
        while (true)
        {
            var slash = relative.IndexOf('/', start);
            var isLast = slash < 0;
            var segmentEnd = isLast ? relative.Length : slash;
            var segment = span[start..segmentEnd];

            if (!segment.IsEmpty)
            {
                if (mandatory.Contains(segment))
                    return false; // covers every segment, leaf included

                // Directory segments are every segment for a directory target, and every
                // segment except the leaf for a file target.
                if ((!isLast || isDirectory) && !EvaluateRules(relative[..segmentEnd], isDirectory: true))
                    return false; // Git cannot re-include a file below an excluded directory.
            }

            if (isLast) break;
            start = slash + 1;
        }

        return EvaluateRules(relative, isDirectory);
    }

    private bool EvaluateRules(string rootRelativePath, bool isDirectory)
    {
        // Memoize the per-(path, kind) verdict. Capture the current cache once so a concurrent
        // Invalidate() swap does not split the lookup and the store between two instances; adding to
        // a just-superseded instance is harmless. No lock around the lookup: a duplicate computation
        // under a race is benign and matches the parsed-rules cache philosophy.
        var verdicts = _verdicts;
        var key = rootRelativePath + "\0" + (isDirectory ? "d" : "f");
        if (verdicts.TryGetValue(key, out var cached)) return cached;

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

        verdicts.TryAdd(key, included);
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
