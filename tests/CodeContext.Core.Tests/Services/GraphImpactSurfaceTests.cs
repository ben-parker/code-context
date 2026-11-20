using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeContext.Core.Diffs;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CodeContext.Core.Tests.Services;

/// <summary>Diff impact over REST and MCP: the same service behind both, its body checked strictly, refusals that say why.</summary>
public sealed class GraphImpactSurfaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-impact-surface-" + Guid.NewGuid().ToString("N"));
    private readonly string _diff;
    private readonly ContextService _service;

    private sealed class Fresh : IIndexFreshness
    {
        public DateTime? FilesReadAsOfUtc => DateTime.UtcNow.AddHours(1);
    }

    public GraphImpactSurfaceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var content = string.Concat(Enumerable.Range(1, 10).Select(line => $"line {line}\n"));
        File.WriteAllText(Path.Combine(_root, "src", "Foo.cs"), content);
        var blob = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false)[..7];
        _diff = $"diff --git a/src/Foo.cs b/src/Foo.cs\nindex 1111111..{blob} 100644\n--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -4 +4 @@\n-old\n+new\n";
        _service = new GraphTestGraph(_root) { Freshness = new Fresh() }
            .Node("Foo.Run", file: "src/Foo.cs", line: 1, endLine: 8).Node("Caller", file: "src/Caller.cs").Edge("Caller", "Foo.Run")
            .Build();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<WebApplication> StartAppAsync(IGraphQueryService graph)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(graph);
        builder.Services.AddSingleton(Substitute.For<IContextService>());
        builder.Services.AddSingleton(Substitute.For<IStatusService>());
        builder.Services.AddSingleton(Substitute.For<IIndexCoordinator>());
        builder.Services.AddSingleton(Substitute.For<ILanguageWorkerService>());
        builder.Services.AddSingleton(Options.Create(new CodeContextOptions()));
        CodeContext.Api.ProgramHelpers.AddRestApi(builder.Services);
        var app = builder.Build();
        CodeContext.Api.ProgramHelpers.ConfigureApiEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(WebApplication app, string url, string body)
    {
        var response = await app.GetTestClient().PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private string Body(string extra = "") => $$"""{"diff":{{JsonSerializer.Serialize(_diff)}}{{extra}}}""";

    [Fact]
    public async Task Rest_AnswersWhatTheServiceAnswers()
    {
        await using var app = await StartAppAsync(_service);

        var (status, body) = await PostAsync(app, "/api/graph/impact", Body(""","limit":5"""));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("complete").GetBoolean());
        Assert.Equal(GraphTestGraph.Identifier("Foo.Run"), body.GetProperty("changes")[0].GetProperty("identifier").GetString());
        Assert.Equal("4", body.GetProperty("changes")[0].GetProperty("changedLines").GetString());
        Assert.Equal(GraphTestGraph.Identifier("Foo.Run"), body.GetProperty("items")[0].GetProperty("seed").GetString());
        Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("/api/graph/impact", """{"diff":"x","identifier":"A"}""", "'identifier' does not apply to impact queries (it is an option of affected queries); impact accepts diff, maxDepth, relation, includeTests, limit.")]
    [InlineData("/api/graph/impact", """{"Diff":"x"}""", "'Diff' is written 'diff' (argument names are case-sensitive).")]
    [InlineData("/api/graph/impact", """{"diff":"x","maxDepth":"3"}""", "maxDepth must be an integer.")]
    [InlineData("/api/graph/impact", """{"diff":"x","includeTests":1}""", "includeTests must be true or false.")]
    [InlineData("/api/graph/impact", """{"limit":3}""", "diff is required.")]
    [InlineData("/api/graph/impact", """{"diff":"  "}""", "diff is required.")]
    [InlineData("/api/graph/impact", """["diff"]""", "the impact arguments must be a JSON object.")]
    [InlineData("/api/graph/impact", """{"diff":"x","limit":0}""", "limit must be between 1 and 100 (got 0).")]
    [InlineData("/api/graph/impact", """{"diff":"@@ -1 +1 @@\n-a\n+b\n"}""", "diff line 1: a hunk with no file header before it; the diff is refused rather than read in part.")]
    [InlineData("/api/graph/impact?limit=3", """{"diff":"x"}""", "impact takes its arguments in a JSON body (diff, maxDepth, relation, includeTests, limit), not in the query string.")]
    [InlineData("/api/graph/impact", "not json", "The body is not a JSON object of impact arguments")]
    public async Task Rest_BadInput_IsA400ThatSaysWhy(string url, string body, string message)
    {
        await using var app = await StartAppAsync(_service);

        var (status, error) = await PostAsync(app, url, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_GRAPH_QUERY", error.GetProperty("error").GetProperty("code").GetString());
        Assert.StartsWith(message, error.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rest_BeforeAnyScanHasFinished_IsA503()
    {
        var graph = Substitute.For<IGraphQueryService>();
        graph.GetImpactAsync(Arg.Any<GraphImpactRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IndexNotReadyException("not yet"));
        await using var app = await StartAppAsync(graph);

        var (status, body) = await PostAsync(app, "/api/graph/impact", Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("INDEX_NOT_READY", body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<CallToolResult> CallAsync(IGraphQueryService graph, string arguments)
    {
        var services = new ServiceCollection().AddSingleton(graph).BuildServiceProvider();
        var context = (RequestContext<CallToolRequestParams>)
            RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        using var document = JsonDocument.Parse(arguments);
        context.Params = new CallToolRequestParams
        {
            Name = "get_impact",
            Arguments = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()),
        };
        context.Services = services;
        return await McpToolCatalog.CallAsync(context, default);
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    [Fact]
    public async Task Mcp_AnswersTheSameAsTheService()
    {
        var result = await CallAsync(_service, Body(""","maxDepth":2,"relation":null"""));

        Assert.True(result.IsError is not true, Text(result));
        Assert.Equal(
            JsonSerializer.Serialize(await _service.GetImpactAsync(new GraphImpactRequest { Diff = _diff, MaxDepth = 2 }),
                CodeContextJsonContext.Default.GraphImpactResponse),
            Text(result));
    }

    [Theory]
    [InlineData("""{"diff":"x","within":"src"}""", "'within' does not apply to impact queries (it is an option of hubs queries)")]
    [InlineData("""{"diff":5}""", "diff must be a string.")]
    [InlineData("""{}""", "diff is required.")]
    [InlineData("""{"diff":"--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n x\n"}""", "diff line 3: the hunk ends early")]
    public async Task Mcp_BadInput_IsAnErrorThatSaysWhy(string arguments, string message)
    {
        var result = await CallAsync(_service, arguments);

        Assert.True(result.IsError);
        Assert.StartsWith(message, Text(result));
    }
}
