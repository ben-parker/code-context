using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    /// <summary>
    /// Edge reads. Every list is in canonical order: ordinal by edge id within a shard, shards in
    /// (parserId, workspaceId) ordinal order.
    /// </summary>
    public interface ICodeEdgeRepository
    {
        Task<IReadOnlyList<CodeEdge>> GetAllAsync();
        Task<IReadOnlyList<CodeEdge>> GetBySourceIdAsync(string sourceId, string? type = null);
        Task<IReadOnlyList<CodeEdge>> GetByTargetIdAsync(string targetId, string? type = null);
        Task UpsertAsync(CodeEdge edge);
        Task DeleteByNodeIdAsync(string nodeId, CancellationToken cancellationToken);
    }
}