
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
    }
}
