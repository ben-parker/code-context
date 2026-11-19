using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Xunit;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// What a query allocates on the way to its answer. A tests query looks at every method of every file whose
/// path says "test", and every hop of a traversal looks a node up by id, so a string or an enumerator per
/// look adds up to megabytes per query; the answers themselves are pinned by the other suites.
/// </summary>
public class QueryAllocationTests
{
    private const string Parser = "csharp";

    private static Dictionary<string, string> Owned(string workspace)
        => new() { ["parserId"] = Parser, ["workspaceId"] = workspace };

    [Fact]
    public void NodeLookupById_AllocatesNothing()
    {
        var database = new InMemoryDatabase();
        database.ReplaceAll(
            [
                new CodeNode { Id = "csharp:a:One", Name = "One", Metadata = Owned("a") },
                new CodeNode { Id = "csharp:b:Two", Name = "Two", Metadata = Owned("b") },
            ],
            []);
        Assert.Equal(2, database.ShardKeys.Count);
        Assert.True(database.TryGetNode("csharp:b:Two", out _));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var found = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (database.TryGetNode("csharp:b:Two", out _)) found++;
            if (database.TryGetNode("csharp:b:Missing", out _)) found++;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1000, found);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task TestsQuery_OverManyTestFiles_AllocatesWithinBound()
    {
        var (service, _) = Build(testFiles: 150, methodsPerFile: 30);
        var warm = await service.GetCompactContextAsync("Widget", includeTests: true);
        Assert.True(warm.Matches[0].Testing!.IsTested);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var response = await service.GetCompactContextAsync("Widget", includeTests: true);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(warm.Matches[0].Testing!.TestFileCount, response.Matches[0].Testing!.TestFileCount);
        // 4,500 test methods with 100-char signatures, each a caller: about 9.6 MB, most of it the traversal
        // over 4,500 callers. Lower-casing each name and signature into strings, and thirteen name patterns
        // per file, brought it to 19 MB.
        Assert.True(allocated < 12_000_000, $"The tests query allocated {allocated:N0} bytes.");
    }

    [Fact]
    public async Task FileRelationships_OfAFileWithManyEdgesToFewFiles_AllocateWithinBound()
    {
        var (service, _) = Build(testFiles: 150, methodsPerFile: 30);
        await service.GetCompactContextAsync("Widget");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var response = await service.GetCompactContextAsync("Widget");
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Widget is called from 4,500 methods in 150 files: one display path per file, not per edge
        // (about 4.0 MB; 5.2 MB with a path per edge).
        Assert.Equal(150, response.Matches[0].Relationships.FileDependentsCount);
        Assert.True(allocated < 4_600_000, $"The query allocated {allocated:N0} bytes.");
    }

    [Theory]
    [InlineData("SHOULD_Work", "void SHOULD_Work()")]
    [InlineData("Works", "[FACT] void Works()")]
    [InlineData("Works", "void Works()")]
    [InlineData("GİVEN_Something", "void M()")]
    [InlineData("Kelvin", "[İTEST] void M()")]
    [InlineData("Run_SHOULD_Pass", "void M()")]
    public async Task TestMethodNames_AreJudgedByTheirInvariantLowerCase(string name, string signature)
    {
        // The rules are written over ToLowerInvariant of the name and the signature, which is not the
        // same relation as an ordinal case-insensitive comparison for every character.
        var lowerName = name.ToLowerInvariant();
        var lowerSignature = signature.ToLowerInvariant();
        var expected = lowerName.StartsWith("test") || lowerName.EndsWith("test") || lowerName.EndsWith("tests")
            || lowerName.StartsWith("should") || lowerName.StartsWith("can") || lowerName.StartsWith("given")
            || lowerName.StartsWith("when") || lowerName.StartsWith("then") || lowerName.Contains("_should_")
            || lowerSignature.Contains("[test") || lowerSignature.Contains("[fact") || lowerSignature.Contains("[theory")
            || lowerSignature.Contains("[testmethod") || lowerSignature.Contains("[testcase");

        var database = new InMemoryDatabase();
        var target = new CodeNode { Id = "csharp:w:Widget", Identifier = "csharp:Widget", Name = "Widget", Type = "Class", FilePath = "/repo/src/Widget.cs", Metadata = Owned("w") };
        var method = new CodeNode { Id = "csharp:w:T.M", Identifier = "csharp:T.M", Name = name, Type = "Method", FilePath = "/repo/tests/WidgetTests.cs", Signature = signature, Metadata = Owned("w") };
        database.ReplaceAll([target, method], []);
        var service = new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database), new InMemoryFileMetadataRepository());

        var response = await service.GetCompleteContextAsync("Widget", exact: true);

        var listed = response.Matches[0].Testing.TestFiles.SelectMany(file => file.TestMethods).Any(node => node.Id == method.Id);
        Assert.Equal(expected, listed);
    }

    /// <summary>A class called from every method of <paramref name="testFiles"/> test files.</summary>
    private static (ContextService Service, InMemoryDatabase Database) Build(int testFiles, int methodsPerFile)
    {
        var metadata = Owned("w");
        var nodes = new List<CodeNode>
        {
            new() { Id = "csharp:w:App.Widget", Identifier = "csharp:App.Widget", Name = "Widget", Type = "Class", FilePath = "/repo/src/Widget.cs", Metadata = metadata },
        };
        var edges = new List<CodeEdge>();
        for (var file = 0; file < testFiles; file++)
        {
            var path = $"/repo/tests/Area{file % 7}/Feature{file}Tests.cs";
            for (var m = 0; m < methodsPerFile; m++)
            {
                var id = $"csharp:w:App.Tests.Feature{file}Tests.Handles_Case{m}_AsExpected()";
                nodes.Add(new CodeNode
                {
                    Id = id, Identifier = id[9..], Name = $"Handles_Case{m}_AsExpected", Type = "Method", FilePath = path, StartLine = 10 * m,
                    Signature = $"[Fact] public async Task Handles_Case{m}_AsExpected(string name, int count, CancellationToken cancellationToken)",
                    Metadata = metadata,
                });
                edges.Add(new CodeEdge { Id = $"{id}=[CALLS]=>csharp:w:App.Widget@{m}", SourceId = id, TargetId = "csharp:w:App.Widget", Type = "CALLS", Metadata = metadata });
            }
        }
        var database = new InMemoryDatabase();
        database.ReplaceAll(nodes, edges);
        return (new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database), new InMemoryFileMetadataRepository()), database);
    }
}
