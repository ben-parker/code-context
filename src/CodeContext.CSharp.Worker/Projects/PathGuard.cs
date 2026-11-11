// Path.GetFullPath and Path.GetRelativePath are banned because they ask the disk about a path
// holding a tilde. Here they are given only shielded paths, which hold none.
#pragma warning disable RS0030

using System.Collections.Concurrent;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// What <see cref="PathGuard"/> may see of the disk. None of it follows a link, so asking
/// never reaches where a link leads.
/// </summary>
internal interface ILinkProbe
{
    /// <summary>
    /// The entries of a directory that are reparse points, by name, each with whether it is a
    /// directory; null when the directory cannot be listed. One listing answers for every
    /// entry in it.
    /// </summary>
    Dictionary<string, bool>? ReparsePointsIn(string directory);

    /// <summary>
    /// One entry, asked for by the name given, which may be a second name for it; null when
    /// nothing answers to that name.
    /// </summary>
    (bool IsReparsePoint, bool IsDirectory)? Entry(string path);

    /// <summary>Whether a reparse point is a link, and not, say, a file kept in the cloud.</summary>
    bool IsLink(string path, bool isDirectory);

    /// <summary>A path outside every repository with its short names written out in full.</summary>
    string Expand(string path);
}

/// <summary>
/// Decides which paths the worker may touch. It only decides: it opens nothing, and sees the
/// disk through an <see cref="ILinkProbe"/>, so every rule here can be tested with a disk
/// that is made up. <see cref="Disk"/> asks it before each access.
///
/// On Windows a path is allowed when it is on a drive letter or on one of the shares this
/// instance was given, however it is spelled, and when no link inside the repository stands
/// in it. The host walks a repository without following a link, and so does the worker: a
/// link the repository holds is refused, whatever it says it leads to, because what a link
/// says can be written so that it reads as one place and leads to another. Off Windows
/// nothing is refused.
///
/// A path can name a link without spelling its name: by its short name
/// (<c>LONGLI~1</c>), by a stream of it (<c>link::$INDEX_ALLOCATION</c>), or through a second
/// name of a directory above the repository. A stream is refused wherever it stands. A name
/// that could be a second name is asked about, and second names above the repository are
/// written out before the path is judged.
/// </summary>
internal sealed class PathGuard
{
    /// <summary>Stands in for <c>~</c> while a path is normalized, which keeps that lexical.</summary>
    private const char Shield = (char)0xE000;

    private enum Entry
    {
        Plain,
        Link,
        Missing,
    }

    private readonly HashSet<string> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILinkProbe _probe;
    private readonly bool _windows;

    /// <summary>The repository in full form with a trailing separator, or null with none.</summary>
    private readonly string? _rootPrefix;

    private readonly ConcurrentDictionary<string, Entry> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Dictionary<string, bool>?> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _expansions = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">The repository, whose own share may be read and whose links are refused.</param>
    /// <param name="locations">
    /// Places whose shares may be read. These come from the machine's owner, never from a
    /// repository: a share a repository names stays refused even when it asks for it.
    /// </param>
    public PathGuard(string? root, IEnumerable<string?> locations, ILinkProbe probe, bool windows)
    {
        _probe = probe;
        _windows = windows;
        foreach (var location in locations.Prepend(root))
        {
            if (!string.IsNullOrEmpty(location) && Lexical(location) is { } full && ShareOf(full) is { } share) _shares.Add(share);
        }

        if (string.IsNullOrEmpty(root) || Lexical(root) is not { } lexical) return;

        // In full form, as the host's own path is: short names in a root must not fail the comparison.
        Root = Path.TrimEndingDirectorySeparator(AllowedAsSpelled(lexical) ? Expanded(lexical) : lexical);
        _rootPrefix = Root.EndsWith(Path.DirectorySeparatorChar) ? Root : Root + Path.DirectorySeparatorChar;
    }

    /// <summary>The repository in full form, or null with none.</summary>
    public string? Root { get; }

    /// <summary>The one place that decides.</summary>
    public bool Refused(string path)
    {
        if (Lexical(path) is not { } full) return true;
        if (!_windows) return false;
        return !AllowedAsSpelled(full) || HoldsALink(WithoutShortNamesOutside(full));
    }

    /// <summary>Below the repository, not the repository itself.</summary>
    public bool InRepository(string path)
        => Lexical(path) is { } full && Below(_windows ? WithoutShortNamesOutside(full) : full);

    /// <summary>Whether a link the repository holds stands in a path below it, on any platform.</summary>
    public bool ThroughALink(string path) => Lexical(path) is { } full && HoldsALink(_windows ? WithoutShortNamesOutside(full) : full);

    /// <summary>Forgets what was learned of the disk. Called before each request that reads the repository.</summary>
    public void Forget()
    {
        _directories.Clear();
        _listings.Clear();
        _expansions.Clear();
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string, string)"/> for a path nothing has vetted. Short names
    /// are written out only above the repository and only where reading is allowed; a refused
    /// path comes back normalized and unread. A path that is unusable comes back as it is, for
    /// the calls after this to refuse.
    /// </summary>
    public string FullPath(string path, string? baseDirectory = null)
    {
        if (string.IsNullOrEmpty(path)) return baseDirectory ?? path;
        if (Lexical(path, baseDirectory) is not { } lexical) return path;
        return _windows && AllowedAsSpelled(lexical) ? WithoutShortNamesOutside(lexical) : lexical;
    }

    /// <summary><see cref="Path.GetRelativePath"/>, which normalizes both of its arguments, without the look at the disk.</summary>
    public static string RelativePath(string relativeTo, string path)
    {
        if (Lexical(relativeTo) is null || Lexical(path) is null) return path;
        return Path.GetRelativePath(Shielded(relativeTo), Shielded(path)).Replace(Shield, '~');
    }

    /// <summary>
    /// On a drive or a share that was given. A colon after the drive names a stream, and a
    /// stream of a directory is the directory by another name, which no comparison of names
    /// would see; nothing the worker reads has one.
    /// </summary>
    private bool AllowedAsSpelled(string full)
        => full.IndexOf(':', 2) < 0 && (IsOnDrive(full) || (ShareOf(full) is { } share && _shares.Contains(share)));

    private bool Below(string full)
        => _rootPrefix is not null && full.Length > _rootPrefix.Length && full.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase);

    private bool IsRootOrBelow(string full)
        => _rootPrefix is not null && (Below(full) || string.Equals(full, Root, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path with the short names above the repository written out, so that a path which
    /// enters the repository by one is seen to be inside it. Each is asked for only while the
    /// path so far is outside the repository, where a link is the owner's.
    /// </summary>
    private string WithoutShortNamesOutside(string full)
    {
        if (_rootPrefix is null) return full;
        var start = Path.GetPathRoot(full)?.Length ?? 0;
        if (start == 0) return full;
        var prefix = full[..start];
        var names = full[start..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (!names.Any(CouldBeASecondName)) return full;
        for (var i = 0; i < names.Length; i++)
        {
            var candidate = Path.Join(prefix, names[i]);
            if (CouldBeASecondName(names[i]) && !IsRootOrBelow(candidate))
            {
                candidate = Expanded(candidate);

                // The owner's own link to a share: theirs, and taken as it is.
                if (!AllowedAsSpelled(candidate)) return full;
            }

            if (IsRootOrBelow(candidate)) return Path.Join([candidate, .. names[(i + 1)..]]);
            prefix = candidate;
        }

        return prefix;
    }

    private string Expanded(string path)
        => _expansions.GetOrAdd(path, spelled =>
        {
            try
            {
                return Lexical(_probe.Expand(spelled)) ?? spelled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return spelled;
            }
        });

    /// <summary>Whether a link the repository holds stands anywhere in a path below it.</summary>
    private bool HoldsALink(string full)
    {
        if (!Below(full)) return false;
        var parent = Path.GetDirectoryName(full)!;
        return Directory(parent) switch
        {
            Entry.Link => true,
            Entry.Missing => false,
            _ => Step(parent, Path.GetFileName(full)) == Entry.Link,
        };
    }

    /// <summary>A directory at or below the repository: plain all the way down, behind a link, or not there.</summary>
    private Entry Directory(string directory)
    {
        if (directory.Length <= Root!.Length) return Entry.Plain;
        return _directories.GetOrAdd(directory, below =>
        {
            var parent = Path.GetDirectoryName(below)!;
            var above = Directory(parent);
            return above == Entry.Plain ? Step(parent, Path.GetFileName(below)) : above;
        });
    }

    /// <summary>One name in a directory that is itself plain.</summary>
    private Entry Step(string directory, string name)
    {
        var reparsePoints = _listings.GetOrAdd(directory, _probe.ReparsePointsIn);
        if (reparsePoints is null) return Entry.Missing;
        var path = Path.Join(directory, name);
        try
        {
            if (!reparsePoints.TryGetValue(name, out var isDirectory))
            {
                // Not a reparse point by this name. A name that could be a second one is asked about.
                if (!CouldBeASecondName(name)) return Entry.Plain;
                if (_probe.Entry(path) is not { } entry) return Entry.Missing;
                if (!entry.IsReparsePoint) return Entry.Plain;
                isDirectory = entry.IsDirectory;
            }

            return _probe.IsLink(path, isDirectory) ? Entry.Link : Entry.Plain;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Entry.Link;
        }
    }

    /// <summary>A short name, or a name Windows trims before it looks.</summary>
    private static bool CouldBeASecondName(string name) => name.Contains('~') || name[^1] is '.' or ' ';

    /// <summary>What cannot name a file. The shield is refused outright: it would come back as a tilde.</summary>
    private static bool IsUsable(string? path)
        => !string.IsNullOrEmpty(path) && !path.Contains('\0') && !path.Contains(Shield);

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> without the look at the disk, or null for what
    /// cannot be normalized, such as a path of spaces.
    /// </summary>
    internal static string? Lexical(string path, string? baseDirectory = null)
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
}
