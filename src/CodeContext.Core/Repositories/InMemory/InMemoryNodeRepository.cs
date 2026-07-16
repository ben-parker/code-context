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
                query = query.Where(n => n.Type == type);
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

            _database.Nodes.AddOrUpdate(node.Id, node, (key, existing) => node);
            _database.NotifyMutation();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            if (_database.Nodes.TryRemove(id, out _))
            {
                _database.NotifyMutation();
            }
            return Task.CompletedTask;
        }
    }
}