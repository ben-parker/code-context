using System.Collections.Concurrent;
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

public sealed class AnalysisCoverageRegistry : IAnalysisCoverageRegistry
{
    private readonly ConcurrentDictionary<(string ParserId, string WorkspaceId), IReadOnlyList<ProjectCoverage>> _reports = new();

    public void Report(string parserId, string workspaceId, IReadOnlyList<ProjectCoverage> projects)
        => _reports[(parserId, workspaceId)] = projects;

    public IReadOnlyList<ProjectCoverage> GetProjects()
        => _reports.OrderBy(report => report.Key.ParserId, StringComparer.Ordinal)
            .ThenBy(report => report.Key.WorkspaceId, StringComparer.Ordinal)
            .SelectMany(report => report.Value)
            .ToList();
}
