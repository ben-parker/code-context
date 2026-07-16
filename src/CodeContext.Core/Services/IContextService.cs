using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodeContext.Core.Services
{
    public interface IContextService
    {
        /// <summary>
        /// Gets complete context for an identifier (name or file path)
        /// </summary>
        /// <param name="identifier">The identifier to search for (name or file path)</param>
        /// <param name="type">Optional type filter (Class, Method, etc.)</param>
        /// <param name="depth">How many relationship levels to traverse (default: 2)</param>
        /// <param name="includeTests">Whether to include test-related information (default: true)</param>
        /// <param name="includeContent">Whether to include file content snippets (default: false)</param>
        /// <param name="exact">Whether to perform exact match vs contains (default: false)</param>
        /// <returns>Complete context information for the identifier</returns>
        Task<CompleteContextResponse> GetCompleteContextAsync(
            string identifier,
            string? type = null,
            int depth = 2,
            bool includeTests = true,
            bool includeContent = false,
            bool exact = false,
            bool includeRelated = true,
            bool includeMetrics = true,
            int maxTestFiles = 5,
            int maxTestMethods = 5,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null);

        Task<CompactContextResponse> GetCompactContextAsync(
            string identifier,
            string? type = null,
            int depth = 1,
            bool includeTests = false,
            bool includeContent = false,
            bool? exact = null,
            bool includeRelated = false,
            bool includeMetrics = false,
            int maxMatches = 5,
            int maxRelationships = 10,
            bool expandAmbiguous = false,
            int maxCallSites = 3,
            int maxTestFiles = 5,
            int maxTestMethods = 5,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null);

        /// <summary>
        /// Gets complete context for multiple identifiers
        /// </summary>
        /// <param name="request">Multi-context request with identifiers and options</param>
        /// <returns>Array of complete context objects</returns>
        Task<List<CompleteContextResponse>> GetMultipleContextAsync(MultiContextRequest request);

        Task<List<CompactContextResponse>> GetMultipleCompactContextAsync(MultiContextRequest request);
    }

    [JsonConverter(typeof(JsonStringEnumConverter<ContextResponseView>))]
    public enum ContextResponseView
    {
        Compact,
        Full
    }

    public class CompactContextResponse
    {
        public string View { get; set; } = "compact";
        public string MatchMode { get; set; } = "substring";
        public bool SubstringSearchSkipped { get; set; }
        public int TotalMatches { get; set; }
        public int ReturnedMatches { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Ambiguous { get; set; }
        public string? DisambiguationHint { get; set; }
        public CompactMatchFacets? Facets { get; set; }
        public List<CompactContextMatch> Matches { get; set; } = new();
    }

    public class CompactMatchFacets
    {
        public Dictionary<string, int> Types { get; set; } = new();
        public Dictionary<string, int> Files { get; set; } = new();
        public int TotalFiles { get; set; }
    }

    public class CompactContextMatch
    {
        public CompactCodeNode Target { get; set; } = null!;
        public CompactRelationships? Relationships { get; set; }
        public CompactTesting? Testing { get; set; }
        public ContextMetrics? Metrics { get; set; }
        public string? Content { get; set; }
    }

    public class CompactCodeNode
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? File { get; set; }
        public int Line { get; set; }
        public string? Signature { get; set; }
        public List<string>? Relations { get; set; }
        public int? Occurrences { get; set; }
        public List<int>? Lines { get; set; }
        public int? CallSiteCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool CallSitesTruncated { get; set; }
        public int? Distance { get; set; }
        public List<string>? RelationPath { get; set; }
        public string Identifier { get; set; } = string.Empty;
        public List<string>? Bindings { get; set; }
    }

    public class CompactRelationships
    {
        public List<CompactCodeNode>? Uses { get; set; }
        public int? UsesCount { get; set; }
        public int? UsesReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool UsesTruncated { get; set; }
        public List<CompactCodeNode>? UsedBy { get; set; }
        public int? UsedByCount { get; set; }
        public int? UsedByReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool UsedByTruncated { get; set; }
        public List<CompactCodeNode>? TransitiveUses { get; set; }
        public int? TransitiveUsesCount { get; set; }
        public int? TransitiveUsesReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool TransitiveUsesTruncated { get; set; }
        public List<CompactCodeNode>? TransitiveUsedBy { get; set; }
        public int? TransitiveUsedByCount { get; set; }
        public int? TransitiveUsedByReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool TransitiveUsedByTruncated { get; set; }
        public List<string>? FileDependencies { get; set; }
        public int? FileDependenciesCount { get; set; }
        public int? FileDependenciesReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool FileDependenciesTruncated { get; set; }
        public List<string>? FileDependents { get; set; }
        public int? FileDependentsCount { get; set; }
        public int? FileDependentsReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool FileDependentsTruncated { get; set; }
        public List<CompactCodeNode>? RelatedItems { get; set; }
        public int? RelatedItemsCount { get; set; }
        public int? RelatedItemsReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool RelatedItemsTruncated { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }
    }

    public class CompactTesting
    {
        /// <summary>Compatibility summary; true for direct, indirect, implementation, or named evidence.</summary>
        public bool IsTested { get; set; }
        public bool DirectlyTested { get; set; }
        public int TestReferenceCount { get; set; }
        public int TestImplementerCount { get; set; }
        public int HeuristicMatchCount { get; set; }
        public int TestFileCount { get; set; }
        public int TestFilesReturnedCount { get; set; }
        public List<CompactTestFile> TestFiles { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool TestFilesTruncated { get; set; }
    }

    public class CompactTestFile
    {
        public string File { get; set; } = string.Empty;
        public int TestCount { get; set; }
        public int TestMethodsReturnedCount { get; set; }
        public List<CompactCodeNode> TestMethods { get; set; } = new();
        public List<string> Evidence { get; set; } = new();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool TestMethodsTruncated { get; set; }
    }

    /// <summary>
    /// Complete context response containing all information about a code construct
    /// </summary>
    public class CompleteContextResponse
    {
        /// <summary>
        /// Array of matches (even for single results to handle ambiguity)
        /// </summary>
        public List<ContextMatch> Matches { get; set; } = new();

        /// <summary>
        /// Optional hint when multiple matches are found
        /// </summary>
        public string? DisambiguationHint { get; set; }
    }

    /// <summary>
    /// Individual context match
    /// </summary>
    public class ContextMatch
    {
        /// <summary>
        /// The target node that was matched
        /// </summary>
        public CodeNode Target { get; set; } = null!;

        /// <summary>
        /// Relationship information
        /// </summary>
        public ContextRelationships Relationships { get; set; } = new();

        /// <summary>
        /// Testing information
        /// </summary>
        public ContextTesting Testing { get; set; } = new();

        /// <summary>
        /// Code metrics
        /// </summary>
        public ContextMetrics Metrics { get; set; } = new();

        /// <summary>
        /// File content snippets (if requested)
        /// </summary>
        public string? Content { get; set; }
    }

    /// <summary>
    /// Relationship information for a code construct
    /// </summary>
    public class ContextRelationships
    {
        /// <summary>
        /// What this construct uses/depends on
        /// </summary>
        public List<CodeNode> Uses { get; set; } = new();

        /// <summary>
        /// What uses this construct
        /// </summary>
        public List<CodeNode> UsedBy { get; set; } = new();

        /// <summary>Nodes beyond the direct Uses list, labeled with shortest path.</summary>
        public List<ContextTransitiveRelationship> TransitiveUses { get; set; } = new();

        /// <summary>Nodes beyond the direct UsedBy list, labeled with shortest path.</summary>
        public List<ContextTransitiveRelationship> TransitiveUsedBy { get; set; } = new();

        /// <summary>
        /// Resolved semantic relationships from this file to other repository files.
        /// </summary>
        public List<string> FileDependencies { get; set; } = new();

        /// <summary>
        /// Repository files with resolved semantic relationships into this file.
        /// </summary>
        public List<string> FileDependents { get; set; } = new();

        /// <summary>Methods connected to the target through implementation or override declarations.</summary>
        public List<CodeNode> MethodFamilyMembers { get; set; } = new();

        /// <summary>Family declarations that have statically bound incoming calls.</summary>
        public List<CodeNode> StaticallyBoundTargets { get; set; } = new();

        /// <summary>
        /// Heuristic same-file and same-namespace proximity; not dependency evidence.
        /// </summary>
        public List<CodeNode> RelatedItems { get; set; } = new();
    }

    public class ContextTransitiveRelationship
    {
        public CodeNode Node { get; set; } = null!;
        public int Distance { get; set; }
        public List<string> RelationPath { get; set; } = new();
    }

    /// <summary>
    /// Testing information for a code construct
    /// </summary>
    public class ContextTesting
    {
        /// <summary>
        /// Test files that test this construct
        /// </summary>
        public List<TestFileInfo> TestFiles { get; set; } = new();

        /// <summary>
        /// Whether this construct appears to be tested
        /// </summary>
        public bool IsTested { get; set; }
        public bool DirectlyTested { get; set; }
        public int TestReferenceCount { get; set; }
        public int TestImplementerCount { get; set; }
        public int HeuristicMatchCount { get; set; }
        public int TestFileCount { get; set; }
        public int TestFilesReturnedCount { get; set; }
        public bool TestFilesTruncated { get; set; }
    }

    /// <summary>
    /// Information about a test file
    /// </summary>
    public class TestFileInfo
    {
        /// <summary>
        /// Path to the test file
        /// </summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>
        /// Test methods that test the target
        /// </summary>
        public List<CodeNode> TestMethods { get; set; } = new();

        /// <summary>
        /// Number of tests in this file
        /// </summary>
        public int TestCount { get; set; }
        public int TestMethodsReturnedCount { get; set; }
        public bool TestMethodsTruncated { get; set; }

        /// <summary>
        /// Coverage percentage (if available)
        /// </summary>
        public double? Coverage { get; set; }
        public List<string> Evidence { get; set; } = new();
    }

    /// <summary>
    /// Code metrics for a construct
    /// </summary>
    public class ContextMetrics
    {
        /// <summary>
        /// Complexity score
        /// </summary>
        public int Complexity { get; set; }

        /// <summary>
        /// Lines of code
        /// </summary>
        public int LinesOfCode { get; set; }

        /// <summary>
        /// Number of dependencies
        /// </summary>
        public int DependencyCount { get; set; }

        /// <summary>
        /// Number of dependents
        /// </summary>
        public int DependentCount { get; set; }
    }

    /// <summary>
    /// Request for multiple context lookups
    /// </summary>
    public class MultiContextRequest
    {
        /// <summary>
        /// List of identifiers to look up
        /// </summary>
        public List<string> Identifiers { get; set; } = new();

        public string? Type { get; set; }

        /// <summary>
        /// Depth for relationship traversal
        /// </summary>
        public int Depth { get; set; } = 1;

        public ContextResponseView View { get; set; } = ContextResponseView.Compact;

        public bool IncludeTests { get; set; }

        public bool IncludeContent { get; set; }

        public bool? Exact { get; set; }

        public bool IncludeRelated { get; set; }

        public bool IncludeMetrics { get; set; }

        public int MaxMatches { get; set; } = 5;

        public int MaxRelationships { get; set; } = 3;

        public int MaxCallSites { get; set; } = 3;

        public int MaxTestFiles { get; set; } = 5;

        public int MaxTestMethods { get; set; } = 5;

        public bool ExpandAmbiguous { get; set; }

        public string? ContainingType { get; set; }
        public string? Namespace { get; set; }
        public string? Signature { get; set; }
        public string? SourceFile { get; set; }

        /// <summary>
        /// Types of relationships to include
        /// </summary>
        public List<string> RelationshipTypes { get; set; } = new();
    }
}
