using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// What a change could break: the declarations a unified diff changes (its changed lines mapped to the innermost
    /// declarations holding them, in the index of the working tree), everything that depends on them as
    /// <see cref="IGraphQueryService.GetAffectedAsync"/> walks it, in one walk, and the test files in reach.
    /// </summary>
    public sealed class GraphImpactRequest
    {
        /// <summary>A unified diff (<c>git diff</c> output) whose new side is the working tree.</summary>
        public string Diff { get; set; } = string.Empty;

        /// <summary>Most dependency hops followed, 1 to <see cref="GraphQueryLimits.MaxDepth"/>.</summary>
        public int MaxDepth { get; set; } = GraphQueryLimits.DefaultAffectedDepth;

        /// <summary>Comma-separated relationship kinds that carry impact; null means every usage kind plus implementations.</summary>
        public string? Relation { get; set; }

        /// <summary>Whether dependents in test files are listed and impact moves on from them.</summary>
        public bool IncludeTests { get; set; }

        /// <summary>Most entries in each list, 1 to <see cref="GraphQueryLimits.MaxLimit"/>.</summary>
        public int Limit { get; set; } = GraphQueryLimits.DefaultLimit;
    }

    /// <summary>A declaration the diff changes.</summary>
    public class GraphChangedNode : CompactCodeNode
    {
        /// <summary>
        /// <c>member</c>, or <c>container</c> for a type or module changed on its own lines (its header, its closing
        /// line, or between its members): impact is walked from the container itself, not from every member.
        /// </summary>
        public string Level { get; set; } = "member";

        /// <summary>The changed new-side lines that are its own, 1-based (<c>12-14,20</c>); null when only lines were removed.</summary>
        public string? ChangedLines { get; set; }

        /// <summary>Removed lines inside it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int RemovedLines { get; set; }

        /// <summary>Listed dependents attributed to it (each dependent is attributed to the nearest change that reaches it).</summary>
        public int Dependents { get; set; }

        /// <summary>Dependents in test files attributed to it that are counted, not listed.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int TestsHidden { get; set; }

        /// <summary>Other nodes of the same span (one declaration indexed more than once); all were walked from.</summary>
        public List<string>? SameDeclaration { get; set; }
    }

    /// <summary>A dependent of the change, and which changed declaration it is attributed to.</summary>
    public class GraphImpactNode : GraphAffectedNode
    {
        /// <summary>
        /// The identifier of the changed declaration whose impact reached it first (nearest, then in change order). It
        /// may depend on other changes too; each dependent is listed once.
        /// </summary>
        public string Seed { get; set; } = string.Empty;
    }

    /// <summary>
    /// Part of the diff that maps to no declaration of the index, and why: the answer does not cover it.
    /// </summary>
    public class GraphUnmappedChange
    {
        /// <summary>The file, repository-relative (as the diff names it when it is outside the indexed root).</summary>
        public string File { get; set; } = string.Empty;

        /// <summary>The path before a rename or copy, or of a deleted file.</summary>
        public string? OldFile { get; set; }

        /// <summary><c>modified</c>, <c>added</c>, <c>deleted</c>, <c>renamed</c> or <c>copied</c>.</summary>
        public string Status { get; set; } = "modified";

        /// <summary>
        /// <c>outsideDeclarations</c>, <c>removedBetweenDeclarations</c>, <c>blockCommentBoundary</c>, <c>deletedFile</c>, <c>renamed</c>, <c>binary</c>,
        /// <c>notIndexed</c>, <c>contentMismatch</c>, <c>notYetIndexed</c>, <c>unreadable</c>, <c>outsideRoot</c>,
        /// <c>invalidPath</c> or <c>combinedDiff</c>.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>New-side lines concerned, 1-based (<c>1-3,8</c>).</summary>
        public string? Lines { get; set; }

        /// <summary>Old-side lines concerned (removed lines), 1-based.</summary>
        public string? OldLines { get; set; }

        /// <summary>Changed lines this entry stands for.</summary>
        public int LineCount { get; set; }

        /// <summary>What it means and what to do.</summary>
        public string Detail { get; set; } = string.Empty;
    }

    public class GraphImpactResponse
    {
        public CacheNotice? Cache { get; set; }

        public int MaxDepth { get; set; }

        /// <summary>
        /// True only when every changed line mapped to a declaration and every mapped file was matched against the working
        /// tree by its blob id. False means part of the change is not covered: see <see cref="Unmapped"/> and
        /// <see cref="ContentUnchecked"/>.
        /// </summary>
        public bool Complete { get; set; }

        /// <summary>Files the diff names.</summary>
        public int DiffFiles { get; set; }

        /// <summary>Files whose new side matched the working tree by the diff's blob id.</summary>
        public int ContentChecked { get; set; }

        /// <summary>Files mapped without that check, because the diff gives no blob id for them (diff -u writes none).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ContentUnchecked { get; set; }

        /// <summary>Changed declarations.</summary>
        public int ChangeCount { get; set; }
        public int ChangesReturned { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool ChangesTruncated { get; set; }
        public List<GraphChangedNode> Changes { get; set; } = new();

        /// <summary>Entries of the diff that map to nothing (one per file and reason).</summary>
        public int UnmappedCount { get; set; }
        public int UnmappedReturned { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool UnmappedTruncated { get; set; }

        /// <summary>Changed lines the unmapped entries stand for.</summary>
        public int UnmappedLines { get; set; }

        /// <summary>Unmapped entries by reason, all of them counted.</summary>
        public Dictionary<string, int>? UnmappedReasons { get; set; }
        public List<GraphUnmappedChange> Unmapped { get; set; } = new();

        /// <summary>
        /// Changed lines that change no declaration: holding only whitespace, or only a comment where no member holds
        /// them (a doc comment above a member, a file header).
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int IgnoredLines { get; set; }

        /// <summary>Files whose mode alone changed.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ModeOnlyFiles { get; set; }

        /// <summary>Dependents found within <see cref="MaxDepth"/>, test-file symbols included only when listed.</summary>
        public int TotalCount { get; set; }
        public int ReturnedCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Truncated { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DepthLimited { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int TestsHidden { get; set; }

        /// <summary>Distinct files of the listed dependents.</summary>
        public int FileCount { get; set; }
        public List<GraphImpactNode> Items { get; set; } = new();

        /// <summary>Test files in reach: holding a changed declaration or a dependent, listed or hidden.</summary>
        public int TestFileCount { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool TestFilesTruncated { get; set; }
        public List<string> TestFiles { get; set; } = new();

        public string? Hint { get; set; }

        public CoverageSummary? Coverage { get; set; }
    }

    /// <summary>
    /// When the index last finished reading every file it holds: a file whose last change is later may not be indexed
    /// as it is now. Null while no such read has finished (the first scan is running, or a saved index is being
    /// re-checked or failed its check).
    /// </summary>
    public interface IIndexFreshness
    {
        DateTime? FilesReadAsOfUtc { get; }
    }

    /// <summary>A diff cannot be matched to the index now; the caller may retry once indexing settles.</summary>
    public sealed class IndexNotReadyException(string message) : System.Exception(message);
}
