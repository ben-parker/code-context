using System.Security.Cryptography;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// The per-user HMAC key that authenticates snapshots: 32 random bytes in a file outside every
    /// repository (by default <c>~/.codecontext/cache.key</c>). Created on first use with user-only
    /// permissions: mode 0600 at creation on Unix (never a chmod afterwards), the profile's ACL on
    /// Windows. The key is never logged; nothing here logs at all.
    /// </summary>
    internal static class SnapshotKey
    {
        public const int Length = 32;

        /// <summary>
        /// The default key path, or null when the user profile folder is unknown: a relative path would put
        /// the key under the current directory, which may be the repository. Callers that run under tests
        /// pass their own path instead.
        /// </summary>
        public static string? DefaultPath() => DefaultPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        /// <summary><see cref="DefaultPath()"/> for a given profile folder; null unless it is a fully qualified path.</summary>
        internal static string? DefaultPath(string? userProfile)
            => string.IsNullOrWhiteSpace(userProfile) || !Path.IsPathFullyQualified(userProfile)
                ? null
                : Path.Combine(userProfile, ".codecontext", "cache.key");

        /// <summary>
        /// Reads the key at <paramref name="path"/>, creating it when it does not exist. Throws an
        /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the key cannot be
        /// read or created, and an <see cref="InvalidDataException"/> when the file is not a key; the
        /// caller then runs without a cache.
        /// </summary>
        public static byte[] LoadOrCreate(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            if (TryReadWhole(path, out var existing)) return existing;

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // The key file itself is created exclusively (O_CREAT | O_EXCL on Unix, CREATE_NEW on
            // Windows), so exactly one concurrent creator, thread or process, wins and every other one
            // reads the winner's key. A rename into place cannot promise that: .NET's
            // File.Move(overwrite: false) on Unix checks for the destination and then calls rename(2),
            // which replaces a file another creator published in between. A loser may open the file
            // before the winner's single write lands; TryReadWhole waits for the whole key.
            var key = RandomNumberGenerator.GetBytes(Length);
            FileStream stream;
            try
            {
                stream = new FileStream(path, CreateOptions());
            }
            catch (IOException) when (File.Exists(path))
            {
                if (TryReadWhole(path, out var winner)) return winner;
                throw new IOException("The snapshot key could not be created.");
            }
            try
            {
                using (stream)
                {
                    stream.Write(key);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch
            {
                // A short file left behind would be refused on every later start; take it away.
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            return key;
        }

        /// <summary>How long a reader waits for a key file shorter than a key to receive the rest of it.</summary>
        private static readonly TimeSpan PartialKeyWait = TimeSpan.FromSeconds(2);

        /// <summary>
        /// <see cref="TryRead"/>, except that a file shorter than a key may be one a concurrent first
        /// user has just created and not yet written: it is read again until it holds a whole key or
        /// <see cref="PartialKeyWait"/> passes, and refused after that. A file longer than a key, or one
        /// that stays short, is not a key and is never overwritten.
        /// </summary>
        private static bool TryReadWhole(string path, out byte[] key)
        {
            var deadline = DateTime.UtcNow + PartialKeyWait;
            while (true)
            {
                try
                {
                    return TryRead(path, out key);
                }
                catch (InvalidDataException) when (DateTime.UtcNow < deadline && !IsLongerThanAKey(path))
                {
                    Thread.Sleep(10);
                }
            }
        }

        // Not "shorter than": the winner's write may land between TryRead's length check and this one,
        // and a file that has just become a whole key is read again, not refused.
        private static bool IsLongerThanAKey(string path)
        {
            try
            {
                return new FileInfo(path).Length > Length;
            }
            catch (IOException)
            {
                return true;
            }
        }

        /// <summary>
        /// Reads an existing key; false when the file does not exist. Throws an
        /// <see cref="InvalidDataException"/> when the file is not a key, and on Unix an
        /// <see cref="UnauthorizedAccessException"/> when its mode grants group or others any access
        /// (another user may have read the key, or may replace it); the caller runs without a cache.
        /// On Windows the profile's ACL protects the key.
        /// </summary>
        public static bool TryRead(string path, out byte[] key)
        {
            key = Array.Empty<byte>();
            if (!File.Exists(path)) return false;
            // FileShare.ReadWrite: on Windows a first user may still hold the file open for writing
            // its key, and a reader that shared only reading would fail with a sharing violation
            // instead of seeing the short file and waiting for the rest.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(stream.SafeFileHandle);
                const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & GroupOrOther) != 0)
                    throw new UnauthorizedAccessException(
                        $"The snapshot key file grants group or others access (mode {Convert.ToString((int)mode, 8)}); it must be user-only (0600).");
            }
            if (stream.Length != Length) throw new InvalidDataException(
                    "The snapshot key file does not hold a key (a first start that was interrupted can leave it empty). Delete this file and the next start makes a new key.");
            var bytes = new byte[Length];
            stream.ReadExactly(bytes);
            key = bytes;
            return true;
        }

        private static FileStreamOptions CreateOptions()
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                // Readable while it is written (see TryRead), so a concurrent first user on Windows
                // sees a short file and waits for the rest rather than failing with a sharing violation.
                Share = FileShare.Read,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return options;
        }

    }
}
