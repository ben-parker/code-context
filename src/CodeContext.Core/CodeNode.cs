
namespace CodeContext.Core
{
    public class CodeNode
    {
        public string? Id { get; set; }
        /// <summary>Parser-owned, stable public identity. Unlike Id, this round-trips across reindexing.</summary>
        public string Identifier { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? FilePath { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public int StartCol { get; set; }
        public int EndCol { get; set; }
        public string? Namespace { get; set; }
        public string? Visibility { get; set; }
        public string? Signature { get; set; }
        public string? Language { get; set; }
        public string? ReturnType { get; set; }
        public string? Parameters { get; set; }
        public string? Modifiers { get; set; }
        public string? Metrics { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// The other declarations of this node's symbol, when the worker reports it declared in more than one place (a
        /// partial type or member in several files, merged declarations in one file): each part's file and span, in
        /// the stored span form. The node's own location is not repeated. Null for the usual single declaration. Kept
        /// out of the REST and MCP answers; diff impact maps a changed line of a part to this node.
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<CodeNodePart>? Parts { get; set; }
    }

    /// <summary>One other declaration of a node's symbol (see <see cref="CodeNode.Parts"/>): its file and span, zero-based, end-exclusive.</summary>
    public sealed record CodeNodePart(string FilePath, int StartLine, int EndLine, int StartCol, int EndCol);
}
