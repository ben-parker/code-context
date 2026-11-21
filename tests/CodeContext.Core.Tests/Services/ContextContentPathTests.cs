using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeContext.Api.Endpoints;
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

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// <c>includeContent</c> reads the file a node names, and a node's <c>filePath</c> is what a repository made its
/// worker say. Hand-built nodes name files the host may not read (outside the indexed root, through a link the
/// repository holds, on a share), and the answer must carry none of their lines: from the service, over REST
/// (<c>/api/context/complete</c> in both views, <c>/api/context/multi</c>) and over MCP (<c>get_context</c>).
/// </summary>
public sealed class ContextContentPathTests : IDisposable
{
    /// <summary>On every line of a file outside the root: in an answer, the host read where it may not.</summary>
    private const string Leaked = "do-not-leak";

    /// <summary>On every line of the file under the root.</summary>
    private const string Inside = "inside-line";

    private readonly string _base = Directory.CreateTempSubdirectory("cc-content-").FullName;
    private readonly string _root;
    private readonly ContextService _service;

    /// <summary>The nodes whose file the host may not read, by name.</summary>
    private readonly List<string> _refused = ["Outside", "DotDot", "Sibling", "Share"];

    public ContextContentPathTests()
    {
        _root = Path.Combine(_base, "root");
        var outside = Path.Combine(_base, "outside");
        var sibling = _root + "-other";
        foreach (var directory in new[] { Path.Combine(_root, "src"), outside, sibling }) Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(_root, "src", "Inside.cs"), Lines(Inside));
        File.WriteAllText(Path.Combine(outside, "Kept.cs"), Lines(Leaked));
        File.WriteAllText(Path.Combine(sibling, "Kept.cs"), Lines(Leaked));

        var graph = new GraphTestGraph(_root)
            .Node("Inside", file: "src/Inside.cs", line: 1, endLine: 3)
            .Node("Outside", path: Path.Combine(outside, "Kept.cs"), line: 1, endLine: 3)
            // Spelled as if under the root.
            .Node("DotDot", path: Path.Combine(_root, "src", "..", "..", "outside", "Kept.cs"), line: 1, endLine: 3)
            // A directory beside the root whose name starts with the root's.
            .Node("Sibling", path: Path.Combine(sibling, "Kept.cs"), line: 1, endLine: 3)
            // By its spelling only: nothing answers at this name here, and on Windows nothing may ask.
            .Node("Share", path: @"\\cc-no-such-host\share\Kept.cs", line: 1, endLine: 3);

        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, "src", "Leaf.cs"), Path.Combine(outside, "Kept.cs"));
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
            graph.Node("Leaf", file: "src/Leaf.cs", line: 1, endLine: 3).Node("Linked", file: "linked/Kept.cs", line: 1, endLine: 3);
            _refused.AddRange(["Leaf", "Linked"]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows without the privilege to make a link.
        }

        // Where the file system tells letter case apart, the root's name in another case is another directory.
        var otherCase = Path.Combine(_base, "ROOT");
        if (!Directory.Exists(otherCase))
        {
            Directory.CreateDirectory(otherCase);
            File.WriteAllText(Path.Combine(otherCase, "Kept.cs"), Lines(Leaked));
            graph.Node("OtherCase", path: Path.Combine(otherCase, "Kept.cs"), line: 1, endLine: 3);
            _refused.Add("OtherCase");
        }

        _service = graph.Build();
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static string Lines(string marker) => string.Concat(Enumerable.Range(0, 6).Select(line => $"{marker} {line}\n"));

    private static string Identifier(string name) => GraphTestGraph.Identifier(name);

    [Fact]
    public async Task AFileUnderTheRoot_IsRead()
    {
        var full = await _service.GetCompleteContextAsync(Identifier("Inside"), includeContent: true, exact: true);
        var compact = await _service.GetCompactContextAsync(Identifier("Inside"), includeContent: true, exact: true);

        Assert.Contains(Inside, Assert.Single(full.Matches).Content);
        Assert.Contains(Inside, Assert.Single(compact.Matches).Content);
    }

    [Fact]
    public async Task AFileTheHostMayNotRead_GivesNoContent()
    {
        foreach (var name in _refused)
        {
            var full = await _service.GetCompleteContextAsync(Identifier(name), includeContent: true, exact: true);
            var compact = await _service.GetCompactContextAsync(Identifier(name), includeContent: true, exact: true);

            Assert.True(Assert.Single(full.Matches).Content is null, name);
            Assert.True(Assert.Single(compact.Matches).Content is null, name);
        }
    }

    [Fact]
    public async Task WithNoRoot_NothingIsUnderIt_AndNothingIsRead()
    {
        var graph = new GraphTestGraph(_root).Node("Inside", file: "src/Inside.cs", line: 1, endLine: 3);
        graph.Commit();
        var rootless = new ContextService(
            new CodeContext.Core.Repositories.InMemory.InMemoryNodeRepository(graph.Database),
            new CodeContext.Core.Repositories.InMemory.InMemoryEdgeRepository(graph.Database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository());

        var full = await rootless.GetCompleteContextAsync(Identifier("Inside"), includeContent: true, exact: true);

        Assert.Null(Assert.Single(full.Matches).Content);
    }

    private async Task<WebApplication> StartAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IContextService>(_service);
        builder.Services.AddSingleton(Substitute.For<IStatusService>());
        builder.Services.AddSingleton(Substitute.For<IIndexCoordinator>());
        builder.Services.AddSingleton(Substitute.For<ILanguageWorkerService>());
        builder.Services.AddSingleton(Options.Create(new CodeContextOptions()));
        CodeContext.Api.ProgramHelpers.AddRestApi(builder.Services);
        var app = builder.Build();
        app.MapCodeContextEndpoints();
        await app.StartAsync();
        return app;
    }

    [Theory]
    [InlineData("compact")]
    [InlineData("full")]
    public async Task Rest_Context_CarriesNoLineOfAFileTheHostMayNotRead(string view)
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        string Url(string name) => $"/api/context/complete?identifier={Uri.EscapeDataString(Identifier(name))}&exact=true&includeContent=true&view={view}";

        var inside = await client.GetAsync(Url("Inside"));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);
        Assert.Contains(Inside, await inside.Content.ReadAsStringAsync());

        foreach (var name in _refused)
        {
            var response = await client.GetAsync(Url(name));
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(Identifier(name), json);
            Assert.False(json.Contains(Leaked), name);
        }
    }

    [Theory]
    [InlineData("Compact")]
    [InlineData("Full")]
    public async Task Rest_MultiContext_CarriesNoLineOfAFileTheHostMayNotRead(string view)
    {
        await using var app = await StartAppAsync();
        var body = JsonSerializer.Serialize(new
        {
            identifiers = _refused.Prepend("Inside").Select(Identifier).ToArray(),
            exact = true,
            includeContent = true,
            view,
        });

        var response = await app.GetTestClient().PostAsync("/api/context/multi", new StringContent(body, Encoding.UTF8, "application/json"));
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Inside, json);
        foreach (var name in _refused) Assert.Contains(Identifier(name), json);
        Assert.DoesNotContain(Leaked, json);
    }

    private async Task<CallToolResult> CallAsync(string name, object arguments)
    {
        var services = new ServiceCollection().AddSingleton<IContextService>(_service).BuildServiceProvider();
        var context = (RequestContext<CallToolRequestParams>)
            RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        context.Params = new CallToolRequestParams
        {
            Name = name,
            Arguments = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()),
        };
        context.Services = services;
        return await McpToolCatalog.CallAsync(context, default);
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    [Theory]
    [InlineData("Compact")]
    [InlineData("Full")]
    public async Task Mcp_GetContext_CarriesNoLineOfAFileTheHostMayNotRead(string view)
    {
        var inside = await CallAsync("get_context", new { identifier = Identifier("Inside"), exact = true, includeContent = true, view });
        Assert.True(inside.IsError is not true, Text(inside));
        Assert.Contains(Inside, Text(inside));

        foreach (var name in _refused)
        {
            var result = await CallAsync("get_context", new { identifier = Identifier(name), exact = true, includeContent = true, view });

            Assert.True(result.IsError is not true, Text(result));
            Assert.Contains(Identifier(name), Text(result));
            Assert.False(Text(result).Contains(Leaked), name);
        }
    }

    /// <summary><c>get_multi_context</c> has no <c>includeContent</c>: it reads no file, whatever a node names.</summary>
    [Fact]
    public async Task Mcp_GetMultiContext_ReadsNoFile()
    {
        var result = await CallAsync("get_multi_context", new { identifiers = _refused.Prepend("Inside").Select(Identifier).ToArray(), exact = true, view = "Full" });

        Assert.True(result.IsError is not true, Text(result));
        Assert.Contains(Identifier("Inside"), Text(result));
        Assert.DoesNotContain(Inside, Text(result));
        Assert.DoesNotContain(Leaked, Text(result));
    }
}
