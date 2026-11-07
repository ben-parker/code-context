using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// Keeps one copy of an assembly that arrives more than once, as a build's conflict
/// resolution does. A shared framework and a package often both carry the same assembly, and
/// a compiler handed two signed versions of it sees each type twice and binds neither.
/// </summary>
internal static class ReferenceConflicts
{
    /// <summary>
    /// The highest assembly version of each file name wins. On a tie the earlier one stays,
    /// which is the framework's: the compiler treats equal identities as one assembly anyway.
    /// </summary>
    public static List<string> KeepNewest(Disk disk, IEnumerable<string> references)
    {
        var all = references.ToList();
        var losers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var copies in all.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Where(group => group.Skip(1).Any()))
        {
            var winner = copies.First();
            var best = VersionOf(disk, winner);
            foreach (var copy in copies.Skip(1))
            {
                var version = VersionOf(disk, copy);
                if (version > best)
                {
                    losers.Add(winner);
                    winner = copy;
                    best = version;
                }
                else
                {
                    losers.Add(copy);
                }
            }
        }
        return losers.Count == 0 ? all : all.Where(path => !losers.Contains(path)).ToList();
    }

    private static Version VersionOf(Disk disk, string path)
    {
        try
        {
            using var stream = disk.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata ? reader.GetMetadataReader().GetAssemblyDefinition().Version : new Version(0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return new Version(0, 0);
        }
    }
}
