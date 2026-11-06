using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeContext.Parser.Protocol;

/// <summary>
/// Describes an installable worker: identity, how to launch it, and what it parses.
/// Bundled workers live under <c>workers/&lt;name&gt;/worker-manifest.json</c> next to
/// the host binary; a relative <c>command</c> resolves against the manifest directory.
/// </summary>
public sealed record WorkerManifest(
    [property: JsonPropertyName("manifestVersion")] int ManifestVersion,
    [property: JsonPropertyName("parserId")] string ParserId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args,
    [property: JsonPropertyName("minProtocolVersion")] int MinProtocolVersion,
    [property: JsonPropertyName("maxProtocolVersion")] int MaxProtocolVersion,
    [property: JsonPropertyName("languages")] IReadOnlyList<string>? Languages = null,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string>? Extensions = null,
    [property: JsonPropertyName("projectMarkers")] IReadOnlyList<string>? ProjectMarkers = null,
    [property: JsonPropertyName("projectInputs")] IReadOnlyList<string>? ProjectInputs = null)
{
    public const int CurrentManifestVersion = 1;

    public static WorkerManifest Load(string manifestPath)
    {
        var json = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize(json, ParserProtocolJsonContext.Default.WorkerManifest)
            ?? throw new InvalidDataException($"Worker manifest '{manifestPath}' deserialized to null.");
        Validate(manifest, manifestPath);
        return manifest;
    }

    private static void Validate(WorkerManifest manifest, string manifestPath)
    {
        if (manifest.ManifestVersion is < 1 or > CurrentManifestVersion)
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' has unsupported manifestVersion {manifest.ManifestVersion}.");
        }
        if (string.IsNullOrWhiteSpace(manifest.ParserId))
        {
            throw new InvalidDataException($"Worker manifest '{manifestPath}' is missing parserId.");
        }
        if (!string.Equals(manifest.ParserId, manifest.ParserId.ToLowerInvariant(), StringComparison.Ordinal)
            || !char.IsAsciiLetterOrDigit(manifest.ParserId[0])
            || manifest.ParserId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' parserId must use lowercase ASCII letters, digits, '.', '_' or '-'.");
        }
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' must declare displayName and version.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Command))
        {
            throw new InvalidDataException($"Worker manifest '{manifestPath}' is missing command.");
        }
        if (manifest.MinProtocolVersion < 1 || manifest.MinProtocolVersion > manifest.MaxProtocolVersion)
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' declares an empty protocol version range.");
        }
        if (manifest.Extensions is { } extensions
            && extensions.Any(extension => string.IsNullOrWhiteSpace(extension)
                || extension[0] != '.'
                || extension.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\')))
        {
            throw new InvalidDataException(
                $"Worker manifest '{manifestPath}' extensions must start with '.' and contain no whitespace or path separators.");
        }
    }

    /// <summary>
    /// Resolves <see cref="Command"/> to an absolute path: beside the manifest first, then,
    /// for a command with no directory component (e.g. <c>dotnet</c>, <c>node</c>), on PATH.
    /// A bare name never leaves here. Given one, Windows and .NET on Unix both look in the
    /// current directory, which is usually the repository being indexed, so a program
    /// committed there would run. A command found nowhere resolves to the path beside the
    /// manifest, which fails to start like any other missing executable.
    /// </summary>
    public string ResolveCommand(string manifestDirectory)
    {
        if (Path.IsPathRooted(Command))
        {
            return Command;
        }

        var candidate = Path.GetFullPath(Path.Combine(manifestDirectory, Command));
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Manifests stay platform-neutral ("worker" / "node"). Windows apphosts
        // carry an .exe suffix, so also probe the native executable extension before
        // searching PATH.
        if (OperatingSystem.IsWindows()
            && !candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var executableCandidate = candidate + ".exe";
            if (File.Exists(executableCandidate))
            {
                return executableCandidate;
            }
        }

        return Path.GetDirectoryName(Command) is { Length: > 0 }
            ? candidate // an explicit relative path must resolve against the manifest even if missing
            : FindOnPath(Command) ?? candidate;
    }

    private static string? FindOnPath(string command)
    {
        // CreateProcess adds .exe to a name with no extension and to nothing else.
        var name = OperatingSystem.IsWindows() && !Path.HasExtension(command) ? command + ".exe" : command;
        var entries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var entry in entries)
        {
            // An empty or relative entry means the current directory, the place being avoided.
            var directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;

            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && (OperatingSystem.IsWindows() || CanExecute(candidate)))
            {
                return candidate;
            }
        }
        return null;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool CanExecute(string path)
        => (File.GetUnixFileMode(path)
            & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
