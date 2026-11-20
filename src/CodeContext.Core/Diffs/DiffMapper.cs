using System.Runtime.InteropServices;

namespace CodeContext.Core.Diffs
{
    /// <summary>
    /// Maps the changed lines of one file to the declarations of the index whose spans hold them. A line belongs to the
    /// innermost declarations containing it: those with no nested declaration of their own on that line. A
    /// declaration's first and last lines (its header, its closing line) are its own as well, so a change there reaches
    /// the declaration and not only what it contains; unless the declaration has no text of its own there (a module
    /// starts with its first declaration and ends with the file). Declarations with the same span count as one.
    ///
    /// A removed run belongs to the innermost declaration around it (one holding the new lines on both sides of the
    /// block) when that declaration is a member, which then changed. When it lies between or at the edge of members, in
    /// a type, a module or no declaration at all, the removed lines may have held a declaration of their own (a
    /// deletion, a rename, a changed signature), which exists only on the old side: that is reported, never mapped.
    /// Lines no declaration holds at all (imports, a namespace line, a file header) are reported too.
    ///
    /// Comment lines (<see cref="SourceLines.IsCommentLine"/>) and blank lines change a member that holds them, as any line
    /// does: inside a multi-line string either is part of a value, and nothing lexical here can tell soundly for every
    /// language a worker reads. Where
    /// no member holds one (a doc comment above a member, which a declaration's span leaves out; a file header; a line
    /// of a string that only looks like a comment) the file itself is read (<see cref="CommentRuns"/>): a line of the
    /// comment just above a declaration, or of the file's leading comment, changes nothing and is only counted; any
    /// other is a line of whatever holds it, as a line of code there would be. A removed run of comments only is judged
    /// the same way, by where it stood (and, when it starts like the inside of a block comment, by one being open
    /// there). A comment line that opens or closes a block comment around unchanged lines
    /// (<see cref="DiffFile.CommentBoundaries"/>) is the exception: where no member holds it, what it commented out or
    /// back in is reported, never counted. So is a conditional-compilation directive
    /// (<see cref="DiffFile.ConditionalDirectives"/>) that a type or module holds and no member does: it is the
    /// container's line, and the unchanged lines under it may now be compiled in or out.
    /// </summary>
    public static class DiffMapper
    {
        /// <summary>A declaration (one or more nodes with one span) that the change reaches.</summary>
        public sealed class Touch
        {
            public required IReadOnlyList<CodeNode> Nodes { get; init; }

            /// <summary>Changed new-side lines that are its own, 1-based, ordered and disjoint.</summary>
            public List<DiffLineRange> Lines { get; } = new();

            /// <summary>Removed lines inside it.</summary>
            public int RemovedLines { get; set; }
        }

        /// <summary>Removed lines between or at the edge of declarations; <see cref="Container"/> holds them, if anything does.</summary>
        public readonly record struct RemovedBetween(DiffLineRange Old, IReadOnlyList<CodeNode>? Container);

        public sealed class Result
        {
            public List<Touch> Touched { get; } = new();

            /// <summary>Changed lines no declaration holds, ordered and disjoint.</summary>
            public List<DiffLineRange> Outside { get; } = new();
            public List<RemovedBetween> RemovedBetween { get; } = new();

            /// <summary>Comment and blank lines above a declaration or at the head of the file, added or removed: counted, not mapped.</summary>
            public int IgnoredComments { get; set; }

            /// <summary>Block-comment boundaries no member holds: the unchanged lines next to them may hold a declaration.</summary>
            public List<DiffLineRange> CommentBoundaries { get; } = new();

            /// <summary>Conditional-compilation directives a container holds and no member does.</summary>
            public List<DiffLineRange> ConditionalDirectives { get; } = new();
        }

        private sealed class Group
        {
            public required CodeNode Node;

            /// <summary>Other nodes with the same span.</summary>
            public List<CodeNode>? More;
            public int Start;
            public int End;
            public (int Line, int Col) From;
            public (int Line, int Col) To;
            public Group? Parent;
            public List<Group>? Children;
            public Touch? Touch;
            public int Depth;

            /// <summary>The last line this group or a sibling before it holds: siblings are ordered by start, so this never falls.</summary>
            public int Reach;

            /// <summary>Whether it holds declarations; asked once.</summary>
            public bool? Container;

            /// <summary>
            /// Whether its first line holds text of its own before anything nested (a header), and its last line text of
            /// its own after anything nested (a closing line). A span that starts exactly where a nested declaration
            /// starts, ends exactly where one ends, or ends at the very start of the next line (a module, which is the
            /// whole file) has neither.
            /// </summary>
            public bool OwnsFirstLine = true;
            public bool OwnsLastLine = true;
        }

        /// <param name="nodes">Every node of the index in the file.</param>
        /// <param name="isContainer">Whether a node holds declarations (a type, a module): a removal between its members is not mapped.</param>
        /// <param name="added">Changed new-side lines; <paramref name="comments"/> changed comment lines; <paramref name="removals"/> the removed runs.</param>
        /// <param name="boundaries">The comment lines among <paramref name="comments"/> that open or close a block comment around unchanged lines.</param>
        /// <param name="conditionals">The lines among <paramref name="added"/> that are conditional-compilation directives.</param>
        /// <param name="readComments">Reads the file's new side, asked once and only when a comment or blank line lies where no member holds it; null when it cannot be read.</param>
        /// <param name="blanks">Added lines that hold only whitespace, judged as comment lines are.</param>
        public static Result Map(
            IReadOnlyList<CodeNode> nodes, Func<CodeNode, bool> isContainer,
            IReadOnlyList<DiffLineRange> added, IReadOnlyList<DiffRemoval> removals, IReadOnlyList<DiffLineRange>? comments = null,
            IReadOnlyList<DiffLineRange>? boundaries = null, IReadOnlyList<DiffLineRange>? conditionals = null,
            Func<CommentRuns?>? readComments = null, IReadOnlyList<DiffLineRange>? blanks = null)
        {
            var result = new Result();
            var groups = Groups(nodes);
            var roots = groups.Where(group => group.Parent is null).ToList();
            var holding = new List<Group>();
            var gaps = new List<DiffLineRange>();

            foreach (var range in added)
            {
                Holding(roots, range.Start, range.End, holding);
                foreach (var group in holding)
                {
                    gaps.Clear();
                    Owned(group, Math.Max(range.Start, group.Start), Math.Min(range.End, group.End), gaps);
                    if (gaps.Count > 0) TouchOf(group, result).Lines.AddRange(gaps);
                }
                Uncovered(roots, range.Start, range.End, result.Outside);
            }

            foreach (var range in conditionals ?? [])
            {
                for (var line = range.Start; line <= range.End; line++)
                {
                    Holding(roots, line, line, holding);
                    if (holding.Count > 0 && !holding.Exists(group => !IsContainer(group, isContainer) && Owns(group, line)))
                        result.ConditionalDirectives.Add(new DiffLineRange(line, line));
                }
            }

            CommentRuns? text = null;
            var textRead = false;
            HashSet<int>? starts = null;
            // Whether the file shows a comment between two of its lines to be the one just above a declaration or its leading one.
            bool AboveADeclarationOrAtTheHead(int before, int after, bool continuesBlockComment = false)
            {
                if (!textRead)
                {
                    text = readComments?.Invoke();
                    textRead = true;
                }
                if (text is null || (continuesBlockComment && !text.IsOpenAfter(before))) return false;
                starts ??= groups.Select(group => group.Start).ToHashSet();
                return text.IsDocCommentOrHeader(before, after, starts);
            }

            HashSet<int>? boundaryLines = null;
            foreach (var boundary in boundaries ?? [])
            {
                for (var line = boundary.Start; line <= boundary.End; line++) (boundaryLines ??= []).Add(line);
            }

            // A comment or blank line a member holds changes it: inside a multi-line string either is part of a value,
            // and nothing here can tell. Where no member holds it, the file says whether it is the comment (with its
            // blank lines) just above a declaration or at the head of the file, which changes nothing.
            void Quiet(IReadOnlyList<DiffLineRange>? ranges, bool comment)
            {
                foreach (var range in ranges ?? [])
                {
                    for (var line = range.Start; line <= range.End; line++)
                    {
                        var here = new DiffLineRange(line, line);
                        Holding(roots, line, line, holding);
                        var member = false;
                        Group? innermost = null;
                        foreach (var group in holding)
                        {
                            if (innermost is null || group.Depth > innermost.Depth) innermost = group;
                            if (IsContainer(group, isContainer) || !Owns(group, line)) continue;
                            TouchOf(group, result).Lines.Add(here);
                            member = true;
                        }
                        if (member) continue;
                        if (comment && boundaryLines?.Contains(line) == true) result.CommentBoundaries.Add(here);
                        else if (AboveADeclarationOrAtTheHead(line - 1, line + 1) && (comment ? text!.IsComment(line) : text!.IsBlank(line)))
                            result.IgnoredComments++;
                        else if (innermost is not null) TouchOf(innermost, result).Lines.Add(here);
                        // A blank line no declaration holds (among the usings, before a namespace line) is in no value
                        // either: the bundled workers hold all code that can carry one (C# top-level statements are
                        // <Main>, a TypeScript module is its whole file).
                        else if (!comment) result.IgnoredComments++;
                        else result.Outside.Add(here);
                    }
                }
            }
            Quiet(comments, comment: true);
            Quiet(blanks, comment: false);

            foreach (var removal in removals)
            {
                // The innermost declaration holding the new lines on both sides of the block.
                Group? around = null;
                if (removal.NewBefore >= 1)
                {
                    Holding(roots, removal.NewBefore, removal.NewBefore, holding);
                    foreach (var group in holding)
                    {
                        if (removal.NewAfter <= group.End && (around is null || group.Depth > around.Depth)) around = group;
                    }
                }
                if (around is not null && !IsContainer(around, isContainer)) TouchOf(around, result).RemovedLines += removal.Old.Count;
                else if (!removal.CommentsOnly) result.RemovedBetween.Add(new RemovedBetween(removal.Old, around is null ? null : NodesOf(around)));
                else if (AboveADeclarationOrAtTheHead(removal.NewBefore, removal.NewAfter, removal.ContinuesBlockComment)) result.IgnoredComments += removal.Old.Count;
                else if (around is not null) TouchOf(around, result).RemovedLines += removal.Old.Count;
                // Blank lines no declaration held, as for added ones.
                else if (removal.BlankOnly) result.IgnoredComments += removal.Old.Count;
                else result.RemovedBetween.Add(new RemovedBetween(removal.Old, null));
            }

            foreach (var touch in result.Touched) Normalize(touch.Lines);
            Normalize(result.Outside);
            Normalize(result.CommentBoundaries);
            Normalize(result.ConditionalDirectives);
            result.Touched.Sort(static (left, right) =>
            {
                var order = left.Nodes[0].StartLine.CompareTo(right.Nodes[0].StartLine);
                return order != 0 ? order : string.CompareOrdinal(left.Nodes[0].Id, right.Nodes[0].Id);
            });
            return result;
        }

        private static bool IsContainer(Group group, Func<CodeNode, bool> isContainer)
        {
            if (group.Container is { } known) return known;
            var container = isContainer(group.Node);
            for (var index = 0; !container && index < (group.More?.Count ?? 0); index++) container = isContainer(group.More![index]);
            group.Container = container;
            return container;
        }

        private static IReadOnlyList<CodeNode> NodesOf(Group group) => group.More is null ? [group.Node] : [group.Node, .. group.More];

        private static Touch TouchOf(Group group, Result result)
        {
            if (group.Touch is null)
            {
                group.Touch = new Touch { Nodes = NodesOf(group) };
                result.Touched.Add(group.Touch);
            }
            return group.Touch;
        }

        /// <summary>
        /// The first of <paramref name="level"/> (siblings, ordered by start) that can hold line <paramref name="lo"/> or
        /// a later one: every group before it ends earlier.
        /// </summary>
        private static int FirstReaching(List<Group> level, int lo)
        {
            int low = 0, high = level.Count;
            while (low < high)
            {
                var middle = (low + high) >>> 1;
                if (level[middle].Reach < lo) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        /// <summary>
        /// Fills <paramref name="found"/> with the groups whose lines meet <c>[lo, hi]</c>, found by descending from
        /// <paramref name="roots"/>: a group's parent holds every line it does, so no other group can meet the range.
        /// </summary>
        private static void Holding(List<Group> roots, int lo, int hi, List<Group> found)
        {
            found.Clear();
            Descend(roots);

            void Descend(List<Group> level)
            {
                for (var index = FirstReaching(level, lo); index < level.Count && level[index].Start <= hi; index++)
                {
                    var group = level[index];
                    if (group.End < lo) continue;
                    found.Add(group);
                    if (group.Children is { } children) Descend(children);
                }
            }
        }

        /// <summary>Whether <paramref name="line"/>, which <paramref name="group"/> holds, is its own: its first or last line, or one no nested declaration holds.</summary>
        private static bool Owns(Group group, int line)
        {
            if ((line == group.Start && group.OwnsFirstLine) || (line == group.End && group.OwnsLastLine) || group.Children is not { } children) return true;
            for (var index = FirstReaching(children, line); index < children.Count && children[index].Start <= line; index++)
            {
                if (children[index].End >= line) return false;
            }
            return true;
        }

        /// <summary>
        /// Adds to <paramref name="into"/> the lines of <c>[lo, hi]</c> that are <paramref name="group"/>'s own: every
        /// line no nested declaration holds, and its first and last lines.
        /// </summary>
        private static void Owned(Group group, int lo, int hi, List<DiffLineRange> into)
        {
            if (lo > hi) return;
            if (group.Children is { } children) Uncovered(children, lo, hi, into);
            else into.Add(new DiffLineRange(lo, hi));
            if (group.OwnsFirstLine && group.Start >= lo && group.Start <= hi) into.Add(new DiffLineRange(group.Start, group.Start));
            if (group.OwnsLastLine && group.End >= lo && group.End <= hi && group.End != group.Start) into.Add(new DiffLineRange(group.End, group.End));
        }

        /// <summary>Adds to <paramref name="into"/> the parts of <c>[lo, hi]</c> that none of <paramref name="covering"/> (siblings, ordered by start) holds.</summary>
        private static void Uncovered(List<Group> covering, int lo, int hi, List<DiffLineRange> into)
        {
            var next = lo;
            for (var index = FirstReaching(covering, lo); index < covering.Count && next <= hi; index++)
            {
                var group = covering[index];
                if (group.Start > hi) break;
                if (group.End < next) continue;
                if (group.Start > next) into.Add(new DiffLineRange(next, group.Start - 1));
                next = Math.Max(next, group.End + 1);
            }
            if (next <= hi) into.Add(new DiffLineRange(next, hi));
        }

        /// <summary>Orders <paramref name="ranges"/> and joins those that meet or touch.</summary>
        internal static void Normalize(List<DiffLineRange> ranges)
        {
            if (ranges.Count < 2) return;
            var span = CollectionsMarshal.AsSpan(ranges);
            var ordered = true;
            for (var index = 1; index < span.Length && ordered; index++) ordered = span[index - 1].End + 1 < span[index].Start;
            if (ordered) return;
            span.Sort(static (left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.End.CompareTo(right.End));
            var kept = 0;
            for (var index = 1; index < span.Length; index++)
            {
                if (span[index].Start <= span[kept].End + 1) span[kept] = new DiffLineRange(span[kept].Start, Math.Max(span[kept].End, span[index].End));
                else span[++kept] = span[index];
            }
            ranges.RemoveRange(kept + 1, ranges.Count - kept - 1);
        }

        /// <summary>
        /// The file's declarations as groups (nodes with one span), each under the innermost group whose span holds it,
        /// children ordered by start. Lines are 1-based here; a span that ends at the very start of a line (an exclusive
        /// end at column 0) does not hold that line.
        /// </summary>
        private static List<Group> Groups(IReadOnlyList<CodeNode> nodes)
        {
            // Outermost first at one start, so that a parent precedes what it holds and nodes with one span lie together.
            var ordered = new CodeNode[nodes.Count];
            for (var index = 0; index < ordered.Length; index++) ordered[index] = nodes[index];
            Array.Sort(ordered, static (left, right) =>
            {
                var order = From(left).CompareTo(From(right));
                if (order == 0) order = To(right).CompareTo(To(left));
                // The end as it was sent tells spans apart that hold the same lines; the id orders the nodes of one span.
                if (order == 0) order = (left.EndLine, left.EndCol).CompareTo((right.EndLine, right.EndCol));
                return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
            });
            var groups = new List<Group>(ordered.Length);
            foreach (var node in ordered)
            {
                if (groups.Count > 0 && groups[^1].Node is var last
                    && (last.StartLine, last.StartCol, last.EndLine, last.EndCol) == (node.StartLine, node.StartCol, node.EndLine, node.EndCol))
                {
                    (groups[^1].More ??= []).Add(node);
                    continue;
                }
                var endLine = node.EndCol == 0 && node.EndLine > node.StartLine ? node.EndLine - 1 : Math.Max(node.EndLine, node.StartLine);
                groups.Add(new Group
                {
                    Node = node, Start = node.StartLine + 1, End = endLine + 1, From = From(node), To = To(node),
                    // A span that ends at the start of a line holds whole lines: none of them closes it.
                    OwnsLastLine = endLine == Math.Max(node.EndLine, node.StartLine),
                });
            }

            var open = new Stack<Group>();
            Group? lastRoot = null;
            foreach (var group in groups)
            {
                while (open.Count > 0 && !(open.Peek().From.CompareTo(group.From) <= 0 && group.To.CompareTo(open.Peek().To) <= 0)) open.Pop();
                Group? before;
                if (open.TryPeek(out var parent))
                {
                    group.Parent = parent;
                    group.Depth = parent.Depth + 1;
                    if (group.From == parent.From) parent.OwnsFirstLine = false;
                    if (group.To == parent.To) parent.OwnsLastLine = false;
                    before = parent.Children is { Count: > 0 } siblings ? siblings[^1] : null;
                    (parent.Children ??= []).Add(group);
                }
                else
                {
                    before = lastRoot;
                    lastRoot = group;
                }
                group.Reach = Math.Max(group.End, before?.Reach ?? 0);
                open.Push(group);
            }
            return groups;

            static (int Line, int Col) From(CodeNode node) => (node.StartLine, node.StartCol);
            static (int Line, int Col) To(CodeNode node)
                => (Math.Max(node.EndLine, node.StartLine), node.EndLine < node.StartLine ? node.StartCol : node.EndCol);
        }
    }
}
