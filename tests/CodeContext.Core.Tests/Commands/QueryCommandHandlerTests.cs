using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Core.Instances;

namespace CodeContext.Core.Tests.Commands;

public class QueryCommandHandlerTests
{
    private const string Root = "C:\\repo";

    [Fact]
    public void CommandModel_RequiresIdentifiersAndSharesRecursiveOptionsWithMulti()
    {
        var root = Program.CreateRootCommand();

        Assert.NotEmpty(root.Parse(["query"]).Errors);
        Assert.NotEmpty(root.Parse(["query", "multi"]).Errors);
        Assert.NotEmpty(root.Parse(["query", "Target", "--depth", "-1"]).Errors);
        Assert.Empty(root.Parse(["query", "csharp:Thing.Run(string,int)"]).Errors);
        Assert.Empty(root.Parse(["query", "multi", "A", "A", "csharp:Thing.Run(string,int)"]).Errors);

        var query = Assert.Single(root.Subcommands, command => command.Name == "query");
        Assert.Contains(query.Options, option => option.Name == "--tests");
        Assert.Contains(query.Options, option => option.Name == "--relation");
        Assert.Contains(query.Options, option => option.Name == "--exact");
        Assert.Contains(query.Options, option => option.Name == "--json");
        Assert.Contains(query.Options, option => option.Name == "--human");
        var multi = Assert.Single(query.Subcommands, command => command.Name == "multi");
        Assert.DoesNotContain(multi.Options,
            option => option.Name is "--path" or "--tests" or "--json" or "--human");

        var testsOption = Assert.IsType<Option<bool>>(
            Assert.Single(query.Options, option => option.Name == "--tests"));
        var pathOption = Assert.IsType<Option<string>>(
            Assert.Single(query.Options, option => option.Name == "--path"));
        var humanOption = Assert.IsType<Option<bool>>(
            Assert.Single(query.Options, option => option.Name == "--human"));
        var beforeSubcommand = root.Parse(
            ["query", "--tests", "--human", "--path", "C:\\selected", "multi", "A"]);
        var afterSubcommand = root.Parse(
            ["query", "multi", "A", "--tests", "--human", "--path", "C:\\selected"]);
        Assert.True(beforeSubcommand.GetValue(testsOption));
        Assert.True(beforeSubcommand.GetValue(humanOption));
        Assert.Equal("C:\\selected", beforeSubcommand.GetValue(pathOption));
        Assert.True(afterSubcommand.GetValue(testsOption));
        Assert.True(afterSubcommand.GetValue(humanOption));
        Assert.Equal("C:\\selected", afterSubcommand.GetValue(pathOption));
    }

    [Theory]
    [InlineData("query", "codecontext query <identifier> [options]")]
    [InlineData("multi", "codecontext query multi <identifier>... [options]")]
    public async Task QueryHelpShowsSupportedSyntaxWithoutParentArgument(string command, string expectedUsage)
    {
        var root = Program.CreateRootCommand();
        var output = new StringWriter();
        var error = new StringWriter();
        var args = command == "multi"
            ? new[] { "query", "multi", "--help" }
            : new[] { "query", "--help" };

        var exit = await root.Parse(args).InvokeAsync(new InvocationConfiguration
        {
            Output = output,
            Error = error,
        });

        Assert.Equal(0, exit);
        Assert.Contains(expectedUsage, output.ToString());
        Assert.DoesNotContain("<identifier> multi <identifier>", output.ToString());
        Assert.Equal(1, output.ToString().Split("Show help and usage information").Length - 1);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task SingleQuery_ConstructsGetForEveryOptionAndPreservesCommaInIdentifier()
    {
        var identifier = "csharp:Thing.Run(string,int)";
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status"
                ? Json(Status("ready"))
                : Json(Matched(identifier)));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings([identifier], Root, 2, true, "CALLS,REFERENCES", true, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests, request => request.RequestUri!.AbsolutePath == "/api/context/complete");
        var query = request.RequestUri!.Query;
        Assert.Contains("identifier=csharp%3AThing.Run%28string%2Cint%29", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("depth=2", query);
        Assert.Contains("includeTests=true", query);
        Assert.Contains("relation=CALLS%2CREFERENCES", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact=true", query);
        Assert.Equal(Matched(identifier), output.ToString());
    }

    [Fact]
    public async Task SingleQuery_OmitsTestsUnlessRequested()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, _, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests, request => request.RequestUri!.AbsolutePath == "/api/context/complete");
        Assert.DoesNotContain("includeTests", request.RequestUri!.Query);
    }

    [Fact]
    public async Task MultiQuery_ConstructsPostAndPreservesOrderAndDuplicates()
    {
        var identifiers = new[] { "A", "A", "csharp:Thing.Run(string,int)" };
        var response = "[" + string.Join(',', identifiers.Select(Matched)) + "]";
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(identifiers, Root, 3, true, "CALLS, MOCK_CALLS", true, true, true),
            runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        var request = Assert.Single(handler.Requests, request => request.RequestUri!.AbsolutePath == "/api/context/multi");
        Assert.Equal(HttpMethod.Post, request.Method);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(identifiers, body.RootElement.GetProperty("identifiers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(3, body.RootElement.GetProperty("depth").GetInt32());
        Assert.True(body.RootElement.GetProperty("includeTests").GetBoolean());
        Assert.True(body.RootElement.GetProperty("exact").GetBoolean());
        Assert.Equal(new[] { "CALLS", "MOCK_CALLS" },
            body.RootElement.GetProperty("relationshipTypes").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(response, output.ToString());
    }

    [Fact]
    public async Task ExistingClosestAncestorIsUsedWithoutStarting()
    {
        var started = false;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, _, error) = Runtime(handler, new FakeRegistry(Instance()), (_, _) =>
        {
            started = true;
            return Task.FromResult(DetachedStartResult.Failed("unexpected"));
        });

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root + "\\src", 1, false, null, false, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(started);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task MissingInstanceIsAutomaticallyStartedAndProgressStaysOnStderr()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, output, error) = Runtime(handler, new FakeRegistry(null),
            (_, _) => Task.FromResult(DetachedStartResult.Started(Instance())));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(Matched("Target"), output.ToString());
        Assert.Contains("Starting", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AutomaticStartupFailureReturnsFour()
    {
        var (runtime, output, error) = Runtime(new RecordingHandler(_ => throw new InvalidOperationException()),
            new FakeRegistry(null), (_, _) => Task.FromResult(DetachedStartResult.Failed("could not launch")));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false),
            runtime, CancellationToken.None);

        Assert.Equal(4, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("could not launch", error.ToString());
    }

    [Theory]
    [InlineData("other", Root, 1)]
    [InlineData("id", "C:\\other", 1)]
    [InlineData("id", Root, 2)]
    public async Task IdentityRootOrContractMismatchReturnsOne(string instanceId, string statusRoot, int contract)
    {
        var handler = new RecordingHandler(_ => Json(Status("ready", instanceId, statusRoot, contract)));
        var (runtime, _, error) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false),
            runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("mismatch", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadinessTimeoutIsDeterministicAndReturnsThree()
    {
        var handler = new RecordingHandler(_ => Json(Status("indexing")));
        var (runtime, _, error) = Runtime(handler, new FakeRegistry(Instance()), readinessTimeout: TimeSpan.Zero);

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false),
            runtime, CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Single(handler.Requests);
        Assert.Contains("did not become ready within 0 seconds", error.ToString());
        Assert.Contains("codecontext status --path", error.ToString());
    }

    [Fact]
    public async Task ApiRejectionReturnsOneWithoutWritingStdout()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready"))
            : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad relation") });
        var (runtime, output, error) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, "NOPE", false, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("bad relation", error.ToString());
    }

    [Fact]
    public async Task StatusApiRejectionReturnsOneImmediately()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("status failed"),
        });
        var (runtime, output, error) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, true, false),
            runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Single(handler.Requests);
        Assert.Empty(output.ToString());
        Assert.Contains("500 Internal Server Error", error.ToString());
        Assert.Contains("status failed", error.ToString());
        Assert.DoesNotContain("status --path", error.ToString());
    }

    [Fact]
    public async Task MissingSingleOrAnyMultiTargetReturnsTwo()
    {
        var singleHandler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(Missing()));
        var (singleRuntime, _, _) = Runtime(singleHandler, new FakeRegistry(Instance()));
        Assert.Equal(2, await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Missing"], Root, 1, false, null, false, false, false), singleRuntime, CancellationToken.None));

        var multiResponse = "[" + Matched("A") + "," + Missing() + "]";
        var multiHandler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(multiResponse));
        var (multiRuntime, _, _) = Runtime(multiHandler, new FakeRegistry(Instance()));
        Assert.Equal(2, await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["A", "Missing"], Root, 1, false, null, false, false, true), multiRuntime, CancellationToken.None));
    }

    [Fact]
    public async Task HumanOutputShowsCanonicalLocationRelationshipCountsTruncationAndTests()
    {
        const string response = """
            {"view":"compact","matchMode":"exact","totalMatches":1,"returnedMatches":1,"matches":[{"target":{"name":"Target","type":"Method","file":"src/T.cs","line":7,"signature":"Target()","identifier":"csharp:T.Target()"},"relationships":{"uses":[{"name":"Dep","type":"Class","file":"src/D.cs","line":2,"relations":["CALLS"],"identifier":"csharp:Dep"}],"usesCount":4,"usesReturnedCount":1,"usesTruncated":true,"usedByCount":0,"usedByReturnedCount":0,"truncated":true},"testing":{"isTested":true,"directlyTested":true,"testReferenceCount":2,"testImplementerCount":0,"heuristicMatchCount":0,"testFileCount":1,"testFilesReturnedCount":1,"testFiles":[{"file":"tests/TTests.cs","testCount":1,"testMethodsReturnedCount":1,"testMethods":[],"evidence":["directCall"]}]}}]}
            """;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, true, null, false, false, false, Human: true), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Target [Method]", output.ToString());
        Assert.Contains("csharp:T.Target()", output.ToString());
        Assert.Contains("src/T.cs:7", output.ToString());
        Assert.Contains("Uses (1/4, truncated)", output.ToString());
        Assert.Contains("CALLS", output.ToString());
        Assert.Contains("Tests", output.ToString());
        Assert.Contains("directly tested", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultOutputIsCompactAgentTextWithCanonicalRelationshipsCountsAndTests()
    {
        const string response = """
            {"view":"compact","matchMode":"exact","totalMatches":1,"returnedMatches":1,"matches":[{"target":{"name":"Target","type":"Method","file":"src/T.cs","line":7,"signature":"Target()","identifier":"csharp:T.Target()"},"relationships":{"uses":[{"name":"Dep","type":"Class","file":"src/D.cs","line":2,"relations":["CALLS"],"identifier":"csharp:Dep"}],"usesCount":4,"usesReturnedCount":1,"usesTruncated":true,"usedByCount":0,"usedByReturnedCount":0,"truncated":true},"testing":{"isTested":true,"directlyTested":true,"testReferenceCount":2,"testImplementerCount":0,"heuristicMatchCount":0,"testFileCount":1,"testFilesReturnedCount":1,"testFiles":[{"file":"tests/TTests.cs","testCount":1,"testMethodsReturnedCount":1,"testMethods":[{"name":"CoversTarget","type":"Method","file":"tests/TTests.cs","line":9,"identifier":"csharp:TTests.CoversTarget()"}],"evidence":["directCall"]}]}}]}
            """;
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, true, null, false, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        var newline = Environment.NewLine;
        Assert.Equal(
            $"target\tcsharp:T.Target()\tMethod\tsrc/T.cs:7{newline}" +
            $"uses\t1/4\ttruncated{newline}" +
            $"\tCALLS\tDep\tClass\tsrc/D.cs:2{newline}" +
            $"usedBy\t0/0{newline}" +
            $"tests\t1/1\tdirect\trefs=2\timpl=0\theuristic=0{newline}" +
            $"\ttests/TTests.cs\t1/1\tdirectCall{newline}" +
            $"\t\tCoversTarget\tMethod\ttests/TTests.cs:9{newline}",
            output.ToString());
    }

    [Fact]
    public async Task JsonAndHumanCannotBeCombined()
    {
        var (runtime, output, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()), new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, true, false, Human: true),
            runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Contains("cannot be combined", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyRelationshipsRemainSuccessful()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("usedBy\t0/0", output.ToString());
        Assert.DoesNotContain("none", output.ToString());
    }

    [Theory]
    [InlineData(false, "relationships\tnot loaded\tambiguous: re-query with a target identifier")]
    [InlineData(true, "Relationships: not loaded (ambiguous: re-query with a target identifier)")]
    public async Task AmbiguousMatchesNeverReportRelationshipsAsNone(bool human, string expected)
    {
        var response = JsonSerializer.Serialize(new
        {
            view = "compact",
            matchMode = "exact",
            totalMatches = 2,
            returnedMatches = 2,
            ambiguous = true,
            matches = new[]
            {
                new { target = new { name = "Target", type = "Method", identifier = "csharp:A.Target()" } },
                new { target = new { name = "Target", type = "Method", identifier = "csharp:B.Target()" } },
            },
        });
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false, Human: human),
            runtime, CancellationToken.None);

        Assert.Contains(expected, output.ToString());
        Assert.DoesNotContain("none", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "relationships\tnot loaded\tdepth 0")]
    [InlineData(true, "Relationships: not loaded (depth 0)")]
    public async Task DepthZeroDoesNotClaimThereAreNoRelationships(bool human, string expected)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 0, false, null, false, false, false, Human: human),
            runtime, CancellationToken.None);

        Assert.Contains(expected, output.ToString());
        Assert.DoesNotContain("usedBy", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "fileDependents\t2/9\ttruncated\tof src/T.cs", "fileDependencies\t1/1\tof src/T.cs", "usedBy\t0/0")]
    [InlineData(true, "File dependents of src/T.cs (2/9, truncated):", "File dependencies of src/T.cs (1/1):", "Used by (0/0):")]
    public async Task FileSectionsNameTheFileTheyDescribe(
        bool human, string dependents, string dependencies, string usedBy)
    {
        const string response = """
            {"view":"compact","matchMode":"exact","totalMatches":1,"returnedMatches":1,"matches":[{"target":{"name":"Interior","type":"Property","file":"src/T.cs","line":3,"identifier":"csharp:T.Interior"},"relationships":{"fileDependencies":["src/A.cs"],"fileDependenciesCount":1,"fileDependenciesReturnedCount":1,"fileDependents":["src/B.cs","src/C.cs"],"fileDependentsCount":9,"fileDependentsReturnedCount":2,"fileDependentsTruncated":true,"truncated":true}}]}
            """;
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Interior"], Root, 1, false, null, false, false, false, Human: human),
            runtime, CancellationToken.None);

        Assert.Contains(dependents, output.ToString());
        Assert.Contains(dependencies, output.ToString());
        Assert.Contains(usedBy, output.ToString());
    }

    [Fact]
    public async Task LimitIsSentToTheSingleQueryAndOmittedByDefault()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(Matched("Target")));
        var (runtime, _, _) = Runtime(handler, new FakeRegistry(Instance()));

        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false, Limit: 50),
            runtime, CancellationToken.None);
        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false),
            runtime, CancellationToken.None);

        var queries = handler.Requests.Where(request => request.RequestUri.AbsolutePath == "/api/context/complete").ToList();
        Assert.Contains("maxRelationships=50", queries[0].RequestUri.Query);
        Assert.DoesNotContain("maxRelationships", queries[1].RequestUri.Query);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(40, 40)]
    public async Task MultiQueryUsesTheSameRelationshipCapAsASingleQuery(int? limit, int expected)
    {
        var response = "[" + Matched("Target") + "]";
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(response));
        var (runtime, _, _) = Runtime(handler, new FakeRegistry(Instance()));

        await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, true, Limit: limit),
            runtime, CancellationToken.None);

        var body = handler.Requests.Single(request => request.RequestUri.AbsolutePath == "/api/context/multi").Body!;
        using var document = JsonDocument.Parse(body);
        Assert.Equal(expected, document.RootElement.GetProperty("maxRelationships").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task LimitOutsideTheApiRangeIsRejected(int limit)
    {
        var (runtime, _, error) = Runtime(
            new RecordingHandler(_ => throw new InvalidOperationException()), new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false, Limit: limit),
            runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("between 1 and 100", error.ToString());
        Assert.NotEmpty(Program.CreateRootCommand().Parse(["query", "Target", "--limit", limit.ToString()]).Errors);
    }

    [Fact]
    public void LimitIsAcceptedOnBothQueryForms()
    {
        var root = Program.CreateRootCommand();

        Assert.Empty(root.Parse(["query", "Target", "--limit", "25"]).Errors);
        Assert.Empty(root.Parse(["query", "multi", "A", "B", "--limit", "25"]).Errors);
    }

    [Fact]
    public async Task AmbiguityIsRenderedAndRemainsSuccessful()
    {
        using var firstDocument = JsonDocument.Parse(Matched("csharp:A.Target"));
        using var secondDocument = JsonDocument.Parse(Matched("csharp:B.Target"));
        var first = firstDocument.RootElement.GetProperty("matches")[0].Clone();
        var second = secondDocument.RootElement.GetProperty("matches")[0].Clone();
        var response = JsonSerializer.Serialize(new
        {
            view = "compact",
            matchMode = "substring",
            totalMatches = 2,
            returnedMatches = 2,
            ambiguous = true,
            disambiguationHint = "Use a canonical identifier.",
            matches = new[] { first, second },
        });
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status("ready")) : Json(response));
        var (runtime, output, _) = Runtime(handler, new FakeRegistry(Instance()));

        var exit = await QueryCommandHandler.ExecuteAsync(
            new QuerySettings(["Target"], Root, 1, false, null, false, false, false), runtime, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("matches\t2/2\tsubstring", output.ToString());
        Assert.Contains("hint\tUse a canonical identifier.", output.ToString());
        Assert.Contains("csharp:A.Target", output.ToString());
        Assert.Contains("csharp:B.Target", output.ToString());
    }

    private static (QueryRuntime Runtime, StringWriter Output, StringWriter Error) Runtime(
        RecordingHandler handler,
        IInstanceRegistry registry,
        Func<string, CancellationToken, Task<DetachedStartResult>>? start = null,
        TimeSpan? readinessTimeout = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runtime = new QueryRuntime(
            registry,
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            start ?? ((_, _) => Task.FromResult(DetachedStartResult.Failed("unexpected start"))),
            output,
            error,
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            readinessTimeout ?? TimeSpan.FromSeconds(30),
            _ => true);
        return (runtime, output, error);
    }

    private static InstanceRecord Instance() => new()
    {
        RootPath = Root,
        Port = 7890,
        Pid = 123,
        InstanceId = "id",
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static string Status(string indexingStatus, string instanceId = "id", string root = Root, int contract = 1)
        => JsonSerializer.Serialize(new
        {
            system = new { instanceId },
            indexing = new { status = indexingStatus, rootPath = root },
            api = new { contractVersion = contract },
        });

    private static string Matched(string identifier)
        => JsonSerializer.Serialize(new
        {
            view = "compact",
            matchMode = "exact",
            totalMatches = 1,
            returnedMatches = 1,
            matches = new[]
            {
                new
                {
                    target = new { name = "Target", type = "Class", file = "src/T.cs", line = 1, identifier },
                    relationships = new { },
                },
            },
        });

    private static string Missing()
        => "{\"view\":\"compact\",\"matchMode\":\"substring\",\"totalMatches\":0,\"returnedMatches\":0,\"matches\":[]}";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeRegistry(InstanceRecord? instance) : IInstanceRegistry
    {
        public IReadOnlyList<InstanceRecord> GetAll() => instance is null ? [] : [instance];
        public void Register(InstanceRecord record) => throw new NotSupportedException();
        public void Unregister(string rootPath, string? instanceId = null) => throw new NotSupportedException();
        public InstanceRecord? FindForPath(string path) => instance;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));
            return respond(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri RequestUri, string? Body);
}
