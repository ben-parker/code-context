using CodeContext.Core.Diffs;

namespace CodeContext.Core.Tests.Diffs;

/// <summary>Which declarations a changed line belongs to, on spans written by hand (0-based, as the store keeps them).</summary>
public class DiffMapperTests
{
    private static CodeNode Node(string id, int startLine, int startCol, int endLine, int endCol, string type = "Method")
        => new() { Id = id, Identifier = id, Name = id, Type = type, StartLine = startLine, StartCol = startCol, EndLine = endLine, EndCol = endCol };

    private static bool Container(CodeNode node) => node.Type is "Class" or "Record" or "Enum";

    private static string[] Touched(DiffMapper.Result result) => result.Touched.SelectMany(touch => touch.Nodes).Select(node => node.Id!).ToArray();

    [Fact]
    public void ALineSharedByATypeAndItsMembers_ChangesBoth()
    {
        // record Parameter(string Name, Kind Kind) on one line (1-based line 5): the record and its properties.
        var nodes = new[]
        {
            Node("Parameter", 4, 4, 4, 60, "Record"),
            Node("Parameter.Name", 4, 21, 4, 32, "Property"),
            Node("Parameter.Kind", 4, 34, 4, 43, "Property"),
        };

        var result = DiffMapper.Map(nodes, Container, [new DiffLineRange(5, 5)], []);

        Assert.Equal(["Parameter", "Parameter.Kind", "Parameter.Name"], Touched(result).Order());
        Assert.Empty(result.Outside);
    }

    [Fact]
    public void ALineInsideANestedMember_ChangesOnlyTheInnermost()
    {
        var nodes = new[]
        {
            Node("Outer", 0, 0, 20, 1, "Class"),
            Node("Outer.Run", 2, 4, 10, 5),
            Node("Outer.Run.Local", 4, 8, 7, 9, "Function"),
        };

        Assert.Equal(["Outer.Run.Local"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(6, 6)], [])));
        Assert.Equal(["Outer.Run"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(10, 10)], [])));
        // The class's closing line is its own.
        Assert.Equal(["Outer"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(21, 21)], [])));
    }

    [Fact]
    public void ADeclarationThatIsAllOfItsFirstOrLastLine_LeavesThatLineToWhatItHoldsThere()
    {
        // A module is the whole file: it starts where its first function starts and ends where the text ends, so it
        // has no header and no closing line of its own. (1-based: helper 1-3, a blank line, added 5-7.)
        var nodes = new[]
        {
            Node("Module", 0, 0, 7, 0, "Module"),
            Node("helper", 0, 0, 2, 1, "Function"),
            Node("added", 4, 0, 6, 1, "Function"),
        };

        // A function appended at the end, and the first line of the first one: the functions' alone.
        Assert.Equal(["added"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(5, 7)], [])));
        Assert.Equal(["helper"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(1, 1)], [])));
        // The line between them is the module's.
        Assert.Equal(["Module"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(4, 4)], [])));

        // The same file without a newline at its end: the module ends with its last function, to the column.
        nodes[0] = Node("Module", 0, 0, 6, 1, "Module");
        Assert.Equal(["added"], Touched(DiffMapper.Map(nodes, Container, [new DiffLineRange(7, 7)], [])));
        Assert.Equal(["added"], Touched(DiffMapper.Map(nodes, Container, [], [], comments: [new DiffLineRange(7, 7)])));

        // A type with a closing line of its own keeps it, and a type on one line with its members changes with them.
        var type = new[] { Node("Type", 0, 0, 4, 1, "Class"), Node("Type.M", 1, 4, 3, 5) };
        Assert.Equal(["Type"], Touched(DiffMapper.Map(type, Container, [new DiffLineRange(5, 5)], [])));
        Assert.Equal(["Type"], Touched(DiffMapper.Map(type, Container, [new DiffLineRange(1, 1)], [])));
    }

    [Fact]
    public void ASpanEndingAtColumnZero_DoesNotHoldThatLine()
    {
        // An end-exclusive span that stops at the start of 1-based line 6 holds lines 1-5.
        var nodes = new[] { Node("Module", 0, 0, 5, 0, "Module") };

        var result = DiffMapper.Map(nodes, Container, [new DiffLineRange(6, 6)], []);

        Assert.Empty(result.Touched);
        Assert.Equal([new DiffLineRange(6, 6)], result.Outside);
    }

    [Fact]
    public void ARangeAcrossSeveralMembers_IsSplitAmongThem_AndTheGapsGoToTheType()
    {
        var nodes = new[]
        {
            Node("Type", 0, 0, 30, 1, "Class"),
            Node("Type.A", 2, 4, 5, 5),
            Node("Type.B", 8, 4, 12, 5),
        };

        var result = DiffMapper.Map(nodes, Container, [new DiffLineRange(4, 10)], []);

        var lines = result.Touched.ToDictionary(touch => touch.Nodes[0].Id!, touch => touch.Lines.ToArray());
        Assert.Equal([new DiffLineRange(4, 6)], lines["Type.A"]);
        Assert.Equal([new DiffLineRange(7, 8)], lines["Type"]);
        Assert.Equal([new DiffLineRange(9, 10)], lines["Type.B"]);
    }

    [Fact]
    public void ARemovalAtTheTopOfTheFile_OrBetweenTypes_IsHeldByNothing()
    {
        var nodes = new[] { Node("A", 2, 0, 5, 1, "Class"), Node("B", 8, 0, 10, 1, "Class") };

        var result = DiffMapper.Map(nodes, Container, [],
            [new DiffRemoval(new DiffLineRange(1, 2), 0, 1), new DiffRemoval(new DiffLineRange(9, 12), 7, 8)]);

        Assert.Empty(result.Touched);
        Assert.Equal([null, null], result.RemovedBetween.Select(removed => removed.Container));
    }

    [Fact]
    public void ChangedLines_GoToTheSameDeclarations_AsAScanOfEveryDeclarationForEveryLine()
    {
        var random = new Random(20261120);
        for (var round = 0; round < 200; round++)
        {
            // A random tree of declarations over 120 lines: each splits its inside among children, with gaps.
            var nodes = new List<CodeNode>();
            void Declare(string id, int from, int to, int depth)
            {
                nodes.Add(Node(id, from, 0, to, 200, depth < 2 && random.Next(2) == 0 ? "Class" : "Method"));
                var next = from + random.Next(2);
                for (var child = 0; depth < 4 && next < to; child++)
                {
                    var end = Math.Min(to - random.Next(2), next + random.Next(8));
                    if (end < next) break;
                    if (random.Next(4) > 0) Declare($"{id}.{child}", next, end, depth + 1);
                    // Siblings share no line: their columns are all the same here, so on one line they would overlap.
                    next = end + 1 + random.Next(2);
                }
            }
            for (int top = 0, at = random.Next(3); at < 120; top++)
            {
                var end = Math.Min(119, at + random.Next(40));
                Declare($"T{top}", at, end, 0);
                at = end + 1 + random.Next(3);
            }
            var added = new List<DiffLineRange>();
            for (var at = 1 + random.Next(6); at <= 125; at += 1 + random.Next(12))
            {
                var end = at + random.Next(5);
                added.Add(new DiffLineRange(at, end));
                at = end + 1;
            }

            var result = DiffMapper.Map(nodes, Container, added, []);

            var expected = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            var outside = new List<int>();
            foreach (var line in added.SelectMany(range => Enumerable.Range(range.Start, range.Count)))
            {
                var holding = nodes.Where(node => node.StartLine + 1 <= line && line <= node.EndLine + 1).ToList();
                if (holding.Count == 0) outside.Add(line);
                foreach (var node in holding)
                {
                    var inside = holding.Where(other => other != node && other.StartLine >= node.StartLine && other.EndLine <= node.EndLine
                        && (other.StartLine, other.EndLine) != (node.StartLine, node.EndLine)).ToList();
                    // Every span here starts at column 0 and ends at column 200: a nested declaration that starts on its
                    // holder's first line starts where the holder does, and one that ends on its last line ends with it.
                    var ownFirst = line == node.StartLine + 1 && !inside.Exists(other => other.StartLine == node.StartLine);
                    var ownLast = line == node.EndLine + 1 && !inside.Exists(other => other.EndLine == node.EndLine);
                    if (ownFirst || ownLast || inside.Count == 0)
                    {
                        // Declarations with one span are one group, named by its first node in id order.
                        var first = nodes.Where(same => (same.StartLine, same.EndLine) == (node.StartLine, node.EndLine)).MinBy(same => same.Id, StringComparer.Ordinal)!;
                        if (!expected.TryGetValue(first.Id!, out var lines)) expected[first.Id!] = lines = [];
                        if (!lines.Contains(line)) lines.Add(line);
                    }
                }
            }
            var actual = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var touch in result.Touched)
            {
                actual[touch.Nodes[0].Id!] = touch.Lines.SelectMany(range => Enumerable.Range(range.Start, range.Count)).ToList();
                Assert.True(touch.Lines.Zip(touch.Lines.Skip(1)).All(pair => pair.First.End + 1 < pair.Second.Start), "ranges are ordered and apart");
            }
            Assert.Equal(expected.Keys, actual.Keys);
            foreach (var (id, lines) in expected) Assert.Equal(lines.Order(), actual[id]);
            Assert.Equal(outside, result.Outside.SelectMany(range => Enumerable.Range(range.Start, range.Count)));

            // The same lines as comment lines, some marked as block-comment boundaries, over a file of random comment,
            // blank and code lines (or none to read): each lands in exactly one place.
            string[] kinds = ["// c", "", "code();", "/* open", " * inside", " */", "#region r"];
            var file = random.Next(5) == 0 ? null : CommentRuns.Of(Enumerable.Range(0, 130).Select(_ => kinds[random.Next(kinds.Length)]).ToList());
            var boundaries = added.Where(_ => random.Next(4) == 0).Select(range => new DiffLineRange(range.Start, range.Start)).ToList();
            var asComments = DiffMapper.Map(nodes, Container, [], [], added, boundaries, readComments: () => file);

            var total = added.Sum(range => range.Count);
            var placed = asComments.Touched.SelectMany(touch => touch.Lines).Concat(asComments.Outside).Concat(asComments.CommentBoundaries)
                .SelectMany(range => Enumerable.Range(range.Start, range.Count)).ToList();
            var mapped = asComments.Touched.SelectMany(touch => touch.Lines).Concat(asComments.Outside)
                .SelectMany(range => Enumerable.Range(range.Start, range.Count)).ToHashSet();
            var reported = asComments.CommentBoundaries.SelectMany(range => Enumerable.Range(range.Start, range.Count)).ToHashSet();
            Assert.Empty(mapped.Intersect(reported));
            Assert.Equal(total, mapped.Count + reported.Count + asComments.IgnoredComments);
            // A member's own line is the member's whatever the file shows, and without the file nothing is ignored.
            Assert.All(placed, line => Assert.InRange(line, 1, 130));
            if (file is null) Assert.Equal(0, asComments.IgnoredComments);
            Assert.All(reported, line => Assert.Contains(new DiffLineRange(line, line), boundaries));
        }
    }
}
