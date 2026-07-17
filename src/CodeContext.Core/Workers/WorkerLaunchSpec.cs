using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Workers;

/// <summary>How to launch one worker process. Built from a manifest or by tests directly.</summary>
public sealed record WorkerLaunchSpec(
    string ParserId,
    string DisplayName,
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    int MinProtocolVersion = ParserProtocol.Version,
    int MaxProtocolVersion = ParserProtocol.Version,
    IReadOnlyDictionary<string, string>? Environment = null)
{
    /// <summary>
    /// Builds a launch spec from a manifest on disk. Relative commands resolve against
    /// the manifest's own directory (bundled workers live next to their manifest), so
    /// resolution never depends on the invoking working directory.
    /// </summary>
    public static WorkerLaunchSpec FromManifest(string manifestPath)
    {
        var manifest = WorkerManifest.Load(manifestPath);
        return FromManifest(manifest, manifestPath);
    }

    public static WorkerLaunchSpec FromManifest(WorkerManifest manifest, string manifestPath)
    {
        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        return new WorkerLaunchSpec(
            manifest.ParserId,
            manifest.DisplayName,
            manifest.ResolveCommand(manifestDirectory),
            manifest.Args ?? [],
            manifestDirectory,
            manifest.MinProtocolVersion,
            manifest.MaxProtocolVersion);
    }
}

/// <summary>Tunables for <see cref="ParserProcessSupervisor"/>; defaults suit production.</summary>
public sealed class ParserWorkerOptions
{
    public TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a worker gets to exit after its stdin is closed before it is killed.</summary>
    public TimeSpan ExitAfterEofTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Unexpected-exit respawns allowed before the session is marked Unavailable.</summary>
    public int MaxRestarts { get; set; } = 3;

    public int MinProtocolVersion { get; set; } = ParserProtocol.Version;
    public int MaxProtocolVersion { get; set; } = ParserProtocol.Version;

    /// <summary>
    /// Host-side environment overlays keyed by parser id (e.g. "csharp"). Applied on top
    /// of any <see cref="WorkerLaunchSpec.Environment"/> when spawning that parser's
    /// worker, so a var set here wins on collision. Null (the default) touches nothing.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? WorkerEnvironment { get; set; }
}

/// <summary>The worker process died or its protocol stream broke mid-conversation.</summary>
public sealed class ParserWorkerFailedException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The worker cannot be used at all (incompatible version, restart budget exhausted).</summary>
public sealed class ParserWorkerUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
