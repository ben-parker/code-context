using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Models;
using CodeContext.Core.Serialization;

namespace CodeContext.Core.Repositories.InMemory
{
    public class InMemoryCodeGraphRepository : ICodeGraphRepository, IGenerationalGraphStore
    {
        private readonly InMemoryDatabase _database;

        public InMemoryCodeGraphRepository(InMemoryDatabase database)
        {
            _database = database;
        }

        public long LastCommittedGeneration => _database.LastCommittedGeneration;
        public void BeginReconciliation() => _database.BeginReconciliation();
        public void CommitReconciliation() => _database.CommitReconciliation();
        public void RollbackReconciliation() => _database.RollbackReconciliation();

        public Task<bool> TryCommitGenerationAsync(
            long generation,
            IReadOnlyList<CodeNode> nodes,
            IReadOnlyList<CodeEdge> edges,
            Func<CodeNode, bool>? replacesScope = null,
            CancellationToken ct = default)
        {
            return Task.FromResult(_database.TryCommitGeneration(generation, nodes, edges, replacesScope));
        }

        public Task<int> PruneFilesNotPresentAsync(IReadOnlyCollection<string> presentFilePaths, CancellationToken ct = default)
        {
            return Task.FromResult(_database.PruneFilesNotPresent(presentFilePaths));
        }

        public GraphStatistics GetStatistics() => _database.GetStatistics();

        public Task<Guid> SaveGraphAsync(CodeGraph graph)
        {
            _database.CurrentGraph = graph;
            _database.ReplaceAll(graph.Nodes, graph.Edges);
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<CodeGraph?> GetGraphAsync()
        {
            return Task.FromResult<CodeGraph?>(_database.CaptureGraph());
        }

        public Task ClearAsync()
        {
            _database.Reset();
            return Task.CompletedTask;
        }

        /// <summary>
        /// JSON reconcile implementation for the shared <see cref="ICodeGraphRepository"/>
        /// contract. The typed
        /// <see cref="TryCommitGenerationAsync"/> is the in-memory hot path; this shim only
        /// adapts and delegates.
        /// </summary>
        public Task<string> ReconcileAndPruneAsync(string nodesJson, string edgesJson)
        {
            var nodeDtos = JsonSerializer.Deserialize(nodesJson, CodeContextJsonContext.Default.ListNodeDto)!;
            var edgeDtos = JsonSerializer.Deserialize(edgesJson, CodeContextJsonContext.Default.ListEdgeDto)!;

            var existingNodeCount = _database.Nodes.Count;

            var nodes = nodeDtos.Select(dto => new CodeNode
            {
                Id = dto.Id,
                Name = dto.Name,
                Type = dto.Type,
                Language = dto.Language,
                FilePath = dto.FilePath,
                StartLine = dto.StartLine,
                EndLine = dto.EndLine,
                StartCol = dto.StartCol,
                EndCol = dto.EndCol,
                Namespace = dto.Namespace,
                Visibility = dto.Visibility,
                Signature = dto.Signature,
                ReturnType = dto.ReturnType,
                Parameters = dto.Parameters,
                Modifiers = dto.Modifiers,
                Metrics = dto.Metrics,
                Metadata = dto.Metadata,
            }).ToList();

            var edges = edgeDtos.Select(dto => new CodeEdge
            {
                Id = dto.Id,
                SourceId = dto.SourceId,
                TargetId = dto.TargetId,
                Type = dto.Type,
                Metadata = dto.Metadata,
            }).ToList();

            _database.TryCommitGeneration(_database.LastCommittedGeneration + 1, nodes, edges, replacesScope: null);

            var stats = new ReconcileStatsDto(
                NodesMerged: nodeDtos.Count,
                EdgesMerged: edgeDtos.Count,
                NodesDeleted: existingNodeCount,
                Operation: "reconcile_and_prune"
            );

            return Task.FromResult(JsonSerializer.Serialize(stats, CodeContextJsonContext.Default.ReconcileStatsDto));
        }
    }
}
