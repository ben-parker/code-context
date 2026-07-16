namespace CodeContext.Core.Models;

/// <summary>
/// Aggregate counts over the committed graph, maintained/cached by the store so
/// status polling never has to materialize nodes or edges.
/// </summary>
public sealed record GraphStatistics(
    int NodeCount,
    int EdgeCount,
    IReadOnlyDictionary<string, int> NodesByType,
    IReadOnlyDictionary<string, int> EdgesByType)
{
    public static readonly GraphStatistics Empty = new(
        0, 0, new Dictionary<string, int>(), new Dictionary<string, int>());
}
