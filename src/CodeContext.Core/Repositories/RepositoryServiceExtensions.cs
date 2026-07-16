using CodeContext.Core.Repositories.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace CodeContext.Core.Repositories
{
    public static class RepositoryServiceExtensions
    {
        /// <summary>
        /// Registers the in-memory repository factory and its repositories.
        /// </summary>
        public static IServiceCollection AddCodeContextRepositories(this IServiceCollection services)
        {
            services.AddSingleton<IRepositoryFactory, InMemoryRepositoryFactory>();

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
