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
    [property: JsonPropertyName("projectMarkers")] IReadOnlyList<string>? ProjectMarkers = null)
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
    /// Resolves <see cref="Command"/> to an absolute path against the manifest's
    /// directory. Commands with no directory component that don't exist beside the
    /// manifest (e.g. <c>dotnet</c>, <c>node</c>) are returned as-is for PATH lookup.
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
        // falling back to PATH lookup.
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
            : Command;  // bare command name: let the OS resolve it on PATH
    }
}
