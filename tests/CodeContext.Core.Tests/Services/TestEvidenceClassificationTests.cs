using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// First real coverage of the test-evidence classifier in
/// <see cref="ContextService"/> (BuildTestingInfoAsync). Existing
/// TestMethodDetectionTests mock all edges empty, so evidence classes
/// (directCall, testReference, testImplementer, potentialDispatch,
/// indirectReference, namingHeuristic, and the new memberCall) were untested.
/// </summary>
public class TestEvidenceClassificationTests
{
    [Fact]
    public async Task ClassTarget_TestMethodCallsMember_SetsDirectlyTestedWithMemberCallEvidence()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var member = Node("m1", "csharp:Ns.Widget.DoWork()", "DoWork", "Method");
        var test = TestMethod("t1", "csharp:Tests.CallsWork()", "CallsWork", "/tests/WorkerTests.cs");
        var edges = new[]
        {
            Edge("cls", "m1", "HAS_METHOD"),
            Edge("t1", "m1", "CALLS", 10),
        };
        var service = CreateService([cls, member, test], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.True(testing.DirectlyTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("memberCall", testFile.Evidence);
        Assert.Contains(testFile.TestMethods, m => m.Name == "CallsWork");
    }

    [Fact]
    public async Task ClassTarget_TestOnlyReferencesClass_NotDirectlyTested_TestReferenceEvidence()
    {
        var cls = Node("cls", "csharp:Ns.Gadget", "Gadget", "Class");
        var test = TestMethod("t1", "csharp:Tests.ExercisesTarget()", "ExercisesTarget", "/tests/Foo.cs");
        var edges = new[] { Edge("t1", "cls", "REFERENCES", 5) };
        var service = CreateService([cls, test], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.True(testing.IsTested);
        Assert.Equal(1, testing.TestReferenceCount);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("testReference", testFile.Evidence);
        Assert.DoesNotContain("memberCall", testFile.Evidence);
    }

    [Fact]
    public async Task ClassTarget_MockCallsToMember_CountsAsMemberCall()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var member = Node("m1", "csharp:Ns.Widget.DoWork()", "DoWork", "Method");
        var test = TestMethod("t1", "csharp:Tests.MocksWork()", "MocksWork", "/tests/WorkerTests.cs");
        var edges = new[]
        {
            Edge("cls", "m1", "HAS_METHOD"),
            Edge("t1", "m1", "MOCK_CALLS", 12),
        };
        var service = CreateService([cls, member, test], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.True(testing.DirectlyTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("memberCall", testFile.Evidence);
    }

    [Fact]
    public async Task ClassTarget_NonTestMethodCallsMember_NoMemberCallEvidence()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var member = Node("m1", "csharp:Ns.Widget.DoWork()", "DoWork", "Method");
        // Lives in a test path but is not a test method (plain name + plain signature).
        var helper = Node("h1", "csharp:Tests.HelperMethod()", "HelperMethod", "Method",
            filePath: "/tests/Helpers.cs", signature: "public void HelperMethod()");
        var edges = new[]
        {
            Edge("cls", "m1", "HAS_METHOD"),
            Edge("h1", "m1", "CALLS", 8),
        };
        var service = CreateService([cls, member, helper], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.All(testing.TestFiles, file => Assert.DoesNotContain("memberCall", file.Evidence));
    }

    [Fact]
    public async Task ClassTarget_MemberCallDoesNotInflateTestReferenceCount()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var member = Node("m1", "csharp:Ns.Widget.DoWork()", "DoWork", "Method");
        var test = TestMethod("t1", "csharp:Tests.CallsWork()", "CallsWork", "/tests/WorkerTests.cs");
        var edges = new[]
        {
            Edge("cls", "m1", "HAS_METHOD"),
            Edge("t1", "m1", "CALLS", 10),
        };
        var service = CreateService([cls, member, test], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.True(testing.DirectlyTested);
        Assert.Contains(testing.TestFiles, file => file.Evidence.Contains("memberCall"));
        Assert.Equal(0, testing.TestReferenceCount);
    }

    [Fact]
    public async Task MethodTarget_DirectCall_SetsDirectlyTestedWithDirectCallEvidence()
    {
        var method = Node("m", "csharp:Ns.Svc.Run()", "Run", "Method");
        var test = TestMethod("t1", "csharp:Tests.InvokesRun()", "InvokesRun", "/tests/SvcTests.cs");
        var edges = new[] { Edge("t1", "m", "CALLS", 20) };
        var service = CreateService([method, test], edges);

        var result = await service.GetCompleteContextAsync(
            method.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.True(testing.DirectlyTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("directCall", testFile.Evidence);
        Assert.DoesNotContain("memberCall", testFile.Evidence);
    }

    [Fact]
    public async Task MethodTarget_FamilyDispatchCall_PotentialDispatchEvidence_NotDirectlyTested()
    {
        // Interface member i and implementation p1 form a family via IMPLEMENTS_MEMBER.
        // The test CALLS the interface member; the target is the implementation.
        var interfaceMember = Node("i", "csharp:Ns.ISvc.Run()", "Run", "Method");
        var impl = Node("p1", "csharp:Ns.Svc.Run()", "Run", "Method");
        var test = TestMethod("t1", "csharp:Tests.ExercisesService()", "ExercisesService", "/tests/DispatchTests.cs");
        var edges = new[]
        {
            Edge("p1", "i", "IMPLEMENTS_MEMBER"),
            Edge("t1", "i", "CALLS", 30),
        };
        var service = CreateService([interfaceMember, impl, test], edges);

        var result = await service.GetCompleteContextAsync(
            impl.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("potentialDispatch", testFile.Evidence);
    }

    [Fact]
    public async Task InterfaceTarget_TestImplementer_EvidenceAndCount()
    {
        var iface = Node("isvc", "csharp:Ns.ISvc", "ISvc", "Interface");
        var fake = Node("fake", "csharp:Tests.FakeSvc", "FakeSvc", "Class", filePath: "/tests/FakeSvc.cs");
        var edges = new[] { Edge("fake", "isvc", "IMPLEMENTS") };
        var service = CreateService([iface, fake], edges);

        var result = await service.GetCompleteContextAsync(
            iface.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.True(testing.IsTested);
        Assert.Equal(1, testing.TestImplementerCount);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("testImplementer", testFile.Evidence);
    }

    [Fact]
    public async Task Target_IndirectTestReachability_IndirectReferenceEvidence()
    {
        // test t1 -> A -> target (distance 2 via reverse traversal).
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var intermediate = Node("a", "csharp:Ns.Aggregator", "Aggregator", "Class", filePath: "/src/Aggregator.cs");
        var test = TestMethod("t1", "csharp:Tests.ExercisesAggregator()", "ExercisesAggregator", "/tests/AggTests.cs");
        var edges = new[]
        {
            Edge("a", "cls", "REFERENCES", 3),
            Edge("t1", "a", "CALLS", 4),
        };
        var service = CreateService([cls, intermediate, test], edges);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.Contains(testing.TestFiles, file => file.Evidence.Contains("indirectReference"));
    }

    [Fact]
    public async Task Target_NameMatchedFileOnly_NamingHeuristicEvidence()
    {
        // No graph edges: only a file named <Target>Tests.cs with a test method.
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var test = TestMethod("t1", "csharp:Tests.SomeScenario()", "SomeScenario", "/tests/WidgetTests.cs");
        var service = CreateService([cls, test], []);

        var result = await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.False(testing.DirectlyTested);
        Assert.True(testing.IsTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("namingHeuristic", testFile.Evidence);
    }

    [Fact]
    public async Task ClassTarget_MemberProbe_IsBounded()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var members = Enumerable.Range(0, 201)
            .Select(i => Node($"member{i}", $"csharp:Ns.Widget.M{i}()", $"M{i}", "Method"))
            .ToArray();
        var edges = members.Select(m => Edge("cls", m.Id!, "HAS_METHOD")).ToArray();
        var nodeRepository = Substitute.For<ICodeNodeRepository>();
        var edgeRepository = Substitute.For<ICodeEdgeRepository>();
        var files = Substitute.For<IFileMetadataRepository>();
        var allNodes = new[] { cls }.Concat(members).ToList();
        nodeRepository.GetAllAsync().Returns(allNodes);
        nodeRepository.StubFindByFilePathFromGetAll();
        nodeRepository.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            allNodes.SingleOrDefault(node => node.Id == call.Arg<string>()));
        nodeRepository.GetByIdentifierAsync(Arg.Any<string>()).Returns(call =>
            allNodes.SingleOrDefault(node => node.Identifier == call.Arg<string>()));
        edgeRepository.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.SourceId == call.ArgAt<string>(0)).ToList());
        edgeRepository.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.TargetId == call.ArgAt<string>(0)).ToList());
        var service = new ContextService(nodeRepository, edgeRepository, files);

        await service.GetCompleteContextAsync(
            cls.Identifier, includeTests: true, includeRelated: false, includeMetrics: false);

        // Exactly 200 distinct member ids probed for inbound calls (cap), not 201.
        await edgeRepository.Received(200).GetByTargetIdAsync(
            Arg.Is<string>(s => s.StartsWith("member")), Arg.Any<string?>());
    }

    [Fact]
    public async Task CompactMapping_MemberCallEvidence_SurfacesInCompactTestFile()
    {
        var cls = Node("cls", "csharp:Ns.Widget", "Widget", "Class");
        var member = Node("m1", "csharp:Ns.Widget.DoWork()", "DoWork", "Method");
        var test = TestMethod("t1", "csharp:Tests.CallsWork()", "CallsWork", "/tests/WorkerTests.cs");
        var edges = new[]
        {
            Edge("cls", "m1", "HAS_METHOD"),
            Edge("t1", "m1", "CALLS", 10),
        };
        var service = CreateService([cls, member, test], edges);

        var result = await service.GetCompactContextAsync(cls.Identifier, includeTests: true);

        var testing = Assert.Single(result.Matches).Testing;
        Assert.NotNull(testing);
        Assert.True(testing!.DirectlyTested);
        var testFile = Assert.Single(testing.TestFiles);
        Assert.Contains("memberCall", testFile.Evidence);
    }

    private static ContextService CreateService(CodeNode[] nodes, CodeEdge[] edges)
    {
        var nodeRepository = Substitute.For<ICodeNodeRepository>();
        var edgeRepository = Substitute.For<ICodeEdgeRepository>();
        var files = Substitute.For<IFileMetadataRepository>();
        nodeRepository.GetAllAsync().Returns(nodes.ToList());
        nodeRepository.StubFindByFilePathFromGetAll();
        nodeRepository.GetByIdAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Id == call.Arg<string>()));
        nodeRepository.GetByIdentifierAsync(Arg.Any<string>()).Returns(call =>
            nodes.SingleOrDefault(node => node.Identifier == call.Arg<string>()));
        nodeRepository.FindByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>()).Returns(call =>
            nodes.Where(node => string.Equals(node.Name, call.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)).ToList());
        edgeRepository.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.SourceId == call.ArgAt<string>(0)).ToList());
        edgeRepository.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            edges.Where(edge => edge.TargetId == call.ArgAt<string>(0)).ToList());
        return new ContextService(nodeRepository, edgeRepository, files);
    }

    private static CodeNode Node(
        string id, string identifier, string name, string type,
        string? filePath = null, string? signature = null) => new()
    {
        Id = id,
        Identifier = identifier,
        Name = name,
        Type = type,
        FilePath = filePath,
        Signature = signature ?? name + "()"
    };

    private static CodeNode TestMethod(string id, string identifier, string name, string filePath) => new()
    {
        Id = id,
        Identifier = identifier,
        Name = name,
        Type = "Method",
        FilePath = filePath,
        Signature = $"[Fact] public void {name}()"
    };

    private static CodeEdge Edge(string source, string target, string type, int? line = null) => new()
    {
        Id = $"{source}-{type}-{target}-{line}",
        SourceId = source,
        TargetId = target,
        Type = type,
        Metadata = line is null ? null : new Dictionary<string, string> { ["line"] = line.Value.ToString() }
    };
}
