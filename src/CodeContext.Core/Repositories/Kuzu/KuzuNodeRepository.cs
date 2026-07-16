using System.Diagnostics;
using System.Text.Json;
using CodeContext.Core.Serialization;
using CSnakes.Runtime;
using CSnakes.Runtime.Python;

namespace CodeContext.Core.Repositories.Kuzu;

public class KuzuNodeRepository : ICodeNodeRepository
{
    private readonly IKuzuApi _kuzuApi;

    public KuzuNodeRepository(IKuzuApi kuzuApi)
    {
        _kuzuApi = kuzuApi;
    }

    public async Task<CodeNode?> GetByIdAsync(string id)
    {
        return await Task.Run(() =>
        {
            var nodeJson = _kuzuApi.GetNodeById(id);
            if (nodeJson == null)
                return null;

            var nodeDto = KuzuResponseParser.ParseResponse(nodeJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.NodeDto);
            if (nodeDto == null)
                return null;

            return ConvertToCodeNode(nodeDto);
        });
    }

    public async Task<List<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false)
    {
        return await Task.Run(() =>
        {
            string nodesJson;
            
            // Use optimized database-level filtering when type is specified
            if (!string.IsNullOrEmpty(type))
            {
                nodesJson = _kuzuApi.FindNodesByNameAndType(name, type, exact: exact);
            }
            else
            {
                // Fallback to original method when no type filtering needed
                nodesJson = _kuzuApi.FindNodesByName(name, exact: exact);
            }
            
            var nodeDtos = KuzuResponseParser.ParseResponse(nodesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeDto);
            if (nodeDtos == null)
                return new List<CodeNode>();

            var codeNodes = nodeDtos.Select(ConvertToCodeNode).ToList();
            return codeNodes;
        });
    }

    public async Task<List<CodeNode>> GetAllAsync()
    {
        return await Task.Run(() =>
        {
            // Since Kuzu doesn't have a direct "get all nodes" in our API,
            // we'll need to use find_nodes_by_type for each known type
            var allNodes = new List<CodeNode>();
            var nodeTypes = new[] { "Class", "Interface", "Method", "Property", "Field", "Enum", "Struct" };

            foreach (var nodeType in nodeTypes)
            {
                var nodesJson = _kuzuApi.FindNodesByType(nodeType);
                var nodeDtos = KuzuResponseParser.ParseResponse(nodesJson, CodeContextJsonContext.Default, CodeContextJsonContext.Default.IReadOnlyListNodeDto);
                if (nodeDtos != null)
                {
                    allNodes.AddRange(nodeDtos.Select(ConvertToCodeNode));
                }
            }

            return allNodes;
        });
    }

    public async Task UpsertAsync(CodeNode node)
    {
        await Task.Run(() =>
        {
            var nodeDto = ConvertToNodeDto(node);
            var nodeJson = JsonSerializer.Serialize(nodeDto, CodeContextJsonContext.Default.NodeDto);
            _kuzuApi.InsertNode(nodeJson);
        });
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await Task.Run(() => _kuzuApi.DeleteNode(id), cancellationToken);
    }

    private CodeNode ConvertToCodeNode(NodeDto nodeDto)
    {
        return new CodeNode
        {
            Id = nodeDto.Id,
            Name = nodeDto.Name,
            Type = nodeDto.Type,
            Language = nodeDto.Language,
            FilePath = nodeDto.FilePath,
            StartLine = nodeDto.StartLine,
            EndLine = nodeDto.EndLine,
            StartCol = nodeDto.StartCol,
            EndCol = nodeDto.EndCol,
            Namespace = nodeDto.Namespace,
            Visibility = nodeDto.Visibility,
            Signature = nodeDto.Signature,
            ReturnType = nodeDto.ReturnType,
            Parameters = nodeDto.Parameters,
            Modifiers = nodeDto.Modifiers,
            Metrics = nodeDto.Metrics,
            Metadata = nodeDto.Metadata
        };
    }

    private NodeDto ConvertToNodeDto(CodeNode node)
    {
        return new NodeDto(
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
        );
    }
}