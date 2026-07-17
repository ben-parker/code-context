using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeContext.Core.Services;
using CodeContext.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Acceptance test for the Phase 3b manual (AOT-safe) MCP tool registration: the
/// <see cref="McpToolCatalog"/>'s <c>tools/list</c> output must be semantically identical to the
/// golden snapshot captured in Phase 1 (before the attribute-discovery path was removed). The
/// comparison is normalized — object key order may differ and tool order is irrelevant — but
/// tool names, descriptions, input schemas, defaults, and the execution hint must match exactly.
/// </summary>
public class McpToolCatalogTests
{
    [Fact]
    public void ListTools_MatchesGoldenSnapshot()
    {
        var actual = SerializeCatalog();
        var expected = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-tools-list.snapshot.json"));

        var actualTools = ToolsByName(actual);
        var expectedTools = ToolsByName(expected);

        Assert.Equal(
            expectedTools.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actualTools.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (name, expectedTool) in expectedTools)
        {
            Assert.True(actualTools.TryGetValue(name, out var actualTool));
            Assert.Equal(expectedTool, actualTool);
        }
    }

    private static string SerializeCatalog() =>
        JsonSerializer.Serialize(McpToolCatalog.ListTools(), McpJsonUtilities.DefaultOptions);

    private static Dictionary<string, string> ToolsByName(string json)
    {
        var tools = JsonNode.Parse(json)!.AsObject()["tools"]!.AsArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = tool!.AsObject()["name"]!.GetValue<string>();
            result[name] = Canonicalize(tool);
        }
        return result;
    }

    // Normalizes object key order (recursively) while preserving array order, so schema arrays
    // like enum:["Compact","Full"] and type:["string","null"] are still compared positionally.
    private static string Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var members = new StringBuilder("{");
                var first = true;
                foreach (var member in obj.OrderBy(m => m.Key, StringComparer.Ordinal))
                {
                    if (!first) members.Append(',');
                    first = false;
                    members.Append(JsonSerializer.Serialize(member.Key)).Append(':').Append(Canonicalize(member.Value));
                }
                return members.Append('}').ToString();
            case JsonArray array:
                var items = new StringBuilder("[");
                var firstItem = true;
                foreach (var item in array)
                {
                    if (!firstItem) items.Append(',');
                    firstItem = false;
                    items.Append(Canonicalize(item));
                }
                return items.Append(']').ToString();
            case null:
                return "null";
            default:
                return node.ToJsonString();
        }
    }
}

/// <summary>
/// Dispatch and argument-binding contract for <see cref="McpToolCatalog.CallAsync"/>. The manual
/// (AOT-safe) handler replaced the SDK's reflection-based argument binding, so these tests lock the
/// behaviour to the taxonomy the pre-upgrade binary produced over stdio (captured empirically):
/// <list type="bullet">
///   <item>missing <c>identifier</c> / missing or non-array <c>identifiers</c> / an out-of-range
///     <c>view</c> → in-band <c>isError</c> <see cref="CallToolResult"/> (the old binding surfaced
///     these as isError results, hidden behind a generic message; we surface the real reason);</item>
///   <item>an explicitly empty <c>identifiers</c> array → a successful empty result (the old binding
///     bound it cleanly);</item>
///   <item>a wrong-kind optional scalar such as <c>depth</c>="5" → falls back to the default and
///     still succeeds (the old binding also returned success for this input);</item>
///   <item>an unknown tool name → a JSON-RPC <em>protocol</em> error
///     (<see cref="McpErrorCode.InvalidParams"/>, -32602), thrown, not folded into an isError result;</item>
///   <item>a tool-body exception → in-band <c>isError</c> carrying the tool's error envelope.</item>
/// </list>
/// </summary>
public class McpToolCatalogCallTests
{
    [Fact]
    public async Task CallAsync_GetContext_DispatchesToContextService_WithBoundArguments()
    {
        using var harness = new Harness();
        harness.Context.GetCompactContextAsync(
                "Widget", Arg.Any<string?>(), 3, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new CompactContextResponse());

        var result = await McpToolCatalog.CallAsync(
            Request("get_context", """{"identifier":"Widget","depth":3}""", harness.Services), default);

        Assert.True(result.IsError is not true);
        await harness.Context.Received(1).GetCompactContextAsync(
            "Widget", Arg.Any<string?>(), 3, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task CallAsync_GetMultiContext_DispatchesToContextService_WithBoundIdentifiers()
    {
        using var harness = new Harness();
        harness.Context.GetMultipleCompactContextAsync(Arg.Any<MultiContextRequest>())
            .Returns(new List<CompactContextResponse>());

        var result = await McpToolCatalog.CallAsync(
            Request("get_multi_context", """{"identifiers":["A","B"]}""", harness.Services), default);

        Assert.True(result.IsError is not true);
        await harness.Context.Received(1).GetMultipleCompactContextAsync(
            Arg.Is<MultiContextRequest>(r => r.Identifiers.SequenceEqual(new[] { "A", "B" })));
    }

    [Fact]
    public async Task CallAsync_GetStatus_DispatchesToStatusService()
    {
        using var harness = new Harness();

        var result = await McpToolCatalog.CallAsync(
            Request("get_status", null, harness.Services), default);

        Assert.True(result.IsError is not true);
        await harness.Status.Received(1).GetStatusAsync();
    }

    [Fact] // Case (a)
    public async Task CallAsync_GetContext_MissingIdentifier_ReturnsIsError()
    {
        using var harness = new Harness();

        var result = await McpToolCatalog.CallAsync(
            Request("get_context", "{}", harness.Services), default);

        Assert.True(result.IsError is true);
        Assert.Equal("identifier is required.", Text(result));
    }

    [Fact] // Case (b)
    public async Task CallAsync_GetMultiContext_MissingIdentifiers_ReturnsIsError()
    {
        using var harness = new Harness();

        var result = await McpToolCatalog.CallAsync(
            Request("get_multi_context", "{}", harness.Services), default);

        Assert.True(result.IsError is true);
        Assert.Equal("identifiers is required.", Text(result));
        await harness.Context.DidNotReceive().GetMultipleCompactContextAsync(Arg.Any<MultiContextRequest>());
    }

    [Fact] // Boundary for case (b): an explicit empty array is a clean bind, not a missing argument.
    public async Task CallAsync_GetMultiContext_EmptyIdentifiersArray_ReturnsSuccess()
    {
        using var harness = new Harness();
        harness.Context.GetMultipleCompactContextAsync(Arg.Any<MultiContextRequest>())
            .Returns(new List<CompactContextResponse>());

        var result = await McpToolCatalog.CallAsync(
            Request("get_multi_context", """{"identifiers":[]}""", harness.Services), default);

        Assert.True(result.IsError is not true);
        await harness.Context.Received(1).GetMultipleCompactContextAsync(
            Arg.Is<MultiContextRequest>(r => r.Identifiers.Count == 0));
    }

    [Fact] // Case (c): wrong-kind optional scalar falls back to the default and still succeeds.
    public async Task CallAsync_GetContext_StringDepth_FallsBackToDefaultAndSucceeds()
    {
        using var harness = new Harness();
        harness.Context.GetCompactContextAsync(
                "X", Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new CompactContextResponse());

        var result = await McpToolCatalog.CallAsync(
            Request("get_context", """{"identifier":"X","depth":"5"}""", harness.Services), default);

        Assert.True(result.IsError is not true);
        // The JSON-string "5" is not a JSON number, so depth falls back to its default of 1.
        await harness.Context.Received(1).GetCompactContextAsync(
            "X", Arg.Any<string?>(), 1, Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool?>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Fact] // Case (d)
    public async Task CallAsync_GetContext_InvalidView_ReturnsIsError()
    {
        using var harness = new Harness();

        var result = await McpToolCatalog.CallAsync(
            Request("get_context", """{"identifier":"X","view":"bogus"}""", harness.Services), default);

        Assert.True(result.IsError is true);
        Assert.Equal("view must be 'Compact' or 'Full'.", Text(result));
    }

    [Fact] // Case (e)
    public async Task CallAsync_UnknownTool_ThrowsProtocolErrorWithInvalidParams()
    {
        using var harness = new Harness();

        var ex = await Assert.ThrowsAsync<McpProtocolException>(
            () => McpToolCatalog.CallAsync(
                Request("no_such_tool", "{}", harness.Services), default).AsTask());

        Assert.Equal("Unknown tool: 'no_such_tool'", ex.Message);
        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task CallAsync_ToolBodyThrows_ReturnsIsErrorWithEnvelope()
    {
        using var harness = new Harness();
        harness.Context.GetCompactContextAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<bool?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns<Task<CompactContextResponse>>(_ => throw new InvalidOperationException("boom"));

        var result = await McpToolCatalog.CallAsync(
            Request("get_context", """{"identifier":"X"}""", harness.Services), default);

        Assert.True(result.IsError is true);
        Assert.Contains("CONTEXT_ERROR", Text(result));
        Assert.Contains("boom", Text(result));
    }

    // --- helpers -------------------------------------------------------------------------------

    // CallAsync only reads Params and Services off the context; the RequestContext<T> constructor
    // demands a non-null McpServer that is impractical to build here, so the instance is created
    // uninitialized and only the two members under test are set.
    private static RequestContext<CallToolRequestParams> Request(
        string name, string? argumentsJson, IServiceProvider services)
    {
        var context = (RequestContext<CallToolRequestParams>)
            RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        context.Params = new CallToolRequestParams { Name = name, Arguments = ParseArguments(argumentsJson) };
        context.Services = services;
        return context;
    }

    private static IDictionary<string, JsonElement>? ParseArguments(string? json)
    {
        if (json is null) return null;
        using var document = JsonDocument.Parse(json);
        var arguments = new Dictionary<string, JsonElement>();
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = property.Value.Clone();
        return arguments;
    }

    private static string Text(CallToolResult result) => ((TextContentBlock)result.Content[0]).Text;

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly IServiceScope _scope;

        public IContextService Context { get; } = Substitute.For<IContextService>();
        public IStatusService Status { get; } = Substitute.For<IStatusService>();
        public IServiceProvider Services => _scope.ServiceProvider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Context);
            services.AddSingleton(Status);
            _root = services.BuildServiceProvider();
            _scope = _root.CreateScope();
        }

        public void Dispose()
        {
            _scope.Dispose();
            _root.Dispose();
        }
    }
}
