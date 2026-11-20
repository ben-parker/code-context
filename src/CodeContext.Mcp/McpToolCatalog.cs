using System.Text.Json;
using CodeContext.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
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
        {"type":"object","properties":{"identifiers":{"description":"List of identifiers to get context for","type":"array","items":{"type":"string"}},"type":{"description":"Optional type filter applied to every identifier","type":["string","null"],"default":null},"depth":{"description":"How many relationship levels to traverse","type":"integer","default":1},"includeTests":{"description":"Whether to include classified test evidence","type":"boolean","default":false},"exact":{"description":"Exact matching; omit for exact-first fallback","type":["boolean","null"],"default":null},"maxRelationships":{"description":"Maximum entries returned per relationship list","type":"integer","default":3},"maxCallSites":{"description":"Maximum source locations per aggregated relationship","type":"integer","default":3},"maxTestFiles":{"description":"Maximum test files returned; zero is count-only","type":"integer","default":5},"maxTestMethods":{"description":"Maximum test methods per file; zero is count-only","type":"integer","default":5},"expandAmbiguous":{"description":"Expand bounded ambiguous matches","type":"boolean","default":false},"containingType":{"description":"Containing type filter","type":["string","null"],"default":null},"namespace":{"description":"Exact namespace or module filter","type":["string","null"],"default":null},"signature":{"description":"Exact signature filter","type":["string","null"],"default":null},"sourceFile":{"description":"Repository-relative or absolute source file filter","type":["string","null"],"default":null},"relation":{"description":"Comma-separated relation kinds to filter uses/usedBy (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only","type":["string","null"],"default":null},"view":{"description":"Response shape; compact is the default","type":"string","enum":["Compact","Full"],"default":"Compact"},"includeExternal":{"description":"Whether to include library symbols, which name matches and uses otherwise leave out","type":"boolean","default":false}},"required":["identifiers"]}
        """;

    private const string GetContextSchema =
        """
        {"type":"object","properties":{"identifier":{"description":"Canonical returned identifier, symbol name, or file path","type":"string"},"type":{"description":"Filter by type (Class, Method, Interface, Property, etc.)","type":["string","null"],"default":null},"depth":{"description":"How many relationship levels to traverse (0-10)","type":"integer","default":1},"includeTests":{"description":"Whether to include test-related information","type":"boolean","default":false},"includeContent":{"description":"Whether to include file content snippets","type":"boolean","default":false},"exact":{"description":"Exact matching; omit for exact-first with substring fallback","type":["boolean","null"],"default":null},"includeRelated":{"description":"Whether to include loosely related symbols","type":"boolean","default":false},"includeMetrics":{"description":"Whether to include heuristic metrics","type":"boolean","default":false},"maxMatches":{"description":"Maximum ambiguous candidates returned","type":"integer","default":5},"maxRelationships":{"description":"Maximum entries returned per relationship list","type":"integer","default":10},"maxCallSites":{"description":"Maximum source locations returned per aggregated relationship; zero is count-only","type":"integer","default":3},"maxTestFiles":{"description":"Maximum test files returned; zero is count-only","type":"integer","default":5},"maxTestMethods":{"description":"Maximum test methods returned per file; zero is count-only","type":"integer","default":5},"expandAmbiguous":{"description":"Expand bounded ambiguous matches instead of returning summaries","type":"boolean","default":false},"containingType":{"description":"Filter members by their containing type","type":["string","null"],"default":null},"namespace":{"description":"Filter by exact namespace or module","type":["string","null"],"default":null},"signature":{"description":"Filter by exact signature","type":["string","null"],"default":null},"sourceFile":{"description":"Filter by repository-relative or absolute source file","type":["string","null"],"default":null},"relation":{"description":"Comma-separated relation kinds to filter uses/usedBy (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); compact view only","type":["string","null"],"default":null},"view":{"description":"Response shape; compact is the default","type":"string","enum":["Compact","Full"],"default":"Compact"},"includeExternal":{"description":"Whether to include library symbols, which name matches and uses otherwise leave out","type":"boolean","default":false}},"required":["identifier"]}
        """;

    private const string GetPathSchema =
        """
        {"type":"object","properties":{"from":{"description":"Where the path starts: canonical identifier, exact or qualified name, or file path (no substring match)","type":"string"},"to":{"description":"Where the path ends, as from","type":"string"},"maxDepth":{"description":"Most relationship hops searched (1-20)","type":"integer","default":10},"relation":{"description":"Comma-separated relation kinds a hop may follow (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); dispatch to implementations is always followed","type":["string","null"],"default":null},"includeTests":{"description":"Whether the path may pass through symbols in test files","type":"boolean","default":false}},"required":["from","to"]}
        """;

    private const string GetAffectedSchema =
        """
        {"type":"object","properties":{"identifier":{"description":"The symbol or file that would change: canonical identifier, exact or qualified name, or file path (no substring match)","type":"string"},"maxDepth":{"description":"Most dependency hops followed (1-20)","type":"integer","default":5},"relation":{"description":"Comma-separated relation kinds that carry impact (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); without it implementations of a changed declaration are affected too","type":["string","null"],"default":null},"includeTests":{"description":"Whether affected symbols in test files are listed and impact moves on from them (without it they are only counted)","type":"boolean","default":false},"limit":{"description":"Most entries returned (1-100)","type":"integer","default":20}},"required":["identifier"]}
        """;

    private const string GetHubsSchema =
        """
        {"type":"object","properties":{"limit":{"description":"Most entries returned (1-100)","type":"integer","default":20},"relation":{"description":"Comma-separated relation kinds counted (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES)","type":["string","null"],"default":null},"type":{"description":"Only symbols of this type (Class, Method, Interface, ...)","type":["string","null"],"default":null},"within":{"description":"Only symbols under this repository-relative directory or file","type":["string","null"],"default":null},"includeTests":{"description":"Whether symbols in test files are ranked and their uses counted","type":"boolean","default":false},"includeExternal":{"description":"Whether library symbols are ranked","type":"boolean","default":false}}}
        """;

    private const string GetImpactSchema =
        """
        {"type":"object","properties":{"diff":{"description":"A unified diff whose new side is the working tree (git diff <base> output, with its index lines). The diff is data: nothing is run.","type":"string"},"maxDepth":{"description":"Most dependency hops followed (1-20)","type":"integer","default":5},"relation":{"description":"Comma-separated relation kinds that carry impact (CALLS, MOCK_CALLS, REFERENCES, IMPLEMENTS, INHERITS, EXTENDS, IMPORTS, USES); without it implementations of a changed declaration are affected too","type":["string","null"],"default":null},"includeTests":{"description":"Whether dependents in test files are listed and impact moves on from them (without it they are counted, and their files listed in testFiles)","type":"boolean","default":false},"limit":{"description":"Most entries in each list: changes, unmapped, dependents, test files (1-100)","type":"integer","default":20}},"required":["diff"]}
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
            "Get context for multiple identifiers in a single request. Useful for batch operations. A cache object means the answer comes from a saved index not yet re-checked; a miss is then not conclusive.",
            GetMultiContextSchema),
        MakeTool(
            "get_context",
            "Get comprehensive context for a code identifier. Returns relationships, dependencies, tests, and metrics. A cache object means the answer comes from a saved index not yet re-checked; a miss is then not conclusive.",
            GetContextSchema),
        MakeTool(
            "get_status",
            "Get comprehensive system health and debugging information",
            GetStatusSchema),
        MakeTool(
            "get_path",
            "Find how one symbol reaches another: a shortest chain of recorded calls, references, inheritance and imports, with dispatch from an interface or base member to its implementations. When there is none, explanation says why, naming your own filters if they hid one. Static evidence, not runtime behavior.",
            GetPathSchema),
        MakeTool(
            "get_affected",
            "List what could break if a symbol or file changes: everything that uses it, transitively, nearest first, including callers through the interface or base member it implements. Nothing is added for merely sitting nearby. Test-file dependents are counted and listed with includeTests. Static evidence, not runtime behavior.",
            GetAffectedSchema),
        MakeTool(
            "get_impact",
            "List what a diff could break: the declarations its changed lines fall in, everything that depends on them (as get_affected, in one walk, each dependent attributed to the change that reached it first), and the test files in reach. A changed declaration is never listed as a dependent: usedByChanges names the other changes that use it. Parts of the diff that map to no declaration (imports, removed or renamed declarations, deleted, binary or unparsed files, a diff whose new side is not checked out) are listed in unmapped, and complete is then false; changed files that cannot change an indexed fact (a README, documentation: no worker parses, names or reported reading them) are listed in setAsideFiles and leave it true. Static evidence, not runtime behavior.",
            GetImpactSchema),
        MakeTool(
            "get_hubs",
            "List the load-bearing symbols: those with the most distinct direct dependents, optionally within a directory or of one type. Tests and library symbols are left out unless asked for.",
            GetHubsSchema),
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

        // An unknown tool name is a JSON-RPC *protocol* error (McpErrorCode.InvalidParams, -32602),
        // matching the SDK's built-in call-tool dispatcher on the pre-upgrade binary (verified over
        // stdio: `no_such_tool` returned {"error":{"code":-32602,...}}). It must be THROWN so the
        // request pipeline emits a JsonRpcError, not folded into the in-band isError path below.
        // McpProtocolException is the McpException subtype that carries the wire error code.
        if (name is not ("get_context" or "get_multi_context" or "get_status" or "get_path" or "get_affected" or "get_hubs" or "get_impact"))
            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);

        try
        {
            var text = name switch
            {
                "get_context" => await InvokeGetContextAsync(context, args),
                "get_multi_context" => await InvokeGetMultiContextAsync(context, args),
                "get_status" => await InvokeGetStatusAsync(context),
                "get_path" => await CodeContextTools.GetPath(Graph(context), GraphArguments(GraphQueryParameters.PathQuery, args), cancellationToken),
                "get_affected" => await CodeContextTools.GetAffected(Graph(context), GraphArguments(GraphQueryParameters.AffectedQuery, args), cancellationToken),
                "get_hubs" => await CodeContextTools.GetHubs(Graph(context), GraphArguments(GraphQueryParameters.HubsQuery, args), cancellationToken),
                "get_impact" => await CodeContextTools.GetImpact(Graph(context), GraphArguments(GraphQueryParameters.ImpactQuery, args), cancellationToken),
                _ => throw new InvalidOperationException("Unreachable: tool name validated above."),
            };
            return Success(text);
        }
        catch (Exception ex)
        {
            // Tool-body failures (missing/invalid arguments, service exceptions) surface in-band as
            // a CallToolResult with isError = true — the same taxonomy the old [McpServerTool]
            // wrapper produced (verified: missing identifier/identifiers and an invalid view all
            // returned isError results, not protocol errors). The old wrapper hid the underlying
            // exception behind a generic "An error occurred invoking '<tool>'." string; we surface
            // the real, actionable message instead (e.g. "identifier is required.").
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
            View(args),
            Bool(args, "includeExternal", false));
    }

    private static Task<string> InvokeGetMultiContextAsync(
        RequestContext<CallToolRequestParams> context, IDictionary<string, JsonElement>? args)
    {
        var service = context.Services!.GetRequiredService<IContextService>();
        return CodeContextTools.GetMultiContext(
            service,
            RequiredStrArray(args, "identifiers"),
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
            View(args),
            Bool(args, "includeExternal", false));
    }

    private static Task<string> InvokeGetStatusAsync(RequestContext<CallToolRequestParams> context)
    {
        var service = context.Services!.GetRequiredService<IStatusService>();
        return CodeContextTools.GetStatus(service);
    }

    private static IGraphQueryService Graph(RequestContext<CallToolRequestParams> context)
        => context.Services!.GetRequiredService<IGraphQueryService>();

    /// <summary>
    /// The arguments of a graph tool, checked strictly, unlike the context tools' lenient binding: a name the tool does not
    /// take (another graph query's option included), a value of the wrong JSON kind, or a missing required one is an
    /// error saying which, as REST answers 400. A JSON null is the same as leaving the argument out.
    /// </summary>
    internal static CodeContextTools.GraphArguments GraphArguments(string query, IDictionary<string, JsonElement>? args)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in args ?? new Dictionary<string, JsonElement>())
        {
            if (GraphQueryParameters.Find(query, key) is not { } parameter)
                throw new ArgumentException(GraphQueryParameters.NotApplicable(query, key));
            // The tool's own argument in another letter case: say that, not that it belongs to another query.
            if (parameter.Name != key)
                throw new ArgumentException($"'{key}' is written '{parameter.Name}' (argument names are case-sensitive).");
            if (value.ValueKind == JsonValueKind.Null) continue;
            var expected = parameter.Kind switch
            {
                GraphQueryParameters.Kind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                GraphQueryParameters.Kind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => value.ValueKind == JsonValueKind.String,
            };
            if (!expected)
            {
                throw new ArgumentException(parameter.Kind switch
                {
                    GraphQueryParameters.Kind.Integer => $"{key} must be an integer.",
                    GraphQueryParameters.Kind.Boolean => $"{key} must be true or false.",
                    _ => $"{key} must be a string.",
                });
            }
            values[key] = value;
        }
        foreach (var parameter in GraphQueryParameters.For(query))
        {
            if (parameter.Required && (!values.TryGetValue(parameter.Name, out var given) || string.IsNullOrWhiteSpace(given.GetString())))
                throw new ArgumentException($"{parameter.Name} is required.");
        }
        return new CodeContextTools.GraphArguments(values);
    }

    private static CallToolResult Success(string text) =>
        new() { Content = { new TextContentBlock { Text = text } } };

    private static CallToolResult Error(string text) =>
        new() { IsError = true, Content = { new TextContentBlock { Text = text } } };

    // Argument extractors mirroring the old [Description]/optional-parameter binding. Optional
    // scalars (Str/Int/Bool/NBool) fall back to their declared default on a missing or wrong-kind
    // value — the old binding likewise returned a success for e.g. depth="5" (verified). Required
    // or constrained inputs (identifiers, view) instead throw; see those methods.
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

    // A required string[] argument. The old reflection binding treated a missing or non-array
    // `identifiers` as a binding failure → in-band isError (verified: {} and a string value both
    // returned isError), while a present-but-empty array bound cleanly and returned an empty
    // success list. Throwing here reproduces that: the CallAsync catch turns it into an isError
    // result; an empty array falls through to a successful [] response.
    private static string[] RequiredStrArray(IDictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"{key} is required.");
        var list = new List<string>();
        foreach (var element in v.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { } s)
                list.Add(s);
        }
        return list.ToArray();
    }

    // Missing/null view defaults to Compact; "compact"/"full" (case-insensitive) parse. Any other
    // value throws — the old enum binding rejected an out-of-range view as a binding failure
    // (verified: view="bogus" returned isError), so silently coercing it to Compact would hide a
    // caller mistake. The throw is caught by CallAsync and surfaced as an isError result, matching
    // the old taxonomy (and REST's ParseView, which 400s on the same input).
    private static ContextResponseView View(IDictionary<string, JsonElement>? args)
    {
        if (args is null || !args.TryGetValue("view", out var v) || v.ValueKind == JsonValueKind.Null)
            return ContextResponseView.Compact;
        if (v.ValueKind == JsonValueKind.String)
        {
            switch (v.GetString()?.ToLowerInvariant())
            {
                case null or "" or "compact":
                    return ContextResponseView.Compact;
                case "full":
                    return ContextResponseView.Full;
            }
        }
        throw new ArgumentException("view must be 'Compact' or 'Full'.");
    }
}
