using System.Collections.Concurrent;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Workers;

/// <summary>
/// What each worker last said it could and could not see, by project. A graph built from
/// partial context looks exactly like a complete one, so this is how a reader learns the
/// difference. It is replaced when a generation commits, in step with the facts it describes.
/// </summary>
public interface IAnalysisCoverageRegistry
{
    void Report(string parserId, string workspaceId, IReadOnlyList<ProjectCoverage> projects);

    IReadOnlyList<ProjectCoverage> GetProjects();
}

/// <summary>
/// Over the in-memory store (<see cref="ForStore"/>, the host's wiring) it reads the coverage recorded
/// with the published shards, so the coverage served always belongs to the facts served: a staged
/// reconciliation's coverage is not visible before its commit, a rolled-back one's never, and a loaded
/// snapshot's is there with its facts. Reports are then only kept as a fallback. Without a store it
/// serves what was reported.
/// </summary>
public sealed class AnalysisCoverageRegistry : IAnalysisCoverageRegistry
{
    private readonly ConcurrentDictionary<(string ParserId, string WorkspaceId), IReadOnlyList<ProjectCoverage>> _reports = new();
    private readonly Func<IReadOnlyList<ProjectCoverage>>? _published;

    public AnalysisCoverageRegistry()
    {
    }

    internal AnalysisCoverageRegistry(Func<IReadOnlyList<ProjectCoverage>> published) => _published = published;

    /// <summary>The registry for a host's store: read through the published annotations when it is the in-memory store.</summary>
    public static IAnalysisCoverageRegistry ForStore(IRepositoryFactory factory)
        => factory is InMemoryRepositoryFactory memory
            ? new AnalysisCoverageRegistry(memory.Database.PublishedCoverage)
            : new AnalysisCoverageRegistry();

    public void Report(string parserId, string workspaceId, IReadOnlyList<ProjectCoverage> projects)
        => _reports[(parserId, workspaceId)] = projects;

    public IReadOnlyList<ProjectCoverage> GetProjects()
        => _published?.Invoke()
            ?? _reports.OrderBy(report => report.Key.ParserId, StringComparer.Ordinal)
                .ThenBy(report => report.Key.WorkspaceId, StringComparer.Ordinal)
                .SelectMany(report => report.Value)
                .ToList();
}
