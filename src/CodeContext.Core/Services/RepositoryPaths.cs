using System;
using System.Linq;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Path checks made against the part of a path under the indexed root, so that where the repository happens to live
    /// (a checkout under <c>~/tests/</c>, a temp folder named for a test run) never changes an answer. Nothing here
    /// allocates: the graph queries make these checks once per node or edge.
    /// </summary>
    public static class RepositoryPaths
    {
        private static readonly StringComparison RootComparison =
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// Whether <paramref name="path"/> is a test file: its root-relative form contains <c>test</c> or <c>spec</c>,
        /// ignoring case. This is the rule <c>--tests</c> evidence and ranking have always used, now applied to the part
        /// of the path inside the repository. It is a name rule: <c>latest.ts</c> and <c>inspector.ts</c> match too.
        /// </summary>
        public static bool IsTestFile(string? path, string? rootPath)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var relative = RootRelative(path, rootPath);
            return relative.Contains("test", StringComparison.OrdinalIgnoreCase)
                || relative.Contains("spec", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The part of <paramref name="path"/> after <paramref name="rootPath"/> and one separator, or the whole path when it
        /// is not under the root (or there is no root).
        /// </summary>
        public static ReadOnlySpan<char> RootRelative(string path, string? rootPath)
        {
            if (string.IsNullOrEmpty(rootPath)) return path;
            var root = rootPath.AsSpan().TrimEnd("/\\");
            if (root.Length == 0) return path.AsSpan().TrimStart("/\\");
            return path.Length > root.Length
                && path.AsSpan().StartsWith(root, RootComparison)
                && IsSeparator(path[root.Length])
                    ? path.AsSpan(root.Length + 1)
                    : path;
        }

        /// <summary>
        /// Whether the root-relative form of <paramref name="path"/> is <paramref name="scope"/> or lies under it, comparing
        /// <c>/</c> and <c>\</c> alike and ignoring case, as <see cref="FilePathMatcher"/> does. <paramref name="scope"/> is
        /// normalized by <see cref="NormalizeScope"/>; an empty scope holds every path.
        /// </summary>
        public static bool IsWithin(string? path, string? rootPath, string scope)
        {
            if (scope.Length == 0) return true;
            if (string.IsNullOrEmpty(path)) return false;
            var relative = RootRelative(path, rootPath);
            if (relative.Length < scope.Length) return false;
            for (var i = 0; i < scope.Length; i++)
            {
                var left = relative[i];
                var right = scope[i];
                if (IsSeparator(left) && IsSeparator(right)) continue;
                if (char.ToUpperInvariant(left) != char.ToUpperInvariant(right)) return false;
            }
            return relative.Length == scope.Length || IsSeparator(relative[scope.Length]);
        }

        /// <summary>
        /// A <c>within</c> value as a root-relative scope with <c>/</c> separators and no leading <c>./</c> or trailing
        /// separator. An absolute path inside the root is made relative; one outside it is refused.
        /// </summary>
        public static string NormalizeScope(string? scope, string? rootPath)
        {
            if (string.IsNullOrWhiteSpace(scope)) return string.Empty;
            var value = scope.Trim();
            if (System.IO.Path.IsPathRooted(value))
            {
                var relative = RootRelative(value, rootPath);
                if (string.IsNullOrEmpty(rootPath) || relative.Length == value.Length
                    && !value.AsSpan().TrimEnd("/\\").Equals(rootPath.AsSpan().TrimEnd("/\\"), RootComparison))
                {
                    throw new ArgumentException(
                        $"within '{scope}' is not inside the indexed root{(string.IsNullOrEmpty(rootPath) ? string.Empty : $" '{rootPath}'")}.",
                        nameof(scope));
                }
                value = relative.Length == value.Length ? string.Empty : relative.ToString();
            }
            value = value.Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
            value = value.Trim('/');
            if (value == ".") value = string.Empty;
            if (value.Split('/').Any(segment => segment == ".."))
                throw new ArgumentException($"within '{scope}' must not leave the indexed root.", nameof(scope));
            return value;
        }

        private static bool IsSeparator(char value) => value is '/' or '\\';
    }
}
