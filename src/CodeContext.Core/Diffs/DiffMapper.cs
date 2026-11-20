namespace CodeContext.Core.Diffs
{
    /// <summary>
    /// Maps the changed lines of one file to the declarations of the index whose spans hold them. A line belongs to the
    /// innermost declarations containing it: those with no nested declaration of their own on that line. A
    /// declaration's first and last lines (its header, its closing line) are its own as well, so a change there reaches
    /// the declaration and not only what it contains. Declarations with the same span count as one.
    ///
    /// A removed run belongs to the innermost declaration around it (one holding the new lines on both sides of the
    /// block) when that declaration is a member, which then changed. When it lies between or at the edge of members, in
    /// a type, a module or no declaration at all, the removed lines may have held a declaration of their own (a
    /// deletion, a rename, a changed signature), which exists only on the old side: that is reported, never mapped.
    /// Lines no declaration holds at all (imports, a namespace line, a file header) are reported too.
    ///
    /// Comment lines (<see cref="UnifiedDiff.IsCommentLine"/>) change a member that holds them, as any line does; where
    /// no member holds them (a doc comment above a member, which a declaration's span leaves out, or a file header) they
    /// change nothing and are only counted, and a removed run of comments only is no removed declaration. A comment line
    /// that opens or closes a block comment around unchanged lines (<see cref="DiffFile.CommentBoundaries"/>) is the
    /// exception: where no member holds it, what it commented out or back in is reported, never counted.
    /// </summary>
    public static class DiffMapper
    {
        /// <summary>A declaration (one or more nodes with one span) that the change reaches.</summary>
        public sealed class Touch
        {
            public required IReadOnlyList<CodeNode> Nodes { get; init; }

            /// <summary>Changed new-side lines that are its own, 1-based.</summary>
            public List<DiffLineRange> Lines { get; } = new();

            /// <summary>Removed lines inside it.</summary>
            public int RemovedLines { get; set; }
        }

        /// <summary>Removed lines between or at the edge of declarations; <see cref="Container"/> holds them, if anything does.</summary>
        public readonly record struct RemovedBetween(DiffLineRange Old, IReadOnlyList<CodeNode>? Container);

        public sealed class Result
        {
            public List<Touch> Touched { get; } = new();
            public List<DiffLineRange> Outside { get; } = new();
            public List<RemovedBetween> RemovedBetween { get; } = new();

            /// <summary>Comment lines no member holds, added or removed: counted, not mapped.</summary>
            public int IgnoredComments { get; set; }

            /// <summary>Block-comment boundaries no member holds: the unchanged lines next to them may hold a declaration.</summary>
            public List<DiffLineRange> CommentBoundaries { get; } = new();
        }

        private sealed class Group
        {
            public required List<CodeNode> Nodes;
            public int Start;
            public int End;
            public (int Line, int Col) From;
            public (int Line, int Col) To;
            public Group? Parent;
            public List<Group>? Children;
            public Touch? Touch;
        }

        /// <param name="nodes">Every node of the index in the file.</param>
        /// <param name="isContainer">Whether a node holds declarations (a type, a module): a removal between its members is not mapped.</param>
        /// <param name="added">Changed new-side lines; <paramref name="comments"/> changed comment lines; <paramref name="removals"/> the removed runs.</param>
        /// <param name="boundaries">The comment lines among <paramref name="comments"/> that open or close a block comment around unchanged lines.</param>
        public static Result Map(
            IReadOnlyList<CodeNode> nodes, Func<CodeNode, bool> isContainer,
            IReadOnlyList<DiffLineRange> added, IReadOnlyList<DiffRemoval> removals, IReadOnlyList<DiffLineRange>? comments = null,
            IReadOnlyList<DiffLineRange>? boundaries = null)
        {
            var result = new Result();
            var groups = Groups(nodes);
            var roots = groups.Where(group => group.Parent is null).ToList();

            foreach (var range in added)
            {
                foreach (var group in groups)
                {
                    var lo = Math.Max(range.Start, group.Start);
                    var hi = Math.Min(range.End, group.End);
                    if (lo > hi) continue;
                    var owned = Owned(group, lo, hi);
                    if (owned.Count == 0) continue;
                    var touch = TouchOf(group, result);
                    foreach (var line in owned) Merge(touch.Lines, line);
                }
                foreach (var outside in Uncovered(roots, range.Start, range.End)) Merge(result.Outside, outside);
            }

            // A comment line is a member's when a member is innermost there; otherwise it changes nothing.
            foreach (var range in comments ?? [])
            {
                for (var line = range.Start; line <= range.End; line++)
                {
                    var member = false;
                    foreach (var group in groups)
                    {
                        if (line < group.Start || line > group.End || group.Nodes.Exists(node => isContainer(node))) continue;
                        if (Owned(group, line, line).Count == 0) continue;
                        Merge(TouchOf(group, result).Lines, new DiffLineRange(line, line));
                        member = true;
                    }
                    if (member) continue;
                    if (boundaries?.Any(boundary => boundary.Start <= line && line <= boundary.End) == true)
                        Merge(result.CommentBoundaries, new DiffLineRange(line, line));
                    else result.IgnoredComments++;
                }
            }

            foreach (var removal in removals)
            {
                // The innermost declaration holding the new lines on both sides of the block.
                Group? around = null;
                if (removal.NewBefore >= 1)
                {
                    foreach (var group in groups)
                    {
                        if (group.Start <= removal.NewBefore && removal.NewAfter <= group.End
                            && (around is null || Depth(group) > Depth(around)))
                        {
                            around = group;
                        }
                    }
                }
                if (around is not null && !around.Nodes.Exists(node => isContainer(node)))
                {
                    TouchOf(around, result).RemovedLines += removal.Old.Count;
                    continue;
                }
                if (removal.CommentsOnly)
                {
                    result.IgnoredComments += removal.Old.Count;
                    continue;
                }
                result.RemovedBetween.Add(new RemovedBetween(removal.Old, around?.Nodes));
            }

            result.Touched.Sort(static (left, right) =>
            {
                var order = left.Nodes[0].StartLine.CompareTo(right.Nodes[0].StartLine);
                return order != 0 ? order : string.CompareOrdinal(left.Nodes[0].Id, right.Nodes[0].Id);
            });
            return result;
        }

        private static Touch TouchOf(Group group, Result result)
        {
            if (group.Touch is null)
            {
                group.Touch = new Touch { Nodes = group.Nodes };
                result.Touched.Add(group.Touch);
            }
            return group.Touch;
        }

        private static int Depth(Group group)
        {
            var depth = 0;
            for (var parent = group.Parent; parent is not null; parent = parent.Parent) depth++;
            return depth;
        }

        /// <summary>
        /// The lines of <c>[lo, hi]</c> that are <paramref name="group"/>'s own: its first and last lines, and every line no
        /// nested declaration holds.
        /// </summary>
        private static List<DiffLineRange> Owned(Group group, int lo, int hi)
        {
            var owned = Uncovered(group.Children, lo, hi);
            if (group.Start >= lo && group.Start <= hi) owned.Add(new DiffLineRange(group.Start, group.Start));
            if (group.End >= lo && group.End <= hi && group.End != group.Start) owned.Add(new DiffLineRange(group.End, group.End));
            return owned;
        }

        /// <summary>The parts of <c>[lo, hi]</c> that none of <paramref name="covering"/> (ordered by start) holds.</summary>
        private static List<DiffLineRange> Uncovered(List<Group>? covering, int lo, int hi)
        {
            var gaps = new List<DiffLineRange>();
            var next = lo;
            foreach (var group in covering ?? [])
            {
                if (next > hi) break;
                if (group.End < next) continue;
                if (group.Start > hi) break;
                if (group.Start > next) gaps.Add(new DiffLineRange(next, group.Start - 1));
                next = Math.Max(next, group.End + 1);
            }
            if (next <= hi) gaps.Add(new DiffLineRange(next, hi));
            return gaps;
        }

        /// <summary>Adds <paramref name="range"/> to ordered, disjoint <paramref name="ranges"/>, joining what touches it.</summary>
        internal static void Merge(List<DiffLineRange> ranges, DiffLineRange range)
        {
            var at = 0;
            while (at < ranges.Count && ranges[at].End + 1 < range.Start) at++;
            var start = range.Start;
            var end = range.End;
            while (at < ranges.Count && ranges[at].Start <= end + 1)
            {
                start = Math.Min(start, ranges[at].Start);
                end = Math.Max(end, ranges[at].End);
                ranges.RemoveAt(at);
            }
            ranges.Insert(at, new DiffLineRange(start, end));
        }

        /// <summary>
        /// The file's declarations as groups (nodes with one span), each under the innermost group whose span holds it,
        /// children ordered by start. Lines are 1-based here; a span that ends at the very start of a line (an exclusive
        /// end at column 0) does not hold that line.
        /// </summary>
        private static List<Group> Groups(IReadOnlyList<CodeNode> nodes)
        {
            var byspan = new Dictionary<(int, int, int, int), Group>();
            var groups = new List<Group>();
            foreach (var node in nodes)
            {
                var key = (node.StartLine, node.StartCol, node.EndLine, node.EndCol);
                if (byspan.TryGetValue(key, out var existing))
                {
                    existing.Nodes.Add(node);
                    continue;
                }
                var endLine = node.EndCol == 0 && node.EndLine > node.StartLine ? node.EndLine - 1 : Math.Max(node.EndLine, node.StartLine);
                var group = new Group
                {
                    Nodes = [node],
                    Start = node.StartLine + 1,
                    End = endLine + 1,
                    From = (node.StartLine, node.StartCol),
                    To = (Math.Max(node.EndLine, node.StartLine), node.EndLine < node.StartLine ? node.StartCol : node.EndCol),
                };
                byspan[key] = group;
                groups.Add(group);
            }

            // Outermost first at one start: a parent precedes what it holds.
            groups.Sort(static (left, right) =>
            {
                var order = left.From.CompareTo(right.From);
                return order != 0 ? order : right.To.CompareTo(left.To);
            });
            var open = new Stack<Group>();
            foreach (var group in groups)
            {
                while (open.Count > 0 && !(open.Peek().From.CompareTo(group.From) <= 0 && group.To.CompareTo(open.Peek().To) <= 0)) open.Pop();
                if (open.TryPeek(out var parent))
                {
                    group.Parent = parent;
                    (parent.Children ??= []).Add(group);
                }
                open.Push(group);
            }
            return groups;
        }
    }
}
