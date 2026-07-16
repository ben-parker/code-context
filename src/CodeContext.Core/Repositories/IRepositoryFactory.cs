using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    public interface IRepositoryFactory
    {
        Task InitializeAsync(string rootPath);
        ICodeGraphRepository CreateGraphRepository();
        ICodeNodeRepository CreateNodeRepository();
        ICodeEdgeRepository CreateEdgeRepository();
        IFileMetadataRepository CreateFileMetadataRepository();
    }
}