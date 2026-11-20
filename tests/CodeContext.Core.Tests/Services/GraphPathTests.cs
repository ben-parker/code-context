using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>Shortest paths on graphs small enough that the right answer is plain from the edges.</summary>
public class GraphPathTests
{
    private static string I(string name) => GraphTestGraph.Identifier(name);

    private static Task<GraphPathResponse> Path(ContextService service, string from, string to,
        int maxDepth = 10, string? relation = null, bool includeTests = false)
        => service.FindPathAsync(new GraphPathRequest
        {
            From = from, To = to, MaxDepth = maxDepth, Relation = relation, IncludeTests = includeTests,
        });

    private static string Render(GraphPathResponse response)
        => string.Join(" ", response.Path!.Select(hop => hop.Hop == "start" ? hop.Name : $"-{hop.Hop}:{hop.Relation}-> {hop.Name}"));

    [Fact]
    public async Task FindsTheShortestOfTwoChains_WithTheLineOfEachRelationship()
    {
        var service = new GraphTestGraph()
            .Node("A").Node("B").Node("C").Node("D").Node("E")
            .Edge("A", "D", line: 3).Edge("D", "E", line: 4).Edge("E", "C", line: 5)
            .Edge("A", "B", line: 9).Edge("B", "C", "REFERENCES", line: 11)
            .Build();

        var response = await Path(service, I("A"), I("C"));

        Assert.True(response.Found);
        Assert.Equal(2, response.Length);
        Assert.Equal("A -uses:CALLS-> B -uses:REFERENCES-> C", Render(response));
        Assert.Equal([null, 10, 12], response.Path!.Select(hop => hop.At));
        Assert.Equal(I("C"), response.Path![^1].Identifier);
        Assert.Equal("identity", response.From.MatchMode);
        Assert.Null(response.Explanation);
    }

    [Fact]
    public async Task ACallThroughAnInterface_ReachesWhatItsImplementationsCall()
    {
        var service = new GraphTestGraph()
            .Node("Caller").Node("IFoo.Run").Node("Foo.Run").Node("Target")
            .Edge("Caller", "IFoo.Run", line: 1)
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("Foo.Run", "Target", line: 7)
            .Build();

        var response = await Path(service, I("Caller"), I("Target"));

        Assert.True(response.Found);
        Assert.Equal("Caller -uses:CALLS-> Run -dispatch:IMPLEMENTS_MEMBER-> Run -uses:CALLS-> Target", Render(response));
        Assert.Equal(2, response.Length);
    }

    [Fact]
    public async Task AnInterfaceMemberOrType_AsTheStart_ReachesWhatItsImplementationsCall()
    {
        // Affected(Target) lists Caller through IFoo.Run, so a path from IFoo.Run (or IFoo) to Target must exist too:
        // an interface member has no body of its own, and a call to it runs an implementation.
        var service = new GraphTestGraph()
            .Node("IFoo", "Interface").Node("IFoo.Run").Node("Foo.Run").Node("Target")
            .Edge("IFoo", "IFoo.Run", "HAS_METHOD")
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("Foo.Run", "Target", line: 7)
            .Build();

        var member = await Path(service, I("IFoo.Run"), I("Target"));
        var type = await Path(service, I("IFoo"), I("Target"));

        Assert.True(member.Found, member.Explanation);
        Assert.Equal("Run -dispatch:IMPLEMENTS_MEMBER-> Run -uses:CALLS-> Target", Render(member));
        Assert.Equal(1, member.Length);
        Assert.True(type.Found, type.Explanation);
        Assert.Equal("IFoo -member:HAS_METHOD-> Run -dispatch:IMPLEMENTS_MEMBER-> Run -uses:CALLS-> Target", Render(type));
    }

    [Fact]
    public async Task AStaticallyBoundBaseCall_ReachesTheNamedMemberAlone()
    {
        // Derived.Run calls base.Run(); Other.Run overrides Base.Run and is the only way on to Target.
        var service = new GraphTestGraph()
            .Node("Derived.Run").Node("Base.Run").Node("Other.Run").Node("Target")
            .Edge("Derived.Run", "Base.Run", staticCall: true)
            .Edge("Other.Run", "Base.Run", "OVERRIDES_MEMBER")
            .Edge("Derived.Run", "Base.Run", "OVERRIDES_MEMBER")
            .Edge("Other.Run", "Target")
            .Build();

        var response = await Path(service, I("Derived.Run"), I("Target"));

        Assert.False(response.Found);
        Assert.Null(response.ExcludedBy);
        Assert.Contains("nothing recorded leads", response.Explanation);
        Assert.True((await Path(service, I("Derived.Run"), I("Base.Run"))).Found);
    }

    [Fact]
    public async Task TypeEndpoints_StandForTheirMembers_AndThePathSaysSo()
    {
        var service = new GraphTestGraph()
            .Node("A", "Class").Node("A.M").Node("B", "Class").Node("B.N")
            .Edge("A", "A.M", "HAS_METHOD").Edge("B", "B.N", "HAS_METHOD")
            .Edge("A.M", "B.N", line: 2)
            .Build();

        var response = await Path(service, I("A"), I("B"));

        Assert.True(response.Found);
        Assert.Equal(1, response.Length);
        Assert.Equal("A -member:HAS_METHOD-> M -uses:CALLS-> N -memberOf:HAS_METHOD-> B", Render(response));
        Assert.Equal(2, response.From.SeedCount);
    }

    [Fact]
    public async Task AUseOfAType_DoesNotReachWhatItsMembersCall()
    {
        var service = new GraphTestGraph()
            .Node("X").Node("T", "Class").Node("T.M").Node("Y")
            .Edge("X", "T", "REFERENCES").Edge("T", "T.M", "HAS_METHOD").Edge("T.M", "Y")
            .Build();

        Assert.False((await Path(service, I("X"), I("Y"))).Found);
    }

    [Fact]
    public async Task ARelationFilterThatHidesThePath_IsNamedAsTheReason()
    {
        var service = new GraphTestGraph()
            .Node("A").Node("B").Node("C")
            .Edge("A", "B", "REFERENCES").Edge("B", "C")
            .Build();

        var filtered = await Path(service, I("A"), I("C"), relation: "CALLS");

        Assert.False(filtered.Found);
        Assert.Equal(["relation"], filtered.ExcludedBy);
        Assert.Contains("relation=CALLS", filtered.Explanation);
        Assert.Contains("one of 2 hop(s) exists without the relation filter", filtered.Explanation);
        Assert.DoesNotContain("nothing recorded", filtered.Explanation);
        Assert.True((await Path(service, I("A"), I("C"), relation: "CALLS,REFERENCES")).Found);
    }

    [Fact]
    public async Task APathThroughATestFile_IsExcludedByDefault_AndTheExplanationSaysWhy()
    {
        var service = new GraphTestGraph()
            .Node("A").Node("Helper", file: "tests/Helpers.cs").Node("C")
            .Edge("A", "Helper").Edge("Helper", "C")
            .Build();

        var excluded = await Path(service, I("A"), I("C"));
        Assert.False(excluded.Found);
        Assert.Equal(["tests"], excluded.ExcludedBy);
        Assert.Contains("test files included", excluded.Explanation);

        var included = await Path(service, I("A"), I("C"), includeTests: true);
        Assert.True(included.Found);
        Assert.Equal(2, included.Length);

        // An endpoint the caller named may be a test.
        Assert.True((await Path(service, I("A"), I("Helper"))).Found);
    }

    [Fact]
    public async Task BothFilters_NeededTogether_AreBothNamed()
    {
        // The only path is a REFERENCES hop into a test helper: either filter alone hides it.
        var service = new GraphTestGraph()
            .Node("A").Node("Helper", file: "tests/Helpers.cs").Node("C")
            .Edge("A", "Helper", "REFERENCES").Edge("Helper", "C")
            .Build();

        var response = await Path(service, I("A"), I("C"), relation: "CALLS");

        Assert.False(response.Found);
        Assert.Equal(["relation", "tests"], response.ExcludedBy);
        Assert.Contains("one of 2 hop(s) exists without the relation filter and with test files included", response.Explanation);
    }

    [Fact]
    public async Task EitherFilter_HidingAPathOfItsOwn_IsNotSaidToNeedTheOther()
    {
        // The shortest chain needs both filters gone (a REFERENCES hop into a test helper), but each filter alone
        // hides a longer chain of its own: one with a REFERENCES hop, one through test files.
        var service = new GraphTestGraph()
            .Node("A").Node("Helper", file: "tests/Helpers.cs").Node("C")
            .Edge("A", "Helper", "REFERENCES").Edge("Helper", "C")
            .Node("B").Node("B2").Edge("A", "B", "REFERENCES").Edge("B", "B2").Edge("B2", "C")
            .Node("T1", file: "tests/T.cs").Node("T2", file: "tests/T.cs").Node("T3", file: "tests/T.cs")
            .Edge("A", "T1").Edge("T1", "T2").Edge("T2", "T3").Edge("T3", "C")
            .Build();

        var response = await Path(service, I("A"), I("C"), relation: "CALLS");

        Assert.False(response.Found);
        Assert.Equal(["relation", "tests"], response.ExcludedBy);
        Assert.Contains("one of 3 hop(s) exists without the relation filter, and one of 4 hop(s) with test files included", response.Explanation);
        Assert.Contains("either change finds a path", response.Explanation);
    }

    [Fact]
    public async Task WithBothFiltersGiven_TheOneTheNearestPathNeedsDropped_IsNamedAlone()
    {
        // Two chains: the shorter passes through a test file, the longer has a REFERENCES hop. The nearest path is
        // hidden by the test filter alone, and that is what the explanation names.
        var service = new GraphTestGraph()
            .Node("A").Node("B").Node("B2").Node("Helper", file: "tests/Helpers.cs").Node("C")
            .Edge("A", "B", "REFERENCES").Edge("B", "B2").Edge("B2", "C")
            .Edge("A", "Helper").Edge("Helper", "C")
            .Build();

        var response = await Path(service, I("A"), I("C"), relation: "CALLS");

        Assert.False(response.Found);
        Assert.Equal(["tests"], response.ExcludedBy);
        Assert.Contains("one of 2 hop(s) exists with test files included", response.Explanation);

        // And the answer is true: that change alone finds the path.
        var withTests = await Path(service, I("A"), I("C"), relation: "CALLS", includeTests: true);
        Assert.True(withTests.Found);
        Assert.Equal(2, withTests.Length);
    }

    [Fact]
    public async Task TheDepthLimit_IsReportedOnlyWhenSomethingLiesBeyondIt()
    {
        var service = new GraphTestGraph()
            .Node("N0").Node("N1").Node("N2").Node("N3").Node("N4").Node("Island")
            .Calls("N0", "N1").Calls("N1", "N2").Calls("N2", "N3").Calls("N3", "N4")
            .Build();

        var limited = await Path(service, I("N0"), I("N4"), maxDepth: 3);
        Assert.False(limited.Found);
        Assert.True(limited.DepthLimited);
        Assert.Contains("raise maxDepth", limited.Explanation);

        var exact = await Path(service, I("N0"), I("N4"), maxDepth: 4);
        Assert.True(exact.Found);
        Assert.Equal(4, exact.Length);

        // Everything reachable from N0 lies within 4 hops: a miss at that limit is not a depth problem.
        var unreachable = await Path(service, I("N0"), I("Island"), maxDepth: 4);
        Assert.False(unreachable.Found);
        Assert.False(unreachable.DepthLimited);
    }

    [Fact]
    public async Task ADepthLimitReachedOnlyWithoutTheFilters_IsNotBlamedOnTheFilteredSearch()
    {
        // With test files excluded nothing lies beyond A; through the test helper, the path is 5 hops, over the limit.
        var service = new GraphTestGraph()
            .Node("A").Node("Helper", file: "tests/Helpers.cs").Node("N1").Node("N2").Node("N3").Node("C")
            .Calls("A", "Helper").Calls("Helper", "N1").Calls("N1", "N2").Calls("N2", "N3").Calls("N3", "C")
            .Build();

        var response = await Path(service, I("A"), I("C"), maxDepth: 3);

        Assert.False(response.Found);
        Assert.False(response.DepthLimited);
        Assert.Null(response.ExcludedBy);
        // Raising maxDepth alone finds nothing: it takes test files too.
        Assert.False((await Path(service, I("A"), I("C"), maxDepth: 20)).Found);
        Assert.DoesNotContain("raise maxDepth", response.Explanation);
        Assert.Contains("with test files included (includeTests, --tests) and a larger maxDepth", response.Explanation);
    }

    [Fact]
    public async Task APathTheOtherWayRound_IsPointedOut()
    {
        var service = new GraphTestGraph().Node("A").Node("B").Calls("B", "A").Build();

        var response = await Path(service, I("A"), I("B"));

        Assert.False(response.Found);
        Assert.Contains("swap from and to", response.Explanation);
    }

    [Fact]
    public async Task AMissingOrAmbiguousEndpoint_SearchesNothing_AndSaysWhichAndWhy()
    {
        var service = new GraphTestGraph()
            .Node("A").Node("X.Run").Node("Y.Run")
            .Build();

        var missing = await Path(service, I("A"), "Nowhere");
        Assert.False(missing.Found);
        Assert.Equal(0, missing.To.TotalMatches);
        Assert.Contains("exactly", missing.To.Hint);
        Assert.StartsWith("to did not resolve", missing.Explanation);

        var ambiguous = await Path(service, "Run", I("A"));
        Assert.True(ambiguous.From.Ambiguous);
        Assert.Equal(2, ambiguous.From.TotalMatches);
        Assert.Equal([I("X.Run"), I("Y.Run")], ambiguous.From.Candidates!.Select(node => node.Identifier).Order());
        Assert.Null(ambiguous.Path);
        // Graph queries take no narrowing filters, so the hint must not suggest any.
        Assert.Equal("'Run' names 2 symbols. Pass one of the candidates' identifiers unchanged.", ambiguous.From.Hint);
    }

    [Fact]
    public async Task ASubstringIsNotGuessed()
    {
        var service = new GraphTestGraph().Node("ContextService").Node("B").Build();

        var response = await Path(service, "ContextServ", I("B"));

        Assert.Equal(0, response.From.TotalMatches);
    }

    [Fact]
    public async Task TheSameSymbolAtBothEnds_IsAPathOfNoHops()
    {
        var service = new GraphTestGraph().Node("A").Build();

        var response = await Path(service, I("A"), "A");

        Assert.True(response.Found);
        Assert.Equal(0, response.Length);
        Assert.Single(response.Path!);
    }

    [Fact]
    public async Task AFilePathEndpoint_StandsForEverySymbolInTheFile()
    {
        var service = new GraphTestGraph()
            .Node("A", file: "src/One.cs").Node("B", file: "src/Two.cs").Node("C", file: "src/Two.cs")
            .Calls("A", "C")
            .Build();

        var response = await Path(service, "src/One.cs", "src/Two.cs");

        Assert.True(response.Found);
        Assert.Equal("src/Two.cs", response.To.File);
        Assert.Equal(2, response.To.SeedCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task ADepthOutOfRange_IsRefused(int maxDepth)
    {
        var service = new GraphTestGraph().Node("A").Build();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Path(service, "A", "A", maxDepth: maxDepth));
        Assert.Equal($"maxDepth must be between 1 and 20 (got {maxDepth}).", error.Message);
    }

    [Fact]
    public async Task AnUnknownRelation_IsRefused()
    {
        var service = new GraphTestGraph().Node("A").Build();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Path(service, "A", "A", relation: "CALL"));
        Assert.Contains("Unknown relation 'CALL'", error.Message);
    }
}
