using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    public interface ICodeNodeRepository
    {
        Task<CodeNode?> GetByIdAsync(string id);
        Task<List<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false);
        Task<List<CodeNode>> GetAllAsync();
        Task UpsertAsync(CodeNode node);
        Task DeleteAsync(string id, CancellationToken cancellationToken);
    }
}