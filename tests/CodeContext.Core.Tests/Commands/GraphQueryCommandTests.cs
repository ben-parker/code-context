using System.CommandLine;
using System.Net;
using System.Text;
using CodeContext.Api;
using CodeContext.Api.Commands;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Commands;

/// <summary>
/// <c>query path</c>, <c>query affected</c> and <c>query hubs</c>: how the command line binds, what it refuses before
/// asking anything, the request it sends, and how it renders the answer.
/// </summary>
public partial class GraphQueryCommandTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? "C:\\repo" : "/repo";

    /// <summary>The settings a command line binds, read back through the handler's test hook instead of running the query.</summary>
    private static QuerySettings Parse(params string[] args)
    {
        var parse = Program.CreateRootCommand().Parse(args);
        Assert.Empty(parse.Errors);
        QuerySettings? captured = null;
        QueryCommandHandler.SettingsForTests.Value = settings => captured = settings;
        try
        {
            Assert.Equal(0, parse.Invoke());
        }
        finally
        {
            QueryCommandHandler.SettingsForTests.Value = null;
        }
        return captured!;
    }

    [Fact]
    public void Subcommands_BindTheirArgumentsAndOnlyTheOptionsGiven()
    {
        var path = Parse("query", "path", "A", "B", "--tests", "--relation", "CALLS", "--depth", "4");
        Assert.Equal(["A", "B"], path.Identifiers);
        Assert.Equal(new GraphQuerySettings(GraphQueryParameters.PathQuery, MaxDepth: 4), path.Graph);
        Assert.True(path.IncludeTests);
        Assert.Equal("CALLS", path.Relation);
        Assert.Equal(["--depth"], path.Given);

        var affected = Parse("query", "affected", "csharp:Thing.Run(string, int)", "--limit", "7");
        Assert.Equal(["csharp:Thing.Run(string, int)"], affected.Identifiers);
        Assert.Null(affected.Graph!.MaxDepth);
        Assert.Equal(7, affected.Limit);

        var hubs = Parse("query", "hubs", "--within", "src/Core", "--type", "Class", "--external");
        Assert.Empty(hubs.Identifiers);
        Assert.Equal(new GraphQuerySettings(GraphQueryParameters.HubsQuery, null, "src/Core", "Class"), hubs.Graph);
        Assert.Equal(["--external", "--within", "--type"], hubs.Given);
    }

    private static string Help(params string[] args)
    {
        var output = new StringWriter();
        var exit = Program.CreateRootCommand().Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = new StringWriter() });
        Assert.Equal(0, exit);
        return output.ToString();
    }

    [Theory]
    // Each subcommand's help lists only the options it reads, with its own depth and limit defaults.
    [InlineData("path", new[] { "--depth", "--relation", "--tests" }, new[] { "--exact", "--limit", "--external", "--within", "--type", "[default: 1]" })]
    [InlineData("affected", new[] { "--depth", "--limit", "--tests" }, new[] { "--exact", "--external", "--within", "--type", "[default: 1]", "default 10" })]
    [InlineData("hubs", new[] { "--limit", "--within", "--type", "--external" }, new[] { "--exact", "--depth", "default 10" })]
    [InlineData("multi", new[] { "--depth", "--exact", "--limit" }, new[] { "--within", "--type" })]
    public void SubcommandHelp_ListsOnlyTheOptionsItReads(string command, string[] listed, string[] absent)
    {
        var help = Help("query", command, "--help");

        foreach (var option in listed) Assert.Contains(option, help);
        foreach (var text in absent) Assert.DoesNotContain(text, help);
    }

    [Fact]
    public void QueryHelp_DoesNotListTheHubsOnlyOptions()
    {
        var help = Help("query", "--help");

        Assert.Contains("--exact", help);
        Assert.DoesNotContain("--within <within>", help);
        Assert.DoesNotContain("--type <type>", help);
        // Rendering one subcommand's help leaves the shared options as they were.
        Help("query", "hubs", "--help");
        Assert.Contains("[default: 1]", Help("query", "--help"));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("affected")]
    [InlineData("hubs")]
    [InlineData("multi")]
    public void ASymbolNamedLikeASubcommand_IsQueriedAfterTheSeparator(string name)
    {
        var settings = Parse("query", "--depth", "2", "--", name);

        Assert.Null(settings.Graph);
        Assert.False(settings.Multi);
        Assert.Equal([name], settings.Identifiers);
        Assert.Equal(2, settings.Depth);
    }

    [Theory]
    [InlineData("query path A")]
    [InlineData("query affected")]
    [InlineData("query hubs X")]
    [InlineData("query hubs --limit 0")]
    [InlineData("query affected X --limit abc")]
    [InlineData("query path A B --depth x")]
    [InlineData("query Target --depth x")]
    public void MalformedCommandLines_AreParseErrors(string commandLine)
        => Assert.NotEmpty(Program.CreateRootCommand().Parse(commandLine.Split(' ')).Errors);

    [Theory]
    [InlineData(new[] { "query", "path", "A", "B", "--exact" }, "--exact does not apply to query path: graph queries always match identifiers exactly (canonical identifier, exact or qualified name, or file path).")]
    [InlineData(new[] { "query", "path", "A", "B", "--limit", "3" }, "--limit does not apply to query path: it applies only to query, query affected, query hubs, query impact.")]
    [InlineData(new[] { "query", "hubs", "--depth", "3" }, "--depth does not apply to query hubs: it applies only to query, query path, query affected, query impact.")]
    [InlineData(new[] { "query", "affected", "X", "--external" }, "--external does not apply to query affected: it applies only to query, query hubs.")]
    [InlineData(new[] { "query", "affected", "X", "--within", "src" }, "--within does not apply to query affected: it applies only to query hubs.")]
    [InlineData(new[] { "query", "X", "--type", "Class" }, "--type does not apply to query: it applies only to query hubs.")]
    [InlineData(new[] { "query", "path", "A", "B", "--depth", "0" }, "--depth must be between 1 and 20 for query path (got 0).")]
    [InlineData(new[] { "query", "affected", "X", "--depth", "21" }, "--depth must be between 1 and 20 for query affected (got 21).")]
    [InlineData(new[] { "query", "path", " ", "B" }, "query path needs two non-empty identifiers: <from> <to>.")]
    public async Task InapplicableOrOutOfRangeOptions_AreRefusedBeforeAnythingIsAsked(string[] args, string message)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("nothing should be asked"));
        var (runtime, output, error) = Runtime(handler);

        var exit = await QueryCommandHandler.ExecuteAsync(Parse(args), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal(message, error.ToString().Trim());
        Assert.Empty(output.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EachSubcommand_SendsOnlyWhatWasGiven_ToItsEndpoint()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/status" => Json(Status()),
            "/api/graph/path" => Json("""{"from":{"requested":"A","matchMode":"exact","totalMatches":1},"to":{"requested":"B","matchMode":"exact","totalMatches":1},"found":false,"maxDepth":10,"explanation":"none"}"""),
            "/api/graph/affected" => Json("""{"seed":{"requested":"X","matchMode":"exact","totalMatches":1},"maxDepth":5,"totalCount":0,"returnedCount":0,"fileCount":0,"items":[]}"""),
            _ => Json("""{"totalCount":0,"returnedCount":0,"items":[]}"""),
        });
        var (runtime, _, _) = Runtime(handler);

        await QueryCommandHandler.ExecuteAsync(Parse("query", "path", "A,B", "csharp:C.D(int)", "--path", Root), runtime, CancellationToken.None);
        await QueryCommandHandler.ExecuteAsync(Parse("query", "affected", "X", "--depth", "3", "--tests", "--limit", "9", "--relation", "CALLS", "--path", Root), runtime, CancellationToken.None);
        await QueryCommandHandler.ExecuteAsync(Parse("query", "hubs", "--within", "src/My Dir", "--type", "Class", "--external", "--path", Root), runtime, CancellationToken.None);

        var graphRequests = handler.Requests.Where(request => request.AbsolutePath.StartsWith("/api/graph/", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "/api/graph/path?from=A%2CB&to=csharp%3AC.D%28int%29",
            "/api/graph/affected?identifier=X&maxDepth=3&relation=CALLS&includeTests=true&limit=9",
            "/api/graph/hubs?includeExternal=true&type=Class&within=src%2FMy%20Dir",
        ], graphRequests.Select(request => request.PathAndQuery));
    }

    [Fact]
    public async Task AFoundPath_IsOneLinePerHop_WithIdentifiersToPassBack()
    {
        var body = """
            {"from":{"requested":"A","matchMode":"exact","totalMatches":1,"target":{"name":"A","type":"Method","file":"src/A.cs","line":3,"identifier":"csharp:A()"}},
             "to":{"requested":"B","matchMode":"exact","totalMatches":1,"target":{"name":"B","type":"Method","file":"src/B.cs","line":9,"identifier":"csharp:B()"}},
             "found":true,"length":1,"maxDepth":10,
             "path":[{"hop":"start","name":"A","type":"Method","file":"src/A.cs","line":3,"identifier":"csharp:A()"},
                     {"hop":"uses","relation":"CALLS","at":5,"name":"B","type":"Method","file":"src/B.cs","line":9,"identifier":"csharp:B()"}]}
            """;
        var (exit, output) = await RunAsync(body, "query", "path", "A", "B", "--path", Root);

        Assert.Equal(0, exit);
        Assert.Equal(
            "path\t1 hop(s)\tcsharp:A()\tcsharp:B()\n"
            + "\tstart\tcsharp:A()\tMethod\tsrc/A.cs:3\n"
            + "\tCALLS line 5\tcsharp:B()\tMethod\tsrc/B.cs:9\n",
            output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task NoPath_SaysWhichFilterHidItAndWhy()
    {
        var body = """
            {"from":{"requested":"A","matchMode":"exact","totalMatches":1},"to":{"requested":"B","matchMode":"exact","totalMatches":1},
             "found":false,"maxDepth":10,"excludedBy":["relation"],"explanation":"No path from A to B with relation=CALLS, but one exists."}
            """;
        var (exit, output) = await RunAsync(body, "query", "path", "A", "B", "--relation", "CALLS", "--path", Root);

        Assert.Equal(0, exit);
        Assert.Equal(
            "path\tnone\texcludedBy relation\nexplanation\tNo path from A to B with relation=CALLS, but one exists.\n",
            output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AMissingOrAmbiguousEndpoint_ExitsTwo()
    {
        var missing = """
            {"from":{"requested":"Nope","matchMode":"none","totalMatches":0,"hint":"No symbol matches."},"to":{"requested":"B","matchMode":"exact","totalMatches":1},"found":false,"maxDepth":10}
            """;
        var (exit, output) = await RunAsync(missing, "query", "path", "Nope", "B", "--path", Root);
        Assert.Equal(2, exit);
        Assert.StartsWith("missing\tfrom\tNope\nhint\tNo symbol matches.\n", output.Replace("\r\n", "\n"));

        var ambiguous = """
            {"seed":{"requested":"Run","matchMode":"exact","totalMatches":2,"ambiguous":true,"candidates":[{"name":"Run","type":"Method","identifier":"csharp:X.Run()"},{"name":"Run","type":"Method","identifier":"csharp:Y.Run()"}]},
             "maxDepth":5,"totalCount":0,"returnedCount":0,"fileCount":0,"items":[]}
            """;
        (exit, output) = await RunAsync(ambiguous, "query", "affected", "Run", "--path", Root);
        Assert.Equal(2, exit);
        Assert.Equal("ambiguous\tidentifier\tRun\t2 matches\n\tcsharp:X.Run()\tMethod\n\tcsharp:Y.Run()\tMethod\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AGraphQuery_MayTakeLongerThanALookup()
    {
        // A context query is a lookup; a graph query walks the graph, and on a graph of millions of edges that is seconds.
        Assert.Equal(TimeSpan.FromSeconds(5), QueryCommandHandler.RequestTimeout(Parse("query", "Thing")));
        Assert.Equal(TimeSpan.FromSeconds(5), QueryCommandHandler.RequestTimeout(Parse("query", "multi", "A", "B")));
        foreach (var graph in new[] { Parse("query", "hubs"), Parse("query", "affected", "A"), Parse("query", "path", "A", "B") })
            Assert.Equal(TimeSpan.FromSeconds(60), QueryCommandHandler.RequestTimeout(graph));
    }

    [Fact]
    public async Task AGraphQueryThatTimesOut_SaysSoAndHowToNarrowIt()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status())
            : throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing.", new TimeoutException()));
        var (runtime, output, error) = Runtime(handler);

        var exit = await QueryCommandHandler.ExecuteAsync(Parse("query", "hubs", "--path", Root), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Empty(output.ToString());
        Assert.Equal(
            "query hubs did not answer within 2 seconds and was stopped. On a very large graph narrow it: --within DIR or --type for hubs, a smaller --depth for path and affected.",
            error.ToString().Trim());
    }

    [Theory]
    [InlineData(false, "coverage\tpartial\t1 of 3 projects not fully analyzed: Api.")]
    [InlineData(true, "Coverage (partial): 1 of 3 projects not fully analyzed: Api.")]
    public async Task HubsFound_StillPrintTheCoverageLine(bool human, string expected)
    {
        var body = """
            {"totalCount":1,"returnedCount":1,"items":[{"identifier":"csharp:App.Core","name":"Core","type":"Class","dependents":4,"dependentFiles":2}],
             "coverage":{"state":"partial","incompleteProjects":1,"totalProjects":3,"names":["Api"]}}
            """;

        var (exit, output) = human
            ? await RunAsync(body, "query", "hubs", "--human", "--path", Root)
            : await RunAsync(body, "query", "hubs", "--path", Root);

        Assert.Equal(0, exit);
        Assert.Contains(expected, output);
    }

    [Fact]
    public async Task CopiesOfOneDeclaration_AreNamedBeforeTheAnswer()
    {
        var body = """
            {"seed":{"requested":"Twice","matchMode":"exact","totalMatches":2,"target":{"name":"Twice","type":"Method","file":"shared/util.ts","line":4,"identifier":"ts:app1/Twice"},"seedCount":2,"sameDeclaration":["ts:app2/Twice"],"hint":"one declaration indexed 2 times"},
             "maxDepth":5,"totalCount":0,"returnedCount":0,"fileCount":0,"items":[]}
            """;
        var (exit, output) = await RunAsync(body, "query", "affected", "Twice", "--path", Root);
        Assert.Equal(0, exit);
        Assert.StartsWith(
            "sameDeclaration\tidentifier\t2 copies searched together\tts:app1/Twice\tts:app2/Twice\nseed\tts:app1/Twice\t2 symbols",
            output.Replace("\r\n", "\n"));

        (exit, output) = await RunAsync(body, "query", "affected", "Twice", "--human", "--path", Root);
        Assert.Equal(0, exit);
        Assert.StartsWith(
            "identifier is one declaration indexed 2 times, searched together: ts:app1/Twice, ts:app2/Twice\n",
            output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AffectedAndHubs_ShowCountsTruncationAndHiddenTests()
    {
        var affected = """
            {"seed":{"requested":"X","matchMode":"exact","totalMatches":1,"target":{"name":"X","type":"Method","identifier":"csharp:X()"}},
             "maxDepth":5,"totalCount":2,"returnedCount":1,"truncated":true,"depthLimited":true,"testsHidden":4,"fileCount":2,
             "items":[{"relation":"CALLS","via":"csharp:X()","through":"csharp:IX.X()","distance":1,"name":"U","type":"Method","file":"src/U.cs","line":2,"identifier":"csharp:U()"}]}
            """;
        var (_, output) = await RunAsync(affected, "query", "affected", "X", "--path", Root);
        Assert.Equal(
            "seed\tcsharp:X()\n"
            + "affected\t1/2\ttruncated\tdepth 5, more beyond\t2 file(s)\n"
            + "testsHidden\t4\t--tests lists them\n"
            + "\t1\tCALLS through csharp:IX.X()\tcsharp:U()\tMethod\tsrc/U.cs:2\tvia csharp:X()\n",
            output.Replace("\r\n", "\n"));

        var hubs = """
            {"totalCount":5,"returnedCount":5,"within":"src","items":[{"dependents":7,"dependentFiles":3,"name":"H","type":"Class","file":"src/H.cs","line":1,"identifier":"csharp:H"}]}
            """;
        (_, output) = await RunAsync(hubs, "query", "hubs", "--within", "src", "--path", Root);
        Assert.Equal("hubs\t5/5\twithin src\n\t7 dependents\t3 files\tcsharp:H\tClass\tsrc/H.cs:1\n", output.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ARefusalFromTheApi_IsPrintedAsItsMessage()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status"
            ? Json(Status())
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"INVALID_GRAPH_QUERY","message":"within '../x' must not leave the indexed root."}}""", Encoding.UTF8, "application/json"),
            });
        var (runtime, _, error) = Runtime(handler);

        var exit = await QueryCommandHandler.ExecuteAsync(Parse("query", "hubs", "--within", "../x", "--path", Root), runtime, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Equal("Query API rejected the request (400 Bad Request): within '../x' must not leave the indexed root.", error.ToString().Trim());
    }

    // ---- Helpers ----

    private static async Task<(int Exit, string Output)> RunAsync(string body, params string[] args)
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath == "/api/status" ? Json(Status()) : Json(body));
        var (runtime, output, error) = Runtime(handler);
        var exit = await QueryCommandHandler.ExecuteAsync(Parse(args), runtime, CancellationToken.None);
        Assert.Empty(error.ToString());
        return (exit, output.ToString());
    }

    private static (QueryRuntime Runtime, StringWriter Output, StringWriter Error) Runtime(
        RecordingHandler handler, string? input = null, bool redirected = true)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var instance = new InstanceRecord { RootPath = Root, Port = 7890, Pid = 123, InstanceId = "id", StartedAt = DateTimeOffset.UtcNow };
        var runtime = new QueryRuntime(
            new Registry(instance),
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) },
            (_, _) => Task.FromResult(DetachedStartResult.Failed("unexpected start")),
            output, error, TimeProvider.System, (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(30), _ => true,
            Input: input is null ? null : new StringReader(input), InputRedirected: redirected);
        return (runtime, output, error);
    }

    private static string Status()
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            system = new { instanceId = "id" },
            indexing = new { status = "ready", rootPath = Root },
            api = new { contractVersion = 1 },
        });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Registry(InstanceRecord instance) : IInstanceRegistry
    {
        public IReadOnlyList<InstanceRecord> GetAll() => [instance];
        public void Register(InstanceRecord record) => throw new NotSupportedException();
        public void Unregister(string rootPath, string? instanceId = null) => throw new NotSupportedException();
        public InstanceRecord? FindForPath(string path) => instance;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
