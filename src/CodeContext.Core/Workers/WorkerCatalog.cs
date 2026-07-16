using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Workers;

/// <summary>One discovered worker: its manifest plus the launch spec derived from it.</summary>
public sealed record RegisteredWorker(WorkerManifest Manifest, WorkerLaunchSpec LaunchSpec, string ManifestPath)
{
    public IReadOnlyList<string> Extensions => Manifest.Extensions ?? [];
}

/// <summary>Discovers installed language workers from their on-disk manifests.</summary>
public interface IWorkerCatalog
{
    IReadOnlyList<RegisteredWorker> Workers { get; }
}

/// <summary>
/// Scans manifests without ever executing repository-local code. Precedence is:
/// explicit <c>CODECONTEXT_WORKERS_DIR</c> roots (in listed order), the per-user
/// install root, then bundled workers beside the host. The first compatible manifest
/// for a parser wins. Invalid/incompatible manifests are logged and skipped — one
/// broken worker installation must not take down the host.
/// </summary>
public sealed class WorkerCatalog : IWorkerCatalog
{
    public const string WorkersDirEnvVar = "CODECONTEXT_WORKERS_DIR";

    private readonly Lazy<IReadOnlyList<RegisteredWorker>> _workers;

    public WorkerCatalog(ILogger<WorkerCatalog> logger)
        : this(ResolveWorkersRoots(), logger)
    {
    }

    public WorkerCatalog(string workersRoot, ILogger<WorkerCatalog> logger)
        : this([workersRoot], logger)
    {
    }

    public WorkerCatalog(IReadOnlyList<string> workersRoots, ILogger<WorkerCatalog> logger)
    {
        _workers = new Lazy<IReadOnlyList<RegisteredWorker>>(() => Discover(workersRoots, logger));
    }

    public IReadOnlyList<RegisteredWorker> Workers => _workers.Value;

    private static IReadOnlyList<string> ResolveWorkersRoots()
    {
        var roots = new List<string>();
        if (Environment.GetEnvironmentVariable(WorkersDirEnvVar) is { Length: > 0 } overridden)
        {
            roots.AddRange(overridden.Split(Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            roots.Add(Path.Combine(userProfile, ".codecontext", "workers"));
        }
        roots.Add(Path.Combine(AppContext.BaseDirectory, "workers"));

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return roots
            .Select(Path.GetFullPath)
            .Distinct(comparer)
            .ToList();
    }

    private static IReadOnlyList<RegisteredWorker> Discover(IReadOnlyList<string> workersRoots, ILogger logger)
    {
        var workers = new List<RegisteredWorker>();
        foreach (var workersRoot in workersRoots)
        {
            DiscoverRoot(workersRoot, workers, logger);
        }
        return workers;
    }

    private static void DiscoverRoot(string workersRoot, List<RegisteredWorker> workers, ILogger logger)
    {
        if (!Directory.Exists(workersRoot))
        {
            logger.LogDebug("Worker discovery root {WorkersRoot} does not exist.", workersRoot);
            return;
        }

        IReadOnlyList<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(workersRoot)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Cannot enumerate worker discovery root {WorkersRoot}; skipping it.", workersRoot);
            return;
        }

        foreach (var directory in directories)
        {
            var manifestPath = Path.Combine(directory, "worker-manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifest = WorkerManifest.Load(manifestPath);
                if (manifest.MaxProtocolVersion < ParserProtocol.Version
                    || manifest.MinProtocolVersion > ParserProtocol.Version)
                {
                    logger.LogWarning(
                        "Skipping worker manifest {ManifestPath}: parser '{ParserId}' supports protocol v{Min}-v{Max}, but this host supports v{HostVersion}.",
                        manifestPath, manifest.ParserId, manifest.MinProtocolVersion,
                        manifest.MaxProtocolVersion, ParserProtocol.Version);
                    continue;
                }
                if (workers.Any(w => string.Equals(w.Manifest.ParserId, manifest.ParserId, StringComparison.Ordinal)))
                {
                    logger.LogWarning(
                        "Skipping duplicate worker manifest {ManifestPath}: parser id '{ParserId}' is already registered.",
                        manifestPath, manifest.ParserId);
                    continue;
                }
                var spec = WorkerLaunchSpec.FromManifest(manifest, manifestPath);
                workers.Add(new RegisteredWorker(manifest, spec, manifestPath));
                logger.LogInformation(
                    "Discovered worker '{ParserId}' ({DisplayName} {Version}) at {ManifestPath}.",
                    manifest.ParserId, manifest.DisplayName, manifest.Version, manifestPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Ignoring invalid worker manifest at {ManifestPath}.", manifestPath);
            }
        }
    }
}
