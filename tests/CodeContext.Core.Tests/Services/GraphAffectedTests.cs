using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>Transitive impact on graphs small enough that the right answer is plain from the edges.</summary>
public class GraphAffectedTests
{
    private static string I(string name) => GraphTestGraph.Identifier(name);

    private static Task<GraphAffectedResponse> Affected(ContextService service, string identifier,
        int maxDepth = 5, string? relation = null, bool includeTests = false, int limit = 20)
        => service.GetAffectedAsync(new GraphAffectedRequest
        {
            Identifier = identifier, MaxDepth = maxDepth, Relation = relation, IncludeTests = includeTests, Limit = limit,
        });

    private static string[] Listed(GraphAffectedResponse response)
        => response.Items.Select(item => $"{item.Identifier["csharp:Demo.".Length..]}@{item.Distance}").ToArray();

    [Fact]
    public async Task DirectAndTransitiveDependents_NearestFirst_EachSayingWhatItDependsOn()
    {
        var service = new GraphTestGraph()
            .Node("Seed").Node("Direct", line: 1).Node("Ref", line: 2).Node("Second").Node("Unrelated")
            .Edge("Direct", "Seed").Edge("Ref", "Seed", "REFERENCES").Edge("Second", "Direct")
            .Edge("Seed", "Unrelated")
            .Build();

        var response = await Affected(service, I("Seed"));

        Assert.Equal(["Direct@1", "Ref@1", "Second@2"], Listed(response));
        Assert.Equal(["CALLS", "REFERENCES", "CALLS"], response.Items.Select(item => item.Relation));
        Assert.Equal([I("Seed"), I("Seed"), I("Direct")], response.Items.Select(item => item.Via));
        Assert.Equal(3, response.TotalCount);
        Assert.False(response.Truncated);
        Assert.False(response.DepthLimited);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(47)]
    [InlineData(60)]
    public async Task ALimitedList_IsTheStartOfTheFullOne(int limit)
    {
        // 30 direct dependents and 30 of theirs, added in an order that is not the listed one (file, then line).
        var graph = new GraphTestGraph().Node("Seed");
        for (var i = 0; i < 30; i++)
        {
            var scrambled = (i * 7) % 30;
            graph.Node($"Near{scrambled}", file: $"src/N{scrambled % 4}.cs", line: 100 - scrambled).Edge($"Near{scrambled}", "Seed");
            graph.Node($"Far{scrambled}", file: $"src/F{scrambled % 3}.cs", line: scrambled).Edge($"Far{scrambled}", $"Near{(scrambled * 11) % 30}");
        }
        var service = graph.Build();

        var full = await Affected(service, I("Seed"), limit: 100);
        var limited = await Affected(service, I("Seed"), limit: limit);

        Assert.Equal(60, full.TotalCount);
        Assert.Equal(60, limited.TotalCount);
        Assert.Equal(7, limited.FileCount);
        Assert.Equal(limit < 60, limited.Truncated);
        Assert.Equal(Listed(full).Take(limit), Listed(limited));
        // The full list is by distance, then file, then line.
        var keys = full.Items.Select(item => (item.Distance, item.File, item.Line)).ToList();
        Assert.Equal(keys.OrderBy(key => key).ToList(), keys);
    }

    [Fact]
    public async Task WhatMerelySitsNearTheSeed_IsNotAffected()
    {
        // Same file, same type, a sibling implementation, the declaration the seed implements, the seed's own
        // dependencies, and a caller of the sibling: none of them depends on the seed.
        var service = new GraphTestGraph()
            .Node("Foo", "Class", file: "src/Foo.cs").Node("Foo.Run", file: "src/Foo.cs").Node("Foo.Other", file: "src/Foo.cs")
            .Node("IFoo.Run").Node("Bar.Run").Node("BarCaller").Node("Dependency")
            .Edge("Foo", "Foo.Run", "HAS_METHOD").Edge("Foo", "Foo.Other", "HAS_METHOD")
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER").Edge("Bar.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("BarCaller", "Bar.Run").Edge("Foo.Run", "Dependency")
            .Build();

        var response = await Affected(service, I("Foo.Run"));

        Assert.Empty(response.Items);
        Assert.Equal(0, response.TotalCount);
        Assert.Contains("Nothing recorded depends on it", response.Hint);
    }

    [Fact]
    public async Task ACallerOfTheDeclaration_IsAffectedThroughDispatch_ButNotAStaticBaseCall()
    {
        var service = new GraphTestGraph()
            .Node("IFoo.Run").Node("Base.Run").Node("Foo.Run")
            .Node("ThroughInterface").Node("ThroughBase").Node("BaseCallFromSibling")
            .Edge("Foo.Run", "Base.Run", "OVERRIDES_MEMBER").Edge("Base.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("ThroughInterface", "IFoo.Run").Edge("ThroughBase", "Base.Run")
            .Edge("BaseCallFromSibling", "Base.Run", staticCall: true)
            .Build();

        var response = await Affected(service, I("Foo.Run"));

        Assert.Equal(["ThroughBase@1", "ThroughInterface@1"], Listed(response));
        Assert.Equal([I("Base.Run"), I("IFoo.Run")], response.Items.Select(item => item.Through));
        Assert.All(response.Items, item => Assert.Equal(I("Foo.Run"), item.Via));

        // The statically bound call reaches Base.Run itself.
        Assert.Contains("BaseCallFromSibling@1", Listed(await Affected(service, I("Base.Run"))));
    }

    [Fact]
    public async Task ChangingADeclaration_AffectsItsImplementationsAndTheirUsers()
    {
        var service = new GraphTestGraph()
            .Node("IFoo.Run").Node("Foo.Run").Node("Bar.Run").Node("UsesFoo")
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER").Edge("Bar.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("UsesFoo", "Foo.Run")
            .Build();

        var response = await Affected(service, I("IFoo.Run"));

        Assert.Equal(["Bar.Run@1", "Foo.Run@1", "UsesFoo@2"], Listed(response));
        Assert.Equal("IMPLEMENTS_MEMBER", response.Items[0].Relation);
    }

    [Fact]
    public async Task ARelationFilter_ChoosesWhichUsesCarryImpact_AndDispatchStillCarriesAFilteredCall()
    {
        var service = new GraphTestGraph()
            .Node("IFoo.Run").Node("Foo.Run").Node("Bar.Run").Node("Caller").Node("Referrer")
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER").Edge("Bar.Run", "Foo.Run", "OVERRIDES_MEMBER")
            .Edge("Caller", "IFoo.Run").Edge("Referrer", "Foo.Run", "REFERENCES")
            .Build();

        Assert.Equal(["Bar.Run@1", "Caller@1", "Referrer@1"], Listed(await Affected(service, I("Foo.Run"))));
        // CALLS alone: the reference and the overriding member drop out; the call through the interface stays.
        Assert.Equal(["Caller@1"], Listed(await Affected(service, I("Foo.Run"), relation: "CALLS")));
    }

    [Fact]
    public async Task TruncationIsSetOnlyWhenSomethingIsLeftOut()
    {
        var service = new GraphTestGraph()
            .Node("Seed").Node("U1").Node("U2").Node("U3")
            .Calls("U1", "Seed").Calls("U2", "Seed").Calls("U3", "Seed")
            .Build();

        var exact = await Affected(service, I("Seed"), limit: 3);
        Assert.Equal(3, exact.ReturnedCount);
        Assert.Equal(3, exact.TotalCount);
        Assert.False(exact.Truncated);

        var cut = await Affected(service, I("Seed"), limit: 2);
        Assert.Equal(2, cut.ReturnedCount);
        Assert.Equal(3, cut.TotalCount);
        Assert.True(cut.Truncated);
    }

    [Fact]
    public async Task TheDepthLimit_IsReportedOnlyWhenADependentLiesBeyondIt()
    {
        var service = new GraphTestGraph()
            .Node("N0").Node("N1").Node("N2").Node("N3")
            .Calls("N1", "N0").Calls("N2", "N1").Calls("N3", "N2")
            .Build();

        var exact = await Affected(service, I("N0"), maxDepth: 3);
        Assert.Equal(3, exact.TotalCount);
        Assert.False(exact.DepthLimited);

        var limited = await Affected(service, I("N0"), maxDepth: 2);
        Assert.Equal(2, limited.TotalCount);
        Assert.True(limited.DepthLimited);
    }

    [Fact]
    public async Task TestFileDependents_AreCountedButListedOnlyWithTests()
    {
        var service = new GraphTestGraph()
            .Node("Seed").Node("Prod").Node("SeedTests.Check", file: "tests/SeedTests.cs")
            .Calls("Prod", "Seed").Calls("SeedTests.Check", "Seed")
            .Build();

        var hidden = await Affected(service, I("Seed"));
        Assert.Equal(["Prod@1"], Listed(hidden));
        Assert.Equal(1, hidden.TestsHidden);
        Assert.Equal(1, hidden.TotalCount);

        var listed = await Affected(service, I("Seed"), includeTests: true);
        Assert.Equal(["Prod@1", "SeedTests.Check@1"], Listed(listed));
        Assert.Equal(0, listed.TestsHidden);
    }

    [Fact]
    public async Task WithTestsExcluded_ImpactDoesNotMoveThroughATestDouble()
    {
        // StubSaver.Save (a test double) touches Seed and implements ISaver.Save, which production code calls: the
        // call reaches Seed only by dispatching to the stub, so with test files excluded it is not affected, as no path
        // from it to Seed passes through test files either.
        var service = new GraphTestGraph()
            .Node("Seed").Node("StubSaver.Save", file: "tests/StubSaver.cs").Node("ISaver.Save").Node("Service.Run")
            .Node("StubTests.Check", file: "tests/StubTests.cs")
            .Calls("StubSaver.Save", "Seed").Edge("StubSaver.Save", "ISaver.Save", "IMPLEMENTS_MEMBER")
            .Calls("Service.Run", "ISaver.Save").Calls("StubTests.Check", "StubSaver.Save")
            .Build();

        var hidden = await Affected(service, I("Seed"));
        Assert.Empty(hidden.Items);
        Assert.Equal(1, hidden.TestsHidden);
        Assert.False((await service.FindPathAsync(new GraphPathRequest { From = I("Service.Run"), To = I("Seed") })).Found);

        var listed = await Affected(service, I("Seed"), includeTests: true);
        Assert.Equal(["StubSaver.Save@1", "Service.Run@2", "StubTests.Check@2"], Listed(listed));
    }

    [Fact]
    public async Task ATypeSeed_IsItsMembersToo_WhichAreNotListedAsAffected()
    {
        var service = new GraphTestGraph()
            .Node("Svc", "Class").Node("Svc.Run").Node("UsesType").Node("CallsMember")
            .Edge("Svc", "Svc.Run", "HAS_METHOD").Edge("UsesType", "Svc", "REFERENCES").Edge("CallsMember", "Svc.Run")
            .Build();

        var response = await Affected(service, I("Svc"));

        Assert.Equal(["CallsMember@1", "UsesType@1"], Listed(response));
        Assert.Equal(2, response.Seed.SeedCount);
    }

    [Fact]
    public async Task OutOfRangeLimitsAreRefused()
    {
        var service = new GraphTestGraph().Node("A").Build();

        Assert.Equal("limit must be between 1 and 100 (got 0).",
            (await Assert.ThrowsAsync<ArgumentException>(() => Affected(service, "A", limit: 0))).Message);
        Assert.Equal("maxDepth must be between 1 and 20 (got 21).",
            (await Assert.ThrowsAsync<ArgumentException>(() => Affected(service, "A", maxDepth: 21))).Message);
        Assert.Equal("identifier must be a non-empty identifier, name or file path.",
            (await Assert.ThrowsAsync<ArgumentException>(() => Affected(service, " "))).Message);
    }
}
