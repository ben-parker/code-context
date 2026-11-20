using System.Text;
using CodeContext.Core.Diffs;
using CodeContext.Core.Models;
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
                indexedAsOf = _freshness.FilesReadAsOfUtc ?? throw new IndexNotReadyException(_freshness.FilesReadNote is { } why
                    ? $"No read of every file is known to have succeeded in this instance: {why}. Changed lines cannot be matched to "
                        + "declarations until one has."
                    : "No scan of the files has finished in this instance yet (it is indexing, or answering from a saved index while it "
                        + "re-checks the files): changed lines cannot be matched to declarations until one has. "
                        + "Retry when codecontext status shows indexing ready.");
            }
            // Read before the generation is pinned: the coordinator moves it only once the commit of the read it stands
            // for is published, so every generation pinned after it holds at least that read. A retry pins a newer
            // generation and keeps this time, which can only call more files notYetIndexed, never fewer.
            var tree = new DiffWorkingTree(_rootPath, indexedAsOf);
            return ReadOneGenerationAsync(shards => GetImpactOnceAsync(
                request, files, tree, new GraphWalk(shards, relations, request.IncludeTests, _rootPath, cancellationToken), shards, cancellationToken),
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

            /// <summary>Its place among the changes in file and line order.</summary>
            public int Order;

            /// <summary>The other changes that use it directly.</summary>
            public HashSet<ChangeEntry>? UsedBy;
        }

        private async Task<GraphImpactResponse> GetImpactOnceAsync(
            GraphImpactRequest request, List<DiffFile> files, DiffWorkingTree tree, GraphWalk walk, IGraphShard[] shards,
            CancellationToken cancellationToken)
        {
            var response = new GraphImpactResponse { Cache = _cacheState?.GetNotice(), MaxDepth = request.MaxDepth, DiffFiles = files.Count };
            var unmapped = new List<GraphUnmappedChange>();
            var changes = new List<ChangeEntry>();
            var changeOf = new Dictionary<string, ChangeEntry>(StringComparer.Ordinal);
            var mismatched = 0;

            // Files that cannot change an indexed fact are set aside; the rest are mapped. What the workers reported
            // reading is consulted once, after the files whose names say nothing have been collected.
            var noWorkerReads = _workerService is not null && _fileSelector is not null && _database is not null
                ? new ChangedFilesNoWorkerReads(_workerService, _fileSelector, tree.Refused)
                : null;
            // The declarations a file holds: its own nodes, and the parts there of declarations made in several places.
            Dictionary<string, List<CodeNode>>? partsByFile = null;
            List<CodeNode> DeclarationsIn(string fullPath)
            {
                var nodes = NodesIn(shards, fullPath);
                partsByFile ??= PartsByFile(shards);
                if (partsByFile.TryGetValue(fullPath, out var parts)) nodes.AddRange(parts);
                return nodes;
            }
            foreach (var file in files)
            {
                if (noWorkerReads is not null && !file.ModeOnly && file.PathProblem is null && !file.Combined && Placed(file) is { } placed)
                {
                    switch (noWorkerReads.Ask(file, placed.Display, placed.Paths, path => DeclarationsIn(path).Count > 0, out var why))
                    {
                        case ChangedFilesNoWorkerReads.ByName.Deferred:
                            continue;
                        case ChangedFilesNoWorkerReads.ByName.Kept:
                            await MapAsync(file, why, keptAsItIs: true);
                            continue;
                        default:
                            await MapAsync(file, why, keptAsItIs: false);
                            continue;
                    }
                }
                await MapAsync(file, null, keptAsItIs: false);
            }
            if (noWorkerReads is { Candidates.Count: > 0 })
            {
                // The reports of the generation the declarations and the walk come from: a commit since the pin replaced
                // them, and then nothing says what was read (the answer is read again unless an outer reader holds the pin).
                if (_database!.LatchPinned() is { } pinned) noWorkerReads.Resolve(pinned.Shards, pinned.Annotations);
                else noWorkerReads.KeepAll("the index changed while this answer was read, so what its workers reported reading is not known");
                var setAside = new List<string>();
                foreach (var candidate in noWorkerReads.Candidates)
                {
                    if (candidate.Kept is null) setAside.Add(candidate.Display);
                    else await MapAsync(candidate.File, candidate.Kept, keptAsItIs: true);
                }
                setAside.Sort(GraphAdjacency.ComparePaths);
                response.SetAsideFileCount = setAside.Count;
                if (setAside.Count > 0) response.SetAsideFiles = setAside.Take(request.Limit).ToList();
                response.SetAsideFilesTruncated = setAside.Count > request.Limit;
            }

            // The file's paths under the root (new, then old when the diff names another), or null when one is outside it.
            (string Display, string[] Paths)? Placed(DiffFile file)
            {
                if (tree.RootRelative(file.Path!) is not { } relative) return null;
                if (file.OldPath is null || file.NewPath is null || file.OldPath == file.NewPath) return (relative, [tree.FullPath(relative)]);
                return tree.RootRelative(file.OldPath) is { } old ? (relative, [tree.FullPath(relative), tree.FullPath(old)]) : null;
            }

            static string NotIndexed(string? kept)
                => kept is null
                    ? "No declaration of the index is in this file: the workers do not parse it, the selection policy leaves it out, "
                        + "or it declares nothing. A project, build or configuration file can still change how everything binds."
                    : $"No declaration of the index is in this file, and it may change what is indexed all the same: {kept}.";

            // Maps one file. kept says why a file with no declaration may feed the index; keptAsItIs that no worker is
            // told when it changes, so the working tree says nothing of whether the index has it.
            async Task MapAsync(DiffFile file, string? kept, bool keptAsItIs)
            {
                var status = file.Status.ToString().ToLowerInvariant();
                response.IgnoredLines += file.IgnoredLines;
                if (file.ModeOnly)
                {
                    response.ModeOnlyFiles++;
                    return;
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
                    return;
                }
                if (file.Combined)
                {
                    Unmapped("combinedDiff", "A combined diff of a merge (diff --cc) is against several parents; pass git diff <one parent> <merge>.");
                    return;
                }
                if (tree.RootRelative(display) is not { } relative)
                {
                    Unmapped("outsideRoot", $"Outside the indexed root {Path.GetFileName(_rootPath)} ({tree.RootPrefix.TrimEnd('/')} under the repository).");
                    return;
                }
                display = relative;
                if (keptAsItIs)
                {
                    Unmapped("notIndexed", NotIndexed(kept));
                    return;
                }
                if (file.Binary)
                {
                    Unmapped("binary", "A binary change; nothing in it maps to a declaration.");
                    return;
                }
                if (file.NoChangedLines)
                {
                    Unmapped("noChangedLines",
                        "The diff names this file and shows no changed line of it: the diff was cut short after the file's header, or "
                        + "made with an option that hides changes (-w, --ignore-blank-lines). Nothing of it is mapped; make the diff again.",
                        count: 0);
                    return;
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
                    else if (DeclarationsIn(deletedPath).Count > 0)
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
                    return;
                }

                var fullPath = tree.FullPath(relative);
                var (verdict, why) = tree.Inspect(fullPath, file.NewBlob);
                switch (verdict)
                {
                    case DiffWorkingTree.Verdict.Missing:
                        mismatched++;
                        Unmapped("contentMismatch", "Not in the working tree: the diff's new side is not what is checked out.");
                        return;
                    case DiffWorkingTree.Verdict.ChangedSinceIndexed:
                        Unmapped("notYetIndexed", _freshness?.FilesReadNote is { } stopped
                            ? $"Changed on disk after the index last read every file, and it will not be known to be indexed by itself: {stopped}."
                            : "Changed on disk after the index last read the files; retry once the instance has re-indexed it.");
                        return;
                    case DiffWorkingTree.Verdict.Refused:
                        Unmapped("unreadable", $"Not compared with the working tree: {why}.");
                        return;
                }

                var nodes = DeclarationsIn(fullPath);
                if (nodes.Count == 0)
                {
                    Unmapped("notIndexed", NotIndexed(kept));
                    return;
                }
                if (verdict == DiffWorkingTree.Verdict.Mismatch)
                {
                    mismatched++;
                    Unmapped("contentMismatch",
                        "The file in the working tree is not the diff's new side (its blob id differs): the diff is between commits that "
                        + "are not checked out, the file changed since the diff was made, or a .gitattributes filter (LFS, ident, "
                        + "working-tree-encoding) rewrites it on checkout. Its line numbers would point at the wrong declarations, so "
                        + "none is mapped.");
                    return;
                }
                // The file's content and time say what is on disk; whether the worker's last read of it succeeded is the
                // file table's to say. A read that failed, or has not finished, left the declarations as they were.
                if (await _fileMetadataRepository.GetByFilePathAsync(fullPath, cancellationToken) is { Status: not FileProcessingStatus.Completed } unread)
                {
                    Unmapped("notYetIndexed", unread.Status == FileProcessingStatus.Failed
                        ? $"The index failed to read it as it is now ({unread.ErrorMessage ?? "no reason recorded"}) and holds its "
                            + "declarations as they were before; codecontext status shows the worker's state. Retry once it has been re-indexed."
                        : "The index has not finished reading it as it is now; retry once the instance has re-indexed it.");
                    return;
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
                var newBlob = file.NewBlob;
                var mapping = DiffMapper.Map(nodes, node => IsContainer(node, walk), added, file.Removals, file.CommentAdded, file.CommentBoundaries,
                    file.ConditionalDirectives, () => tree.ReadComments(fullPath, newBlob), file.BlankAdded);
                response.IgnoredLines += mapping.IgnoredComments;

                foreach (var touch in mapping.Touched)
                {
                    var ordered = touch.Nodes.Count == 1 ? [touch.Nodes[0]] : touch.Nodes.OrderBy(PublicIdentifier, StringComparer.Ordinal).ToList();
                    var entry = new ChangeEntry
                    {
                        Node = ordered[0], Copies = ordered, Container = ordered.Exists(node => IsContainer(node, walk)),
                        RemovedLines = touch.RemovedLines,
                    };
                    entry.Lines.AddRange(touch.Lines);
                    changes.Add(entry);
                }
                // An added file's own using and namespace lines bind only its declarations, every one of them a change here.
                var outside = mapping.Outside;
                if (file.Status == DiffFileStatus.Added && file.FileScopedDirectives.Count > 0 && mapping.Touched.Count > 0)
                {
                    outside = Subtract(mapping.Outside, file.FileScopedDirectives);
                    response.IgnoredLines += mapping.Outside.Sum(range => range.Count) - outside.Sum(range => range.Count);
                }
                if (outside.Count > 0)
                {
                    Unmapped("outsideDeclarations",
                        "Changed lines that no declaration holds (imports or usings, a namespace line, a file header): a change here "
                        + $"can alter how every symbol of the file binds; query affected {relative} walks them all.",
                        lines: FormatRanges(outside), count: outside.Sum(range => range.Count));
                }
                if (mapping.CommentBoundaries.Count > 0)
                {
                    Unmapped("blockCommentBoundary",
                        "A block comment opens or closes here and its other end is not among the lines changed with it, so unchanged "
                        + "lines beside it were commented out or back in: a declaration among them may now exist on one side only, "
                        + "and what used it is not in the index. Read the file around these lines.",
                        lines: FormatRanges(mapping.CommentBoundaries), count: mapping.CommentBoundaries.Sum(range => range.Count));
                }
                if (mapping.ConditionalDirectives.Count > 0)
                {
                    Unmapped("conditionalDirective",
                        "A conditional-compilation directive (#if, #elif, #else, #endif, #define, #undef) changed here, outside "
                        + "any member: unchanged lines under it may now be compiled in or out, so a declaration among them may "
                        + "exist on one side only and what used it is not in the index. Read the file around these lines.",
                        lines: FormatRanges(mapping.ConditionalDirectives), count: mapping.ConditionalDirectives.Sum(range => range.Count));
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

            // One walk from every changed declaration, as affected walks from one, the changes in file and line order: a
            // dependent two changes reach at one distance is the first one's.
            // A declaration in parts is a change per part the diff changes, and is walked once, as its first part listed.
            changes.Sort(static (left, right) => CompareByPlace(left.Node, right.Node));
            var seeds = new List<string>(changes.Count);
            for (var index = 0; index < changes.Count; index++)
            {
                changes[index].Order = index;
                foreach (var copy in changes[index].Copies)
                {
                    if (changeOf.TryAdd(copy.Id!, changes[index])) seeds.Add(copy.Id!);
                }
            }
            var affected = new List<GraphWalk.Affected>();
            if (seeds.Count > 0)
            {
                // A change is never a dependent, so a change that uses another is recorded with the one it uses.
                affected = walk.AffectedBy(seeds, request.MaxDepth, out var depthLimited, (user, used) =>
                {
                    if (changeOf[user] is var by && changeOf[used] is var of && by != of) (of.UsedBy ??= []).Add(by);
                });
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
                if (change.UsedBy is { } users)
                {
                    compact.UsedByChangeCount = users.Count;
                    compact.UsedByChanges = users.OrderBy(user => user.Order).Take(GraphQueryLimits.MaxUsedByChanges)
                        .Select(user => PublicIdentifier(user.Node)).ToList();
                }
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
            foreach (var (item, change) in Leading(listed, request.Limit, static (left, right) => NearestFirst(left.Item, right.Item)))
            {
                var compact = ToDependent<GraphImpactNode>(item, walk);
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
            response.Coverage = CoverageOf(changes.Select(change => change.Node.Id));
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

        /// <summary>
        /// The other parts of every declaration the workers report made in several places (<see cref="CodeNode.Parts"/>),
        /// by file: each a copy of its node at the part's span, which the mapping treats as a declaration of that file. A
        /// part that repeats the node's own place, or another part, is left out.
        /// </summary>
        private static Dictionary<string, List<CodeNode>> PartsByFile(IGraphShard[] shards)
        {
            var comparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var byFile = new Dictionary<string, List<CodeNode>>(comparer);
            foreach (var shard in shards)
            {
                foreach (var node in shard.GetAllNodes())
                {
                    if (node.Parts is not { Count: > 0 } parts || string.IsNullOrEmpty(node.Id) || IsExternal(node)) continue;
                    var seen = new HashSet<CodeNodePart>();
                    foreach (var part in parts)
                    {
                        if (string.IsNullOrEmpty(part.FilePath)
                            || (comparer.Equals(part.FilePath, node.FilePath ?? string.Empty) && (part.StartLine, part.EndLine, part.StartCol, part.EndCol)
                                == (node.StartLine, node.EndLine, node.StartCol, node.EndCol))
                            || !seen.Add(part))
                            continue;
                        if (!byFile.TryGetValue(part.FilePath, out var list)) byFile[part.FilePath] = list = [];
                        list.Add(new CodeNode
                        {
                            Id = node.Id, Identifier = node.Identifier, Name = node.Name, Type = node.Type, Language = node.Language,
                            FilePath = part.FilePath, StartLine = part.StartLine, EndLine = part.EndLine, StartCol = part.StartCol, EndCol = part.EndCol,
                            Namespace = node.Namespace, Visibility = node.Visibility, Signature = node.Signature, ReturnType = node.ReturnType,
                            Parameters = node.Parameters, Modifiers = node.Modifiers, Metrics = node.Metrics, Metadata = node.Metadata,
                        });
                    }
                }
            }
            return byFile;
        }

        /// <summary>Whether a node holds declarations: a type or module kind, or anything with a containment edge out of it.</summary>
        private static bool IsContainer(CodeNode node, GraphWalk walk)
            => (node.Type is { } type && ContainerTypes.Contains(type)) || walk.HoldsMembers(node.Id!);

        /// <summary>The lines of <paramref name="ranges"/> that none of <paramref name="removed"/> holds; both ordered and disjoint.</summary>
        private static List<DiffLineRange> Subtract(List<DiffLineRange> ranges, List<DiffLineRange> removed)
        {
            var left = new List<DiffLineRange>();
            var cut = 0;
            foreach (var range in ranges)
            {
                var next = range.Start;
                while (cut < removed.Count && removed[cut].End < next) cut++;
                // A cut that reaches past this range is looked at again for the next one.
                for (var at = cut; at < removed.Count && removed[at].Start <= range.End; at++)
                {
                    if (removed[at].Start > next) left.Add(new DiffLineRange(next, removed[at].Start - 1));
                    next = Math.Max(next, removed[at].End + 1);
                }
                if (next <= range.End) left.Add(new DiffLineRange(next, range.End));
            }
            return left;
        }

        /// <summary>Ranges as <c>12-14,20</c>.</summary>
        internal static string FormatRanges(IEnumerable<DiffLineRange> ranges)
        {
            var merged = ranges.ToList();
            DiffMapper.Normalize(merged);
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
