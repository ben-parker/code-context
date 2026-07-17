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
            _database.TryGetNode(id, out var node);
            return Task.FromResult(node);
        }

        public Task<CodeNode?> GetByIdentifierAsync(string identifier)
        {
            if (_database.TryGetNodeIdByIdentifier(identifier, out var id)
                && _database.TryGetNode(id!, out var node))
            {
                return Task.FromResult(node);
            }
            return Task.FromResult<CodeNode?>(null);
        }

        public Task<List<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false)
        {
            if (name == null)
            {
                return Task.FromResult(new List<CodeNode>());
            }

            var query = _database.EnumerateNodes().Where(n =>
                exact
                    ? string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
                    : n.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);

            if (type != null)
            {
                query = query.Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(query.ToList());
        }

        // Resolves through the version-stamped adjacency snapshot (built once per mutation version)
        // rather than materializing the live dictionaries on every call; returns a fresh List so the
        // caller owns its copy. Order is ordinal-shard-then-per-shard-dictionary order (identical to the
        // former single-dictionary enumeration in the common single-shard case).
        public Task<List<CodeNode>> GetAllAsync()
        {
            return Task.FromResult(new List<CodeNode>(_database.GetAdjacency().Nodes));
        }

        public Task UpsertAsync(CodeNode node)
        {
            if (node.Id == null)
            {
                throw new ArgumentException("Node must have an Id", nameof(node));
            }

            // Routing, identifier derivation, cross-shard duplicate enforcement, stale-identifier
            // cleanup and the mutation notification are all encapsulated by the store.
            _database.UpsertNode(node);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            _database.RemoveNode(id);
            return Task.CompletedTask;
        }
    }
}
