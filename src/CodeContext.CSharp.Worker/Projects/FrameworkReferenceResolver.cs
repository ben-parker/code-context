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
            var pack = PackOf(name, packDownloads, packageFolders, dotnetRoots);
            var directory = FindRefPack(pack, framework, folder, packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
            if (directory is not null)
            {
                assemblies.AddRange(Directory.GetFiles(directory, "*.dll").Order(StringComparer.Ordinal));
            }
            else if (FindRuntime(pack.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase) ? pack[..^4] : name, framework.Version, dotnetRoots) is { } runtime)
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

    /// <summary>
    /// The pack behind a framework reference. A pack usually adds <c>.Ref</c> to the name,
    /// but a reference can name a part of one, as <c>Microsoft.WindowsDesktop.App.WPF</c>
    /// does, or the pack plus a profile, as <c>Microsoft.Windows.SDK.NET.Ref.Windows</c> does.
    /// What a restore downloaded is authoritative; otherwise the name is cut back until a
    /// pack of that name is on this machine.
    /// </summary>
    private static string PackOf(
        string name,
        IReadOnlyDictionary<string, string> packDownloads,
        IReadOnlyList<string> packageFolders,
        IReadOnlyList<string> dotnetRoots)
    {
        var direct = name + ".Ref";
        if (packDownloads.ContainsKey(direct)) return direct;

        var downloaded = packDownloads.Keys
            .Where(pack => name.StartsWith(pack + ".", StringComparison.OrdinalIgnoreCase))
            .MaxBy(pack => pack.Length);
        if (downloaded is not null) return downloaded;

        for (var candidate = name; candidate.Contains('.'); candidate = candidate[..candidate.LastIndexOf('.')])
        {
            var pack = candidate + ".Ref";
            if (PackLocations(pack, packageFolders, dotnetRoots).Exists(Directory.Exists)) return pack;
        }
        return direct;
    }

    private static List<string> PackLocations(
        string pack, IReadOnlyList<string> packageFolders, IReadOnlyList<string> dotnetRoots)
        => packageFolders.Select(root => Path.Combine(root, pack.ToLowerInvariant()))
            .Concat(dotnetRoots.Select(root => Path.Combine(root, "packs", pack)))
            .ToList();

    private static string? FindRefPack(
        string pack,
        TargetFrameworkName framework,
        string folder,
        string? version,
        IReadOnlyList<string> packageFolders,
        IReadOnlyList<string> dotnetRoots)
    {
        var locations = PackLocations(pack, packageFolders, dotnetRoots);

        if (version is not null)
        {
            foreach (var location in locations)
            {
                var exact = Path.Combine(location, version, "ref", folder);
                if (Directory.Exists(exact)) return exact;

                // Some packs ship as a library for the oldest framework they support.
                if (BestLibFolder(Path.Combine(location, version, "lib"), framework) is { } library) return library;
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

    private static string? BestLibFolder(string lib, TargetFrameworkName framework)
    {
        if (!Directory.Exists(lib)) return null;
        return Directory.GetDirectories(lib)
            .Select(directory => (Directory: directory, Parsed: TargetFrameworkName.TryParse(Path.GetFileName(directory), out var asset) ? asset : null))
            .Where(candidate => candidate.Parsed is not null && framework.CanConsume(candidate.Parsed))
            .OrderByDescending(candidate => candidate.Parsed!.Version)
            .Select(candidate => candidate.Directory)
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
        var directory = library ?? FindRefPack(pack, framework, folder, packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
        if (directory is not null)
        {
            return new FrameworkResolution(Directory.GetFiles(directory, "*.dll").Order(StringComparer.Ordinal).ToList(), true, [], []);
        }

        // 2.1 is a superset of what older targets may use.
        var wider = FindRefPack(pack, framework, "netstandard2.1", packDownloads.GetValueOrDefault(pack), packageFolders, dotnetRoots);
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
