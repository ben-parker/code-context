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
}
