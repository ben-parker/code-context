using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories.InMemory
{
    public class InMemoryEdgeRepository : ICodeEdgeRepository
    {
        private readonly InMemoryDatabase _database;

        public InMemoryEdgeRepository(InMemoryDatabase database)
        {
            _database = database;
        }

        // Reads resolve through the version-stamped adjacency index (O(degree)) instead of a full
        // O(E) scan of the edge table. GetAll returns the composite's cached, downcast-proof view
        // (zero copy); keyed lookups without a type filter wrap the shared backing array in a
        // read-only facade so the raw CodeEdge[] never escapes downcastable.
        public Task<IReadOnlyList<CodeEdge>> GetAllAsync()
        {
            return Task.FromResult(_database.GetAdjacency().EdgesView);
        }

        public Task<IReadOnlyList<CodeEdge>> GetBySourceIdAsync(string sourceId, string? type = null)
        {
            var edges = _database.GetAdjacency().GetEdgesBySource(sourceId);

            if (type == null)
            {
                return Task.FromResult(AsReadOnly(edges));
            }

            return Task.FromResult<IReadOnlyList<CodeEdge>>(
                edges.Where(e => e.Type == type).ToList());
        }

        public Task<IReadOnlyList<CodeEdge>> GetByTargetIdAsync(string targetId, string? type = null)
        {
            var edges = _database.GetAdjacency().GetEdgesByTarget(targetId);

            if (type == null)
            {
                return Task.FromResult(AsReadOnly(edges));
            }

            return Task.FromResult<IReadOnlyList<CodeEdge>>(
                edges.Where(e => e.Type == type).ToList());
        }

        // Wraps a keyed lookup's result — which may be the raw shared CodeEdge[] backing a frozen
        // shard bucket — in a read-only facade. One tiny wrapper alloc, still cheaper than today's
        // full List copy, and `as CodeEdge[]` / `as List<CodeEdge>` on the result are both null.
        private static IReadOnlyList<CodeEdge> AsReadOnly(IReadOnlyList<CodeEdge> edges)
            => edges is IList<CodeEdge> list
                ? new ReadOnlyCollection<CodeEdge>(list)
                : new ReadOnlyCollection<CodeEdge>(edges.ToList());

        public Task UpsertAsync(CodeEdge edge)
        {
            if (string.IsNullOrEmpty(edge.Id))
            {
                edge.Id = Guid.NewGuid().ToString();
            }

            _database.UpsertEdge(edge);
            return Task.CompletedTask;
        }

        public Task DeleteByNodeIdAsync(string nodeId, CancellationToken ct)
        {
            _database.RemoveEdgesTouchingNode(nodeId);
            return Task.CompletedTask;
        }
    }
}
