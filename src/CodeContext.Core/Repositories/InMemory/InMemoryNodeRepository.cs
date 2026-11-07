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

        // Every shard answers in id order and shards are concatenated in ShardKey order, so the
        // result is canonical whatever order the facts were committed in.
        public Task<IReadOnlyList<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false)
        {
            if (name == null)
            {
                return Task.FromResult<IReadOnlyList<CodeNode>>(new List<CodeNode>());
            }

            // A fresh list per call (never a shared backing store), so it is returned as is.
            var nodes = _database.GetAdjacency().FindNodesByName(name, exact);

            if (type == null)
            {
                return Task.FromResult(nodes);
            }

            return Task.FromResult<IReadOnlyList<CodeNode>>(nodes
                .Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        // Resolves through the version-stamped adjacency path index (O(matches)) instead of a full
        // O(N) scan + FilePathMatcher.Matches filter; the result is in canonical order.
        // FindNodesByPath builds a fresh list, so the no-type result is returned directly; the type
        // filter runs the SAME case-insensitive (ordinal) comparison ContextService.FindNodesByFilePathAsync
        // used, over that already-narrowed set.
        public Task<IReadOnlyList<CodeNode>> FindByFilePathAsync(string filePath, string? type = null)
        {
            var nodes = _database.GetAdjacency().FindNodesByPath(filePath);

            if (string.IsNullOrEmpty(type))
            {
                return Task.FromResult(nodes);
            }

            return Task.FromResult<IReadOnlyList<CodeNode>>(nodes
                .Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        // Resolves through the version-stamped adjacency snapshot (built once per mutation version)
        // and returns its cached, downcast-proof read-only view — zero per-call copy, and the result
        // cannot be cast back to the shared List/array backing store. Canonical order: shards in
        // ShardKey order, id order inside each shard.
        public Task<IReadOnlyList<CodeNode>> GetAllAsync()
        {
            return Task.FromResult(_database.GetAdjacency().NodesView);
        }

        public Task<IReadOnlyList<string>> GetFilePathsAsync()
        {
            return Task.FromResult(_database.GetAdjacency().FilePaths);
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
