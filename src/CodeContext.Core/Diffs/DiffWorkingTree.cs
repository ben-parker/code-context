using System.Security.Cryptography;
using System.Text;
using CodeContext.Core.Paths;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Diffs
{
    /// <summary>
    /// What the working tree says of the files a diff names, for one request: whether a file is there, whether it
    /// changed after the index last read the tree, and whether its content is the diff's new side (git's blob id of
    /// it). Only files under the indexed root are looked at, by the rule the host applies to every path a repository
    /// can name (<see cref="ReportedPaths.RefusedOnDisk"/>: on a drive or the root's own share, through no link the
    /// repository holds), so a diff cannot make the host read outside the root or reach a network share. Nothing is
    /// run: a blob id is computed here, as git computes it, never asked of git.
    /// </summary>
    internal sealed class DiffWorkingTree
    {
        /// <summary>Files larger than this are not hashed; their content is reported as unchecked.</summary>
        public const long MaxHashedBytes = 64L * 1024 * 1024;

        public enum Verdict
        {
            /// <summary>The content is the diff's new side.</summary>
            Match,

            /// <summary>There, but its content is not the diff's new side.</summary>
            Mismatch,

            /// <summary>There; the diff gives no blob id to compare with.</summary>
            Unchecked,

            /// <summary>Not in the working tree.</summary>
            Missing,

            /// <summary>Changed after the index last read the tree: the index may not hold it as it is.</summary>
            ChangedSinceIndexed,

            /// <summary>Not looked at: through a link, not a regular file, too large, or not readable.</summary>
            Refused,
        }

        private readonly string _root;
        private readonly Func<string, bool> _refused;
        private readonly DateTime? _indexedAsOf;
        private readonly Dictionary<string, (Verdict, string?)> _seen = new(StringComparer.Ordinal);

        /// <param name="root">The indexed root, a full path.</param>
        /// <param name="indexedAsOf">When the index last read every file; null skips the time check.</param>
        public DiffWorkingTree(string root, DateTime? indexedAsOf)
        {
            _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            _refused = ReportedPaths.RefusedOnDisk(_root);
            _indexedAsOf = indexedAsOf;
            DiffRoot = FindRepositoryTop(_root);
            var relative = Path.GetRelativePath(DiffRoot, _root).Replace('\\', '/');
            RootPrefix = relative == "." ? string.Empty : relative + "/";
        }

        /// <summary>The directory the diff's paths are relative to: the git work tree holding the root, else the root.</summary>
        public string DiffRoot { get; }

        /// <summary>The root relative to <see cref="DiffRoot"/>, with a trailing <c>/</c>; empty when they are one.</summary>
        public string RootPrefix { get; }

        /// <summary>
        /// The nearest directory at or above <paramref name="root"/> holding <c>.git</c>, where <c>git diff</c> starts its
        /// paths; the root itself when there is none. Only the root's own ancestors are looked at.
        /// </summary>
        internal static string FindRepositoryTop(string root)
        {
            for (var directory = root; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory))
            {
                var marker = Path.Combine(directory, ".git");
                if (Directory.Exists(marker) || File.Exists(marker)) return directory;
            }
            return root;
        }

        /// <summary>The path relative to the root of a path the diff names, or null when it is outside the root.</summary>
        public string? RootRelative(string diffPath)
            => RootPrefix.Length == 0 ? diffPath
                : diffPath.StartsWith(RootPrefix, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    ? diffPath[RootPrefix.Length..]
                    : null;

        public string FullPath(string rootRelative) => Path.Combine(_root, rootRelative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>What the working tree holds at <paramref name="fullPath"/>, compared with <paramref name="blob"/> when given.</summary>
        public (Verdict Verdict, string? Why) Inspect(string fullPath, string? blob)
        {
            var key = fullPath + "\0" + blob;
            if (_seen.TryGetValue(key, out var known)) return known;
            var verdict = InspectCore(fullPath, blob);
            _seen[key] = verdict;
            return verdict;
        }

        private (Verdict, string?) InspectCore(string fullPath, string? blob)
        {
            if (_refused(fullPath)) return (Verdict.Refused, "it is reached through a link, or by a path the host does not read");
            FileInfo info;
            try
            {
                info = new FileInfo(fullPath);
                if (Directory.Exists(fullPath)) return (Verdict.Refused, "it is a directory (a submodule?)");
                if (!info.Exists) return (Verdict.Missing, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (Verdict.Refused, exception.Message);
            }
            if (_indexedAsOf is { } asOf && FileChangeTime.TryRead(fullPath, out var changed) && changed > asOf)
                return (Verdict.ChangedSinceIndexed, null);
            if (blob is null) return (Verdict.Unchecked, null);
            if (info.Length > MaxHashedBytes) return (Verdict.Refused, $"it is larger than {MaxHashedBytes / (1024 * 1024)} MB, so its content was not compared");
            byte[] content;
            try
            {
                content = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (Verdict.Refused, exception.Message);
            }
            if (Matches(content, blob)) return (Verdict.Match, null);
            // A checkout with core.autocrlf holds CRLF where the blob holds LF.
            if (Array.IndexOf(content, (byte)'\r') >= 0 && Matches(WithoutCarriageReturns(content), blob)) return (Verdict.Match, null);
            return (Verdict.Mismatch, null);
        }

        /// <summary>
        /// The lines of the file at <paramref name="fullPath"/> (UTF-8, carriage returns dropped), read as <see cref="Inspect"/>
        /// reads it and only when its content is still <paramref name="blob"/> (when given); null otherwise.
        /// </summary>
        public IReadOnlyList<string>? ReadLines(string fullPath, string? blob)
        {
            if (_refused(fullPath)) return null;
            byte[] content;
            try
            {
                if (new FileInfo(fullPath) is not { Exists: true } info || info.Length > MaxHashedBytes) return null;
                content = File.ReadAllBytes(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            if (blob is not null && !Matches(content, blob)
                && !(Array.IndexOf(content, (byte)'\r') >= 0 && Matches(WithoutCarriageReturns(content), blob)))
            {
                return null;
            }
            var text = new UTF8Encoding(false).GetString(content);
            if (text.EndsWith('\n')) text = text[..^1];
            return text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line).ToArray();
        }

        /// <summary>Whether <paramref name="blob"/> (hexadecimal, maybe abbreviated) is git's id of <paramref name="content"/>, SHA-1 or SHA-256.</summary>
        internal static bool Matches(byte[] content, string blob)
        {
            if (blob.Length <= 40 && BlobId(content, sha256: false).StartsWith(blob, StringComparison.OrdinalIgnoreCase)) return true;
            return BlobId(content, sha256: true).StartsWith(blob, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>git's object id of a blob: the hash of <c>blob &lt;size&gt;\0</c> followed by the content.</summary>
        internal static string BlobId(byte[] content, bool sha256)
        {
            var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
            using var hash = IncrementalHash.CreateHash(sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            hash.AppendData(header);
            hash.AppendData(content);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        private static byte[] WithoutCarriageReturns(byte[] content)
        {
            var result = new byte[content.Length];
            var length = 0;
            for (var i = 0; i < content.Length; i++)
            {
                if (content[i] == '\r' && i + 1 < content.Length && content[i + 1] == '\n') continue;
                result[length++] = content[i];
            }
            return result[..length];
        }
    }
}
