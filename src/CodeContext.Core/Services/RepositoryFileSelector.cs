using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Enumeration;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Services;

/// <summary>
/// Package-free implementation of the project-local portions of gitignore semantics.
/// Rules are compiled once per directory and applied root-to-leaf, so matching cost is
/// proportional to path depth rather than ignore-files times repository files.
/// This is close to Git but not identical: a negated class can match a slash, so
/// <c>x[!a]b</c> matches <c>x/b</c>.
/// </summary>
public sealed class RepositoryFileSelector : IRepositoryFileSelector
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _rootPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<IgnorePattern>> _rulesByDirectory = new(PathComparer);
    private readonly HashSet<string> _loadedIgnoreFiles = new(PathComparer);
    private readonly HashSet<string> _mandatoryNames;
    private int _ignoredPathCount;

    // Per-(path, kind) include/exclude verdict memoization. IsIncluded runs multiple times per
    // surviving FS event and once per entry during scans; each call would otherwise re-walk every
    // ancestor directory and re-run every rule (O(depth^2) evaluations per leaf). Keyed by
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
            RulesFor(directory);
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
            var rules = RulesFor(directory);
            if (rules.Count == 0) continue;

            var relativeToRules = Normalize(Path.GetRelativePath(directory,
                Path.Combine(_rootPath, rootRelativePath.Replace('/', Path.DirectorySeparatorChar))));
            var path = IgnorePath.Create(relativeToRules, OperatingSystem.IsWindows());
            foreach (var rule in rules)
            {
                if (rule.Matches(path, isDirectory))
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

    /// <summary>
    /// A directory's rules, read if need be. Loading and returning are one step: with a gap
    /// between them, an <see cref="Invalidate"/> landing there leaves no rules to apply, and
    /// the path being tested reads as included.
    /// </summary>
    private IReadOnlyList<IgnorePattern> RulesFor(string directory)
    {
        directory = Path.GetFullPath(directory);
        lock (_gate)
        {
            if (_rulesByDirectory.TryGetValue(directory, out var loaded)) return loaded;
            var ignoreFile = Path.Combine(directory, ".gitignore");
            var rules = new List<IgnorePattern>();
            if (File.Exists(ignoreFile))
            {
                try
                {
                    foreach (var line in File.ReadLines(ignoreFile))
                    {
                        if (IgnorePattern.TryParse(line, OperatingSystem.IsWindows(), out var rule)) rules.Add(rule);
                    }
                    _loadedIgnoreFiles.Add(ignoreFile);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            _rulesByDirectory[directory] = rules;
            return rules;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

internal static class CharacterSearchExtensions
{
    public static bool ContainsAny(this string value, params char[] characters)
        => value.IndexOfAny(characters) >= 0;
}
