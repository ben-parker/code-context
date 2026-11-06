using CodeContext.Core.Workers;

namespace CodeContext.Core.Snapshots
{
    /// <summary>A file in the workspace cache directory that this code wrote, recognized by its name.</summary>
    internal enum CacheFileKind
    {
        /// <summary><c>graph.&lt;sequence&gt;.&lt;id&gt;.ccsnap</c>.</summary>
        Snapshot,

        /// <summary><c>graph.&lt;sequence&gt;.&lt;id&gt;.tmp</c>: a write in progress, or left by a crash.</summary>
        Temp,

        /// <summary><c>worker-&lt;parser&gt;.&lt;sequence&gt;.&lt;id&gt;.state</c>: a worker's saved state (<see cref="WorkerStateFile"/>).</summary>
        WorkerState,

        /// <summary><c>worker-&lt;parser&gt;.&lt;sequence&gt;.&lt;id&gt;.state.tmp</c>: a worker's save in progress, or left by a crash.</summary>
        WorkerStateTemp,
    }

    /// <summary>One of this code's files; <see cref="Sequence"/> is the snapshot sequence its name carries.</summary>
    internal sealed record CacheFile(string Path, string Name, CacheFileKind Kind, ulong Sequence)
    {
        public bool IsWorkerState => Kind is CacheFileKind.WorkerState or CacheFileKind.WorkerStateTemp;
    }

    /// <summary>How taking the writer lock ended.</summary>
    internal enum WriterLockResult
    {
        Acquired,

        /// <summary>Another process (or another instance in this one) holds it: read-only.</summary>
        HeldElsewhere,

        /// <summary>The lock path is a link, a directory, or cannot be opened for another reason: no cache.</summary>
        Refused,
    }

    /// <summary>
    /// The cache directory of plan section 3.1, <c>&lt;root&gt;/.codecontext/cache/&lt;workspace&gt;/</c>, and the
    /// only code that creates, enumerates or deletes anything in it.
    ///
    /// The directory lives inside a repository, which may be hostile. So: the cache is refused when
    /// <c>.codecontext</c>, <c>cache</c> or the workspace directory is a symlink, junction or other
    /// reparse point, or exists as something other than a directory; nothing here follows a link
    /// (files are created new, renamed, or deleted by name, which acts on a link itself and never on
    /// its target, and enumeration skips links); and only files whose names this code produces are ever
    /// deleted. Directories this code creates are 0700 on Unix. Nothing read from the directory is
    /// parsed here: snapshots are read only through <see cref="SnapshotFile.Open"/>.
    /// </summary>
    internal sealed class CacheDirectory
    {
        public const string DirectoryName = ".codecontext";
        public const string CacheDirectoryName = "cache";
        public const string LockFileName = "writer.lock";
        public const string GitignoreFileName = ".gitignore";

        /// <summary>The host's workspace id, which names its directory (the id every worker shard of this host carries).</summary>
        public const string HostWorkspaceId = LanguageWorkerService.DefaultWorkspaceId;

        private const string SnapshotSuffix = ".ccsnap";
        private const string TempSuffix = ".tmp";
        private const string GitignoreTempName = ".gitignore.tmp";

        /// <summary>Ignores everything in the cache directory, this file included, so <c>git status</c> stays clean.</summary>
        private static readonly byte[] GitignoreContent = "*\n"u8.ToArray();

        private const UnixFileMode UserOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        private CacheDirectory(string codecontextPath, string cachePath, string workspacePath)
        {
            CodecontextPath = codecontextPath;
            CachePath = cachePath;
            WorkspacePath = workspacePath;
        }

        /// <summary><c>&lt;root&gt;/.codecontext</c>.</summary>
        public string CodecontextPath { get; }

        /// <summary><c>&lt;root&gt;/.codecontext/cache</c>.</summary>
        public string CachePath { get; }

        /// <summary><c>&lt;root&gt;/.codecontext/cache/&lt;workspace&gt;</c>, where snapshots and the lock live.</summary>
        public string WorkspacePath { get; }

        /// <summary>
        /// Opens the cache directory under <paramref name="rootPath"/>, creating each missing level. Returns
        /// null with the reason when a level is a link or not a directory, or cannot be created.
        /// </summary>
        public static CacheDirectory? Open(string rootPath, string workspaceId, out string? refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(workspaceId) || workspaceId.IndexOfAny(['/', '\\', ':']) >= 0 || workspaceId is "." or "..")
            {
                refusal = $"the workspace id '{workspaceId}' cannot name a directory";
                return null;
            }

            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
            {
                refusal = $"the root '{root}' is not a directory";
                return null;
            }

            var codecontext = Path.Combine(root, DirectoryName);
            var cache = Path.Combine(codecontext, CacheDirectoryName);
            var workspace = Path.Combine(cache, workspaceId);
            foreach (var level in new[] { codecontext, cache, workspace })
            {
                refusal = EnsureDirectory(level);
                if (refusal is not null) return null;
            }
            return new CacheDirectory(codecontext, cache, workspace);
        }

        /// <summary>
        /// Checks the three levels again: why one is now a link, another reparse point, missing, or not a
        /// directory, or null when all three are still plain directories.
        ///
        /// What this proves differs by platform. On Windows, once the writer lock's handle is open inside
        /// the workspace directory, the system refuses to rename any directory above it, so after the
        /// check that follows the lock (see <see cref="TryAcquireWriterLock"/>) the chain cannot change
        /// for as long as the lock is held. On Unix a directory can be renamed while files in it are open,
        /// so a check proves only that the chain was plain at that instant: a process that can write the
        /// repository can still swap a level for a symbolic link between this check and the next
        /// path-based operation (create, rename, open, delete), which then follows the link. The service
        /// re-checks before every write, every retire pass and the start-up cleanup, which narrows that
        /// window to the operations of one write; closing it needs <c>openat</c> with
        /// <c>O_NOFOLLOW</c> relative to a held directory descriptor, which .NET does not expose.
        /// </summary>
        public string? RecheckLevels()
        {
            foreach (var level in new[] { CodecontextPath, CachePath, WorkspacePath })
            {
                try
                {
                    if (CheckNotLink(level, expectDirectory: true) is { } refusal) return refusal;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return $"the cache directory '{level}' cannot be checked: {exception.Message}";
                }
            }
            return null;
        }

        /// <summary>
        /// Takes the exclusive writer lock: an open handle on <c>writer.lock</c> that nobody else can open
        /// (sharing mode none, which .NET maps to <c>flock(LOCK_EX)</c> on Unix). The OS releases it when the
        /// process exits however it exits, so a crash never blocks the next start; the file's existence
        /// means nothing. The caller holds the returned stream for the process lifetime.
        /// </summary>
        public WriterLockResult TryAcquireWriterLock(out FileStream? lockStream, out string? reason)
        {
            lockStream = null;
            reason = null;
            var path = Path.Combine(WorkspacePath, LockFileName);
            if (CheckNotLink(path, expectDirectory: false) is { } refusal)
            {
                reason = refusal;
                return WriterLockResult.Refused;
            }

            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 0,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UserOnlyFile;
            try
            {
                lockStream = new FileStream(path, options);
            }
            catch (IOException exception) when (IsHeldByAnother(exception))
            {
                reason = $"another instance holds {path} ({exception.Message})";
                return WriterLockResult.HeldElsewhere;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reason = $"the writer lock {path} cannot be opened: {exception.Message}";
                return WriterLockResult.Refused;
            }

            // Re-checked after the open: a link that appeared at the lock path is refused, and so is a
            // level of the chain swapped for a link (or junction) between Open and now, which would have
            // put the lock outside the repository. On Windows the open handle now pins the chain.
            if ((CheckNotLink(path, expectDirectory: false) ?? RecheckLevels()) is { } late)
            {
                lockStream.Dispose();
                lockStream = null;
                reason = late;
                return WriterLockResult.Refused;
            }
            return WriterLockResult.Acquired;
        }

        private const int ErrorSharingViolation = unchecked((int)0x80070020);
        private const int ErrorLockViolation = unchecked((int)0x80070021);

        /// <summary>
        /// Whether opening the lock failed because another handle holds it. On Windows, a sharing or lock
        /// violation. On Unix, .NET takes <c>flock(LOCK_EX | LOCK_NB)</c> for a <see cref="FileShare.None"/>
        /// open and reports its would-block failure as an <see cref="IOException"/> whose HResult is the
        /// raw errno: <c>EWOULDBLOCK</c>, which is 11 on Linux and 35 on macOS and FreeBSD. Any other
        /// failure is not contention.
        /// </summary>
        internal static bool IsHeldByAnother(IOException exception)
        {
            if (OperatingSystem.IsWindows()) return exception.HResult is ErrorSharingViolation or ErrorLockViolation;
            var wouldBlock = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() || OperatingSystem.IsIOS() ? 35 : 11;
            return exception.HResult == wouldBlock;
        }

        /// <summary>
        /// Makes <c>cache/.gitignore</c> hold exactly <c>*</c>, so nothing below shows in <c>git status</c>.
        /// Returns why it could not, or null. Written new, or replaced by a rename; never written through.
        /// </summary>
        public string? EnsureGitignore()
        {
            var path = Path.Combine(CachePath, GitignoreFileName);
            try
            {
                if (TryGetAttributes(path, out var attributes))
                {
                    if (CheckNotLink(path, expectDirectory: false) is { } refusal) return refusal;
                    if (HasContent(path, attributes)) return null;
                    var temp = Path.Combine(CachePath, GitignoreTempName);
                    if (TryGetAttributes(temp, out _))
                    {
                        if (CheckNotLink(temp, expectDirectory: false) is { } tempRefusal) return tempRefusal;
                        File.Delete(temp);
                    }
                    WriteNew(temp);
                    File.Move(temp, path, overwrite: true);
                    return null;
                }
                WriteNew(path);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"the cache .gitignore cannot be written: {exception.Message}";
            }
        }

        /// <summary>
        /// The snapshot, worker state and temp files in the workspace directory, by name. Directories, links
        /// and every other name are left out, so they are never touched.
        /// </summary>
        public IReadOnlyList<CacheFile> EnumerateOwnFiles()
        {
            var files = new List<CacheFile>();
            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Directory | FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false,
            };
            foreach (var path in Directory.EnumerateFiles(WorkspacePath, "graph.*", enumeration))
            {
                var name = Path.GetFileName(path);
                if (SnapshotFile.TryParseName(name, out var sequence))
                    files.Add(new CacheFile(path, name, CacheFileKind.Snapshot, sequence));
                else if (TryParseTempName(name, out sequence))
                    files.Add(new CacheFile(path, name, CacheFileKind.Temp, sequence));
            }
            // Matched case-insensitively on Windows, parsed ordinally: an upper-case WORKER-...STATE is never listed or deleted (never made here).
            foreach (var path in Directory.EnumerateFiles(WorkspacePath, WorkerStateFile.Prefix + "*", enumeration))
            {
                var name = Path.GetFileName(path);
                if (WorkerStateFile.TryParseName(name, out _, out var sequence))
                    files.Add(new CacheFile(path, name, CacheFileKind.WorkerState, sequence));
                else if (WorkerStateFile.TryParseTempName(name, out sequence))
                    files.Add(new CacheFile(path, name, CacheFileKind.WorkerStateTemp, sequence));
            }
            return files;
        }

        /// <summary>
        /// The absolute path of the worker state file <paramref name="fileName"/> of parser
        /// <paramref name="parserId"/>, and its length, when the name is one this code produces for that parser
        /// and a regular file (not a link, not a directory) of that name is in the workspace directory; else
        /// null. The only way a name read from a snapshot becomes a path: the name is checked against
        /// <see cref="WorkerStateFile"/>'s pattern, which admits no separator, no <c>..</c> and nothing rooted,
        /// and is joined to this directory.
        /// </summary>
        public (string Path, long Length)? ExistingWorkerStateFile(string parserId, string fileName)
        {
            if (!WorkerStateFile.TryParseName(fileName, out var owner, out _)
                || !string.Equals(owner, parserId, StringComparison.Ordinal)) return null;
            var path = PathOf(fileName);
            try
            {
                if (!TryGetAttributes(path, out var attributes) || CheckNotLink(path, expectDirectory: false) is not null) return null;
                if ((attributes & FileAttributes.Directory) != 0) return null;
                return (path, new FileInfo(path).Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Deletes a worker state file, and its temp file, of this code's names; quietly, for a save that will not be recorded.</summary>
        public void DeleteWorkerStateQuietly(string fileName)
        {
            if (!WorkerStateFile.TryParseName(fileName, out _, out var sequence)) return;
            foreach (var (name, kind) in new[] { (fileName, CacheFileKind.WorkerState), (WorkerStateFile.TempNameFor(fileName), CacheFileKind.WorkerStateTemp) })
            {
                var path = PathOf(name);
                try
                {
                    // Only this code's names, and never through a link: a link or directory there is left alone.
                    if (TryGetAttributes(path, out var attributes) && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                        && new FileInfo(path).LinkTarget is null)
                        TryDelete(new CacheFile(path, name, kind, sequence));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left for the next retire pass or start-up cleanup.
                }
            }
        }

        /// <summary>The temp name a write of <paramref name="snapshotName"/> uses before its rename.</summary>
        public static string TempNameFor(string snapshotName)
        {
            if (!snapshotName.EndsWith(SnapshotSuffix, StringComparison.Ordinal))
                throw new ArgumentException("Not a snapshot name.", nameof(snapshotName));
            return string.Concat(snapshotName.AsSpan(0, snapshotName.Length - SnapshotSuffix.Length), TempSuffix);
        }

        /// <summary>Parses <c>graph.&lt;10 digits&gt;.&lt;8 lowercase hex&gt;.tmp</c>.</summary>
        public static bool TryParseTempName(string fileName, out ulong sequence)
        {
            sequence = 0;
            if (!fileName.EndsWith(TempSuffix, StringComparison.Ordinal)) return false;
            return SnapshotFile.TryParseName(
                string.Concat(fileName.AsSpan(0, fileName.Length - TempSuffix.Length), SnapshotSuffix), out sequence);
        }

        public string PathOf(string fileName) => Path.Combine(WorkspacePath, fileName);

        /// <summary>
        /// Deletes one of this code's files. False when it cannot be deleted now (another process has it
        /// open or mapped on Windows, or an antivirus holds it); the caller retries later. A file refused
        /// because it is read-only (Windows refuses to delete one) has the attribute cleared and is
        /// deleted again once, so a planted read-only name cannot stay forever. Enumeration skips links,
        /// so the attribute is cleared on this code's own name, never on a link's target.
        /// </summary>
        public static bool TryDelete(CacheFile file)
        {
            try
            {
                File.Delete(file.Path);
                return true;
            }
            catch (UnauthorizedAccessException) when (TryClearReadOnly(file.Path))
            {
                try
                {
                    File.Delete(file.Path);
                    return true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool TryClearReadOnly(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string? EnsureDirectory(string path)
        {
            try
            {
                if (!TryGetAttributes(path, out _))
                {
                    if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
                    else Directory.CreateDirectory(path, UserOnlyDirectory);
                }
                return CheckNotLink(path, expectDirectory: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"the cache directory '{path}' cannot be created: {exception.Message}";
            }
        }

        /// <summary>Why <paramref name="path"/> may not be used, or null when it is absent or of the expected kind and no link.</summary>
        private static string? CheckNotLink(string path, bool expectDirectory)
        {
            if (!TryGetAttributes(path, out var attributes)) return expectDirectory ? $"'{path}' does not exist" : null;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).LinkTarget is not null)
                return $"'{path}' is a link or another reparse point";
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (expectDirectory && !isDirectory) return $"'{path}' exists and is not a directory";
            if (!expectDirectory && isDirectory) return $"'{path}' is a directory";
            return null;
        }

        /// <summary>The attributes of the entry itself (a link's own, never its target's); false when nothing is there.</summary>
        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                attributes = default;
                return false;
            }
        }

        private static bool HasContent(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Directory) != 0) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != GitignoreContent.Length) return false;
            var bytes = new byte[GitignoreContent.Length];
            stream.ReadExactly(bytes);
            return bytes.AsSpan().SequenceEqual(GitignoreContent);
        }

        private static void WriteNew(string path)
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UserOnlyFile;
            using var stream = new FileStream(path, options);
            stream.Write(GitignoreContent);
            stream.Flush(flushToDisk: true);
        }
    }
}
