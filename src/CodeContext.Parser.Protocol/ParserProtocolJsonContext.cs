using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeContext.Parser.Protocol;

/// <summary>
/// Source-generated serialization for every protocol message. Workers written in
/// other languages implement <c>protocol/parser-protocol.schema.json</c> instead.
/// </summary>
[JsonSerializable(typeof(JsonRpcMessage))]
[JsonSerializable(typeof(JsonRpcError))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(SpanSemantics))]
[JsonSerializable(typeof(WorkerCapabilities))]
[JsonSerializable(typeof(OpenWorkspaceParams))]
[JsonSerializable(typeof(OpenWorkspaceResult))]
[JsonSerializable(typeof(IndexWorkspaceParams))]
[JsonSerializable(typeof(IndexWorkspaceResult))]
[JsonSerializable(typeof(ApplyChangesParams))]
[JsonSerializable(typeof(ApplyChangesResult))]
[JsonSerializable(typeof(FileChangeDto))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(NativeSyntaxTreeParams))]
[JsonSerializable(typeof(NativeSyntaxTreeResult))]
[JsonSerializable(typeof(AnalysisDelta))]
[JsonSerializable(typeof(AnalysisProgress))]
[JsonSerializable(typeof(ProtocolNode))]
[JsonSerializable(typeof(ProtocolEdge))]
[JsonSerializable(typeof(UnresolvedReference))]
[JsonSerializable(typeof(ProtocolDiagnostic))]
[JsonSerializable(typeof(WorkerManifest))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<FileChangeDto>))]
[JsonSerializable(typeof(List<ProtocolNode>))]
[JsonSerializable(typeof(List<ProtocolEdge>))]
[JsonSerializable(typeof(List<UnresolvedReference>))]
[JsonSerializable(typeof(List<ProtocolDiagnostic>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ParserProtocolJsonContext : JsonSerializerContext
{
}
