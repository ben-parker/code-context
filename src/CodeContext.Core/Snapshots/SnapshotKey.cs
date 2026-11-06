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
            if (TryRead(path, out var existing)) return existing;

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Written to a private temp name and moved into place without overwrite, so a concurrent
            // reader never sees a short key and two creators agree on the one that won.
            var key = RandomNumberGenerator.GetBytes(Length);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, CreateOptions()))
                {
                    stream.Write(key);
                    stream.Flush(flushToDisk: true);
                }
                try
                {
                    File.Move(temp, path, overwrite: false);
                    return key;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // Another process created the key first; use that one.
                }
            }
            finally
            {
                TryDelete(temp);
            }

            if (TryRead(path, out var winner)) return winner;
            throw new IOException("The snapshot key could not be created.");
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
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(stream.SafeFileHandle);
                const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                if ((mode & GroupOrOther) != 0)
                    throw new UnauthorizedAccessException(
                        $"The snapshot key file grants group or others access (mode {Convert.ToString((int)mode, 8)}); it must be user-only (0600).");
            }
            if (stream.Length != Length) throw new InvalidDataException("The snapshot key file does not hold a key.");
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
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            return options;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
