using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Repositories.Kuzu;
using CSnakes.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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
        public void AddCodeContextRepositories_InMemory_ResolvesInMemoryFactory()
        {
            var services = CreateBaseServices();

            services.AddCodeContextRepositories(BackendType.InMemory);
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IRepositoryFactory>();
            Assert.IsType<InMemoryRepositoryFactory>(factory);
        }

        [Fact]
        public void AddCodeContextRepositories_InMemory_ResolvesWithoutKuzuApiRegistered()
        {
            // The in-memory path must have zero Python/Kuzu dependency:
            // no IKuzuApi (or IPythonEnvironment) is registered here, and resolution must still succeed.
            var services = CreateBaseServices();

            services.AddCodeContextRepositories(BackendType.InMemory);
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IRepositoryFactory>();
            Assert.NotNull(factory.CreateNodeRepository());
            Assert.NotNull(factory.CreateEdgeRepository());
            Assert.NotNull(factory.CreateGraphRepository());
            Assert.NotNull(factory.CreateFileMetadataRepository());
        }

        [Fact]
        public void AddCodeContextRepositories_Kuzu_ResolvesKuzuFactory()
        {
            var services = CreateBaseServices();
            services.AddSingleton(Substitute.For<IKuzuApi>());

            services.AddCodeContextRepositories(BackendType.Kuzu);
            using var provider = services.BuildServiceProvider();

            var factory = provider.GetRequiredService<IRepositoryFactory>();
            Assert.IsType<KuzuRepositoryFactory>(factory);
        }

        [Fact]
        public void AddCodeContextRepositories_RegistersRepositoriesFromFactory()
        {
            var services = CreateBaseServices();

            services.AddCodeContextRepositories(BackendType.InMemory);
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<ICodeNodeRepository>());
            Assert.NotNull(provider.GetRequiredService<ICodeEdgeRepository>());
            Assert.NotNull(provider.GetRequiredService<IFileMetadataRepository>());
        }

        [Fact]
        public void CodeContextOptions_DefaultsToInMemoryBackend()
        {
            var options = new CodeContextOptions();

            Assert.Equal(BackendType.InMemory, options.Backend);
            Assert.Equal(7890, options.Port);
        }
    }
}
