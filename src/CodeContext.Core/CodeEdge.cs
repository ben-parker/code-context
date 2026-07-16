
namespace CodeContext.Core
{
    public class CodeEdge
    {
        public string? Id { get; set; }
        public string? SourceId { get; set; }
        public string? TargetId { get; set; }
        public string? Type { get; set; }
        public IReadOnlyDictionary<string, string>? Metadata { get; set; }
    }
}
