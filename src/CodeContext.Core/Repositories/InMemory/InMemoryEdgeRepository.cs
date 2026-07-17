using System;
using System.Collections.Generic;
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
        // O(E) scan of the edge table, then return a fresh List so callers keep exclusive ownership.
        public Task<List<CodeEdge>> GetAllAsync()
        {
            return Task.FromResult(new List<CodeEdge>(_database.GetAdjacency().Edges));
        }

        public Task<List<CodeEdge>> GetBySourceIdAsync(string sourceId, string? type = null)
        {
            var edges = _database.GetAdjacency().GetEdgesBySource(sourceId);
            IEnumerable<CodeEdge> query = edges;

            if (type != null)
            {
                query = query.Where(e => e.Type == type);
            }

            return Task.FromResult(query.ToList());
        }

        public Task<List<CodeEdge>> GetByTargetIdAsync(string targetId, string? type = null)
        {
            var edges = _database.GetAdjacency().GetEdgesByTarget(targetId);
            IEnumerable<CodeEdge> query = edges;

            if (type != null)
            {
                query = query.Where(e => e.Type == type);
            }

            return Task.FromResult(query.ToList());
        }

        public Task UpsertAsync(CodeEdge edge)
        {
            if (string.IsNullOrEmpty(edge.Id))
            {
                edge.Id = Guid.NewGuid().ToString();
            }

            _database.Edges.AddOrUpdate(edge.Id, edge, (key, existing) => edge);
            _database.NotifyMutation();
            return Task.CompletedTask;
        }

        public Task DeleteByNodeIdAsync(string nodeId, CancellationToken ct)
        {
            
            var edgesToRemove = _database.Edges.Values
                .Where(e => e.SourceId == nodeId || e.TargetId == nodeId)
                .Select(e => e.Id)
                .Where(id => id != null)
                .ToList();

            foreach (var edgeId in edgesToRemove)
            {
                _database.Edges.TryRemove(edgeId!, out _);
            }

            if (edgesToRemove.Count > 0)
            {
                _database.NotifyMutation();
            }

            return Task.CompletedTask;
        }
    }
}