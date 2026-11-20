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
        /// Whether <paramref name="path"/> is a test file, by the naming conventions test runners themselves discover
        /// tests with, applied to the part of the path under the root. Each directory name, and the file name without
        /// its extension, is read as the pieces between <c>.</c>, <c>-</c> and <c>_</c>; the path is a test file when a
        /// piece
        /// <list type="bullet">
        /// <item>is <c>test</c>, <c>tests</c>, <c>spec</c> or <c>specs</c> in any letter case: directories
        /// <c>test/</c>, <c>tests/</c>, <c>spec/</c>, <c>specs/</c>, <c>__tests__/</c>, <c>Widget.Tests/</c>,
        /// <c>e2e-tests/</c>; files <c>widget.test.ts</c>, <c>widget.spec.tsx</c>, <c>widget_test.go</c>,
        /// <c>test_widget.py</c>, <c>widget_spec.rb</c>, <c>widget-test.js</c>, <c>test.js</c>;</item>
        /// <item>ends in <c>Test</c> or <c>Tests</c> written so (the camel-case suffix): <c>WidgetTests.cs</c>,
        /// <c>WidgetTest.java</c>, <c>Widget.UnitTests/</c>, <c>IntegrationTests/</c>;</item>
        /// <item>or the file is pytest's <c>conftest</c>.</item>
        /// </list>
        /// It is also a test file when a whole directory name is <c>testdata</c>, <c>unittests</c> or <c>__mocks__</c>
        /// in any letter case (Go's fixtures, a C++ <c>unittests/</c>, Jest's manual mocks), or when the file name
        /// follows Cypress's <c>*.cy.*</c>: <c>cy</c>, in any letter case, between two dots after a first piece
        /// (<c>login.cy.ts</c>). Those count only so, not as pieces: <c>testdatabase/</c>, <c>testdata.json</c>,
        /// <c>mocks/</c>, <c>cy.ts</c>, <c>cy/</c> and <c>login-cy.ts</c> are production.
        /// A word that merely contains those letters is not a test (<c>Latest.cs</c>, <c>inspector.ts</c>,
        /// <c>Contest.cs</c>, <c>aspects/</c>, <c>ab-testing/</c>), and neither is a camel-case <c>Spec</c> suffix or <c>Test</c> prefix,
        /// which production code uses for specifications and for verbs (<c>WorkerLaunchSpec.cs</c>,
        /// <c>TestConnection.cs</c>). The check allocates nothing.
        /// </summary>
        public static bool IsTestFile(string? path, string? rootPath)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var relative = RootRelative(path, rootPath);

            // The file name is read without its extension; a name that starts with its only dot has none.
            var nameStart = relative.LastIndexOfAny('/', '\\') + 1;
            var name = relative[nameStart..];
            var extension = name.LastIndexOf('.');
            var end = extension > 0 ? nameStart + extension : relative.Length;
            if (name[..(end - nameStart)].Equals("conftest", StringComparison.OrdinalIgnoreCase)) return true;

            var pieceStart = 0;
            var directoryStart = 0;
            for (var i = 0; i <= end; i++)
            {
                if (i < end && relative[i] is not ('/' or '\\' or '.' or '-' or '_')) continue;
                var piece = relative[pieceStart..i];
                if (IsTestWord(piece)) return true;
                if (i < end && relative[i] is '/' or '\\')
                {
                    if (IsTestDirectory(relative[directoryStart..i])) return true;
                    directoryStart = i + 1;
                }
                // Cypress's *.cy.*: cy between two dots of the file name, where the first dot does not begin the name.
                else if (pieceStart > nameStart + 1 && relative[pieceStart - 1] == '.'
                    && i < relative.Length && relative[i] == '.'
                    && piece.Equals("cy", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                pieceStart = i + 1;
            }
            return false;
        }

        private static bool IsTestDirectory(ReadOnlySpan<char> name) => name.Length switch
        {
            8 => name.Equals("testdata", StringComparison.OrdinalIgnoreCase),
            9 => name.Equals("unittests", StringComparison.OrdinalIgnoreCase) || name.Equals("__mocks__", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

        private static bool IsTestWord(ReadOnlySpan<char> piece) => piece.Length switch
        {
            < 4 => false,
            4 => piece.Equals("test", StringComparison.OrdinalIgnoreCase) || piece.Equals("spec", StringComparison.OrdinalIgnoreCase),
            5 when piece.Equals("tests", StringComparison.OrdinalIgnoreCase) || piece.Equals("specs", StringComparison.OrdinalIgnoreCase) => true,
            _ => piece.EndsWith("Test", StringComparison.Ordinal) || piece.EndsWith("Tests", StringComparison.Ordinal),
        };

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
                        $"within '{scope}' is not inside the indexed root{(string.IsNullOrEmpty(rootPath) ? string.Empty : $" '{rootPath}'")}.");
                }
                value = relative.Length == value.Length ? string.Empty : relative.ToString();
            }
            value = value.Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
            value = value.Trim('/');
            if (value == ".") value = string.Empty;
            if (value.Split('/').Any(segment => segment == ".."))
                throw new ArgumentException($"within '{scope}' must not leave the indexed root.");
            return value;
        }

        private static bool IsSeparator(char value) => value is '/' or '\\';
    }
}
