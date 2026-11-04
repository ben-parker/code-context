using System.Text;
using System.Text.RegularExpressions;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// A project's <c>Compile</c> item operations, kept in order and run per file rather than
/// expanded against the disk. A file is compiled when the last operation matching it
/// includes it, which is what running them in sequence over a list would leave behind.
/// </summary>
public sealed class CompileItemSet
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly RegexOptions PatternOptions = RegexOptions.CultureInvariant
        | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);

    private static readonly StringComparison PrefixComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly List<Operation> _operations = [];

    public void Include(IEnumerable<string> fullPatterns, IEnumerable<string> fullExcludes)
    {
        var excludes = new PathSet(fullExcludes);
        var includes = new PathSet(fullPatterns);

        // Old-style projects list thousands of single files; one set keeps that cheap.
        if (_operations.LastOrDefault() is { Includes: true, Excludes.IsEmpty: true } last
            && excludes.IsEmpty)
        {
            last.Paths.Merge(includes);
            return;
        }
        _operations.Add(new Operation(true, includes, excludes));
    }

    public void Remove(IEnumerable<string> fullPatterns)
        => _operations.Add(new Operation(false, new PathSet(fullPatterns), new PathSet([])));

    public bool Contains(string fullPath)
    {
        var path = Normalize(fullPath);
        var included = false;
        foreach (var operation in _operations)
        {
            if (operation.Paths.Matches(path) && !operation.Excludes.Matches(path))
            {
                included = operation.Includes;
            }
        }
        return included;
    }

    /// <summary>Whether an include names something outside this directory, as a linked file does.</summary>
    public bool IncludesOutside(string directory)
    {
        var prefix = Normalize(directory).TrimEnd('/') + "/";
        return _operations.Any(operation => operation.Includes && operation.Paths.ReachesOutside(prefix));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed record Operation(bool Includes, PathSet Paths, PathSet Excludes);

    private sealed class PathSet
    {
        private readonly HashSet<string> _literals = new(PathComparer);
        private readonly List<(string Prefix, Regex Pattern)> _patterns = [];

        public PathSet(IEnumerable<string> fullPatterns)
        {
            foreach (var pattern in fullPatterns.Select(Normalize))
            {
                if (pattern.AsSpan().IndexOfAny('*', '?') < 0)
                {
                    _literals.Add(pattern);
                }
                else
                {
                    _patterns.Add((LiteralPrefix(pattern), ToRegex(pattern)));
                }
            }
        }

        public bool IsEmpty => _literals.Count == 0 && _patterns.Count == 0;

        public void Merge(PathSet other)
        {
            _literals.UnionWith(other._literals);
            _patterns.AddRange(other._patterns);
        }

        public bool ReachesOutside(string prefix)
            => _literals.Any(literal => !literal.StartsWith(prefix, PrefixComparison))
                || _patterns.Any(pattern => !pattern.Prefix.StartsWith(prefix, PrefixComparison));

        public bool Matches(string path)
        {
            if (_literals.Contains(path)) return true;
            foreach (var (prefix, pattern) in _patterns)
            {
                if (path.StartsWith(prefix, PrefixComparison) && pattern.IsMatch(path)) return true;
            }
            return false;
        }

        private static string LiteralPrefix(string pattern)
        {
            var wildcard = pattern.AsSpan().IndexOfAny('*', '?');
            var separator = pattern.LastIndexOf('/', wildcard);
            return separator < 0 ? string.Empty : pattern[..(separator + 1)];
        }

        private static Regex ToRegex(string pattern)
        {
            var regex = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var character = pattern[i];
                if (character == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                    {
                        regex.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        regex.Append(".*");
                        i += 1;
                    }
                }
                else if (character == '*')
                {
                    regex.Append("[^/]*");
                }
                else if (character == '?')
                {
                    regex.Append("[^/]");
                }
                else
                {
                    regex.Append(Regex.Escape(character.ToString()));
                }
            }
            return new Regex(regex.Append('$').ToString(), PatternOptions);
        }
    }
}
