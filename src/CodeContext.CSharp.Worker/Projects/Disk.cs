// RS0030 bans every raw route to the file system in this project, so that the worker can only
// reach the disk through this class. This is the one file allowed to use them: it is where the
// guard sits. A test fails if the suppression appears anywhere else.
#pragma warning disable RS0030

using System.Collections.Concurrent;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// The worker's only route to the file system. Reading a repository must never reach out to
/// the network: opening <c>\\host\share</c> makes Windows authenticate to that host, which
/// tells a repository who indexed it and hands it an NTLM exchange to use. Paths arrive from
/// project files, restore data and environment variables, so nothing about a path can be
/// trusted until this class has looked at it.
///
/// On Windows a path is allowed if it is on a drive letter or on one of the shares this
/// instance was given, and everything else is refused, however it is spelled. A refused path
/// looks like a missing one, so callers need no check of their own and have no way to forget
/// one. Off Windows nothing is refused.
///
/// A link is how a path on a drive reaches a share after all. Inside the repository, where a
/// link is the repository's to make, a path is followed through links only while each leads
/// to a place that would itself be read; the link is read, never opened, to find out. Outside
/// the repository a link is the machine owner's and is taken as it is.
///
/// Normalizing a path counts as reaching the disk: <see cref="Path.GetFullPath(string)"/> asks
/// the file system for the long form of any path holding a <c>~</c>, and so opens a connection
/// to whatever host the path names. <see cref="FullPath"/> and <see cref="RelativePath"/> are
/// the guarded forms.
///
/// The worker writes one thing: its own saved state, inside the repository's cache directory
/// (see <see cref="ReplaceStateFile"/>). Nothing else here writes.
/// </summary>
public sealed class Disk
{
    /// <summary>Stands in for <c>~</c> while a path is normalized, which keeps that lexical.</summary>
    private const char Shield = (char)0xE000;

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const int MaxLinkHops = 40;

    /// <summary>Stand in for a directory in <see cref="_realDirectories"/>; compared by reference.</summary>
    private static readonly string RefusedMark = new('!', 1);
    private static readonly string MissingMark = new('?', 1);

    private readonly HashSet<string> _shares = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The repository in full form with no trailing separator, or null with none.</summary>
    private readonly string? _root;

    /// <summary>A directory inside the repository, and where it really is, or a mark.</summary>
    private readonly ConcurrentDictionary<string, string> _realDirectories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A directory, and its entries that are reparse points (true for a directory), from one
    /// listing; null when it cannot be listed.
    /// </summary>
    private readonly ConcurrentDictionary<string, Dictionary<string, bool>?> _reparsePoints = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, Dictionary<string, bool>?> _reparsePointsIn = ReparsePointsIn;
    private readonly Func<string, bool, string?> _linkTarget = LinkTargetOf;

    /// <summary><c>&lt;root&gt;/.codecontext/cache</c>, or null with no root, when nothing may be written.</summary>
    private readonly string? _cacheDirectory;

    /// <param name="root">The repository, whose own share it may read.</param>
    /// <param name="toolchain">Where this machine's owner keeps toolchains.</param>
    public Disk(string? root, ToolchainLocations toolchain)
        : this(toolchain.All.Prepend(root))
    {
        // In full form, as the host's own path is: short names in a root must not fail the comparison.
        if (string.IsNullOrEmpty(root)) return;
        var full = FullPath(root);
        _cacheDirectory = Path.Combine(full, ".codecontext", "cache");
        _root = Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>For tests: a disk whose links are what these say, so a link to a share needs no privilege to make.</summary>
    internal Disk(
        string root,
        ToolchainLocations toolchain,
        Func<string, Dictionary<string, bool>?> reparsePointsIn,
        Func<string, bool, string?> linkTarget)
        : this(root, toolchain)
    {
        _reparsePointsIn = reparsePointsIn;
        _linkTarget = linkTarget;
    }

    /// <summary>Forgets which directories are links. Called before each request that reads the repository.</summary>
    public void ForgetLinks()
    {
        _realDirectories.Clear();
        _reparsePoints.Clear();
    }

    /// <param name="locations">
    /// Places whose shares may be read. These come from the machine's owner, never from a
    /// repository: a share a repository names stays refused even when it asks for it.
    /// </param>
    public Disk(IEnumerable<string?> locations)
    {
        foreach (var location in locations)
        {
            if (location is not null && Lexical(location) is { } full && ShareOf(full) is { } share) _shares.Add(share);
        }
    }

    public bool FileExists(string path) => !Refused(path) && File.Exists(path);

    public bool DirectoryExists(string path) => !Refused(path) && Directory.Exists(path);

    /// <summary>The time .NET reports for a file that is not there, when this one is refused.</summary>
    public DateTime LastWriteUtc(string path)
        => Refused(path) ? DateTime.FromFileTimeUtc(0) : File.GetLastWriteTimeUtc(path);

    public string[] Files(string directory, string pattern)
        => Refused(directory) ? [] : Directory.GetFiles(directory, pattern);

    public string[] Directories(string directory)
        => Refused(directory) ? [] : Directory.GetDirectories(directory);

    /// <summary>The directories here that are not links to somewhere else.</summary>
    public List<string> RealDirectories(string directory)
        => Refused(directory)
            ? []
            : new DirectoryInfo(directory).EnumerateDirectories()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(child => child.FullName)
                .ToList();

    /// <summary>Where a link finally leads, or null when this is not a link.</summary>
    public string? LinkTarget(string path)
        => Refused(path) ? null : new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;

    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public Stream OpenRead(string path) => Refused(path) ? throw Absent(path) : File.OpenRead(path);

    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public string ReadAllText(string path) => Refused(path) ? throw Absent(path) : File.ReadAllText(path);

    /// <summary>A file read once from start to end, whose whole length is wanted up front.</summary>
    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public Stream OpenForOneScan(string path)
        => Refused(path)
            ? throw Absent(path)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);

    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public byte[] ReadAllBytes(string path) => Refused(path) ? throw Absent(path) : File.ReadAllBytes(path);

    /// <summary>A file's size and last-write time, both from one look at it.</summary>
    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public (long Length, DateTime LastWriteUtc) Stat(string path)
    {
        if (Refused(path)) throw Absent(path);
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Reads a project file. A document type is refused, so nothing it names is fetched.</summary>
    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public XDocument LoadXml(string path)
    {
        using var stream = OpenRead(path);
        using var reader = XmlReader.Create(stream, XmlSettings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// An assembly to compile against. Roslyn opens any further modules of the assembly itself,
    /// later, but only by a plain file name beside this one, so they stay where this file is.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public MetadataReference Reference(string path)
        => Refused(path) ? throw Absent(path) : MetadataReference.CreateFromFile(path);

    /// <summary>
    /// Replaces a worker state file: written to <c>&lt;statePath&gt;.tmp</c> (created afresh, user-only
    /// on Unix; whatever was at that name is removed first, never written through), flushed to disk,
    /// then renamed over the path. On any failure the temp file is gone and the exception propagates.
    /// The only write the worker makes, and only to a <c>.state</c> file directly inside a workspace
    /// directory of this repository's cache, <c>&lt;root&gt;/.codecontext/cache/&lt;workspace&gt;/</c>.
    /// No directory is created.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The path is anywhere else. Nothing was touched.</exception>
    public T ReplaceStateFile<T>(string statePath, Func<Stream, T> write, CancellationToken ct)
    {
        if (!IsStateFile(statePath)) throw new UnauthorizedAccessException($"The worker does not write to '{statePath}'.");

        var temp = statePath + ".tmp";
        try
        {
            File.Delete(temp);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0 };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            T written;
            using (var stream = new FileStream(temp, options))
            {
                written = write(stream);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, statePath, overwrite: true);
            return written;
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nothing more can be done; the host deletes temp files it finds.
            }
            throw;
        }
    }

    private bool IsStateFile(string path)
    {
        if (_cacheDirectory is null || Refused(path) || !path.EndsWith(".state", PathComparison)) return false;
        var workspace = Path.GetDirectoryName(FullPath(path));
        return workspace is not null && string.Equals(Path.GetDirectoryName(workspace), _cacheDirectory, PathComparison);
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string, string)"/> for a path nothing has vetted. Short names
    /// are expanded only where reading is allowed; a refused path comes back normalized and
    /// unread. A path that is unusable comes back as it is, for the calls after this to refuse.
    /// </summary>
    public string FullPath(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(path)) return baseDirectory ?? path;
        if (Lexical(path, baseDirectory) is not { } lexical) return path;
        if (!lexical.Contains('~') || Refused(lexical)) return lexical;
        return baseDirectory is null ? Path.GetFullPath(path) : Path.GetFullPath(path, baseDirectory);
    }

    /// <summary><see cref="Path.GetRelativePath"/>, which normalizes both of its arguments.</summary>
    public string RelativePath(string relativeTo, string path)
    {
        if (Lexical(relativeTo) is null || Lexical(path) is null) return path;
        return Refused(relativeTo) || Refused(path)
            ? Path.GetRelativePath(Shielded(relativeTo), Shielded(path)).Replace(Shield, '~')
            : Path.GetRelativePath(relativeTo, path);
    }

    /// <summary>
    /// The one place that decides. Private, because a refused path already looks missing and
    /// so no caller has anything to check.
    /// </summary>
    private bool Refused(string path)
    {
        if (Lexical(path) is not { } full) return true;
        if (!OperatingSystem.IsWindows()) return false;
        return !AllowedAsSpelled(full) || LeadsSomewhereRefused(full);
    }

    private bool AllowedAsSpelled(string full) => IsOnDrive(full) || (ShareOf(full) is { } share && _shares.Contains(share));

    /// <summary>Below the repository, not the repository itself.</summary>
    private bool InRepository(string full)
        => _root is not null && full.Length > _root.Length + 1 && full[_root.Length] == Path.DirectorySeparatorChar
            && full.StartsWith(_root, StringComparison.OrdinalIgnoreCase);

    private bool LeadsSomewhereRefused(string full)
    {
        if (!InRepository(full)) return false;
        var parent = RealDirectory(Path.GetDirectoryName(full)!, 0);
        if (ReferenceEquals(parent, RefusedMark)) return true;
        if (ReferenceEquals(parent, MissingMark)) return false;
        return ReferenceEquals(Followed(Path.Combine(parent, Path.GetFileName(full)), 0), RefusedMark);
    }

    private static bool IsMark(string value) => ReferenceEquals(value, RefusedMark) || ReferenceEquals(value, MissingMark);

    /// <summary>Where a directory really is: itself, unless a link inside the repository leads elsewhere.</summary>
    private string RealDirectory(string directory, int hops)
    {
        if (_realDirectories.TryGetValue(directory, out var known)) return known;
        string real;
        if (!InRepository(directory))
        {
            real = directory;
        }
        else
        {
            var parent = RealDirectory(Path.GetDirectoryName(directory)!, hops);
            real = IsMark(parent) ? parent : Followed(Path.Combine(parent, Path.GetFileName(directory)), hops);
        }
        _realDirectories[directory] = real;
        return real;
    }

    /// <summary>An entry whose directory is real: itself, where its link leads, or a mark.</summary>
    private string Followed(string entry, int hops)
    {
        var directory = Path.GetDirectoryName(entry);
        if (directory is null) return entry;
        var reparsePoints = _reparsePoints.GetOrAdd(directory, _reparsePointsIn);
        if (reparsePoints is null) return MissingMark;
        if (!reparsePoints.TryGetValue(Path.GetFileName(entry), out var isDirectory)) return entry;
        if (hops >= MaxLinkHops) return RefusedMark;

        string? target;
        try
        {
            target = _linkTarget(entry, isDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RefusedMark;
        }

        // A reparse point of another kind, such as a file kept in the cloud, leads nowhere else.
        if (target is null) return entry;
        if (Lexical(target, directory) is not { } full || !AllowedAsSpelled(full)) return RefusedMark;
        if (!InRepository(full)) return full;
        var parent = RealDirectory(Path.GetDirectoryName(full)!, hops + 1);
        return IsMark(parent) ? parent : Followed(Path.Combine(parent, Path.GetFileName(full)), hops + 1);
    }

    /// <summary>One listing of a directory, which says of each entry whether it is a reparse point without opening it.</summary>
    private static Dictionary<string, bool>? ReparsePointsIn(string directory)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a link says, read from the link and not from where it leads.</summary>
    private static string? LinkTargetOf(string entry, bool isDirectory)
        => isDirectory ? new DirectoryInfo(entry).LinkTarget : new FileInfo(entry).LinkTarget;

    /// <summary>
    /// What cannot name a file. A path holding the shield as well as a tilde could not be told
    /// apart from its normalized form, so it is treated the same way.
    /// </summary>
    private static bool IsUsable(string? path)
        => !string.IsNullOrEmpty(path) && !path.Contains('\0') && !(path.Contains(Shield) && path.Contains('~'));

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> without the look at the disk, or null for what
    /// cannot be normalized, such as a path of spaces.
    /// </summary>
    private static string? Lexical(string path, string? baseDirectory = null)
    {
        if (!IsUsable(path) || (baseDirectory is not null && !IsUsable(baseDirectory))) return null;
        try
        {
            var full = baseDirectory is null
                ? Path.GetFullPath(Shielded(path))
                : Path.GetFullPath(Shielded(path), Shielded(baseDirectory));
            return full.Replace(Shield, '~');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Shielded(string path) => path.Replace('~', Shield);

    private static bool IsOnDrive(string fullPath)
        => fullPath.Length >= 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\';

    /// <summary>
    /// <c>host\share</c> of a plain network path. A device path such as <c>\\?\UNC\host\share</c>
    /// has none: the system does not normalize those, so its segments cannot be taken at their word.
    /// </summary>
    private static string? ShareOf(string fullPath)
    {
        if (!fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var parts = fullPath[2..].Split('\\');
        return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 && parts[0] is not ("?" or ".")
            ? parts[0] + '\\' + parts[1]
            : null;
    }

    private static FileNotFoundException Absent(string path) => new($"Could not find file '{path}'.", path);
}
