using System.Diagnostics;
using System.Text.Json;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using CSnakes.Runtime.Python;

namespace CodeContext.Core.Repositories.Kuzu;

public class KuzuGraphRepository : ICodeGraphRepository
{
    private readonly IKuzuApi _kuzuApi;

    public KuzuGraphRepository(IKuzuApi kuzuApi)
    {
        _kuzuApi = kuzuApi;
    }

    public async Task<Guid> SaveGraphAsync(CodeGraph graph)
    {
        return await Task.Run(() =>
        {
            // Convert nodes to DTOs
            var nodeDtos = graph.Nodes.Select(node => new NodeDto(
                Id: node.Id,
                Name: node.Name,
                Type: node.Type,
                Language: node.Language,
                FilePath: node.FilePath,
                StartLine: node.StartLine,
                EndLine: node.EndLine,
                StartCol: node.StartCol,
                EndCol: node.EndCol,
                Namespace: node.Namespace,
                Visibility: node.Visibility,
                Signature: node.Signature,
                ReturnType: node.ReturnType,
                Parameters: node.Parameters,
                Modifiers: node.Modifiers,
                Metrics: node.Metrics,
                Metadata: node.Metadata
            )).ToList();

            // Convert edges to DTOs
            var edgeDtos = graph.Edges.Select(edge =>
                new EdgeDto(
                    Id: edge.Id,
                    SourceId: edge.SourceId,
                    TargetId: edge.TargetId,
                    Type: edge.Type,
                    Metadata: edge.Metadata
                )).ToList();

            // Insert nodes and edges in batches
            if (nodeDtos.Any())
            {
                var nodesJson = JsonSerializer.Serialize(nodeDtos, CodeContextJsonContext.Default.ListNodeDto);
                _kuzuApi.InsertNodesBatch(nodesJson);
            }
            
            if (edgeDtos.Any())
            {
                var edgesJson = JsonSerializer.Serialize(edgeDtos, CodeContextJsonContext.Default.ListEdgeDto);
                _kuzuApi.InsertEdgesBatch(edgesJson);
            }

            // Return a new GUID as the graph ID
            // In a real implementation, we might want to store this mapping
            return Guid.NewGuid();
        });
    }

    public async Task<CodeGraph?> GetGraphAsync()
    {
        return await Task.Run(() =>
        {
            // Get all nodes by querying each type
            var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };
            var allNodes = new List<CodeNode>();

            foreach (var nodeType in nodeTypes)
            {
                var nodesJson = _kuzuApi.FindNodesByType(nodeType);
                var nodeDtos = KuzuResponseParser.ParseResponse(nodesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeDto);
                if (nodeDtos != null)
                {
                    foreach (var nodeDto in nodeDtos)
                    {
                        // Skip nodes with null IDs
                        if (nodeDto.Id != null)
                        {
                            allNodes.Add(ConvertToCodeNode(nodeDto));
                        }
                    }
                }
            }

            // For edges, we need to get them by querying relationships for each node
            // This is inefficient but necessary given the current API
            var allEdges = new List<CodeEdge>();
            var processedNodeIds = new HashSet<string>();

            foreach (var node in allNodes.Where(n => n.Id != null))
            {
                if (processedNodeIds.Contains(node.Id!))
                    continue;

                processedNodeIds.Add(node.Id!);

                // Get outgoing edges
                var dependenciesJson = _kuzuApi.GetDependencies(node.Id!);
                var dependencies = KuzuResponseParser.ParseResponse(dependenciesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeWithRelationshipTypeDto);
                if (dependencies != null)
                {
                    foreach (var dep in dependencies)
                    {
                        var edge = new CodeEdge
                        {
                            SourceId = node.Id,
                            TargetId = dep.Id,
                            Type = dep.RelationshipType
                        };
                        allEdges.Add(edge);
                    }
                }
            }

            return new CodeGraph
            {
                Nodes = allNodes,
                Edges = allEdges
            };
        });
    }

    public async Task ClearAsync()
    {
        await Task.Run(() =>
        {
            _kuzuApi.ClearDatabase();
        });
    }

    public async Task<string> ReconcileAndPruneAsync(string nodesJson, string edgesJson)
    {
        return await Task.Run(() =>
        {
            var result = _kuzuApi.ReconcileAndPruneGraph(nodesJson, edgesJson);
            
            // Check if the result is an error response
            if (KuzuResponseParser.IsErrorResponse(result))
            {
                // This will throw the appropriate exception
                throw KuzuResponseParser.ParseErrorResponse(result);
            }
            
            return result;
        });
    }

    private static CodeNode ConvertToCodeNode(NodeDto nodeDto)
    {
        Debug.Assert(nodeDto.Id != null);
        
        return new CodeNode
        {
            Id = nodeDto.Id,
            Name = nodeDto.Name,
            Type = nodeDto.Type,
            Language = nodeDto.Language,
            FilePath = nodeDto.FilePath,
            StartLine = nodeDto.StartLine,
            EndLine = nodeDto.EndLine,
            Namespace = nodeDto.Namespace,
            Visibility = nodeDto.Visibility,
            Signature = nodeDto.Signature
        };
    }
}