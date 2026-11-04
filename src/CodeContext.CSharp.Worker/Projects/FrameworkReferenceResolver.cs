using System.Reflection.PortableExecutable;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>Where a project's framework assemblies were found, or why they were not.</summary>
public sealed record FrameworkResolution(
    IReadOnlyList<string> Assemblies,
    bool Found,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Notes);

/// <summary>
/// Finds the assemblies behind a target framework. Reference packs come first because they
/// describe what the project compiles against; a runtime directory is a stand-in, which is
/// said in a note since its API can differ from the reference pack's.
/// </summary>
public static class FrameworkReferenceResolver
{
    private const string CoreFramework = "Microsoft.NETCore.App";

    public static FrameworkResolution Resolve(
        TargetFrameworkName framework,
        IReadOnlyList<string> frameworkReferences,
        IReadOnlyDictionary<string, string> packDownloads,
        IReadOnlyList<string> packageFolders,
        ToolchainLocations toolchain,
        IReadOnlyList<string> dotnetRoots)
    {
        if (framework.IsNetCoreApp)
        {
            return ResolveNetCore(framework, frameworkReferences, packDownloads, packageFolders, dotnetRoots);
        }
        if (framework.IsNetStandard)
        {
            return ResolveNetStandard(framework, packDownloads, packageFolders, dotnetRoots);
        }
        return ResolveNetFramework(framework, packageFolders, toolchain);
    }

    private static FrameworkResolution ResolveNetCore(
        TargetFrameworkName framework,
        IReadOnlyList<string> frameworkReferences,
        IReadOnlyDictionary<string, string> packDownloads,
        IReadOnlyList<string> packageFolders,
        IReadOnlyList<string> dotnetRoots)
    {
        var folder = framework.Version.Major >= 5
            ? $"net{framework.Version.Major}.{framework.Version.Minor}"
            : $"netcoreapp{framework.Version.Major}.{framework.Version.Minor}";

        var assemblies = new List<string>();
        var missing = new List<string>();
        var notes = new List<string>();
        var coreFound = false;
        foreach (var name in frameworkReferences.Prepend(CoreFramework).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var pack = name + ".Ref";
            var directory = FindRefPack(pack, folder, packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
            if (directory is not null)
            {
                assemblies.AddRange(Directory.GetFiles(directory, "*.dll").Order(StringComparer.Ordinal));
            }
            else if (FindRuntime(name, framework.Version, dotnetRoots) is { } runtime)
            {
                assemblies.AddRange(ManagedAssemblies(runtime.Directory));
                notes.Add(runtime.ExactVersion
                    ? $"{name}: no reference pack for {folder}, using the installed runtime."
                    : $"{name}: {folder} is not installed, using runtime {Path.GetFileName(runtime.Directory)}.");
            }
            else
            {
                missing.Add(name);
                continue;
            }
            if (name.Equals(CoreFramework, StringComparison.OrdinalIgnoreCase)) coreFound = true;
        }
        return new FrameworkResolution(assemblies, coreFound, missing, notes);
    }

    private static string? FindRefPack(
        string pack,
        string folder,
        string? version,
        IReadOnlyList<string> packageFolders,
        IReadOnlyList<string> dotnetRoots)
    {
        var lower = pack.ToLowerInvariant();
        var locations = packageFolders.Select(root => Path.Combine(root, lower))
            .Concat(dotnetRoots.Select(root => Path.Combine(root, "packs", pack)))
            .ToList();

        if (version is not null)
        {
            foreach (var location in locations)
            {
                var exact = Path.Combine(location, version, "ref", folder);
                if (Directory.Exists(exact)) return exact;
            }
        }

        // Unpinned: a pack bundled with the SDK is not in the restore data.
        return locations.Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(directory => Path.Combine(directory, "ref", folder))
            .Where(Directory.Exists)
            .OrderByDescending(VersionOfPackDirectory)
            .FirstOrDefault();
    }

    private static Version VersionOfPackDirectory(string refDirectory)
    {
        var versionDirectory = Path.GetDirectoryName(Path.GetDirectoryName(refDirectory));
        return MsBuildExpander.TryVersion(Path.GetFileName(versionDirectory) ?? string.Empty, out var version)
            ? version
            : new Version(0, 0);
    }

    private static (string Directory, bool ExactVersion)? FindRuntime(
        string name, Version wanted, IReadOnlyList<string> dotnetRoots)
    {
        var installed = dotnetRoots
            .Select(root => Path.Combine(root, "shared", name))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(directory => (Directory: directory, Version: RuntimeVersion(directory)))
            .Where(candidate => candidate.Version is not null)
            .OrderByDescending(candidate => candidate.Version)
            .ToList();
        if (installed.Count == 0) return null;

        foreach (var (directory, version) in installed)
        {
            if (version!.Major == wanted.Major && version.Minor == wanted.Minor) return (directory, true);
        }

        // The closest one that is newer keeps every API the project can name, in all but rare cases.
        var newer = installed.Where(candidate => candidate.Version >= wanted).LastOrDefault();
        return ((newer.Directory ?? installed[0].Directory), false);
    }

    private static Version? RuntimeVersion(string directory)
        => MsBuildExpander.TryVersion(Path.GetFileName(directory), out var version) ? version : null;

    private static IEnumerable<string> ManagedAssemblies(string directory)
        => Directory.GetFiles(directory, "*.dll").Where(IsManagedAssembly).Order(StringComparer.Ordinal);

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    private static FrameworkResolution ResolveNetStandard(
        TargetFrameworkName framework,
        IReadOnlyDictionary<string, string> packDownloads,
        IReadOnlyList<string> packageFolders,
        IReadOnlyList<string> dotnetRoots)
    {
        var folder = $"netstandard{framework.Version.Major}.{framework.Version.Minor}";
        const string pack = "NETStandard.Library.Ref";

        // NETStandard.Library carries the 2.0 surface in build/, added by its own targets.
        var library = packageFolders
            .Select(root => Path.Combine(root, "netstandard.library"))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(directory => Path.Combine(directory, "build", folder, "ref"))
            .Where(Directory.Exists)
            .OrderByDescending(path => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))) is { } version
                && MsBuildExpander.TryVersion(Path.GetFileName(version), out var parsed) ? parsed : new Version(0, 0))
            .FirstOrDefault();
        var directory = library ?? FindRefPack(pack, folder, packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
        if (directory is not null)
        {
            return new FrameworkResolution(Directory.GetFiles(directory, "*.dll").Order(StringComparer.Ordinal).ToList(), true, [], []);
        }

        // 2.1 is a superset of what older targets may use.
        var wider = FindRefPack(pack, "netstandard2.1", packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
        return wider is null
            ? new FrameworkResolution([], false, [folder], [])
            : new FrameworkResolution(
                Directory.GetFiles(wider, "*.dll").Order(StringComparer.Ordinal).ToList(),
                true,
                [],
                [$"{folder}: no reference assemblies, using netstandard2.1."]);
    }

    private static FrameworkResolution ResolveNetFramework(
        TargetFrameworkName framework,
        IReadOnlyList<string> packageFolders,
        ToolchainLocations toolchain)
    {
        var version = framework.Version.Build > 0
            ? $"{framework.Version.Major}.{framework.Version.Minor}.{framework.Version.Build}"
            : $"{framework.Version.Major}.{framework.Version.Minor}";
        var moniker = "net" + version.Replace(".", string.Empty, StringComparison.Ordinal);

        var exact = packageFolders
            .Select(root => Path.Combine(root, $"microsoft.netframework.referenceassemblies.{moniker}"))
            .Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Select(directory => Path.Combine(directory, "build", ".NETFramework", "v" + version))
            .Concat(toolchain.ReferenceAssemblyRoots.Select(root => Path.Combine(root, "v" + version)))
            .Concat(toolchain.ReferenceAssemblyRoots.Select(root => Path.Combine(root, version + "-api")))
            .FirstOrDefault(HoldsFramework);
        if (exact is not null)
        {
            return new FrameworkResolution(FrameworkAssemblies(exact, Directory.GetFiles), true, [], []);
        }

        // A newer framework keeps the older API, and Windows always has its runtime.
        var standIn = NewerReferenceAssemblies(framework.Version, toolchain.ReferenceAssemblyRoots)
            ?? toolchain.NetFrameworkRuntimes.FirstOrDefault(HoldsFramework);
        return standIn is null
            ? new FrameworkResolution([], false, [".NETFramework v" + version], [])
            : new FrameworkResolution(
                FrameworkAssemblies(standIn, (directory, _) => ManagedAssemblies(directory).ToArray()),
                true,
                [],
                [$".NETFramework v{version}: no reference assemblies, using {standIn}."]);
    }

    private static string? NewerReferenceAssemblies(Version wanted, IReadOnlyList<string> roots)
        => roots.Where(Directory.Exists)
            .SelectMany(Directory.GetDirectories)
            .Where(HoldsFramework)
            .Select(directory => (Directory: directory, Version: MsBuildExpander.TryVersion(Path.GetFileName(directory), out var parsed) ? parsed : null))
            .Where(candidate => candidate.Version is not null && candidate.Version >= wanted)
            .OrderBy(candidate => candidate.Version)
            .Select(candidate => candidate.Directory)
            .FirstOrDefault();

    /// <summary>A language pack can leave a version directory holding only documentation.</summary>
    private static bool HoldsFramework(string directory) => File.Exists(Path.Combine(directory, "mscorlib.dll"));

    private static List<string> FrameworkAssemblies(string directory, Func<string, string, string[]> list)
    {
        var assemblies = list(directory, "*.dll").Order(StringComparer.Ordinal).ToList();
        var facades = Path.Combine(directory, "Facades");
        if (Directory.Exists(facades)) assemblies.AddRange(list(facades, "*.dll").Order(StringComparer.Ordinal));
        return assemblies;
    }
}
