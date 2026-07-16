using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Repositories.Kuzu;
using Microsoft.Extensions.DependencyInjection;

namespace CodeContext.Core.Repositories
{
    public static class RepositoryServiceExtensions
    {
        /// <summary>
        /// Registers the repository factory for the selected backend plus the individual
        /// repositories resolved from it. The Kuzu backend additionally requires an
        /// IKuzuApi registration (see ProgramHelpers); the in-memory backend has no
        /// external dependencies.
        /// </summary>
        public static IServiceCollection AddCodeContextRepositories(this IServiceCollection services, BackendType backend)
        {
            if (backend == BackendType.Kuzu)
            {
                services.AddSingleton<IRepositoryFactory, KuzuRepositoryFactory>();
            }
            else
            {
                services.AddSingleton<IRepositoryFactory, InMemoryRepositoryFactory>();
            }

            services.AddSingleton<IFileMetadataRepository>(sp =>
                sp.GetRequiredService<IRepositoryFactory>().CreateFileMetadataRepository());
            services.AddSingleton<ICodeNodeRepository>(sp =>
                sp.GetRequiredService<IRepositoryFactory>().CreateNodeRepository());
            services.AddSingleton<ICodeEdgeRepository>(sp =>
                sp.GetRequiredService<IRepositoryFactory>().CreateEdgeRepository());

            return services;
        }
    }
}
