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

        /// <summary>Whether the host may not look at <paramref name="fullPath"/>: it is reached through a link the repository holds, or by a refused spelling.</summary>
        public bool Refused(string fullPath) => _refused(fullPath);

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
            DateTime changed = default;
            if (_indexedAsOf is { } asOf)
            {
                if (!FileChangeTime.TryRead(fullPath, out changed)) return (Verdict.Refused, "the time it last changed cannot be read");
                if (changed > asOf) return (Verdict.ChangedSinceIndexed, null);
            }
            if (blob is null) return (Verdict.Unchecked, null);
            if (info.Length > MaxHashedBytes) return (Verdict.Refused, $"it is larger than {MaxHashedBytes / (1024 * 1024)} MB, so its content was not compared");
            try
            {
                BeforeHashForTests?.Invoke(fullPath);
                bool matches;
                using (var stream = Open(fullPath)) matches = Matches(stream, blob);
                // The content hashed is the content the index read only if nothing changed the file since its time was
                // looked at: one rewritten in between is the diff's new side now and was not when it was indexed.
                if (_indexedAsOf is not null && (!FileChangeTime.TryRead(fullPath, out var after) || after != changed))
                    return (Verdict.ChangedSinceIndexed, null);
                return (matches ? Verdict.Match : Verdict.Mismatch, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (Verdict.Refused, exception.Message);
            }
        }

        /// <summary>Called with the path between the look at a file's time and the read of its content.</summary>
        internal Action<string>? BeforeHashForTests { get; init; }

        private const int HashBufferBytes = 64 * 1024;

        /// <summary>
        /// Whether <paramref name="blob"/> is git's id of what <paramref name="stream"/> holds, as it is or with each CRLF
        /// read as LF (a checkout with <c>core.autocrlf</c> holds CRLF where the blob holds LF). Read in passes of a fixed
        /// buffer, never held whole; content whose length changes while it is read matches nothing.
        /// </summary>
        internal static bool Matches(Stream stream, string blob)
        {
            var length = stream.Length;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(HashBufferBytes);
            byte[]? stripped = null;
            try
            {
                using var sha1 = blob.Length <= 40 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA1) : null;
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Header(length);
                long total = 0, pairs = 0;
                var afterCarriageReturn = false;
                int read;
                while ((read = stream.Read(buffer, 0, HashBufferBytes)) > 0)
                {
                    Append(buffer, read);
                    for (var i = 0; i < read; i++)
                    {
                        if (afterCarriageReturn && buffer[i] == '\n') pairs++;
                        afterCarriageReturn = buffer[i] == '\r';
                    }
                    total += read;
                }
                if (total != length) return false;
                if (Found()) return true;
                if (pairs == 0) return false;

                stream.Position = 0;
                stripped = System.Buffers.ArrayPool<byte>.Shared.Rent(HashBufferBytes + 1);
                Header(length - pairs);
                long written = 0;
                var pending = false;
                while ((read = stream.Read(buffer, 0, HashBufferBytes)) > 0)
                {
                    var count = 0;
                    for (var i = 0; i < read; i++)
                    {
                        var value = buffer[i];
                        if (pending && value != '\n') stripped[count++] = (byte)'\r';
                        pending = value == '\r';
                        if (!pending) stripped[count++] = value;
                    }
                    Append(stripped, count);
                    written += count;
                }
                if (pending)
                {
                    stripped[0] = (byte)'\r';
                    Append(stripped, 1);
                    written++;
                }
                return written == length - pairs && Found();

                void Header(long size)
                {
                    var header = Encoding.ASCII.GetBytes($"blob {size}\0");
                    sha1?.AppendData(header);
                    sha256.AppendData(header);
                }

                void Append(byte[] data, int count)
                {
                    sha1?.AppendData(data, 0, count);
                    sha256.AppendData(data, 0, count);
                }

                bool Found()
                {
                    var one = sha1 is null ? null : Convert.ToHexStringLower(sha1.GetHashAndReset());
                    var two = Convert.ToHexStringLower(sha256.GetHashAndReset());
                    return (one is not null && one.StartsWith(blob, StringComparison.OrdinalIgnoreCase))
                        || two.StartsWith(blob, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                if (stripped is not null) System.Buffers.ArrayPool<byte>.Shared.Return(stripped);
            }
        }

        /// <summary>
        /// The comment lines of the file at <paramref name="fullPath"/>, read as <see cref="Inspect"/> reads it and only
        /// when its content is still <paramref name="blob"/> (when given); null otherwise. The file is held whole only
        /// while it is read (in a pooled buffer when it is under 1 MB), and its lines are never held as strings.
        /// </summary>
        public CommentRuns? ReadComments(string fullPath, string? blob)
        {
            if (_refused(fullPath)) return null;
            byte[]? buffer = null;
            var pooled = false;
            try
            {
                using var stream = Open(fullPath);
                if (stream.Length > MaxHashedBytes) return null;
                var length = (int)stream.Length;
                // A large buffer is left to the collector: the pool would keep it for the life of an idle host.
                pooled = length < MaxPooledBytes;
                buffer = pooled ? System.Buffers.ArrayPool<byte>.Shared.Rent(length + 1) : new byte[length + 1];
                var total = 0;
                int read;
                while (total <= length && (read = stream.Read(buffer, total, length + 1 - total)) > 0) total += read;
                if (total != length) return null;
                if (blob is not null && !Matches(new MemoryStream(buffer, 0, length, writable: false), blob)) return null;
                return CommentRuns.Read(buffer.AsSpan(0, length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            finally
            {
                if (pooled) System.Buffers.ArrayPool<byte>.Shared.Return(buffer!);
            }
        }

        /// <summary>The largest file read into a pooled buffer.</summary>
        private const int MaxPooledBytes = 1024 * 1024;

        private static FileStream Open(string fullPath)
            => new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.SequentialScan);

        /// <summary>git's object id of a blob: the hash of <c>blob &lt;size&gt;\0</c> followed by the content.</summary>
        internal static string BlobId(byte[] content, bool sha256)
        {
            var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
            using var hash = IncrementalHash.CreateHash(sha256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1);
            hash.AppendData(header);
            hash.AppendData(content);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }

    }
}
