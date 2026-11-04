using System.Globalization;

namespace CodeContext.CSharp.Worker.Projects;

/// <summary>A parsed target framework moniker such as <c>net8.0-windows</c> or <c>net472</c>.</summary>
public sealed record TargetFrameworkName(string Moniker, string Identifier, Version Version, string? Platform)
{
    public const string NetCoreApp = ".NETCoreApp";
    public const string NetStandard = ".NETStandard";
    public const string NetFramework = ".NETFramework";

    private static readonly Version[] NetFrameworkVersions =
    [
        new(2, 0, 0), new(3, 0, 0), new(3, 5, 0), new(4, 0, 0), new(4, 5, 0), new(4, 5, 1), new(4, 5, 2),
        new(4, 6, 0), new(4, 6, 1), new(4, 6, 2), new(4, 7, 0), new(4, 7, 1), new(4, 7, 2), new(4, 8, 0), new(4, 8, 1),
    ];

    private static readonly Version[] NetStandardVersions =
        [new(1, 0), new(1, 1), new(1, 2), new(1, 3), new(1, 4), new(1, 5), new(1, 6), new(2, 0), new(2, 1)];

    private static readonly Version[] NetCoreAppVersions =
        [new(1, 0), new(1, 1), new(2, 0), new(2, 1), new(2, 2), new(3, 0), new(3, 1)];

    public bool IsNetCoreApp => Identifier == NetCoreApp;
    public bool IsNetStandard => Identifier == NetStandard;
    public bool IsNetFramework => Identifier == NetFramework;

    public static bool TryParse(string? moniker, out TargetFrameworkName framework)
    {
        framework = null!;
        if (string.IsNullOrWhiteSpace(moniker)) return false;
        var text = moniker.Trim();

        // The long form appears in restore data: .NETCoreApp,Version=v8.0
        if (text.StartsWith('.'))
        {
            var parts = text.Split(',', StringSplitOptions.TrimEntries);
            var versionPart = parts.FirstOrDefault(part => part.StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
            if (versionPart is null || !MsBuildExpander.TryVersion(versionPart["Version=".Length..], out var longVersion))
            {
                return false;
            }
            framework = new TargetFrameworkName(text, parts[0], longVersion, null);
            return true;
        }

        var lower = text.ToLowerInvariant();
        var dash = lower.IndexOf('-');
        var platform = dash >= 0 ? new string(lower[(dash + 1)..].TakeWhile(char.IsLetter).ToArray()) : null;
        var name = dash >= 0 ? lower[..dash] : lower;

        if (name.StartsWith("netstandard", StringComparison.Ordinal)
            && MsBuildExpander.TryVersion(name["netstandard".Length..], out var standard))
        {
            framework = new TargetFrameworkName(text, NetStandard, standard, platform);
            return true;
        }
        if (name.StartsWith("netcoreapp", StringComparison.Ordinal)
            && MsBuildExpander.TryVersion(name["netcoreapp".Length..], out var core))
        {
            framework = new TargetFrameworkName(text, NetCoreApp, core, platform);
            return true;
        }
        if (!name.StartsWith("net", StringComparison.Ordinal)) return false;

        var digits = name[3..];
        if (digits.Contains('.'))
        {
            if (!MsBuildExpander.TryVersion(digits, out var modern)) return false;
            framework = new TargetFrameworkName(text, NetCoreApp, modern, platform);
            return true;
        }
        if (digits.Length is < 2 or > 3 || !digits.All(char.IsDigit)) return false;

        // net472 is 4.7.2.
        var version = new Version(digits[0] - '0', digits[1] - '0', digits.Length == 3 ? digits[2] - '0' : 0);
        framework = new TargetFrameworkName(text, NetFramework, version, platform);
        return true;
    }

    /// <summary>The moniker for an old-style project's <c>TargetFrameworkVersion</c> such as <c>v4.7.2</c>.</summary>
    public static string? FromLegacyVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || !MsBuildExpander.TryVersion(version, out var parsed)) return null;
        var patch = parsed.Build > 0 ? parsed.Build.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"net{parsed.Major}{parsed.Minor}{patch}";
    }

    /// <summary>The compile symbols the .NET SDK defines for this framework.</summary>
    public IEnumerable<string> PreprocessorSymbols()
    {
        if (IsNetFramework)
        {
            var current = ThreePart(Version);
            yield return "NETFRAMEWORK";
            yield return "NET" + Digits(current);
            foreach (var candidate in NetFrameworkVersions.Where(candidate => candidate <= current))
            {
                yield return $"NET{Digits(candidate)}_OR_GREATER";
            }
            yield break;
        }

        if (IsNetStandard)
        {
            yield return "NETSTANDARD";
            yield return $"NETSTANDARD{Version.Major}_{Version.Minor}";
            foreach (var candidate in NetStandardVersions.Where(candidate => candidate <= Version))
            {
                yield return $"NETSTANDARD{candidate.Major}_{candidate.Minor}_OR_GREATER";
            }
            yield break;
        }

        yield return "NETCOREAPP";
        foreach (var candidate in NetCoreAppVersions.Where(candidate => candidate <= Version))
        {
            yield return $"NETCOREAPP{candidate.Major}_{candidate.Minor}_OR_GREATER";
        }

        if (Version.Major < 5)
        {
            yield return $"NETCOREAPP{Version.Major}_{Version.Minor}";
            yield break;
        }

        yield return "NET";
        yield return $"NET{Version.Major}_{Version.Minor}";
        for (var major = 5; major <= Version.Major; major++)
        {
            yield return $"NET{major}_0_OR_GREATER";
        }

        if (Platform is { Length: > 0 } platform)
        {
            yield return platform.ToUpperInvariant();
        }
    }

    private static string Digits(Version version)
        => version.Build > 0 ? $"{version.Major}{version.Minor}{version.Build}" : $"{version.Major}{version.Minor}";

    private static Version ThreePart(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>Whether a project on this framework can use an asset built for <paramref name="asset"/>.</summary>
    public bool CanConsume(TargetFrameworkName asset)
    {
        if (asset.Platform is { Length: > 0 } && asset.Platform != Platform) return false;
        if (Identifier == asset.Identifier) return ThreePart(asset.Version) <= ThreePart(Version);
        if (!asset.IsNetStandard) return false;
        if (IsNetCoreApp) return Version.Major >= 3 || asset.Version <= new Version(2, 0);
        return IsNetFramework && ThreePart(Version) >= new Version(4, 6, 1) && asset.Version <= new Version(2, 0);
    }
}
