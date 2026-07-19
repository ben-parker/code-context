using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

public class MethodFamilyContextTests
{
    [Fact]
    public async Task MethodUsedBy_UnifiesFamilyCallersButUsesSelectedBodyOnly()
    {
        var nodes = new[]
        {
            Node("i", "csharp:Example.IService.Run()", "Run", "Method"),
            Node("p1", "csharp:Example.Service.Run()", "Run", "Method"),
            Node("p2", "csharp:Example.OtherService.Run()", "Run", "Method"),
            Node("c1", "csharp:Example.CallInterface()", "CallInterface", "Method"),
            Node("c2", "csharp:Example.CallImplementation()", "CallImplementation", "Method"),
            Node("shared", "csharp:Example.CallBoth()", "CallBoth", "Method"),
            Node("higher", "csharp:Example.Higher()", "Higher", "Method"),
            Node("selected-dep", "csharp:Example.SelectedDependency()", "SelectedDependency", "Method"),
            Node("other-dep", "csharp:Example.OtherDependency()", "OtherDependency", "Method"),
        };
        var edges = new[]
        {
            Edge("p1", "i", "IMPLEMENTS_MEMBER"), Edge("p2", "i", "IMPLEMENTS_MEMBER"),
            Edge("c1", "i", "CALLS", 10), Edge("c1", "i", "CALLS", 11),
            Edge("c2", "p1", "CALLS", 20),
            Edge("shared", "i", "CALLS", 30), Edge("shared", "p2", "CALLS", 31),
            Edge("higher", "c2", "CALLS", 40),
            Edge("p1", "selected-dep", "CALLS", 50),
            Edge("p2", "other-dep", "CALLS", 60),
        };
        var service = CreateService(nodes, edges);

        var result = await service.GetCompactContextAsync(
            "csharp:Example.Service.Run()", depth: 2, maxRelationships: 10);
        Assert.Equal("identity", result.MatchMode);
        var match = Assert.Single(result.Matches);
        Assert.Equal("csharp:Example.Service.Run()", match.Target.Identifier);

        var relationships = match.Relationships!;
        Assert.Equal(["SelectedDependency"], relationships.Uses!.Select(node => node.Name));
        Assert.DoesNotContain(relationships.Uses!, node => node.Name == "OtherDependency");
        Assert.Equal(3, relationships.UsedByCount);
        Assert.Equal(
            ["CallBoth", "CallImplementation", "CallInterface"],
            relationships.UsedBy!.Select(node => node.Name).Order());

        var interfaceCaller = relationships.UsedBy!.Single(node => node.Name == "CallInterface");
        Assert.Equal(2, interfaceCaller.Occurrences);
        Assert.Equal(["interface"], interfaceCaller.Bindings);
        var shared = relationships.UsedBy!.Single(node => node.Name == "CallBoth");
        Assert.Equal(["implementation", "interface"], shared.Bindings);

        var transitive = Assert.Single(relationships.TransitiveUsedBy!);
        Assert.Equal("Higher", transitive.Name);
        Assert.Equal(2, transitive.Distance);
    }

    [Fact]
    public async Task TestCaps_AreIndependentAndSupportCountOnly()
    {
        var target = Node("target", "csharp:Example.Target.Run()", "Run", "Method");
        var tests = Enumerable.Range(0, 3).SelectMany(file => Enumerable.Range(0, 4).Select(method =>
        {
            var node = Node($"test-{file}-{method}", $"csharp:Tests.F{file}.M{method}()", $"M{method}", "Method");
            node.FilePath = $"tests/F{file}Tests.cs";
            node.Metadata = new Dictionary<string, string> { ["isTest"] = "true" };
            return node;
        })).ToArray();
        var edges = tests.Select((test, line) => Edge(test.Id!, target.Id!, "CALLS", line)).ToArray();
        var service = CreateService([target, .. tests], edges);

        var counted = await service.GetCompactContextAsync(
            target.Identifier!, includeTests: true, maxRelationships: 1,
            maxTestFiles: 2, maxTestMethods: 0);
        var testing = Assert.Single(counted.Matches).Testing!;
        Assert.Equal(3, testing.TestFileCount);
        Assert.Equal(2, testing.TestFilesReturnedCount);
        Assert.True(testing.TestFilesTruncated);
        Assert.All(testing.TestFiles, file =>
        {
            Assert.Equal(4, file.TestCount);
            Assert.Equal(0, file.TestMethodsReturnedCount);
            Assert.Empty(file.TestMethods);
            Assert.True(file.TestMethodsTruncated);
        });

        var full = await service.GetCompleteContextAsync(
            target.Identifier!, includeTests: true, includeRelated: false, includeMetrics: false,
            maxTestFiles: 1, maxTestMethods: 0);
        var fullTesting = Assert.Single(full.Matches).Testing;
        Assert.Equal(3, fullTesting.TestFileCount);
        Assert.Equal(1, fullTesting.TestFilesReturnedCount);
        Assert.True(fullTesting.TestFilesTruncated);
        var fullFile = Assert.Single(fullTesting.TestFiles);
        Assert.Equal(4, fullFile.TestCount);
        Assert.Equal(0, fullFile.TestMethodsReturnedCount);
        Assert.True(fullFile.TestMethodsTruncated);
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

    private static CodeNode Node(string id, string identifier, string name, string type) => new()
    {
        Id = id, Identifier = identifier, Name = name, Type = type, Signature = name + "()"
    };

    private static CodeEdge Edge(string source, string target, string type, int? line = null) => new()
    {
        Id = $"{source}-{type}-{target}-{line}", SourceId = source, TargetId = target, Type = type,
        Metadata = line is null ? null : new Dictionary<string, string> { ["line"] = line.Value.ToString() }
    };
}
