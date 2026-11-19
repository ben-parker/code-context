using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// One source declaration indexed more than once (a file seen through two projects or workspaces, each giving the
/// symbol an id and identifier of its own) is one thing to change: its copies are searched together, and the answer
/// says so. Choosing one copy would silently leave out what depends on the others.
/// </summary>
public class GraphSameDeclarationTests
{
    private const string Root = "/repo";

    private static CodeNode Node(string workspace, string identifier, string name, string file, int line,
        string type = "Method", string? signature = null)
        => new()
        {
            Id = $"lang:{workspace}:{identifier}",
            Identifier = $"lang:{workspace}/{identifier}",
            Name = name,
            Type = type,
            FilePath = $"{Root}/{file}",
            StartLine = line,
            EndLine = line + 2,
            StartCol = 4,
            EndCol = 5,
            Signature = signature ?? name + "()",
            Metadata = new Dictionary<string, string> { ["parserId"] = "lang", ["workspaceId"] = workspace },
        };

    private static CodeEdge Edge(string workspace, string source, string target, string type = "CALLS")
        => new()
        {
            Id = $"lang:{workspace}:{source}>{type}>{target}",
            SourceId = $"lang:{workspace}:{source}",
            TargetId = $"lang:{workspace}:{target}",
            Type = type,
            Metadata = new Dictionary<string, string> { ["parserId"] = "lang", ["workspaceId"] = workspace },
        };

    /// <summary>
    /// shared/Util.ts declares Twice once; workspaces app1 and app2 each index it, and each has a caller of its own
    /// copy. Other.Twice is a different declaration with the same name.
    /// </summary>
    private static ContextService Service(bool withOther = false)
    {
        var nodes = new List<CodeNode>
        {
            Node("app1", "Util", "Util", "shared/Util.ts", 1, type: "Class"),
            Node("app2", "Util", "Util", "shared/Util.ts", 1, type: "Class"),
            Node("app1", "Util.Twice", "Twice", "shared/Util.ts", 3),
            Node("app2", "Util.Twice", "Twice", "shared/Util.ts", 3),
            Node("app1", "One.Run", "Run", "app1/One.ts", 2),
            Node("app2", "Two.Go", "Go", "app2/Two.ts", 2),
            Node("app2", "Two.Top", "Top", "app2/Two.ts", 9),
        };
        var edges = new List<CodeEdge>
        {
            Edge("app1", "Util", "Util.Twice", "HAS_METHOD"),
            Edge("app2", "Util", "Util.Twice", "HAS_METHOD"),
            Edge("app1", "One.Run", "Util.Twice"),
            Edge("app2", "Two.Go", "Util.Twice"),
            Edge("app2", "Two.Top", "Two.Go"),
        };
        if (withOther) nodes.Add(Node("app1", "Other.Twice", "Twice", "app1/Other.ts", 7));
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, edges, scope: null));
        Assert.Equal(2, database.ShardKeys.Count);
        return new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = Root }));
    }

    [Fact]
    public async Task Affected_ByAName_SeedsEveryCopyOfTheOneDeclaration_AndSaysSo()
    {
        var response = await Service().GetAffectedAsync(new GraphAffectedRequest { Identifier = "Twice" });

        Assert.False(response.Seed.Ambiguous);
        Assert.Equal(2, response.Seed.TotalMatches);
        Assert.Equal(2, response.Seed.SeedCount);
        Assert.Equal("lang:app1/Util.Twice", response.Seed.Target!.Identifier);
        Assert.Equal(["lang:app2/Util.Twice"], response.Seed.SameDeclaration);
        Assert.Contains("one declaration", response.Seed.Hint);
        Assert.Contains("shared/Util.ts:4", response.Seed.Hint);
        Assert.Equal(["lang:app1/One.Run", "lang:app2/Two.Go", "lang:app2/Two.Top"], response.Items.Select(item => item.Identifier));
        Assert.Equal([1, 1, 2], response.Items.Select(item => item.Distance));
        // Each dependent names the copy it uses.
        Assert.Equal(["lang:app1/Util.Twice", "lang:app2/Util.Twice", "lang:app2/Two.Go"], response.Items.Select(item => item.Via));
    }

    [Theory]
    [InlineData("lang:app1/Util.Twice", "lang:app2/Util.Twice")]
    [InlineData("lang:app2/Util.Twice", "lang:app1/Util.Twice")]
    public async Task Affected_ByOneCopysIdentifier_StillSeedsTheOthers(string identifier, string other)
    {
        var response = await Service().GetAffectedAsync(new GraphAffectedRequest { Identifier = identifier });

        Assert.Equal("identity", response.Seed.MatchMode);
        Assert.Equal(identifier, response.Seed.Target!.Identifier);
        Assert.Equal([other], response.Seed.SameDeclaration);
        Assert.Equal(3, response.TotalCount);
    }

    [Fact]
    public async Task AType_StandsForTheMembersOfEveryCopy()
    {
        var response = await Service().GetAffectedAsync(new GraphAffectedRequest { Identifier = "Util" });

        Assert.Equal(4, response.Seed.SeedCount);
        Assert.Equal(["lang:app2/Util"], response.Seed.SameDeclaration);
        Assert.Equal(3, response.TotalCount);
    }

    [Fact]
    public async Task Path_StartsFromAndEndsAtAnyCopy()
    {
        var service = Service();

        // Only app2's copy is called by Top.
        var into = await service.FindPathAsync(new GraphPathRequest { From = "lang:app2/Two.Top", To = "lang:app1/Util.Twice" });
        Assert.True(into.Found);
        Assert.Equal(2, into.Length);
        Assert.Equal(["lang:app2/Util.Twice"], into.To.SameDeclaration);
        Assert.Equal("lang:app2/Util.Twice", into.Path![^1].Identifier);

        var from = await service.FindPathAsync(new GraphPathRequest { From = "Util", To = "Util" });
        Assert.True(from.Found);
        Assert.Equal(0, from.Length);
    }

    [Fact]
    public async Task DifferentDeclarationsOfOneName_StayAmbiguous_WithEveryCopyListed()
    {
        var response = await Service(withOther: true).GetAffectedAsync(new GraphAffectedRequest { Identifier = "Twice" });

        Assert.True(response.Seed.Ambiguous);
        Assert.Equal(3, response.Seed.TotalMatches);
        Assert.Null(response.Seed.SameDeclaration);
        Assert.Equal(0, response.TotalCount);
        Assert.Equal(3, response.Seed.Candidates!.Count);
    }

    [Fact]
    public async Task OverloadsAndNeighboursAreNotCopies()
    {
        // Same file, same name, same line even: a different signature, span or type is a different declaration.
        var nodes = new List<CodeNode>
        {
            Node("app1", "A.Run()", "Run", "src/A.ts", 3),
            Node("app1", "A.Run(int)", "Run", "src/A.ts", 3, signature: "Run(int)"),
            Node("app2", "A.Run", "Run", "src/A.ts", 3, type: "Property"),
        };
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, new List<CodeEdge>(), scope: null));
        var service = new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = Root }));

        var byName = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "Run" });
        Assert.True(byName.Seed.Ambiguous);

        var byIdentity = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "lang:app1/A.Run()" });
        Assert.Null(byIdentity.Seed.SameDeclaration);
        Assert.Equal(1, byIdentity.Seed.SeedCount);
    }

    [Fact]
    public async Task LibrarySymbolsWithoutAFile_AreNotCopiesOfEachOther()
    {
        var external = new Dictionary<string, string> { ["external"] = "true" };
        var nodes = new List<CodeNode>
        {
            new() { Id = "lang:app1:lib.Go", Identifier = "lang:app1/lib.Go", Name = "Go", Type = "Method", Signature = "Go()", Metadata = external },
            new() { Id = "lang:app2:lib.Go", Identifier = "lang:app2/lib.Go", Name = "Go", Type = "Method", Signature = "Go()", Metadata = external },
        };
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, new List<CodeEdge>(), scope: null));
        var service = new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = Root }));

        var response = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "Go" });
        Assert.True(response.Seed.Ambiguous);
    }
}
