using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeContext.Api;
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

/// <summary>
/// The graph queries over REST and MCP: one service behind both, canonical identifiers, and bad input refused with a
/// message (a 400 over REST, an isError result over MCP), including options that belong to another query.
/// </summary>
public class GraphSurfaceTests
{
    private static string I(string name) => GraphTestGraph.Identifier(name);

    private static ContextService Sample()
        => new GraphTestGraph()
            .Node("Caller").Node("IFoo.Run").Node("Foo.Run").Node("Target")
            .Edge("Caller", "IFoo.Run", line: 4)
            .Edge("Foo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("Foo.Run", "Target", line: 8)
            .Build();

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

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(WebApplication app, string url)
    {
        var response = await app.GetTestClient().GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task Rest_Path_AnswersWithCanonicalIdentifiersAndHops()
    {
        await using var app = await StartAppAsync(Sample());

        var (status, body) = await GetAsync(app, $"/api/graph/path?from={Uri.EscapeDataString(I("Caller"))}&to=Target");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("found").GetBoolean());
        Assert.Equal(2, body.GetProperty("length").GetInt32());
        Assert.Equal(I("Target"), body.GetProperty("to").GetProperty("target").GetProperty("identifier").GetString());
        var hops = body.GetProperty("path").EnumerateArray().Select(hop => hop.GetProperty("hop").GetString()!).ToArray();
        Assert.Equal(["start", "uses", "dispatch", "uses"], hops);
        Assert.Equal(5, body.GetProperty("path")[1].GetProperty("at").GetInt32());
    }

    [Fact]
    public async Task Rest_AffectedAndHubs_FollowTheCompactCountSemantics()
    {
        await using var app = await StartAppAsync(Sample());

        var (_, affected) = await GetAsync(app, $"/api/graph/affected?identifier={Uri.EscapeDataString(I("Target"))}&limit=1");
        Assert.Equal(2, affected.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, affected.GetProperty("returnedCount").GetInt32());
        Assert.True(affected.GetProperty("truncated").GetBoolean());
        Assert.Equal(I("Foo.Run"), affected.GetProperty("items")[0].GetProperty("identifier").GetString());

        var (_, hubs) = await GetAsync(app, "/api/graph/hubs?limit=3");
        // IFoo.Run and Target: an implementation is not a use.
        Assert.Equal(2, hubs.GetProperty("totalCount").GetInt32());
        Assert.False(hubs.TryGetProperty("truncated", out _));
    }

    [Theory]
    [InlineData("/api/graph/path?from=A&to=B&within=src", "'within' does not apply to path queries (it is an option of hubs queries); path accepts from, to, maxDepth, relation, includeTests.")]
    [InlineData("/api/graph/hubs?maxDepth=2", "'maxDepth' does not apply to hubs queries (it is an option of path queries and affected queries); hubs accepts limit, relation, type, within, includeTests, includeExternal.")]
    [InlineData("/api/graph/affected?identifier=A&depth=2", "'depth' does not apply to affected queries; affected accepts identifier, maxDepth, relation, includeTests, limit.")]
    [InlineData("/api/graph/affected?identifier=A&maxDepth=abc", "maxDepth must be an integer (got 'abc').")]
    [InlineData("/api/graph/hubs?includeTests=yes", "includeTests must be true or false (got 'yes').")]
    [InlineData("/api/graph/hubs?limit=1&limit=2", "limit is given 2 times; give it once.")]
    [InlineData("/api/graph/path?from=A", "to is required.")]
    [InlineData("/api/graph/path?from=A&to=%20", "to is required.")]
    [InlineData("/api/graph/affected?identifier=A&limit=0", "limit must be between 1 and 100 (got 0).")]
    [InlineData("/api/graph/path?from=A&to=B&maxDepth=21", "maxDepth must be between 1 and 20 (got 21).")]
    [InlineData("/api/graph/hubs?within=../x", "within '../x' must not leave the indexed root.")]
    public async Task Rest_BadInput_IsA400ThatSaysWhy(string url, string message)
    {
        await using var app = await StartAppAsync(Sample());

        var (status, body) = await GetAsync(app, url);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_GRAPH_QUERY", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Rest_AGraphThatKeepsChanging_IsA503()
    {
        var graph = Substitute.For<IGraphQueryService>();
        graph.GetHubsAsync(Arg.Any<GraphHubsRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GraphChangingException("changing"));
        await using var app = await StartAppAsync(graph);

        var (status, body) = await GetAsync(app, "/api/graph/hubs");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("GRAPH_CHANGING", body.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- MCP ----

    private static async Task<CallToolResult> CallAsync(IGraphQueryService graph, string tool, string arguments)
    {
        var services = new ServiceCollection().AddSingleton(graph).BuildServiceProvider();
        var context = (RequestContext<CallToolRequestParams>)
            RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        using var document = JsonDocument.Parse(arguments);
        context.Params = new CallToolRequestParams
        {
            Name = tool,
            Arguments = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()),
        };
        context.Services = services;
        return await McpToolCatalog.CallAsync(context, default);
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    [Fact]
    public async Task Mcp_AnswersTheSameAsTheService()
    {
        var service = Sample();
        var path = await CallAsync(service, "get_path", $$"""{"from":"{{I("Caller")}}","to":"Target","maxDepth":4}""");
        var affected = await CallAsync(service, "get_affected", """{"identifier":"Target","relation":null}""");
        var hubs = await CallAsync(service, "get_hubs", """{"limit":2,"includeTests":true}""");

        Assert.True(path.IsError is not true, Text(path));
        Assert.Equal(
            JsonSerializer.Serialize(await service.FindPathAsync(new GraphPathRequest { From = I("Caller"), To = "Target", MaxDepth = 4 }),
                CodeContext.Core.Serialization.CodeContextJsonContext.Default.GraphPathResponse),
            Text(path));
        Assert.Equal(2, JsonDocument.Parse(Text(affected)).RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, JsonDocument.Parse(Text(hubs)).RootElement.GetProperty("returnedCount").GetInt32());
    }

    [Theory]
    [InlineData("get_path", """{"from":"A","to":"B","within":"src"}""", "'within' does not apply to path queries (it is an option of hubs queries); path accepts from, to, maxDepth, relation, includeTests.")]
    [InlineData("get_hubs", """{"includeExternal":true,"maxDepth":3}""", "'maxDepth' does not apply to hubs queries (it is an option of path queries and affected queries); hubs accepts limit, relation, type, within, includeTests, includeExternal.")]
    [InlineData("get_affected", """{"identifier":"A","maxDepth":"3"}""", "maxDepth must be an integer.")]
    [InlineData("get_affected", """{"identifier":"A","limit":1.5}""", "limit must be an integer.")]
    [InlineData("get_hubs", """{"includeTests":"true"}""", "includeTests must be true or false.")]
    [InlineData("get_path", """{"from":"A"}""", "to is required.")]
    [InlineData("get_affected", """{"identifier":"  "}""", "identifier is required.")]
    [InlineData("get_hubs", """{"limit":0}""", "limit must be between 1 and 100 (got 0).")]
    [InlineData("get_path", """{"from":"A","to":"B","relation":"CALL"}""", "Unknown relation 'CALL'")]
    public async Task Mcp_BadInput_IsAnErrorThatSaysWhy(string tool, string arguments, string message)
    {
        var result = await CallAsync(Sample(), tool, arguments);

        Assert.True(result.IsError);
        Assert.StartsWith(message, Text(result));
    }
}
