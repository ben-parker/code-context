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
    // Directories test runners and project layouts use.
    [InlineData("test/widget.js", true)]
    [InlineData("Tests/Widget.cs", true)]
    [InlineData("spec/widget.rb", true)]
    [InlineData("specs/widget.rb", true)]
    [InlineData("src/__tests__/widget.ts", true)]
    [InlineData("src/main/test/java/Widget.java", true)]
    [InlineData("src/Widget.Tests/Helpers.cs", true)]
    [InlineData("src/Widget.UnitTests/Helpers.cs", true)]
    [InlineData("src\\Widget.IntegrationTests\\Helpers.cs", true)]
    [InlineData("e2e-tests/login.ts", true)]
    [InlineData("WidgetTests/Helpers.cs", true)]
    [InlineData("pkg/parse/testdata/input.json", true)]
    [InlineData("pkg/TestData/input.json", true)]
    [InlineData("unittests/widget.cpp", true)]
    [InlineData("src/UnitTests/Widget.cs", true)]
    [InlineData("src/__mocks__/fs.js", true)]
    [InlineData("src\\__mocks__\\fs.js", true)]
    // File names.
    [InlineData("src/widget.test.ts", true)]
    [InlineData("src/widget.spec.tsx", true)]
    [InlineData("src/widget.e2e-spec.ts", true)]
    [InlineData("src/widget.test-d.ts", true)]
    [InlineData("src/WidgetTests.cs", true)]
    [InlineData("src/WidgetTest.cs", true)]
    [InlineData("src/WidgetTests.Helpers.cs", true)]
    [InlineData("pkg/widget_test.go", true)]
    [InlineData("pkg/test_widget.py", true)]
    [InlineData("lib/widget_spec.rb", true)]
    [InlineData("src/widget-test.js", true)]
    [InlineData("test.js", true)]
    [InlineData("conftest.py", true)]
    [InlineData("pkg/conftest.py", true)]
    [InlineData("cypress/e2e/login.cy.ts", true)]
    [InlineData("login.cy.js", true)]
    [InlineData("src/Login.CY.tsx", true)]
    [InlineData("src/admin.login.cy.ts", true)]
    // Near the additions, and kept production: the directory names count only whole, and cy only as a middle
    // file-name piece between dots.
    [InlineData("src/ab-testing/x.ts", false)]
    [InlineData("src/testing/harness.ts", false)]
    [InlineData("testdatabase/schema.sql", false)]
    [InlineData("src/testdata.json", false)]
    [InlineData("src/my-testdata/x.json", false)]
    [InlineData("src/unittests.py", false)]
    [InlineData("src/unittest/x.py", false)]
    [InlineData("mockser/a.ts", false)]
    [InlineData("src/mocks/a.ts", false)]
    [InlineData("src/__mock__/a.ts", false)]
    [InlineData("src/__mocks__.ts", false)]
    [InlineData("src/__mocks__x/a.ts", false)]
    [InlineData("cy.ts", false)]
    [InlineData("src/cy/a.ts", false)]
    [InlineData("src/a.cy/b.ts", false)]
    [InlineData("src/login.cy", false)]
    [InlineData("src/.cy.ts", false)]
    [InlineData("src/login-cy.ts", false)]
    [InlineData("src/login_cy.ts", false)]
    [InlineData("src/login.cy-old.ts", false)]
    [InlineData("src/login.cyp.ts", false)]
    [InlineData("cypress/support/commands.ts", false)]
    [InlineData("src/Fancy.cs", false)]
    // Deliberately not tests: a camel-case Spec suffix and a Test prefix.
    [InlineData("src/WidgetSpec.cs", false)]
    [InlineData("src/TestRunner.cs", false)]
    // Words that only contain the letters, and what production code names so.
    [InlineData("src/Workers/WorkerLaunchSpec.cs", false)]
    [InlineData("src/Inspector.cs", false)]
    [InlineData("src/inspector/latest.ts", false)]
    [InlineData("src/Latest.cs", false)]
    [InlineData("src/Aspects/Aspect.cs", false)]
    [InlineData("src/Contest.cs", false)]
    [InlineData("src/contests/Entry.cs", false)]
    [InlineData("src/Specification/Rule.cs", false)]
    [InlineData("src/TestConnectionCommand.cs", false)]
    [InlineData("src/Attestation.cs", false)]
    [InlineData("src/respects.ts", false)]
    [InlineData("src/Widget.cs", false)]
    [InlineData("src/widget.spec", false)]
    [InlineData("src/.test", true)]
    [InlineData("", false)]
    public void IsTestFile_FollowsNamingConventions_NotSubstrings(string path, bool expected)
        => Assert.Equal(expected, RepositoryPaths.IsTestFile(path, null));

    [Fact]
    public void IsTestFile_AllocatesNothing()
    {
        string[] paths =
        [
            "/repo/src/CodeContext.Core/Services/Widgets/WidgetLaunchSpecification.cs",
            "/repo/src/__mocks__x/testdatabase/unittest/login.cy-old.ts",
            "/repo/src/__mocks__/fs.js",
            "/repo/pkg/testdata/input.json",
            "/repo/cypress/e2e/login.cy.ts",
        ];
        foreach (var path in paths) RepositoryPaths.IsTestFile(path, "/repo");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var tests = 0;
        for (var i = 0; i < 1000; i++)
        {
            foreach (var path in paths)
            {
                if (RepositoryPaths.IsTestFile(path, "/repo")) tests++;
            }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(3000, tests);
    }

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
