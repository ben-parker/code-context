namespace CodeContext.Core.Paths;

/// <summary>
/// Which of the paths a worker reported (protocol <c>inputs</c>) the host may look at. A repository decides what its
/// worker reports: a project or a source file can name any path, and a path the worker's own guard refused looks
/// absent to it and may be reported as such. On Windows, looking at <c>\\host\share</c> authenticates to that host,
/// so nothing a repository writes may make the host look there, as nothing makes a worker (AGENTS.md). The rule is
/// the C# worker's <see cref="PathGuard"/>, the same source compiled here: on Windows a path is looked at only on a
/// drive or on the share the root itself is on, with no stream and no device's name; and on every platform no link
/// the repository holds is followed, as the host follows none when it walks the root.
/// The shares of the toolchain, which the machine's owner chose and the worker may read, are not known here, so a
/// path on one is refused too.
///
/// A refused path is never answered for: the caller treats what it belongs to as not known, never as unchanged.
/// </summary>
internal static class ReportedPaths
{
    /// <summary>A disk with no links on it, for the part of the rule that reads only a path's text.</summary>
    internal static readonly ILinkProbe NoDisk = new NoLinks();

    /// <summary>
    /// The rule by a path's spelling alone, which looks at nothing: for a report as it arrives. Null off Windows,
    /// where no spelling is refused.
    /// </summary>
    public static Func<string, bool>? RefusedAsSpelled(string? root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var guard = new PathGuard(root, [], NoDisk, windows: true);
        return guard.Refused;
    }

    /// <summary>
    /// The whole rule, for the moment a path is about to be looked at: its spelling, then the links the repository
    /// holds on the way to it, asked of the disk without following any. One instance remembers what it learned of the
    /// disk, so it serves one check. The host's own walkers ask it too, before they look at a path that a watcher event,
    /// a refresh or a changed directory names, so that watcher events and refreshes select what a scan does: a scan
    /// skips each link it lists.
    /// </summary>
    public static Func<string, bool> RefusedOnDisk(string? root)
    {
        var guard = new PathGuard(root, [], new Links(), OperatingSystem.IsWindows());
        return path => guard.Refused(path) || guard.ThroughALink(path);
    }

    private sealed class NoLinks : ILinkProbe
    {
        public Dictionary<string, bool>? ReparsePointsIn(string directory) => [];

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path) => (false, false);

        public bool IsLink(string path, bool isDirectory) => false;

        public string Expand(string path) => path;
    }

    /// <summary>The disk as the C# worker's own probe reads it (<c>Disk.Probe</c>): each answer is about the entry itself.</summary>
    private sealed class Links : ILinkProbe
    {
        public Dictionary<string, bool>? ReparsePointsIn(string directory)
        {
            try
            {
                var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) found[entry.Name] = entry.Attributes.HasFlag(FileAttributes.Directory);
                }
                return found;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes.HasFlag(FileAttributes.ReparsePoint), attributes.HasFlag(FileAttributes.Directory));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        public bool IsLink(string path, bool isDirectory)
            => (isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget) is not null;

        public string Expand(string path) => Path.GetFullPath(path);
    }
}
