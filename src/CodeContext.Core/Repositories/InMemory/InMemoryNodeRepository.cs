using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories.InMemory
{
    public class InMemoryNodeRepository : ICodeNodeRepository
    {
        private readonly InMemoryDatabase _database;

        public InMemoryNodeRepository(InMemoryDatabase database)
        {
            _database = database;
        }

        public Task<CodeNode?> GetByIdAsync(string id)
        {
            _database.Nodes.TryGetValue(id, out var node);
            return Task.FromResult(node);
        }

        public Task<CodeNode?> GetByIdentifierAsync(string identifier)
        {
            if (_database.Identifiers.TryGetValue(identifier, out var id)
                && _database.Nodes.TryGetValue(id, out var node))
            {
                return Task.FromResult<CodeNode?>(node);
            }
            return Task.FromResult<CodeNode?>(null);
        }

        public Task<List<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false)
        {
            if (name == null)
            {
                return Task.FromResult(new List<CodeNode>());
            }

            var query = _database.Nodes.Values.Where(n => 
                exact 
                    ? string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                    : n.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);
            
            if (type != null)
            {
                query = query.Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(query.ToList());
        }

        public Task<List<CodeNode>> GetAllAsync()
        {
            return Task.FromResult(_database.Nodes.Values.ToList());
        }

        public Task UpsertAsync(CodeNode node)
        {
            if (node.Id == null)
            {
                throw new ArgumentException("Node must have an Id", nameof(node));
            }

            if (string.IsNullOrEmpty(node.Identifier))
                node.Identifier = InMemoryDatabase.DeriveLegacyIdentifier(node) ?? string.Empty;
            _database.Nodes.TryGetValue(node.Id, out var existingNode);
            if (!string.IsNullOrWhiteSpace(node.Identifier))
            {
                if (!_database.Identifiers.TryAdd(node.Identifier, node.Id)
                    && _database.Identifiers[node.Identifier] != node.Id)
                {
                    throw new InvalidDataException($"Duplicate public symbol identifier '{node.Identifier}'.");
                }
            }
            _database.Nodes.AddOrUpdate(node.Id, node, (key, existing) => node);
            if (existingNode?.Identifier is { Length: > 0 } previousIdentifier
                && previousIdentifier != node.Identifier
                && _database.Identifiers.TryGetValue(previousIdentifier, out var previousId)
                && previousId == node.Id)
            {
                _database.Identifiers.TryRemove(previousIdentifier, out _);
            }
            _database.NotifyMutation();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            if (_database.Nodes.TryRemove(id, out _))
            {
                foreach (var entry in _database.Identifiers.Where(entry => entry.Value == id).ToList())
                    _database.Identifiers.TryRemove(entry.Key, out _);
                _database.NotifyMutation();
            }
            return Task.CompletedTask;
        }
    }
}
