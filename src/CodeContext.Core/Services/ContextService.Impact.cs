using System.Text;
using CodeContext.Core.Diffs;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Diff impact (<see cref="IGraphQueryService.GetImpactAsync"/>): <c>affected</c>, seeded from what a diff changes.
    /// The diff is data: it is parsed here, never applied, and nothing is run to produce or check it.
    /// </summary>
    public partial class ContextService
    {
        /// <summary>Kinds of node that hold declarations, whether or not the worker recorded containment edges for them.</summary>
        private static readonly HashSet<string> ContainerTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Class", "Interface", "Struct", "Record", "Enum", "Module", "Namespace",
        };

        public Task<GraphImpactResponse> GetImpactAsync(GraphImpactRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            RequireRange(request.MaxDepth, 1, GraphQueryLimits.MaxDepth, "maxDepth");
            RequireRange(request.Limit, 1, GraphQueryLimits.MaxLimit, "limit");
            var relations = ParseRelationFilter(request.Relation);
            var files = UnifiedDiff.Parse(request.Diff);
            if (string.IsNullOrEmpty(_rootPath))
                throw new NotSupportedException("Diff impact needs the indexed root, to place the diff's paths.");

            // Line numbers mean something only against the content the index read. Until a read of every file has
            // finished here, the store holds a saved index (being re-checked, or that failed its check) or a partial
            // scan, and nothing says which files it holds as they are now.
            DateTime? indexedAsOf = null;
            if (_freshness is not null)
            {
                indexedAsOf = _freshness.FilesReadAsOfUtc ?? throw new IndexNotReadyException(
                    "No scan of the files has finished in this instance yet (it is indexing, or answering from a saved index while it "
                    + "re-checks the files, or that check failed): changed lines cannot be matched to declarations until one has. "
                    + "Retry when codecontext status shows indexing ready.");
            }
            var tree = new DiffWorkingTree(_rootPath, indexedAsOf);
            return ReadOneGenerationAsync(shards => Task.FromResult(GetImpactOnce(
                request, files, tree, new GraphWalk(shards, relations, request.IncludeTests, _rootPath, cancellationToken), shards)),
                cancellationToken);
        }

        /// <summary>A changed declaration while the answer is built.</summary>
        private sealed class ChangeEntry
        {
            public required CodeNode Node;
            public required List<CodeNode> Copies;
            public required bool Container;
            public List<DiffLineRange> Lines = new();
            public int RemovedLines;
            public int Dependents;
            public int TestsHidden;
        }

        private GraphImpactResponse GetImpactOnce(
            GraphImpactRequest request, List<DiffFile> files, DiffWorkingTree tree, GraphWalk walk, IGraphShard[] shards)
        {
            var response = new GraphImpactResponse { Cache = _cacheState?.GetNotice(), MaxDepth = request.MaxDepth, DiffFiles = files.Count };
            var unmapped = new List<GraphUnmappedChange>();
            var changes = new List<ChangeEntry>();
            var changeOf = new Dictionary<string, ChangeEntry>(StringComparer.Ordinal);
            var mismatched = 0;

            foreach (var file in files)
            {
                var status = file.Status.ToString().ToLowerInvariant();
                response.IgnoredLines += file.IgnoredLines;
                if (file.ModeOnly)
                {
                    response.ModeOnlyFiles++;
                    continue;
                }

                var display = file.Path!;
                void Unmapped(string reason, string detail, string? lines = null, string? oldLines = null, int? count = null)
                    => unmapped.Add(new GraphUnmappedChange
                    {
                        File = display, OldFile = file.Status is DiffFileStatus.Renamed or DiffFileStatus.Copied ? file.OldPath : null,
                        Status = status, Reason = reason, Lines = lines, OldLines = oldLines,
                        LineCount = count ?? file.AddedLineCount + file.RemovedLineCount, Detail = detail,
                    });

                if (file.PathProblem is { } problem)
                {
                    Unmapped("invalidPath", $"The diff names {problem}; it is not looked at.");
                    continue;
                }
                if (file.Combined)
                {
                    Unmapped("combinedDiff", "A combined diff of a merge (diff --cc) is against several parents; pass git diff <one parent> <merge>.");
                    continue;
                }
                if (tree.RootRelative(display) is not { } relative)
                {
                    Unmapped("outsideRoot", $"Outside the indexed root {Path.GetFileName(_rootPath)} ({tree.RootPrefix.TrimEnd('/')} under the repository).");
                    continue;
                }
                display = relative;
                if (file.Binary)
                {
                    Unmapped("binary", "A binary change; nothing in it maps to a declaration.");
                    continue;
                }

                if (file.Status == DiffFileStatus.Deleted)
                {
                    var deletedPath = tree.FullPath(relative);
                    var (gone, _) = tree.Inspect(deletedPath, null);
                    if (gone != DiffWorkingTree.Verdict.Missing && gone != DiffWorkingTree.Verdict.Refused)
                    {
                        mismatched++;
                        Unmapped("contentMismatch", "The diff deletes it, but it is in the working tree: the diff's new side is not what is checked out.");
                    }
                    else if (NodesIn(shards, deletedPath).Count > 0)
                    {
                        Unmapped("notYetIndexed", "Deleted from the working tree but still in the index; retry once the instance has re-indexed.");
                    }
                    else
                    {
                        Unmapped("deletedFile",
                            "Deleted: its declarations exist only on the old side, so what used them is not in the index (callers that "
                            + "still name them no longer resolve). Search their names with rg.",
                            oldLines: file.RemovedLineCount > 0 ? $"1-{file.RemovedLineCount}" : null, count: file.RemovedLineCount);
                    }
                    continue;
                }

                var fullPath = tree.FullPath(relative);
                var (verdict, why) = tree.Inspect(fullPath, file.NewBlob);
                switch (verdict)
                {
                    case DiffWorkingTree.Verdict.Missing:
                        mismatched++;
                        Unmapped("contentMismatch", "Not in the working tree: the diff's new side is not what is checked out.");
                        continue;
                    case DiffWorkingTree.Verdict.ChangedSinceIndexed:
                        Unmapped("notYetIndexed", "Changed on disk after the index last read the files; retry once the instance has re-indexed it.");
                        continue;
                    case DiffWorkingTree.Verdict.Refused:
                        Unmapped("unreadable", $"Not compared with the working tree: {why}.");
                        continue;
                }

                var nodes = NodesIn(shards, fullPath);
                if (nodes.Count == 0)
                {
                    Unmapped("notIndexed",
                        "No declaration of the index is in this file: the workers do not parse it, the selection policy leaves it out, "
                        + "or it declares nothing. A project, build or configuration file can still change how everything binds.");
                    continue;
                }
                if (verdict == DiffWorkingTree.Verdict.Mismatch)
                {
                    mismatched++;
                    Unmapped("contentMismatch",
                        "The file in the working tree is not the diff's new side (its blob id differs): the diff is between commits that "
                        + "are not checked out, or the file changed since the diff was made. Its line numbers would point at the wrong "
                        + "declarations, so none is mapped.");
                    continue;
                }
                if (verdict == DiffWorkingTree.Verdict.Match) response.ContentChecked++;
                else response.ContentUnchecked++;

                if (file.Status == DiffFileStatus.Renamed)
                {
                    Unmapped("renamed",
                        $"Moved from {file.OldPath}. What referred to the old path (an import by path) is not in the index; "
                        + $"query affected {relative} lists what uses its declarations now.",
                        count: 0);
                }

                // A copy is a new file: every declaration in it is new, whatever the hunks show.
                IReadOnlyList<DiffLineRange> added = file.Status == DiffFileStatus.Copied
                    ? [new DiffLineRange(1, Math.Max(1, nodes.Max(node => Math.Max(node.EndLine, node.StartLine)) + 1))]
                    : file.Added;
                var mapping = DiffMapper.Map(nodes, node => IsContainer(node, walk), added, file.Removals, file.CommentAdded, file.CommentBoundaries);
                response.IgnoredLines += mapping.IgnoredComments;

                foreach (var touch in mapping.Touched)
                {
                    var ordered = touch.Nodes.OrderBy(PublicIdentifier, StringComparer.Ordinal).ToList();
                    var entry = new ChangeEntry
                    {
                        Node = ordered[0], Copies = ordered, Container = ordered.Exists(node => IsContainer(node, walk)),
                        RemovedLines = touch.RemovedLines,
                    };
                    entry.Lines.AddRange(touch.Lines);
                    changes.Add(entry);
                    foreach (var node in ordered) changeOf[node.Id!] = entry;
                }
                if (mapping.Outside.Count > 0)
                {
                    Unmapped("outsideDeclarations",
                        "Changed lines that no declaration holds (imports or usings, a namespace line, a file header): a change here "
                        + $"can alter how every symbol of the file binds; query affected {relative} walks them all.",
                        lines: FormatRanges(mapping.Outside), count: mapping.Outside.Sum(range => range.Count));
                }
                if (mapping.CommentBoundaries.Count > 0)
                {
                    Unmapped("blockCommentBoundary",
                        "A block comment opens or closes here and its other end is not among the lines changed with it, so unchanged "
                        + "lines beside it were commented out or back in: a declaration among them may now exist on one side only, "
                        + "and what used it is not in the index. Read the file around these lines.",
                        lines: FormatRanges(mapping.CommentBoundaries), count: mapping.CommentBoundaries.Sum(range => range.Count));
                }
                if (mapping.RemovedBetween.Count > 0)
                {
                    var holders = mapping.RemovedBetween.Where(removed => removed.Container is not null)
                        .Select(removed => PublicIdentifier(removed.Container![0])).Distinct(StringComparer.Ordinal).ToList();
                    Unmapped("removedBetweenDeclarations",
                        "Removed lines between or at the edge of declarations"
                        + (holders.Count > 0 ? $" (in {string.Join(", ", holders)})" : string.Empty)
                        + ": if they held a declaration (a deletion, a rename, a changed signature), it exists only on the old side and "
                        + "what used it is not in the index. Search its old name with rg.",
                        oldLines: FormatRanges(mapping.RemovedBetween.Select(removed => removed.Old)),
                        count: mapping.RemovedBetween.Sum(removed => removed.Old.Count));
                }
            }

            // One walk from every changed declaration, as affected walks from one.
            var seeds = changeOf.Keys.Order(StringComparer.Ordinal).ToList();
            var affected = new List<GraphWalk.Affected>();
            if (seeds.Count > 0)
            {
                affected = walk.AffectedBy(seeds, request.MaxDepth, out var depthLimited);
                response.DepthLimited = depthLimited;
            }

            // Each dependent is attributed to the change whose impact reached it first: results are nearest first, and
            // what a dependent is reached through is a changed declaration or a dependent listed before it.
            var rootOf = new Dictionary<string, ChangeEntry>(changeOf, StringComparer.Ordinal);
            var testFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                foreach (var copy in change.Copies)
                {
                    if (walk.IsTestNode(copy)) testFiles.Add(ToDisplayPath(copy.FilePath!));
                }
            }
            var listed = new List<(GraphWalk.Affected Item, ChangeEntry Change)>();
            foreach (var item in affected)
            {
                var change = rootOf[item.Via];
                rootOf[item.Node.Id!] = change;
                if (walk.IsTestNode(item.Node)) testFiles.Add(ToDisplayPath(item.Node.FilePath!));
                if (item.Hidden)
                {
                    response.TestsHidden++;
                    change.TestsHidden++;
                    continue;
                }
                change.Dependents++;
                listed.Add((item, change));
            }

            // Changes, in file and line order.
            changes.Sort(static (left, right) => CompareByPlace(left.Node, right.Node));
            response.ChangeCount = changes.Count;
            foreach (var change in changes.Take(request.Limit))
            {
                var compact = ToCompact<GraphChangedNode>(change.Node);
                compact.Level = change.Container ? "container" : "member";
                compact.ChangedLines = change.Lines.Count == 0 ? null : FormatRanges(change.Lines);
                compact.RemovedLines = change.RemovedLines;
                compact.Dependents = change.Dependents;
                compact.TestsHidden = change.TestsHidden;
                compact.SameDeclaration = change.Copies.Count > 1 ? change.Copies.Skip(1).Select(PublicIdentifier).ToList() : null;
                response.Changes.Add(compact);
            }
            response.ChangesReturned = response.Changes.Count;
            response.ChangesTruncated = response.ChangeCount > response.ChangesReturned;

            unmapped.Sort(static (left, right) =>
            {
                var order = GraphAdjacency.ComparePaths(left.File, right.File);
                return order != 0 ? order : string.CompareOrdinal(left.Reason, right.Reason);
            });
            response.UnmappedCount = unmapped.Count;
            response.UnmappedLines = unmapped.Sum(entry => entry.LineCount);
            if (unmapped.Count > 0)
            {
                response.UnmappedReasons = unmapped.GroupBy(entry => entry.Reason, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            }
            response.Unmapped = unmapped.Take(request.Limit).ToList();
            response.UnmappedReturned = response.Unmapped.Count;
            response.UnmappedTruncated = response.UnmappedCount > response.UnmappedReturned;

            // Dependents, as affected lists them, each with the change it is attributed to.
            var dependentFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (item, _) in listed)
            {
                if (!string.IsNullOrEmpty(item.Node.FilePath)) dependentFiles.Add(item.Node.FilePath);
            }
            response.TotalCount = listed.Count;
            response.FileCount = dependentFiles.Count;
            foreach (var (item, change) in Leading(listed, request.Limit, static (left, right) =>
            {
                var order = left.Item.Distance.CompareTo(right.Item.Distance);
                return order != 0 ? order : CompareByPlace(left.Item.Node, right.Item.Node);
            }))
            {
                var compact = ToCompact<GraphImpactNode>(item.Node);
                compact.Distance = item.Distance;
                compact.Relation = item.Relation;
                compact.Via = PublicIdentifier(walk.Node(item.Via)!);
                compact.Through = item.Through is null ? null : PublicIdentifier(walk.Node(item.Through)!);
                compact.Seed = PublicIdentifier(change.Node);
                response.Items.Add(compact);
            }
            response.ReturnedCount = response.Items.Count;
            response.Truncated = response.TotalCount > response.ReturnedCount;

            var orderedTests = testFiles.Order(Comparer<string>.Create(GraphAdjacency.ComparePaths)).ToList();
            response.TestFileCount = orderedTests.Count;
            response.TestFiles = orderedTests.Take(request.Limit).ToList();
            response.TestFilesTruncated = response.TestFileCount > response.TestFiles.Count;

            response.Complete = response.UnmappedCount == 0 && response.ContentUnchecked == 0;
            response.Hint = ImpactHint(request, response, walk, mismatched);
            if (response.TotalCount == 0) response.Coverage = CoverageForEmptyAnswer();
            response.Cache = CacheAfterReads(response.Cache);
            return response;
        }

        private static string? ImpactHint(GraphImpactRequest request, GraphImpactResponse response, GraphWalk walk, int mismatched)
        {
            var hints = new List<string>();
            if (mismatched > 0 && response.ContentChecked + response.ContentUnchecked == 0)
            {
                hints.Add("No file of the diff matches the working tree: its new side is not what is checked out (a diff between two "
                    + "commits, or files changed since). Check out its new side, or diff against the working tree (git diff <base>).");
            }
            else if (response.ChangeCount == 0 && response.UnmappedCount > 0)
            {
                hints.Add("No changed line maps to a declaration of the index; unmapped says why for each file.");
            }
            if (response.ContentUnchecked > 0)
            {
                hints.Add($"{response.ContentUnchecked} file(s) were mapped without checking that the working tree is the diff's new side: "
                    + "the diff gives no blob id for them (git diff writes an index line; diff -u does not).");
            }
            if (response.ChangeCount > 0 && response.TotalCount == 0)
            {
                hints.Add(response.TestsHidden > 0
                    ? $"Only symbols in test files depend on the changes ({response.TestsHidden}); includeTests (--tests) lists them."
                    : $"Nothing recorded depends on the changed declarations{(walk.FiltersRelations ? $" through relation={request.Relation!.Trim()}" : string.Empty)}. "
                        + "Relationships are static evidence: cross-check with rg before concluding nothing else is affected.");
            }
            return hints.Count == 0 ? null : string.Join(" ", hints);
        }

        /// <summary>Every node of the index in the file at <paramref name="fullPath"/>, library stubs aside.</summary>
        private static List<CodeNode> NodesIn(IGraphShard[] shards, string fullPath)
        {
            var nodes = new List<CodeNode>();
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (var shard in shards)
            {
                foreach (var node in shard.FindNodesByPath(fullPath))
                {
                    if (!string.IsNullOrEmpty(node.Id) && !IsExternal(node) && string.Equals(node.FilePath, fullPath, comparison))
                        nodes.Add(node);
                }
            }
            return nodes;
        }

        /// <summary>Whether a node holds declarations: a type or module kind, or anything with a containment edge out of it.</summary>
        private static bool IsContainer(CodeNode node, GraphWalk walk)
            => (node.Type is { } type && ContainerTypes.Contains(type)) || walk.HoldsMembers(node.Id!);

        /// <summary>Ranges as <c>12-14,20</c>.</summary>
        internal static string FormatRanges(IEnumerable<DiffLineRange> ranges)
        {
            var merged = new List<DiffLineRange>();
            foreach (var range in ranges) DiffMapper.Merge(merged, range);
            var text = new StringBuilder();
            foreach (var range in merged)
            {
                if (text.Length > 0) text.Append(',');
                text.Append(range.Start);
                if (range.End != range.Start) text.Append('-').Append(range.End);
            }
            return text.ToString();
        }
    }
}
