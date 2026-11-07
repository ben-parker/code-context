namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// Where this machine keeps what a project refers to without naming a path: .NET installs,
/// NuGet package folders and .NET Framework reference assemblies. Only read, never run.
/// </summary>
public sealed record ToolchainLocations(
    IReadOnlyList<string> DotnetRoots,
    IReadOnlyList<string> PackageFolders,
    IReadOnlyList<string> ReferenceAssemblyRoots,
    IReadOnlyList<string> NetFrameworkRuntimes)
{
    /// <summary>Every location, each one found from this machine's own settings.</summary>
    public IEnumerable<string> All => DotnetRoots.Concat(PackageFolders).Concat(ReferenceAssemblyRoots).Concat(NetFrameworkRuntimes);

    public static ToolchainLocations Detect()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dotnetRoots = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            RuntimeInstallRoot(),
        };
        dotnetRoots.AddRange(OnPath());
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (programFiles is { Length: > 0 }) dotnetRoots.Add(Path.Combine(programFiles, "dotnet"));
        dotnetRoots.AddRange(
        [
            Path.Combine(home, ".dotnet"), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet",
            "/usr/local/share/dotnet", "/opt/dotnet",
        ]);

        var packageFolders = new List<string?>
        {
            Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            Path.Combine(home, ".nuget", "packages"),
        };

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var referenceRoots = new List<string?>
        {
            programFilesX86 is { Length: > 0 }
                ? Path.Combine(programFilesX86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework")
                : null,
            "/usr/lib/mono",
            "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono",
        };

        var windows = Environment.GetEnvironmentVariable("WINDIR");
        var runtimes = new List<string?>
        {
            windows is { Length: > 0 } ? Path.Combine(windows, "Microsoft.NET", "Framework64", "v4.0.30319") : null,
            windows is { Length: > 0 } ? Path.Combine(windows, "Microsoft.NET", "Framework", "v4.0.30319") : null,
            "/usr/lib/mono/4.5",
            "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5",
        };

        return new ToolchainLocations(
            Existing(dotnetRoots), Existing(packageFolders), Existing(referenceRoots), Existing(runtimes));
    }

    /// <summary>The install this worker runs from, when it is framework-dependent.</summary>
    private static string? RuntimeInstallRoot()
    {
        var directory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var frameworkFolder = directory is null ? null : Path.GetDirectoryName(directory);
        return Path.GetFileName(frameworkFolder) == "Microsoft.NETCore.App"
            ? Path.GetDirectoryName(Path.GetDirectoryName(frameworkFolder))
            : null;
    }

    private static IEnumerable<string?> OnPath()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (entry.Length == 0) continue;
            string? found = null;
            try
            {
                var candidate = Path.Combine(entry, name);
                if (File.Exists(candidate))
                {
                    var target = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true);
                    found = target is null ? entry : Path.GetDirectoryName(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A PATH entry that cannot be read says nothing about an install.
            }
            if (found is not null) yield return found;
        }
    }

    private static List<string> Existing(IEnumerable<string?> paths)
        => paths.OfType<string>()
            .Where(path => path.Length > 0 && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
}
