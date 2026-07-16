using System;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    public interface ICodeGraphRepository
    {
        Task<Guid> SaveGraphAsync(CodeGraph graph);
        Task<CodeGraph?> GetGraphAsync();
        Task ClearAsync();
        Task<string> ReconcileAndPruneAsync(string nodesJson, string edgesJson);
    }
}