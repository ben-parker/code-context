using System.IO;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Canonical file-path normalization and matching used both by <see cref="ContextService"/>
    /// (when resolving a file-path identifier and aggregating file-level relationships) and by the
    /// in-memory adjacency index (<c>GraphAdjacency.NodesByFilePath</c>). Kept in one place so the
    /// index cannot silently drift from the semantics <c>ContextService</c> exposes.
    ///
    /// Matching is <see cref="System.StringComparison.OrdinalIgnoreCase"/> on <em>every</em>
    /// platform (this is intentionally NOT the store's per-OS path comparer): a rooted request must
    /// match the indexed path exactly (after normalization), while a relative request matches either
    /// exactly or as a trailing path segment ("/" + request).
    /// </summary>
    internal static class FilePathMatcher
    {
        public static string Normalize(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        public static bool Matches(string? indexedPath, string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(indexedPath) || string.IsNullOrWhiteSpace(requestedPath))
                return false;

            var normalizedIndexed = Normalize(indexedPath);
            var normalizedRequested = Normalize(requestedPath);

            if (Path.IsPathRooted(requestedPath))
            {
                return string.Equals(
                    normalizedIndexed, normalizedRequested, System.StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(normalizedIndexed, normalizedRequested, System.StringComparison.OrdinalIgnoreCase)
                || normalizedIndexed.EndsWith(
                    "/" + normalizedRequested, System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
