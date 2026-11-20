using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Whole-graph questions answered in one call from the in-memory store: how one symbol reaches another
    /// (<see cref="FindPathAsync"/>), what depends on a symbol transitively (<see cref="GetAffectedAsync"/>) or on what
    /// a diff changes (<see cref="GetImpactAsync"/>), and which symbols the most others depend on
    /// (<see cref="GetHubsAsync"/>). Each answer is read from one graph generation. Relationships are static evidence, as
    /// in <see cref="IContextService"/>: what the workers recorded, not what runs.
    /// </summary>
    public interface IGraphQueryService
    {
        Task<GraphPathResponse> FindPathAsync(GraphPathRequest request, CancellationToken cancellationToken = default);

        Task<GraphAffectedResponse> GetAffectedAsync(GraphAffectedRequest request, CancellationToken cancellationToken = default);

        Task<GraphHubsResponse> GetHubsAsync(GraphHubsRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// What a change could break: the declarations a unified diff changes, mapped by line against the index of the
        /// working tree, and what depends on them, walked as <see cref="GetAffectedAsync"/> walks one symbol. Part of the
        /// diff that maps to nothing is reported with the reason, never dropped.
        /// </summary>
        Task<GraphImpactResponse> GetImpactAsync(GraphImpactRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>Limits shared by REST, MCP and the CLI.</summary>
    public static class GraphQueryLimits
    {
        public const int MaxDepth = 20;
        public const int DefaultPathDepth = 10;
        public const int DefaultAffectedDepth = 5;
        public const int MaxLimit = 100;
        public const int DefaultLimit = 20;

        /// <summary>Most other changes named for one changed declaration of a diff as using it.</summary>
        public const int MaxUsedByChanges = 5;
    }

    /// <summary>The shortest chain of recorded relationships from <see cref="From"/> to <see cref="To"/>.</summary>
    public sealed class GraphPathRequest
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;

        /// <summary>Most relationship hops searched, 1 to <see cref="GraphQueryLimits.MaxDepth"/>.</summary>
        public int MaxDepth { get; set; } = GraphQueryLimits.DefaultPathDepth;

        /// <summary>Comma-separated relationship kinds a hop may follow; null follows every usage kind.</summary>
        public string? Relation { get; set; }

        /// <summary>Whether the path may pass through symbols in test files.</summary>
        public bool IncludeTests { get; set; }
    }

    /// <summary>What depends, directly or transitively, on <see cref="Identifier"/>.</summary>
    public sealed class GraphAffectedRequest
    {
        public string Identifier { get; set; } = string.Empty;

        /// <summary>Most dependency hops followed, 1 to <see cref="GraphQueryLimits.MaxDepth"/>.</summary>
        public int MaxDepth { get; set; } = GraphQueryLimits.DefaultAffectedDepth;

        /// <summary>Comma-separated relationship kinds that carry impact; null means every usage kind plus implementations.</summary>
        public string? Relation { get; set; }

        /// <summary>
        /// Whether symbols in test files are listed and impact moves on from them; without it, those reached are only
        /// counted (<see cref="GraphAffectedResponse.TestsHidden"/>).
        /// </summary>
        public bool IncludeTests { get; set; }

        /// <summary>Most entries returned, 1 to <see cref="GraphQueryLimits.MaxLimit"/>.</summary>
        public int Limit { get; set; } = GraphQueryLimits.DefaultLimit;
    }

    /// <summary>The symbols with the most direct dependents.</summary>
    public sealed class GraphHubsRequest
    {
        /// <summary>Most entries returned, 1 to <see cref="GraphQueryLimits.MaxLimit"/>.</summary>
        public int Limit { get; set; } = GraphQueryLimits.DefaultLimit;

        /// <summary>Comma-separated relationship kinds counted; null counts every usage kind.</summary>
        public string? Relation { get; set; }

        /// <summary>Only symbols of this type (Class, Method, ...), compared ignoring case.</summary>
        public string? Type { get; set; }

        /// <summary>Only symbols in this repository-relative directory or file.</summary>
        public string? Within { get; set; }

        /// <summary>Whether symbols in test files are ranked and their uses counted.</summary>
        public bool IncludeTests { get; set; }

        /// <summary>Whether library symbols are ranked.</summary>
        public bool IncludeExternal { get; set; }
    }

    /// <summary>
    /// How an identifier given to a graph query resolved. A single symbol is <see cref="Target"/>; a file path
    /// stands for every symbol in that file (<see cref="File"/>). A type stands for itself and its members, so
    /// <see cref="SeedCount"/> can exceed one. Nothing is traversed for an endpoint that is missing or ambiguous.
    /// </summary>
    public class GraphEndpoint
    {
        public string Requested { get; set; } = string.Empty;

        /// <summary><c>identity</c>, <c>exact</c>, <c>filePath</c>, or <c>none</c>.</summary>
        public string MatchMode { get; set; } = "none";

        public int TotalMatches { get; set; }

        public CompactCodeNode? Target { get; set; }

        /// <summary>The file a file-path identifier resolved to, repository-relative.</summary>
        public string? File { get; set; }

        /// <summary>How many symbols the endpoint stands for: itself and, for a type, its members.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int SeedCount { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Ambiguous { get; set; }

        /// <summary>
        /// The identifiers of the other copies of <see cref="Target"/>: the same source declaration (file, span, name,
        /// type and signature) indexed more than once, as a file seen through several projects or workspaces is. The
        /// copies are one thing to change, so all of them were searched together; null when there is only one.
        /// </summary>
        public List<string>? SameDeclaration { get; set; }

        /// <summary>The first few candidates of an ambiguous name; pass one's identifier back unchanged.</summary>
        public List<CompactCodeNode>? Candidates { get; set; }

        public string? Hint { get; set; }
    }

    /// <summary>One step of a path: the node reached and how.</summary>
    public class GraphPathHop : CompactCodeNode
    {
        /// <summary>
        /// <c>start</c> (the first node), <c>uses</c> (a recorded relationship from the previous node),
        /// <c>dispatch</c> (to an implementation or override of the member just reached or started from, as a call
        /// through the declaration can), <c>member</c> (from a starting type into one of its members), or <c>memberOf</c>
        /// (from a member into the target type that contains it).
        /// </summary>
        public string Hop { get; set; } = "start";

        /// <summary>The relationship kind followed (CALLS, REFERENCES, IMPLEMENTS_MEMBER, HAS_METHOD, ...).</summary>
        public string? Relation { get; set; }

        /// <summary>For a <c>uses</c> hop, the 1-based line in the previous node's file where the relationship is recorded.</summary>
        public int? At { get; set; }
    }

    public class GraphPathResponse
    {
        /// <summary>Present only while answers come from a saved index not yet checked against the files.</summary>
        public CacheNotice? Cache { get; set; }

        public GraphEndpoint From { get; set; } = new();
        public GraphEndpoint To { get; set; } = new();

        public bool Found { get; set; }

        /// <summary>Relationship hops on the path (<c>uses</c> hops); dispatch and containment steps are free.</summary>
        public int? Length { get; set; }

        public List<GraphPathHop>? Path { get; set; }

        public int MaxDepth { get; set; }

        /// <summary>The search stopped at <see cref="MaxDepth"/> with nodes left to explore.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DepthLimited { get; set; }

        /// <summary>
        /// When no path was found and one exists with fewer filters: which of the request's own filters excluded it,
        /// <c>relation</c> and/or <c>tests</c>.
        /// </summary>
        public List<string>? ExcludedBy { get; set; }

        /// <summary>Why no path was found, in words, and what to try.</summary>
        public string? Explanation { get; set; }

        /// <summary>Set when this answer may be short because a project of its worker was not fully analyzed.</summary>
        public CoverageSummary? Coverage { get; set; }
    }

    /// <summary>A symbol that depends on the seed, directly or through other dependents.</summary>
    public class GraphAffectedNode : CompactCodeNode
    {
        /// <summary>The kind of the relationship by which it depends on <see cref="Via"/>.</summary>
        public string Relation { get; set; } = string.Empty;

        /// <summary>The identifier of the seed or affected symbol it depends on, one hop nearer the seed.</summary>
        public string Via { get; set; } = string.Empty;

        /// <summary>
        /// Set when it reaches <see cref="Via"/> through dispatch: it uses this declaration (an interface or base
        /// member) that <see cref="Via"/> implements or overrides.
        /// </summary>
        public string? Through { get; set; }
    }

    public class GraphAffectedResponse
    {
        public CacheNotice? Cache { get; set; }

        public GraphEndpoint Seed { get; set; } = new();

        public int MaxDepth { get; set; }

        /// <summary>Affected symbols found within <see cref="MaxDepth"/>, test-file symbols included only when listed.</summary>
        public int TotalCount { get; set; }

        public int ReturnedCount { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }

        /// <summary>More dependents lie beyond <see cref="MaxDepth"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DepthLimited { get; set; }

        /// <summary>Affected symbols in test files, not listed (nor moved on from) unless tests are included.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int TestsHidden { get; set; }

        /// <summary>Distinct files of the counted symbols.</summary>
        public int FileCount { get; set; }

        public List<GraphAffectedNode> Items { get; set; } = new();

        public string? Hint { get; set; }

        public CoverageSummary? Coverage { get; set; }
    }

    /// <summary>A symbol ranked by how many others use it directly.</summary>
    public class GraphHubNode : CompactCodeNode
    {
        /// <summary>Distinct symbols with a counted relationship to it.</summary>
        public int Dependents { get; set; }

        /// <summary>Distinct files those symbols are in.</summary>
        public int DependentFiles { get; set; }
    }

    public class GraphHubsResponse
    {
        public CacheNotice? Cache { get; set; }

        /// <summary>Symbols passing the filters with at least one dependent.</summary>
        public int TotalCount { get; set; }

        public int ReturnedCount { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }

        /// <summary>The <c>within</c> filter as applied, repository-relative.</summary>
        public string? Within { get; set; }

        public List<GraphHubNode> Items { get; set; } = new();

        public string? Hint { get; set; }

        public CoverageSummary? Coverage { get; set; }
    }

    /// <summary>The graph changed under every attempt to read one generation of it; the caller may retry.</summary>
    public sealed class GraphChangingException(string message) : System.Exception(message);
}
