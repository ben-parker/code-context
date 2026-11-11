// RS0030 bans every raw route to the file system in this project, so that the worker can only
// reach the disk through this class. This is the one file allowed to use them: it is where the
// guard sits. A test fails if the suppression appears anywhere else.
#pragma warning disable RS0030

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
/// <see cref="PathGuard"/> decides what may be touched, and this class asks it before every
/// access. A refused path looks like a missing one, so callers need no check of their own and
/// have no way to forget one. Off Windows nothing is refused.
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
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly PathGuard _guard;

    /// <summary><c>&lt;root&gt;/.codecontext/cache</c>, or null with no root, when nothing may be written.</summary>
    private readonly string? _cacheDirectory;

    /// <param name="root">The repository, whose own share it may read.</param>
    /// <param name="toolchain">Where this machine's owner keeps toolchains.</param>
    public Disk(string? root, ToolchainLocations toolchain)
        : this(root, toolchain.All, new Probe())
    {
    }

    /// <param name="locations">
    /// Places whose shares may be read. These come from the machine's owner, never from a
    /// repository: a share a repository names stays refused even when it asks for it.
    /// </param>
    public Disk(IEnumerable<string?> locations)
        : this(null, locations, new Probe())
    {
    }

    /// <summary>For tests: a disk that sees links through <paramref name="probe"/>, so a link to a share needs no privilege to make.</summary>
    internal Disk(string? root, IEnumerable<string?> locations, ILinkProbe probe)
    {
        _guard = new PathGuard(root, locations, probe, OperatingSystem.IsWindows());
        if (_guard.Root is { } full) _cacheDirectory = Path.Combine(full, ".codecontext", "cache");
    }

    /// <summary>Forgets which directories hold links. Called before each request that reads the repository.</summary>
    public void ForgetLinks() => _guard.Forget();

    public bool FileExists(string path) => !Refused(path) && File.Exists(path);

    public bool DirectoryExists(string path) => !Refused(path) && Directory.Exists(path);

    /// <summary>The time .NET reports for a file that is not there, when this one is refused.</summary>
    public DateTime LastWriteUtc(string path)
        => Refused(path) ? DateTime.FromFileTimeUtc(0) : File.GetLastWriteTimeUtc(path);

    /// <param name="pattern">A pattern for names in <paramref name="directory"/>: one that names another directory finds nothing.</param>
    public string[] Files(string directory, string pattern)
        => Refused(directory) || pattern.AsSpan().IndexOfAny('\\', '/', ':') >= 0 ? [] : Directory.GetFiles(directory, pattern);

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
    /// An assembly to compile against. Given a file, Roslyn opens any further modules of the
    /// assembly itself, later, by a plain file name beside it, and asks nobody. Beside a file
    /// in the repository that name could be a link, so such an assembly is read whole here and
    /// Roslyn is given the bytes, from which it can open nothing.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file is missing or refused.</exception>
    public MetadataReference Reference(string path)
    {
        if (Refused(path)) throw Absent(path);
        return _guard.InRepository(path)
            ? MetadataReference.CreateFromImage(File.ReadAllBytes(path), filePath: path)
            : MetadataReference.CreateFromFile(path);
    }

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
        // A link on the way would put the file somewhere else, on any platform.
        if (_cacheDirectory is null || Refused(path) || _guard.ThroughALink(path) || !path.EndsWith(".state", PathComparison)) return false;
        var workspace = Path.GetDirectoryName(FullPath(path));
        return workspace is not null && string.Equals(Path.GetDirectoryName(workspace), _cacheDirectory, PathComparison);
    }

    /// <summary><see cref="PathGuard.FullPath"/>.</summary>
    public string FullPath(string path, string? baseDirectory = null) => _guard.FullPath(path, baseDirectory);

    /// <summary><see cref="Path.GetRelativePath"/>, which normalizes both of its arguments.</summary>
    public string RelativePath(string relativeTo, string path) => PathGuard.RelativePath(relativeTo, path);

    /// <summary>Private, because a refused path already looks missing and so no caller has anything to check.</summary>
    private bool Refused(string path) => _guard.Refused(path);

    private static FileNotFoundException Absent(string path) => new($"Could not find file '{path}'.", path);

    /// <summary>The real disk, asked without following a link.</summary>
    private sealed class Probe : ILinkProbe
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path)
        {
            try
            {
                // The attributes of the entry itself: a link is not followed to answer.
                var attributes = File.GetAttributes(path);
                return (attributes.HasFlag(FileAttributes.ReparsePoint), attributes.HasFlag(FileAttributes.Directory));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        public bool IsLink(string path, bool isDirectory)
            => (isDirectory ? new DirectoryInfo(path).LinkTarget : new FileInfo(path).LinkTarget) is not null;

        public string Expand(string path) => Path.GetFullPath(path);
    }
}
