using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    public interface ICodeEdgeRepository
    {
        Task<IReadOnlyList<CodeEdge>> GetAllAsync();
        Task<IReadOnlyList<CodeEdge>> GetBySourceIdAsync(string sourceId, string? type = null);
        Task<IReadOnlyList<CodeEdge>> GetByTargetIdAsync(string targetId, string? type = null);
        Task UpsertAsync(CodeEdge edge);
        Task DeleteByNodeIdAsync(string nodeId, CancellationToken cancellationToken);
    }
}