using System.Text.Json;
using CodeContext.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CodeContext.Mcp;

/// <summary>
/// AOT-safe, reflection-free MCP tool registration. Replaces the SDK's
/// <c>.WithTools&lt;CodeContextTools&gt;()</c> attribute discovery (which builds the tool list
/// and JSON schemas via reflection) with hand-authored <see cref="Tool"/> definitions whose
/// input schemas are parsed once from const JSON literals. The literals are byte-for-byte the
/// Phase-1 <c>tools/list</c> snapshot, so <c>McpToolCatalogTests</c> can assert wire parity.
/// <para>
/// <see cref="CallAsync"/> dispatches by tool name, reads arguments from the request with the
/// same defaults the parameter attributes produced, resolves services from the request scope,
/// and delegates to the unchanged <see cref="CodeContextTools"/> method bodies.
/// </para>
/// </summary>
internal static class McpToolCatalog
{
    // Byte-identical to the captured tools/list snapshot (Fixtures/mcp-tools-list.snapshot.json).
    private const string GetMultiContextSchema =
        """
        {"type":"object","properties":{"identifiers":{"description":"List of identifiers to get context for","type":"array","items":{"type":"string"}},"type":{"description":"Optional type filter applied to every identifier","type":["string","null"],"default":null},"depth":{"description":"How many relationship levels to traverse","type":"integer","default":1},"includeTests":{"description":"Whether to include classified test evidence","type":"boolean","default":false},"exact":{"description":"Exact matching; omit for exact-first fallback","type":["boolean","null"],"default":null},"maxRelationships":{"description":"Maximum entries returned per relationship list","type":"integer","default":3},"maxCallSites":{"description":"Maximum source locations per aggregated relationship","type":"integer","default":3},"maxTestFiles":{"description":"Maximum test files returned; zero is count-only","type":"integer","default":5},"maxTestMethods":{"description":"Maximum test methods per file; zero is count-only","type":"integer","default":5},"expandAmbiguous":{"description":"Expand bounded ambiguous matches","type":"boolean","default":false},"containingType":{"description":"Containing type filter","type":["string","null"],"default":null},"namespace":{"description":"Exact namespace or module filter","type":["string","null"],"default":null},"signature":{"description":"Exact signature filter","type":["string","null"],"default":null},"sourceFile":{"description":"Repository-relative or absolute source file filter","type":["string","null"],"default":null},"relation":{"description":"Comma-separated relation kinds to filter uses/usedBy (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only","type":["string","null"],"default":null},"view":{"description":"Response shape; compact is the default","type":"string","enum":["Compact","Full"],"default":"Compact"}},"required":["identifiers"]}
        """;

    private const string GetContextSchema =
        """
        {"type":"object","properties":{"identifier":{"description":"Canonical returned identifier, symbol name, or file path","type":"string"},"type":{"description":"Filter by type (Class, Method, Interface, Property, etc.)","type":["string","null"],"default":null},"depth":{"description":"How many relationship levels to traverse (0-10)","type":"integer","default":1},"includeTests":{"description":"Whether to include test-related information","type":"boolean","default":false},"includeContent":{"description":"Whether to include file content snippets","type":"boolean","default":false},"exact":{"description":"Exact matching; omit for exact-first with substring fallback","type":["boolean","null"],"default":null},"includeRelated":{"description":"Whether to include loosely related symbols","type":"boolean","default":false},"includeMetrics":{"description":"Whether to include heuristic metrics","type":"boolean","default":false},"maxMatches":{"description":"Maximum ambiguous candidates returned","type":"integer","default":5},"maxRelationships":{"description":"Maximum entries returned per relationship list","type":"integer","default":10},"maxCallSites":{"description":"Maximum source locations returned per aggregated relationship; zero is count-only","type":"integer","default":3},"maxTestFiles":{"description":"Maximum test files returned; zero is count-only","type":"integer","default":5},"maxTestMethods":{"description":"Maximum test methods returned per file; zero is count-only","type":"integer","default":5},"expandAmbiguous":{"description":"Expand bounded ambiguous matches instead of returning summaries","type":"boolean","default":false},"containingType":{"description":"Filter members by their containing type","type":["string","null"],"default":null},"namespace":{"description":"Filter by exact namespace or module","type":["string","null"],"default":null},"signature":{"description":"Filter by exact signature","type":["string","null"],"default":null},"sourceFile":{"description":"Filter by repository-relative or absolute source file","type":["string","null"],"default":null},"relation":{"description":"Comma-separated relation kinds to filter uses/usedBy (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only","type":["string","null"],"default":null},"view":{"description":"Response shape; compact is the default","type":"string","enum":["Compact","Full"],"default":"Compact"}},"required":["identifier"]}
        """;

    private const string GetStatusSchema =
        """
        {"type":"object","properties":{}}
        """;

    // Tool order matches the captured snapshot (reflection discovery order).
    private static readonly Tool[] ToolDefinitions =
    [
        MakeTool(
            "get_multi_context",
            "Get context for multiple identifiers in a single request. Useful for batch operations.",
            GetMultiContextSchema),
        MakeTool(
            "get_context",
            "Get comprehensive context for a code identifier. Returns relationships, dependencies, tests, and metrics.",
            GetContextSchema),
        MakeTool(
            "get_status",
            "Get comprehensive system health and debugging information",
            GetStatusSchema),
    ];

    private static Tool MakeTool(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        var tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = document.RootElement.Clone(),
        };
        // The reflection-based .WithTools<T>() path stamped every tool with an
        // execution.taskSupport = "optional" hint; reproduce it so the tools/list wire shape
        // is unchanged. ToolExecution is [Experimental(MCPEXP001)] in ModelContextProtocol
        // 1.4.1 — suppressed narrowly here because matching the existing contract requires it.
#pragma warning disable MCPEXP001
        tool.Execution = new ToolExecution { TaskSupport = ToolTaskSupport.Optional };
#pragma warning restore MCPEXP001
        return tool;
    }

    public static ListToolsResult ListTools() => new() { Tools = ToolDefinitions.ToList() };

    public static async ValueTask<CallToolResult> CallAsync(
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        var name = context.Params?.Name;
        var args = context.Params?.Arguments;

        try
        {
            var text = name switch
            {
                "get_context" => await InvokeGetContextAsync(context, args),
                "get_multi_context" => await InvokeGetMultiContextAsync(context, args),
                "get_status" => await InvokeGetStatusAsync(context),
                _ => throw new ArgumentException($"Unknown tool: '{name}'."),
            };
            return Success(text);
        }
        catch (Exception ex)
        {
            // Mirror the SDK's default tool-invocation behavior: surface the failure in-band as
            // a CallToolResult with isError = true rather than as a JSON-RPC protocol error.
            return Error(ex.Message);
        }
    }

    private static Task<string> InvokeGetContextAsync(
        RequestContext<CallToolRequestParams> context, IDictionary<string, JsonElement>? args)
    {
        var service = context.Services!.GetRequiredService<IContextService>();
        return CodeContextTools.GetContext(
            service,
            Str(args, "identifier") ?? throw new ArgumentException("identifier is required."),
            Str(args, "type"),
            Int(args, "depth", 1),
            Bool(args, "includeTests", false),
            Bool(args, "includeContent", false),
            NBool(args, "exact"),
            Bool(args, "includeRelated", false),
            Bool(args, "includeMetrics", false),
            Int(args, "maxMatches", 5),
            Int(args, "maxRelationships", 10),
            Int(args, "maxCallSites", 3),
            Int(args, "maxTestFiles", 5),
            Int(args, "maxTestMethods", 5),
            Bool(args, "expandAmbiguous", false),
            Str(args, "containingType"),
            Str(args, "namespace"),
            Str(args, "signature"),
            Str(args, "sourceFile"),
            Str(args, "relation"),
            View(args));
    }

    private static Task<string> InvokeGetMultiContextAsync(
        RequestContext<CallToolRequestParams> context, IDictionary<string, JsonElement>? args)
    {
        var service = context.Services!.GetRequiredService<IContextService>();
        return CodeContextTools.GetMultiContext(
            service,
            StrArray(args, "identifiers"),
            Str(args, "type"),
            Int(args, "depth", 1),
            Bool(args, "includeTests", false),
            NBool(args, "exact"),
            Int(args, "maxRelationships", 3),
            Int(args, "maxCallSites", 3),
            Int(args, "maxTestFiles", 5),
            Int(args, "maxTestMethods", 5),
            Bool(args, "expandAmbiguous", false),
            Str(args, "containingType"),
            Str(args, "namespace"),
            Str(args, "signature"),
            Str(args, "sourceFile"),
            Str(args, "relation"),
            View(args));
    }

    private static Task<string> InvokeGetStatusAsync(RequestContext<CallToolRequestParams> context)
    {
        var service = context.Services!.GetRequiredService<IStatusService>();
        return CodeContextTools.GetStatus(service);
    }

    private static CallToolResult Success(string text) =>
        new() { Content = { new TextContentBlock { Text = text } } };

    private static CallToolResult Error(string text) =>
        new() { IsError = true, Content = { new TextContentBlock { Text = text } } };

    // Argument extractors mirroring the defaults the [Description]/optional-parameter binding
    // produced. Missing or wrong-kind values fall back to the declared default.
    private static string? Str(IDictionary<string, JsonElement>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(IDictionary<string, JsonElement>? args, string key, int fallback) =>
        args is not null && args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : fallback;

    private static bool Bool(IDictionary<string, JsonElement>? args, string key, bool fallback) =>
        args is not null && args.TryGetValue(key, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static bool? NBool(IDictionary<string, JsonElement>? args, string key) =>
        args is not null && args.TryGetValue(key, out var v)
        && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private static string[] StrArray(IDictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var element in v.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
                list.Add(s);
        }
        return list.ToArray();
    }

    private static ContextResponseView View(IDictionary<string, JsonElement>? args) =>
        args is not null && args.TryGetValue("view", out var v) && v.ValueKind == JsonValueKind.String
        && string.Equals(v.GetString(), "Full", StringComparison.OrdinalIgnoreCase)
            ? ContextResponseView.Full
            : ContextResponseView.Compact;
}
