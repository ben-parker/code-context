using System.Diagnostics;
using System.Text.Json;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using CSnakes.Runtime.Python;

namespace CodeContext.Core.Repositories.Kuzu;

public class KuzuEdgeRepository : ICodeEdgeRepository
{
    private readonly IKuzuApi _kuzuApi;

    public KuzuEdgeRepository(IKuzuApi kuzuApi)
    {
        _kuzuApi = kuzuApi;
    }

    public async Task<List<CodeEdge>> GetAllAsync()
    {
        return await Task.Run(() =>
        {
            var edgesJson = _kuzuApi.GetAllEdges();
            var edgeDtos = KuzuResponseParser.ParseResponse(edgesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListEdgeDto);
            if (edgeDtos == null)
                return new List<CodeEdge>();

            var edges = new List<CodeEdge>();

            foreach (var dto in edgeDtos)
            {
                var edge = new CodeEdge
                {
                    Id = dto.Id,
                    SourceId = dto.SourceId,
                    TargetId = dto.TargetId,
                    Type = dto.Type,
                    Metadata = dto.Metadata
                };
                edges.Add(edge);
            }

            return edges;
        });
    }

    public async Task<List<CodeEdge>> GetBySourceIdAsync(string sourceId, string? type = null)
    {
        return await Task.Run(() =>
        {
            var dependenciesJson = _kuzuApi.GetDependencies(sourceId);
            var dependencies = KuzuResponseParser.ParseResponse(dependenciesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeWithRelationshipTypeDto);
            if (dependencies == null)
                return new List<CodeEdge>();

            var edges = new List<CodeEdge>();

            foreach (var dep in dependencies)
            {
                var edge = new CodeEdge
                {
                    SourceId = sourceId,
                    TargetId = dep.Id,
                    Type = dep.RelationshipType
                };

                // Filter by type if specified
                if (string.IsNullOrEmpty(type) || edge.Type == type)
                {
                    edges.Add(edge);
                }
            }

            return edges;
        });
    }

    public async Task<List<CodeEdge>> GetByTargetIdAsync(string targetId, string? type = null)
    {
        return await Task.Run(() =>
        {
            var callersJson = _kuzuApi.GetCallers(targetId);
            var callers = KuzuResponseParser.ParseResponse(callersJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeWithEdgeInfoDto);
            if (callers == null)
                return new List<CodeEdge>();

            var edges = new List<CodeEdge>();

            foreach (var caller in callers)
            {
                // Extract edge info
                if (caller.EdgeInfo != null)
                {
                    var edge = new CodeEdge
                    {
                        SourceId = caller.Id,
                        TargetId = targetId,
                        Type = caller.EdgeInfo.Type,
                        Id = caller.EdgeInfo.Id,
                        Metadata = caller.EdgeInfo.Metadata
                    };

                    // Filter by type if specified
                    if (string.IsNullOrEmpty(type) || edge.Type == type)
                    {
                        edges.Add(edge);
                    }
                }
            }

            return edges;
        });
    }

    public async Task UpsertAsync(CodeEdge edge)
    {
        await Task.Run(() =>
        {
            var edgeDto = ConvertToEdgeDto(edge);
            var edgeJson = JsonSerializer.Serialize(edgeDto, CodeContextJsonContext.Default.EdgeDto);
            _kuzuApi.InsertEdge(edgeJson);
        });
    }

    public async Task DeleteByNodeIdAsync(string nodeId, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            _kuzuApi.DeleteEdgesByNode(nodeId);            
        }, cancellationToken);
    }

    private EdgeDto ConvertToEdgeDto(CodeEdge edge)
    {
        return new EdgeDto(
            Id: edge.Id,
            SourceId: edge.SourceId,
            TargetId: edge.TargetId,
            Type: edge.Type,
            Metadata: edge.Metadata
        );
    }
}