using System.Runtime.InteropServices;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// The latest time anything about a file or directory changed, as far as this platform tells, for
    /// <see cref="SnapshotFreshness"/>. On Linux (glibc) it is the status change time (<c>ctime</c>, read with
    /// <c>statx</c>): every write, truncation, rename into place, link, mode change and every setting of the
    /// modification time moves it to the current clock, and no user tool sets it back, so a file unpacked by
    /// <c>tar -x</c>, copied by <c>cp -p</c> or <c>rsync -a</c>, or touched with <c>touch -d</c> still reads as new,
    /// and a modification time set in the future is ignored. Elsewhere (and where <c>statx</c> is missing) it is the
    /// later of the last write and creation times: Windows and macOS keep a creation time that a copy or an unpack
    /// sets to now, but content written in place with an older modification time set afterwards is not seen there.
    ///
    /// Host only: the C# worker never compiles this file (it may not call the system directly).
    /// </summary>
    internal static partial class FileChangeTime
    {
        private const int AtFdCwd = -100;
        private const int AtSymlinkNoFollow = 0x100;
        private const uint StatxMode = 0x2;
        private const uint StatxMtime = 0x40;
        private const uint StatxCtime = 0x80;
        private const int StatxSize = 256;
        private const int ModeOffset = 28;
        private const int CtimeOffset = 96;
        private const int MtimeOffset = 112;
        private const ushort FileTypeMask = 0xF000;
        private const ushort SymbolicLink = 0xA000;

        private static volatile bool s_statxUnavailable = !OperatingSystem.IsLinux();

        /// <summary>Whether <see cref="TryRead"/> reads the status change time in this process (Linux with <c>statx</c>).</summary>
        public static bool ReadsChangeTime
        {
            get
            {
                if (s_statxUnavailable) return false;
                // One real call decides: a missing entry point turns it off for the process.
                TryStatx("/", followLinks: true, out _, out _);
                return !s_statxUnavailable;
            }
        }

        /// <summary>
        /// The entry's latest change time, UTC; false when it does not exist or cannot be read. A symbolic link counts
        /// as the later of itself and what it leads to, since a reader reads through it and replacing it moves only its
        /// own time.
        /// </summary>
        public static bool TryRead(string path, out DateTime latestUtc)
        {
            if (!s_statxUnavailable)
            {
                if (TryStatx(path, followLinks: false, out var own, out var isLink))
                {
                    latestUtc = own;
                    if (isLink && TryStatx(path, followLinks: true, out var target, out _) && target > own) latestUtc = target;
                    return true;
                }
                if (!s_statxUnavailable)
                {
                    latestUtc = default;
                    return false;
                }
            }
            return TryReadManaged(path, out latestUtc);
        }

        /// <summary>The later of the last write and creation times, through .NET; false when the entry is missing.</summary>
        internal static bool TryReadManaged(string path, out DateTime latestUtc)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                if (!info.Exists)
                {
                    latestUtc = default;
                    return false;
                }
                var write = info.LastWriteTimeUtc;
                var creation = info.CreationTimeUtc;
                latestUtc = write > creation ? write : creation;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                latestUtc = default;
                return false;
            }
        }

        private static unsafe bool TryStatx(string path, bool followLinks, out DateTime changedUtc, out bool isLink)
        {
            changedUtc = default;
            isLink = false;
            if (path.Length == 0 || path.Contains('\0')) return false;
            byte* buffer = stackalloc byte[StatxSize];
            int result;
            try
            {
                result = Statx(AtFdCwd, path, followLinks ? 0 : AtSymlinkNoFollow, StatxMode | StatxMtime | StatxCtime, buffer);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                s_statxUnavailable = true;
                return false;
            }
            if (result != 0)
            {
                // ENOSYS: a kernel without statx (before 4.11), so the managed times are used from here on.
                if (Marshal.GetLastPInvokeError() == 38) s_statxUnavailable = true;
                return false;
            }
            var mask = *(uint*)buffer;
            isLink = (mask & StatxMode) != 0 && (*(ushort*)(buffer + ModeOffset) & FileTypeMask) == SymbolicLink;
            // A file system that keeps no status change time (none known on Linux) falls back to the modification time.
            var offset = (mask & StatxCtime) != 0 ? CtimeOffset : MtimeOffset;
            var seconds = *(long*)(buffer + offset);
            var nanoseconds = *(uint*)(buffer + offset + 8);
            try
            {
                changedUtc = DateTime.UnixEpoch.AddTicks(checked(seconds * TimeSpan.TicksPerSecond + nanoseconds / 100));
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
            {
                changedUtc = seconds < 0 ? DateTime.MinValue : DateTime.MaxValue;
            }
            return true;
        }

        [LibraryImport("libc.so.6", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial int Statx(int directory, string path, int flags, uint mask, byte* buffer);
    }
}
