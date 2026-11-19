using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>Hub ranking on graphs small enough that the right counts are plain from the edges.</summary>
public class GraphHubsTests
{
    private static Task<GraphHubsResponse> Hubs(ContextService service, int limit = 20, string? relation = null,
        string? type = null, string? within = null, bool includeTests = false, bool includeExternal = false)
        => service.GetHubsAsync(new GraphHubsRequest
        {
            Limit = limit, Relation = relation, Type = type, Within = within,
            IncludeTests = includeTests, IncludeExternal = includeExternal,
        });

    private static string[] Ranked(GraphHubsResponse response)
        => response.Items.Select(item => $"{item.Name}:{item.Dependents}/{item.DependentFiles}").ToArray();

    [Fact]
    public async Task RanksByDistinctDependents_NotByEdges_AndIgnoresSelfUse()
    {
        var service = new GraphTestGraph()
            .Node("Hub").Node("Busy").Node("U1", file: "src/One.cs").Node("U2", file: "src/One.cs").Node("U3", file: "src/Two.cs")
            .Calls("U1", "Hub").Calls("U2", "Hub").Calls("U3", "Hub")
            // One caller, many calls, and a recursive call: still one dependent.
            .Edge("U1", "Busy", line: 1).Edge("U1", "Busy", line: 2).Edge("U1", "Busy", line: 3).Edge("Busy", "Busy")
            .Build();

        var response = await Hubs(service);

        Assert.Equal(["Hub:3/2", "Busy:1/1"], Ranked(response));
        Assert.Equal(2, response.TotalCount);
        Assert.False(response.Truncated);
    }

    [Fact]
    public async Task Within_KeepsOnlySymbolsUnderThatDirectory_OnSegmentBoundaries()
    {
        var service = new GraphTestGraph()
            .Node("InCore", file: "src/Core/A.cs").Node("InCoreExtra", file: "src/CoreExtra/B.cs").Node("Elsewhere", file: "lib/C.cs")
            .Node("User", file: "app/User.cs")
            .Calls("User", "InCore", "InCoreExtra", "Elsewhere")
            .Build();

        var response = await Hubs(service, within: "src/Core");
        Assert.Equal(["InCore:1/1"], Ranked(response));
        Assert.Equal("src/Core", response.Within);

        Assert.Equal(["InCore:1/1"], Ranked(await Hubs(service, within: "./src\\Core/")));
        Assert.Equal(["InCore:1/1"], Ranked(await Hubs(service, within: "/repo/src/Core")));
        Assert.Equal(["InCore:1/1"], Ranked(await Hubs(service, within: "src/core/A.cs")));
        Assert.Equal(3, (await Hubs(service)).TotalCount);

        var none = await Hubs(service, within: "docs");
        Assert.Empty(none.Items);
        Assert.Equal("No indexed symbol under docs passes the filters.", none.Hint);

        await Assert.ThrowsAsync<ArgumentException>(() => Hubs(service, within: "/elsewhere/src"));
        await Assert.ThrowsAsync<ArgumentException>(() => Hubs(service, within: "../other"));
    }

    [Fact]
    public async Task TestFiles_AreNeitherRankedNorCounted_UnlessIncluded()
    {
        var service = new GraphTestGraph()
            .Node("Prod").Node("Fixture", file: "tests/Fixture.cs")
            .Node("ProdUser").Node("TestA", file: "tests/ATests.cs").Node("TestB", file: "tests/BTests.cs")
            .Calls("ProdUser", "Prod").Calls("TestA", "Prod", "Fixture").Calls("TestB", "Prod", "Fixture")
            .Build();

        Assert.Equal(["Prod:1/1"], Ranked(await Hubs(service)));
        Assert.Equal(["Prod:3/3", "Fixture:2/2"], Ranked(await Hubs(service, includeTests: true)));
    }

    [Fact]
    public async Task LibrarySymbols_AreRankedOnlyWhenAskedFor()
    {
        var service = new GraphTestGraph()
            .Node("List.Add", external: true).Node("Mine").Node("U1").Node("U2")
            .Calls("U1", "List.Add", "Mine").Calls("U2", "List.Add")
            .Build();

        Assert.Equal(["Mine:1/1"], Ranked(await Hubs(service)));
        Assert.Equal(["Add:2/2", "Mine:1/1"], Ranked(await Hubs(service, includeExternal: true)));
        var external = (await Hubs(service, includeExternal: true)).Items[0];
        Assert.Equal("Lib", external.External);
        Assert.Null(external.File);
    }

    [Fact]
    public async Task TruncationIsSetOnlyWhenSomethingIsLeftOut()
    {
        var service = new GraphTestGraph()
            .Node("H1").Node("H2").Node("U")
            .Calls("U", "H1", "H2")
            .Build();

        Assert.False((await Hubs(service, limit: 2)).Truncated);
        var cut = await Hubs(service, limit: 1);
        Assert.True(cut.Truncated);
        Assert.Equal(1, cut.ReturnedCount);
        Assert.Equal(2, cut.TotalCount);
    }

    [Fact]
    public async Task TypeAndRelationFilters_Apply()
    {
        var service = new GraphTestGraph()
            .Node("Svc", "Class").Node("Svc.Run").Node("U1").Node("U2")
            .Edge("U1", "Svc", "REFERENCES").Edge("U2", "Svc", "REFERENCES").Edge("U1", "Svc.Run")
            .Build();

        Assert.Equal(["Svc:2/2"], Ranked(await Hubs(service, type: "class")));
        Assert.Equal(["Run:1/1"], Ranked(await Hubs(service, relation: "CALLS")));
        var none = await Hubs(service, type: "Enum");
        Assert.Equal("No indexed symbol of type Enum passes the filters.", none.Hint);
    }
}
