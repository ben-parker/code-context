using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodeContext.Core.Tests.Repositories
{
    public class BackendSelectionTests
    {
        private static ServiceCollection CreateBaseServices()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            return services;
        }

        [Fact]
        public void AddCodeContextRepositories_ResolvesInMemoryFactory()
        {
            var services = CreateBaseServices();

            services.AddCodeContextRepositories();
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IRepositoryFactory>();
            Assert.IsType<InMemoryRepositoryFactory>(factory);
        }

        [Fact]
        public void AddCodeContextRepositories_ResolvesRepositories()
        {
            var services = CreateBaseServices();

            services.AddCodeContextRepositories();
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IRepositoryFactory>();
            Assert.NotNull(factory.CreateNodeRepository());
            Assert.NotNull(factory.CreateEdgeRepository());
            Assert.NotNull(factory.CreateGraphRepository());
            Assert.NotNull(factory.CreateFileMetadataRepository());
        }

        [Fact]
        public void AddCodeContextRepositories_RegistersRepositoriesFromFactory()
        {
            var services = CreateBaseServices();

            services.AddCodeContextRepositories();
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<ICodeNodeRepository>());
            Assert.NotNull(provider.GetRequiredService<ICodeEdgeRepository>());
            Assert.NotNull(provider.GetRequiredService<IFileMetadataRepository>());
        }

        [Fact]
        public void CodeContextOptions_DefaultsPort()
        {
            var options = new CodeContextOptions();

            Assert.Equal(7890, options.Port);
        }
    }
}
