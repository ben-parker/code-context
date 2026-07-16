using CSnakes.Runtime;
using Microsoft.Extensions.Logging;

namespace CodeContext.Core.Repositories.Kuzu;

public class KuzuRepositoryFactory : IRepositoryFactory
{
    private readonly IKuzuApi _kuzuApi;
    private readonly ILoggerFactory _loggerFactory;
    private string? _dbPath;

    public KuzuRepositoryFactory(IKuzuApi kuzuApi, ILoggerFactory loggerFactory)
    {
        _kuzuApi = kuzuApi ?? throw new ArgumentNullException(nameof(kuzuApi));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public async Task InitializeAsync(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        if (string.IsNullOrEmpty(rootPath))
            throw new ArgumentException("Root path cannot be null or empty.", nameof(rootPath));
            
        // Create database in the .codecontext directory within the root path
        var codeContextDir = Path.Combine(rootPath, ".codecontext");
        Directory.CreateDirectory(codeContextDir);
        
        _dbPath = Path.Combine(codeContextDir, "codecontext.kuzu");
        
        // Initialize Kuzu database
        await Task.Run(() => _kuzuApi.InitializeDatabase(_dbPath));
    }

    public ICodeEdgeRepository CreateEdgeRepository()
    {
        if (_dbPath == null)
            throw new InvalidOperationException("Repository factory not initialized. Call InitializeAsync first.");
            
        return new KuzuEdgeRepository(_kuzuApi);
    }

    public ICodeGraphRepository CreateGraphRepository()
    {
        if (_dbPath == null)
            throw new InvalidOperationException("Repository factory not initialized. Call InitializeAsync first.");
            
        return new KuzuGraphRepository(_kuzuApi);
    }

    public ICodeNodeRepository CreateNodeRepository()
    {
        if (_dbPath == null)
            throw new InvalidOperationException("Repository factory not initialized. Call InitializeAsync first.");
            
        return new KuzuNodeRepository(_kuzuApi);
    }

    public IFileMetadataRepository CreateFileMetadataRepository()
    {
        if (_dbPath == null)
            throw new InvalidOperationException("Repository factory not initialized. Call InitializeAsync first.");
            
        return new KuzuFileMetadataRepository(_kuzuApi, _loggerFactory.CreateLogger<KuzuFileMetadataRepository>());
    }
}