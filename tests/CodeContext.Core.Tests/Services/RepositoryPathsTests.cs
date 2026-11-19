using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// One test-file rule for every query, judged on the part of the path under the root: where a checkout lives never
/// makes its production code test code.
/// </summary>
public class RepositoryPathsTests
{
    [Theory]
    [InlineData("/home/u/tests/repo/src/Widget.cs", "/home/u/tests/repo", false)]
    [InlineData("/home/u/tests/repo/tests/WidgetTests.cs", "/home/u/tests/repo", true)]
    [InlineData("/home/u/tests/repo/src/widget.spec.ts", "/home/u/tests/repo/", true)]
    [InlineData("/elsewhere/tests/Widget.cs", "/home/u/repo", true)]
    [InlineData("src/Widget.cs", null, false)]
    [InlineData("tests/Widget.cs", null, true)]
    [InlineData(null, "/repo", false)]
    public void IsTestFile_LooksOnlyBelowTheRoot(string? path, string? root, bool expected)
        => Assert.Equal(expected, RepositoryPaths.IsTestFile(path, root));

    [Theory]
    [InlineData("/repo/src/Core/A.cs", "src/Core", true)]
    [InlineData("/repo/src/Core", "src/Core", true)]
    [InlineData("/repo/src/CoreExtra/A.cs", "src/Core", false)]
    [InlineData("/repo/src/core/a.cs", "src/Core/A.cs", true)]
    [InlineData("/repo/src\\Core\\A.cs", "src/Core", true)]
    [InlineData("/repo/lib/A.cs", "", true)]
    public void IsWithin_MatchesWholeSegments(string path, string scope, bool expected)
        => Assert.Equal(expected, RepositoryPaths.IsWithin(path, "/repo", scope));

    [Theory]
    [InlineData("  ./src/Core/ ", "src/Core")]
    [InlineData("src\\Core", "src/Core")]
    [InlineData("/repo/src", "src")]
    [InlineData("/repo", "")]
    [InlineData(".", "")]
    [InlineData(null, "")]
    public void NormalizeScope_IsRootRelativeWithForwardSlashes(string? scope, string expected)
        => Assert.Equal(expected, RepositoryPaths.NormalizeScope(scope, "/repo"));

    [Theory]
    [InlineData("/other/src")]
    [InlineData("src/../../x")]
    public void NormalizeScope_RefusesWhatLeavesTheRoot(string scope)
        => Assert.Throws<ArgumentException>(() => RepositoryPaths.NormalizeScope(scope, "/repo"));

    [Fact]
    public async Task TestEvidence_InARepositoryUnderATestsFolder_IsNotEveryCaller()
    {
        // The checkout lives under /work/tests/: a production caller must not count as test evidence for --tests,
        // and graph queries must not hide it as a test file.
        const string root = "/work/tests/app";
        var nodes = new[]
        {
            new CodeNode { Id = "w", Identifier = "csharp:App.Widget.Run()", Name = "Run", Type = "Method", FilePath = $"{root}/src/Widget.cs" },
            new CodeNode { Id = "p", Identifier = "csharp:App.Page.Render()", Name = "Render", Type = "Method", FilePath = $"{root}/src/Page.cs" },
        };
        var edges = new[] { new CodeEdge { Id = "p>w", SourceId = "p", TargetId = "w", Type = "CALLS" } };
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, nodes, edges, scope: null));
        var service = new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = root }));

        var context = await service.GetCompactContextAsync("csharp:App.Widget.Run()", includeTests: true);
        var testing = Assert.Single(context.Matches).Testing!;
        Assert.False(testing.IsTested);
        Assert.Equal(0, testing.TestReferenceCount);

        var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "csharp:App.Widget.Run()" });
        Assert.Equal(0, affected.TestsHidden);
        Assert.Equal(["csharp:App.Page.Render()"], affected.Items.Select(item => item.Identifier));
    }
}
