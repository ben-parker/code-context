using System.Text.Json;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Commands;

/// <summary><c>query impact</c>: where it reads the diff from, what it refuses before asking, what it sends and prints.</summary>
public partial class GraphQueryCommandTests
{
    private const string ImpactDiff = "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-a\n+b\n";

    [Fact]
    public void Impact_BindsItsDiffSourceAndOnlyTheOptionsGiven()
    {
        var impact = Parse("query", "impact", "--diff", "change.patch", "--depth", "3", "--limit", "7", "--tests");
        Assert.Empty(impact.Identifiers);
        Assert.Equal(new GraphQuerySettings(GraphQueryParameters.ImpactQuery, MaxDepth: 3, Diff: "change.patch"), impact.Graph);
        Assert.Equal(["--depth", "--limit", "--diff"], impact.Given);
        Assert.Null(Parse("query", "impact").Graph!.Diff);
    }

    [Fact]
    public void ImpactHelp_ListsOnlyTheOptionsItReads_AndNoOtherHelpListsDiff()
    {
        var help = Help("query", "impact", "--help");
        foreach (var option in new[] { "--diff", "--depth", "--limit", "--tests", "--relation", "git diff <base> | codecontext query impact" })
            Assert.Contains(option, help);
        foreach (var option in new[] { "--exact", "--external", "--within", "--type", "[default: 1]" }) Assert.DoesNotContain(option, help);

        foreach (var command in new[] { "path", "affected", "hubs", "multi" }) Assert.DoesNotContain("--diff <diff>", Help("query", command, "--help"));
        Assert.DoesNotContain("--diff <diff>", Help("query", "--help"));
        Assert.Contains("--diff <diff>", help);
    }

    [Theory]
    [InlineData(new[] { "query", "affected", "X", "--diff", "a.patch" }, "--diff does not apply to query affected: it applies only to query impact.")]
    [InlineData(new[] { "query", "impact", "--within", "src" }, "--within does not apply to query impact: it applies only to query hubs.")]
    [InlineData(new[] { "query", "impact", "--exact" }, "--exact does not apply to query impact: graph queries always match identifiers exactly (canonical identifier, exact or qualified name, or file path).")]
    [InlineData(new[] { "query", "impact", "--depth", "0" }, "--depth must be between 1 and 20 for query impact (got 0).")]
    public async Task Impact_InapplicableOptions_AreRefusedBeforeAnythingIsAsked(string[] args, string message)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("nothing should be asked"));
        var (runtime, output, error) = Runtime(handler, ImpactDiff);

        var exit = await QueryCommandHandler.ExecuteAsync(Parse(args), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(message, error.ToString().Trim());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null, false, "query impact reads a unified diff from standard input or --diff FILE, and none was given")]
    [InlineData("", true, "The diff from standard input is refused: diff is empty")]
    [InlineData("@@ -1 +1 @@\n-a\n+b\n", true, "The diff from standard input is refused: diff line 1: a hunk with no file header before it")]
    [InlineData("--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n x\n", true, "The diff from standard input is refused: diff line 3: the hunk ends early")]
    public async Task Impact_ADiffThatCannotBeUsed_IsRefusedBeforeAnythingIsAskedOrStarted(string? input, bool redirected, string message)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("nothing should be asked"));
        var (runtime, output, error) = Runtime(handler, input, redirected);

        var exit = await QueryCommandHandler.ExecuteAsync(Parse("query", "impact", "--path", Root), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.StartsWith(message, error.ToString().Trim());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Impact_AMissingDiffFile_SaysWhichAndWhy()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("nothing should be asked"));
        var (runtime, _, error) = Runtime(handler);
        var missing = Path.Combine(Path.GetTempPath(), "cc-no-such-" + Guid.NewGuid().ToString("N") + ".patch");

        var exit = await QueryCommandHandler.ExecuteAsync(Parse("query", "impact", "--diff", missing, "--path", Root), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.StartsWith($"Could not read the diff from {missing}: ", error.ToString());
    }

    private const string ImpactAnswer = """
        {"maxDepth":3,"complete":false,"diffFiles":2,"contentChecked":1,"changeCount":1,"changesReturned":1,
         "changes":[{"level":"member","changedLines":"1","dependents":1,"testsHidden":2,"name":"Run","type":"Method","file":"src/A.cs","line":1,"identifier":"csharp:A.Run()"}],
         "unmappedCount":1,"unmappedReturned":1,"unmappedLines":4,"unmappedReasons":{"notIndexed":1},
         "unmapped":[{"file":"README.md","status":"modified","reason":"notIndexed","lineCount":4,"detail":"No declaration."}],
         "ignoredLines":1,"totalCount":1,"returnedCount":1,"testsHidden":2,"fileCount":1,
         "items":[{"seed":"csharp:A.Run()","relation":"CALLS","via":"csharp:A.Run()","distance":1,"name":"U","type":"Method","file":"src/U.cs","line":4,"identifier":"csharp:U()"}],
         "testFileCount":1,"testFiles":["tests/ATests.cs"],"hint":"Look."}
        """;

    [Fact]
    public async Task Impact_PostsTheDiffWithOnlyTheOptionsGiven_AndExitsTwoWhenIncomplete()
    {
        string? posted = null;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/status") return Json(Status());
            Assert.Equal(HttpMethod.Post, request.Method);
            posted = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(ImpactAnswer);
        });
        var (runtime, output, error) = Runtime(handler, ImpactDiff);

        var exit = await QueryCommandHandler.ExecuteAsync(
            Parse("query", "impact", "--depth", "3", "--relation", "CALLS", "--path", Root), runtime, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Empty(error.ToString());
        Assert.Equal("/api/graph/impact", handler.Requests.Last().AbsolutePath);
        Assert.Equal($$"""{"diff":{{JsonSerializer.Serialize(ImpactDiff)}},"maxDepth":3,"relation":"CALLS"}""", posted);
        Assert.Equal(
            "impact\tincomplete\t1 changed\t1 affected in 1 file(s)\tdepth 3\t2 file(s) in the diff\n"
            + "content\t1 file(s) match the working tree\n"
            + "changed\t1/1\n"
            + "\tmember\tlines 1\t1 dependents +2 in tests\tcsharp:A.Run()\tMethod\tsrc/A.cs:1\n"
            + "unmapped\t1/1\t4 line(s)\tnotIndexed=1\n"
            + "\tnotIndexed\tREADME.md\t-\tNo declaration.\n"
            + "ignored\t1 whitespace-only line(s)\t0 mode-only file(s)\n"
            + "affected\t1/1\tdepth 3\t1 file(s)\n"
            + "testsHidden\t2\t--tests lists them\n"
            + "\t1\tCALLS\tcsharp:U()\tMethod\tsrc/U.cs:4\tvia csharp:A.Run()\tfrom csharp:A.Run()\n"
            + "tests\t1/1\n"
            + "\ttests/ATests.cs\n"
            + "hint\tLook.\n",
            output.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Impact_ACompleteAnswerExitsZero_AndTheHumanFormSaysItIsComplete()
    {
        var complete = ImpactAnswer.Replace("\"complete\":false", "\"complete\":true");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status()) : Json(complete));
        var (runtime, output, _) = Runtime(handler, ImpactDiff);

        Assert.Equal(0, await QueryCommandHandler.ExecuteAsync(Parse("query", "impact", "--human", "--path", Root), runtime, CancellationToken.None));
        Assert.StartsWith("Every changed line of the 2 file(s) in the diff maps to a declaration.", output.ToString());
    }

    [Fact]
    public void Impact_TakesNoIdentifier()
        => Assert.NotEmpty(Program.CreateRootCommand().Parse(["query", "impact", "X"]).Errors);

    [Fact]
    public void Impact_WaitsAsLongAsAGraphQuery()
        => Assert.Equal(TimeSpan.FromSeconds(60), QueryCommandHandler.RequestTimeout(Parse("query", "impact")));
}
