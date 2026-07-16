using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Repositories.InMemory
{
    public class InMemoryRepositoryFactory : IRepositoryFactory
    {
        private readonly ILogger<InMemoryRepositoryFactory> _logger;
        private readonly InMemoryDatabase _database;

        public InMemoryRepositoryFactory(ILogger<InMemoryRepositoryFactory> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _database = new InMemoryDatabase();
        }

        public Task InitializeAsync(string rootPath)
        {
            _logger.LogInformation($"Initialized in-memory database for root path: {rootPath}");
            return Task.CompletedTask;
        }

        public ICodeGraphRepository CreateGraphRepository()
        {
            return new InMemoryCodeGraphRepository(_database);
        }

        public ICodeNodeRepository CreateNodeRepository()
        {
            return new InMemoryNodeRepository(_database);
        }

        public ICodeEdgeRepository CreateEdgeRepository()
        {
            return new InMemoryEdgeRepository(_database);
        }

        public IFileMetadataRepository CreateFileMetadataRepository()
        {
            return new InMemoryFileMetadataRepository();
        }
    }
}