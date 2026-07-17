using CodeContext.Api;
using CodeContext.Api.Endpoints;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodeContext.Core.Tests.Services;

public class CodeContextEndpointTests
{
    [Fact]
    public async Task CompleteContext_DefaultsToCompactView()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false)
            .Returns(new CompactContextResponse());
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync("/api/context/complete?identifier=Target");
        var json = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("\"view\":\"compact\"", json);
        await service.Received(1).GetCompactContextAsync(
            "Target", null, 1, false, false, null, false, false, 5, 10, false);
    }

    [Fact]
    public async Task CompleteContext_FullViewIsExplicit()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompleteContextAsync(
                "Target", null, 1, false, false, false, false, false)
            .Returns(new CompleteContextResponse());
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync(
            "/api/context/complete?identifier=Target&view=full");

        response.EnsureSuccessStatusCode();
        await service.Received(1).GetCompleteContextAsync(
            "Target", null, 1, false, false, false, false, false);
    }

    [Fact]
    public async Task SyntaxTree_DefaultsToCompactDepthTwoAndFullIsExplicit()
    {
        var service = Substitute.For<IContextService>();
        var workers = Substitute.For<ILanguageWorkerService>();
        var tree = JsonSerializer.SerializeToElement(new
        {
            kind = "CompilationUnit", rawKind = 8840,
            span = new { start = 0, length = 12, end = 12 },
            fullSpan = new { start = 0, length = 40, end = 40 },
            children = Enumerable.Range(0, 20).Select(index =>
                new
                {
                    kind = "IdentifierToken", rawKind = 8508,
                    span = new { start = index, length = 5, end = index + 5 },
                    fullSpan = new { start = index, length = 30, end = index + 30 },
                    text = "Value", valueText = "Value", isMissing = false,
                    extraDebugMetadata = "deliberately verbose parser-native metadata repeated on every token"
                }).ToArray()
        });
        workers.GetNativeSyntaxTreeAsync("Sample.cs", null, null, 2, Arg.Any<CancellationToken>())
            .Returns(new NativeSyntaxTreeResult(
                "csharp", "1.0", "default", "Sample.cs", "roslyn-csharp-syntax-v1", tree, false));
        await using var app = await StartAppAsync(service, workers);
        var client = app.GetTestClient();

        var compactResponse = await client.PostAsJsonAsync("/api/syntax-tree", new { filePath = "Sample.cs" });
        var compactJson = await compactResponse.Content.ReadAsStringAsync();
        compactResponse.EnsureSuccessStatusCode();
        using var compact = JsonDocument.Parse(compactJson);
        Assert.Equal("compact", compact.RootElement.GetProperty("view").GetString());
        var compactTree = compact.RootElement.GetProperty("tree");
        Assert.Equal(JsonValueKind.Array, compactTree.GetProperty("span").ValueKind);
        Assert.False(compactTree.TryGetProperty("rawKind", out _));
        Assert.Equal("Value", compactTree.GetProperty("children")[0].GetProperty("text").GetString());

        workers.GetNativeSyntaxTreeAsync("Sample.cs", null, null, 2, Arg.Any<CancellationToken>())
            .Returns(new NativeSyntaxTreeResult(
                "csharp", "1.0", "default", "Sample.cs", "roslyn-csharp-syntax-v1", tree, false));
        var fullResponse = await client.PostAsJsonAsync(
            "/api/syntax-tree", new { filePath = "Sample.cs", view = "full", maxDepth = 2 });
        var fullJson = await fullResponse.Content.ReadAsStringAsync();
        fullResponse.EnsureSuccessStatusCode();
        using var full = JsonDocument.Parse(fullJson);
        Assert.Equal("full", full.RootElement.GetProperty("view").GetString());
        Assert.True(full.RootElement.GetProperty("tree").TryGetProperty("rawKind", out _));
        Assert.True(compactJson.Length * 2 <= fullJson.Length);
        await workers.Received().GetNativeSyntaxTreeAsync(
            "Sample.cs", null, null, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteContext_ArgumentExceptionReturnsBadRequestContextError()
    {
        // Full-shape (byte-identity) live assertion — locks the CONTEXT_ERROR envelope emitted by
        // the real endpoint, not just the presence of the code. Mirrors the DTO-level golden in
        // ErrorContractTests, driven end-to-end through the ASP.NET host's Results.Json path.
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false)
            .Returns<CompactContextResponse>(_ => throw new InvalidOperationException("boom-ctx"));
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync("/api/context/complete?identifier=Target");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"CONTEXT_ERROR\",\"message\":\"boom-ctx\",\"details\":{\"identifier\":\"Target\",\"depth\":1,\"includeTests\":false,\"includeContent\":false,\"includeRelated\":false,\"includeMetrics\":false,\"maxMatches\":5,\"maxRelationships\":10,\"maxCallSites\":3,\"maxTestFiles\":5,\"maxTestMethods\":5,\"expandAmbiguous\":false,\"view\":\"compact\"}}}",
            json);
    }

    [Fact]
    public async Task MultiContext_ExceptionReturnsBadRequestMultiContextError()
    {
        // Full-shape live assertion for the MULTI_CONTEXT_ERROR envelope: the posted request is
        // echoed back in details, so the whole shape (camelCase, null-omitted, defaults) is locked.
        var service = Substitute.For<IContextService>();
        service.GetMultipleCompactContextAsync(Arg.Any<MultiContextRequest>())
            .Returns<Task<List<CompactContextResponse>>>(_ => throw new InvalidOperationException("boom-multi"));
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/api/context/multi", new { identifiers = new[] { "A", "B" } });
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"MULTI_CONTEXT_ERROR\",\"message\":\"boom-multi\",\"details\":{\"identifiers\":[\"A\",\"B\"],\"depth\":1,\"view\":\"Compact\",\"includeTests\":false,\"includeContent\":false,\"includeRelated\":false,\"includeMetrics\":false,\"maxMatches\":5,\"maxRelationships\":3,\"maxCallSites\":3,\"maxTestFiles\":5,\"maxTestMethods\":5,\"expandAmbiguous\":false,\"relationshipTypes\":[]}}}",
            json);
    }

    [Fact]
    public async Task RefreshFile_ExceptionReturnsBadRequestRefreshError()
    {
        // Full-shape live assertion for the single-file REFRESH_ERROR variant (path present).
        var service = Substitute.For<IContextService>();
        var coordinator = Substitute.For<IIndexCoordinator>();
        coordinator.RefreshFileAsync("x/y.cs", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        await using var app = await StartAppAsync(service, coordinator: coordinator);

        var response = await app.GetTestClient().PostAsync("/api/index/refresh?path=x/y.cs", content: null);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":\"REFRESH_ERROR\",\"message\":\"boom\",\"details\":{\"path\":\"x/y.cs\"}}}",
            json);
    }

    [Fact]
    public async Task CompleteContext_RelationParam_ForwardedToCompactService()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false,
                3, 5, 5, null, null, null, null, "CALLS")
            .Returns(new CompactContextResponse());
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync(
            "/api/context/complete?identifier=Target&relation=CALLS");

        response.EnsureSuccessStatusCode();
        await service.Received(1).GetCompactContextAsync(
            "Target", null, 1, false, false, null, false, false, 5, 10, false,
            3, 5, 5, null, null, null, null, "CALLS");
    }

    [Fact]
    public async Task CompleteContext_RelationWithFullView_Returns400()
    {
        var service = Substitute.For<IContextService>();
        await using var app = await StartAppAsync(service);

        var response = await app.GetTestClient().GetAsync(
            "/api/context/complete?identifier=Target&relation=CALLS&view=full");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("CONTEXT_ERROR", json);
    }

    private static async Task<WebApplication> StartAppAsync(
        IContextService service, ILanguageWorkerService? workers = null,
        IIndexCoordinator? coordinator = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(Substitute.For<IStatusService>());
        builder.Services.AddSingleton(coordinator ?? Substitute.For<IIndexCoordinator>());
        builder.Services.AddSingleton(workers ?? Substitute.For<ILanguageWorkerService>());
        builder.Services.AddSingleton(Options.Create(new CodeContextOptions()));
        ProgramHelpers.AddRestApi(builder.Services);
        var app = builder.Build();
        app.MapCodeContextEndpoints();
        await app.StartAsync();
        return app;
    }
}
