using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;

namespace CodeContext.Api.Commands;

/// <summary><c>query path</c>, <c>query affected</c> and <c>query hubs</c>: the graph endpoints, rendered like <c>query</c>.</summary>
public static partial class QueryCommandHandler
{
    /// <summary>Which query modes read each option; any other is refused rather than ignored.</summary>
    private static readonly (string Option, string[] Modes)[] OptionModes =
    [
        ("--depth", ["context", GraphQueryParameters.PathQuery, GraphQueryParameters.AffectedQuery]),
        ("--exact", ["context"]),
        ("--limit", ["context", GraphQueryParameters.AffectedQuery, GraphQueryParameters.HubsQuery]),
        ("--external", ["context", GraphQueryParameters.HubsQuery]),
        ("--within", [GraphQueryParameters.HubsQuery]),
        ("--type", [GraphQueryParameters.HubsQuery]),
    ];

    /// <summary>Whether <paramref name="mode"/> (<c>context</c> or a graph query) reads <paramref name="option"/>.</summary>
    internal static bool Reads(string mode, string option)
        => Array.Find(OptionModes, entry => entry.Option == option).Modes is not { } modes || modes.Contains(mode);

    /// <summary>What a graph query's help says of a shared option whose meaning or default differs there, or null.</summary>
    internal static string? GraphOptionDescription(string mode, string option) => (mode, option) switch
    {
        (GraphQueryParameters.PathQuery, "--depth") =>
            $"Most relationship hops searched (1-{GraphQueryLimits.MaxDepth}, default {GraphQueryLimits.DefaultPathDepth}).",
        (GraphQueryParameters.AffectedQuery, "--depth") =>
            $"Most dependency hops followed (1-{GraphQueryLimits.MaxDepth}, default {GraphQueryLimits.DefaultAffectedDepth}).",
        (GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery, "--limit") =>
            $"Most entries listed (1-{GraphQueryLimits.MaxLimit}, default {GraphQueryLimits.DefaultLimit}).",
        (GraphQueryParameters.PathQuery or GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery, "--tests") =>
            "Include symbols in test files (left out by default).",
        (GraphQueryParameters.PathQuery or GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery, "--relation") =>
            "Comma-separated relationship kinds followed or counted.",
        (GraphQueryParameters.HubsQuery, "--external") => "Rank library symbols too.",
        _ => null,
    };

    private static string ModeName(string mode) => mode == "context" ? "query" : $"query {mode}";

    /// <summary>The first option given that the query does not read, as an error line, or null.</summary>
    internal static string? InapplicableOption(QuerySettings settings)
    {
        var mode = settings.Graph?.Query ?? "context";
        foreach (var (option, modes) in OptionModes)
        {
            if (settings.Given?.Contains(option) != true || modes.Contains(mode)) continue;
            var reason = option == "--exact" && mode != "context"
                ? "graph queries always match identifiers exactly (canonical identifier, exact or qualified name, or file path)"
                : $"it applies only to {string.Join(", ", modes.Select(ModeName))}";
            return $"{option} does not apply to {ModeName(mode)}: {reason}.";
        }
        return null;
    }

    private static string? GraphArgumentProblem(QuerySettings settings, GraphQuerySettings graph)
    {
        var expected = graph.Query switch
        {
            GraphQueryParameters.PathQuery => 2,
            GraphQueryParameters.AffectedQuery => 1,
            _ => 0,
        };
        if (settings.Identifiers.Count != expected || settings.Identifiers.Any(string.IsNullOrWhiteSpace))
        {
            return graph.Query switch
            {
                GraphQueryParameters.PathQuery => "query path needs two non-empty identifiers: <from> <to>.",
                GraphQueryParameters.AffectedQuery => "query affected needs one non-empty identifier.",
                _ => "query hubs takes no identifier.",
            };
        }
        if (graph.MaxDepth is < 1 or > GraphQueryLimits.MaxDepth)
            return $"--depth must be between 1 and {GraphQueryLimits.MaxDepth} for {ModeName(graph.Query)} (got {graph.MaxDepth}).";
        return null;
    }

    private static string GraphQueryString(QuerySettings settings, GraphQuerySettings graph)
    {
        var query = new StringBuilder();
        void Add(string name, string? value)
        {
            if (value is null) return;
            query.Append(query.Length == 0 ? string.Empty : "&").Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
        switch (graph.Query)
        {
            case GraphQueryParameters.PathQuery:
                Add("from", settings.Identifiers[0]);
                Add("to", settings.Identifiers[1]);
                break;
            case GraphQueryParameters.AffectedQuery:
                Add("identifier", settings.Identifiers[0]);
                break;
        }
        Add("maxDepth", graph.MaxDepth?.ToString(CultureInfo.InvariantCulture));
        Add("relation", string.IsNullOrWhiteSpace(settings.Relation) ? null : settings.Relation);
        if (settings.IncludeTests) Add("includeTests", "true");
        Add("limit", settings.Limit?.ToString(CultureInfo.InvariantCulture));
        if (graph.Query == GraphQueryParameters.HubsQuery)
        {
            if (settings.External) Add("includeExternal", "true");
            Add("type", graph.Type);
            Add("within", graph.Within);
        }
        return query.ToString();
    }

    private static async Task<int> ExecuteGraphAsync(
        QuerySettings settings, InstanceRecord instance, QueryRuntime runtime, CancellationToken ct)
    {
        var graph = settings.Graph!;
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://localhost:{instance.Port}/api/graph/{graph.Query}?{GraphQueryString(settings, graph)}");
        using var response = await runtime.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadError(body) ?? body;
            runtime.Error.WriteLine(
                $"Query API rejected the request ({(int)response.StatusCode} {response.ReasonPhrase}): {message}");
            return 1;
        }
        return RenderGraph(body, settings, runtime);
    }

    private static string? TryReadError(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, CodeContextJsonContext.Default.ApiErrorResponse)?.Error.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The request <see cref="ExecuteGraphAsync"/> would send, answered by the same service with the endpoint's defaults.</summary>
    private static async Task<int> AnswerGraphFromSnapshotAsync(
        QuerySettings settings, OneShotSnapshot snapshot, QueryRuntime runtime, CancellationToken ct)
    {
        var graph = settings.Graph!;
        var relation = string.IsNullOrWhiteSpace(settings.Relation) ? null : settings.Relation;
        var options = ProgramHelpers.ApiJsonOptions;
        string body = graph.Query switch
        {
            GraphQueryParameters.PathQuery => JsonSerializer.Serialize(
                await snapshot.Graph.FindPathAsync(new GraphPathRequest
                {
                    From = settings.Identifiers[0], To = settings.Identifiers[1],
                    MaxDepth = graph.MaxDepth ?? GraphQueryLimits.DefaultPathDepth, Relation = relation,
                    IncludeTests = settings.IncludeTests,
                }, ct),
                options.GetTypeInfo(typeof(GraphPathResponse))),
            GraphQueryParameters.AffectedQuery => JsonSerializer.Serialize(
                await snapshot.Graph.GetAffectedAsync(new GraphAffectedRequest
                {
                    Identifier = settings.Identifiers[0], MaxDepth = graph.MaxDepth ?? GraphQueryLimits.DefaultAffectedDepth,
                    Relation = relation, IncludeTests = settings.IncludeTests, Limit = settings.Limit ?? GraphQueryLimits.DefaultLimit,
                }, ct),
                options.GetTypeInfo(typeof(GraphAffectedResponse))),
            _ => JsonSerializer.Serialize(
                await snapshot.Graph.GetHubsAsync(new GraphHubsRequest
                {
                    Limit = settings.Limit ?? GraphQueryLimits.DefaultLimit, Relation = relation, Type = graph.Type,
                    Within = graph.Within, IncludeTests = settings.IncludeTests, IncludeExternal = settings.External,
                }, ct),
                options.GetTypeInfo(typeof(GraphHubsResponse))),
        };
        return RenderGraph(body, settings, runtime);
    }

    private static int RenderGraph(string body, QuerySettings settings, QueryRuntime runtime)
    {
        var output = runtime.Output;
        switch (settings.Graph!.Query)
        {
            case GraphQueryParameters.PathQuery:
            {
                var response = JsonSerializer.Deserialize(body, CodeContextJsonContext.Default.GraphPathResponse);
                if (response is null) return EmptyResponse(runtime);
                if (settings.Json) output.Write(body);
                else if (settings.Human) WriteHumanPath(response, output);
                else WriteAgentPath(response, output);
                return Resolved(response.From) && Resolved(response.To) ? 0 : 2;
            }
            case GraphQueryParameters.AffectedQuery:
            {
                var response = JsonSerializer.Deserialize(body, CodeContextJsonContext.Default.GraphAffectedResponse);
                if (response is null) return EmptyResponse(runtime);
                if (settings.Json) output.Write(body);
                else if (settings.Human) WriteHumanAffected(response, output);
                else WriteAgentAffected(response, output);
                return Resolved(response.Seed) ? 0 : 2;
            }
            default:
            {
                var response = JsonSerializer.Deserialize(body, CodeContextJsonContext.Default.GraphHubsResponse);
                if (response is null) return EmptyResponse(runtime);
                if (settings.Json) output.Write(body);
                else if (settings.Human) WriteHumanHubs(response, output);
                else WriteAgentHubs(response, output);
                return 0;
            }
        }
    }

    private static int EmptyResponse(QueryRuntime runtime)
    {
        runtime.Error.WriteLine("Query API returned an empty response.");
        return 1;
    }

    private static bool Resolved(GraphEndpoint endpoint) => !endpoint.Ambiguous && endpoint.TotalMatches > 0;

    // ---- Agent text ----

    /// <summary>A missing or ambiguous endpoint, or nothing for one that resolved. Returns whether it resolved.</summary>
    private static bool WriteAgentEndpoint(string role, GraphEndpoint endpoint, TextWriter output)
    {
        if (endpoint.TotalMatches == 0)
        {
            output.WriteLine($"missing\t{role}\t{Clean(endpoint.Requested)}");
        }
        else if (endpoint.Ambiguous)
        {
            output.WriteLine($"ambiguous\t{role}\t{Clean(endpoint.Requested)}\t{endpoint.TotalMatches} matches");
            foreach (var candidate in endpoint.Candidates ?? []) WriteAgentNode("\t", candidate, output);
        }
        else
        {
            // One declaration indexed more than once: every copy was searched, and the answer is about all of them.
            if (endpoint.SameDeclaration is { Count: > 0 } copies)
            {
                output.WriteLine($"sameDeclaration\t{role}\t{copies.Count + 1} copies searched together"
                    + $"\t{Clean(endpoint.Target?.Identifier)}\t{string.Join('\t', copies.Select(Clean))}");
            }
            return true;
        }
        if (!string.IsNullOrWhiteSpace(endpoint.Hint)) output.WriteLine($"hint\t{Clean(endpoint.Hint)}");
        return false;
    }

    private static void WriteAgentNode(string prefix, CompactCodeNode node, TextWriter output, string? suffix = null)
    {
        output.Write(prefix);
        output.Write(Clean(node.Identifier));
        output.Write('\t');
        output.Write(Clean(node.Type ?? "Unknown"));
        WriteOptionalField(output, Location(node));
        WriteOptionalField(output, suffix);
        output.WriteLine();
    }

    private static string Seed(GraphEndpoint endpoint)
        => endpoint.File is { } file ? $"file {file}" : endpoint.Target?.Identifier ?? endpoint.Requested;

    private static void WriteAgentPath(GraphPathResponse response, TextWriter output)
    {
        WriteAgentCache(response.Cache, output);
        var fromResolved = WriteAgentEndpoint("from", response.From, output);
        var toResolved = WriteAgentEndpoint("to", response.To, output);
        if (!fromResolved || !toResolved) return;

        if (response.Found && response.Path is { } path)
        {
            output.WriteLine($"path\t{response.Length} hop(s)\t{Clean(Seed(response.From))}\t{Clean(Seed(response.To))}");
            foreach (var hop in path)
            {
                var how = hop.Hop switch
                {
                    "start" => "start",
                    "uses" => hop.At is { } at ? $"{hop.Relation} line {at}" : hop.Relation,
                    _ => $"{hop.Hop} {hop.Relation}",
                };
                WriteAgentNode($"\t{Clean(how)}\t", hop, output);
            }
            return;
        }

        output.WriteLine("path\tnone"
            + (response.ExcludedBy is { Count: > 0 } excluded ? $"\texcludedBy {string.Join(',', excluded)}" : string.Empty)
            + (response.DepthLimited ? $"\tdepth {response.MaxDepth} reached" : string.Empty));
        if (!string.IsNullOrWhiteSpace(response.Explanation)) output.WriteLine($"explanation\t{Clean(response.Explanation)}");
        WriteCoverage(response.Coverage, "coverage\t{0}\t", response.Cache, output);
    }

    private static void WriteAgentAffected(GraphAffectedResponse response, TextWriter output)
    {
        WriteAgentCache(response.Cache, output);
        if (!WriteAgentEndpoint("identifier", response.Seed, output)) return;

        output.WriteLine($"seed\t{Clean(Seed(response.Seed))}"
            + (response.Seed.SeedCount > 1 ? $"\t{response.Seed.SeedCount} symbols with its members" : string.Empty));
        output.WriteLine($"affected\t{response.ReturnedCount}/{response.TotalCount}"
            + (response.Truncated ? "\ttruncated" : string.Empty)
            + $"\tdepth {response.MaxDepth}" + (response.DepthLimited ? ", more beyond" : string.Empty)
            + $"\t{response.FileCount} file(s)");
        if (response.TestsHidden > 0) output.WriteLine($"testsHidden\t{response.TestsHidden}\t--tests lists them");
        foreach (var item in response.Items)
        {
            var how = item.Through is { } through ? $"{item.Relation} through {through}" : item.Relation;
            WriteAgentNode($"\t{item.Distance}\t{Clean(how)}\t", item, output, $"via {item.Via}");
        }
        if (!string.IsNullOrWhiteSpace(response.Hint)) output.WriteLine($"hint\t{Clean(response.Hint)}");
        WriteCoverage(response.Coverage, "coverage\t{0}\t", response.Cache, output);
    }

    private static void WriteAgentHubs(GraphHubsResponse response, TextWriter output)
    {
        WriteAgentCache(response.Cache, output);
        output.WriteLine($"hubs\t{response.ReturnedCount}/{response.TotalCount}"
            + (response.Truncated ? "\ttruncated" : string.Empty)
            + (response.Within is { } within ? $"\twithin {Clean(within)}" : string.Empty));
        foreach (var hub in response.Items)
            WriteAgentNode($"\t{hub.Dependents} dependents\t{hub.DependentFiles} files\t", hub, output);
        if (!string.IsNullOrWhiteSpace(response.Hint)) output.WriteLine($"hint\t{Clean(response.Hint)}");
    }

    // ---- Human text ----

    private static bool WriteHumanEndpoint(string role, GraphEndpoint endpoint, TextWriter output)
    {
        if (endpoint.TotalMatches == 0) output.WriteLine($"No match for {role}: {endpoint.Requested}");
        else if (endpoint.Ambiguous)
        {
            output.WriteLine($"Ambiguous {role}: {endpoint.Requested} ({endpoint.TotalMatches} matches)");
            foreach (var candidate in endpoint.Candidates ?? [])
                output.WriteLine($"  - {candidate.Identifier} [{candidate.Type ?? "Unknown"}]{HumanLocation(candidate)}");
        }
        else
        {
            if (endpoint.SameDeclaration is { Count: > 0 } copies)
            {
                output.WriteLine($"{role} is one declaration indexed {copies.Count + 1} times, searched together: "
                    + $"{endpoint.Target?.Identifier}, {string.Join(", ", copies)}");
            }
            return true;
        }
        if (!string.IsNullOrWhiteSpace(endpoint.Hint)) output.WriteLine($"Hint: {endpoint.Hint}");
        return false;
    }

    private static string HumanLocation(CompactCodeNode node) => Location(node) is { } location ? $" — {location}" : string.Empty;

    private static void WriteHumanPath(GraphPathResponse response, TextWriter output)
    {
        WriteHumanCache(response.Cache, output);
        var fromResolved = WriteHumanEndpoint("from", response.From, output);
        var toResolved = WriteHumanEndpoint("to", response.To, output);
        if (!fromResolved || !toResolved) return;
        if (response.Found && response.Path is { } path)
        {
            output.WriteLine($"Path from {Seed(response.From)} to {Seed(response.To)}, {response.Length} hop(s):");
            foreach (var hop in path)
            {
                var how = hop.Hop switch
                {
                    "start" => string.Empty,
                    "uses" => $"{hop.Relation}{(hop.At is { } at ? $" (line {at})" : string.Empty)} → ",
                    "dispatch" => $"dispatches to ({hop.Relation}) → ",
                    "member" => "member → ",
                    _ => "member of → ",
                };
                output.WriteLine($"  {how}{hop.Identifier} [{hop.Type ?? "Unknown"}]{HumanLocation(hop)}");
            }
            return;
        }
        output.WriteLine($"No path from {Seed(response.From)} to {Seed(response.To)}.");
        if (!string.IsNullOrWhiteSpace(response.Explanation)) output.WriteLine(response.Explanation);
        WriteCoverage(response.Coverage, "Coverage ({0}): ", response.Cache, output);
    }

    private static void WriteHumanAffected(GraphAffectedResponse response, TextWriter output)
    {
        WriteHumanCache(response.Cache, output);
        if (!WriteHumanEndpoint("identifier", response.Seed, output)) return;
        output.WriteLine($"Affected by a change to {Seed(response.Seed)}: {response.ReturnedCount}/{response.TotalCount}"
            + (response.Truncated ? ", truncated" : string.Empty)
            + $", within {response.MaxDepth} hop(s){(response.DepthLimited ? " (more beyond)" : string.Empty)}, in {response.FileCount} file(s)");
        if (response.TestsHidden > 0) output.WriteLine($"Test-file symbols affected: {response.TestsHidden} (--tests lists them)");
        foreach (var item in response.Items)
        {
            var through = item.Through is { } declaration ? $" through {declaration}" : string.Empty;
            output.WriteLine($"  {item.Distance}. {item.Identifier} [{item.Type ?? "Unknown"}]{HumanLocation(item)}");
            output.WriteLine($"     {item.Relation}{through} {item.Via}");
        }
        if (!string.IsNullOrWhiteSpace(response.Hint)) output.WriteLine(response.Hint);
        WriteCoverage(response.Coverage, "Coverage ({0}): ", response.Cache, output);
    }

    private static void WriteHumanHubs(GraphHubsResponse response, TextWriter output)
    {
        WriteHumanCache(response.Cache, output);
        output.WriteLine($"Most depended-on symbols{(response.Within is { } within ? $" under {within}" : string.Empty)}: "
            + $"{response.ReturnedCount}/{response.TotalCount}{(response.Truncated ? ", truncated" : string.Empty)}");
        foreach (var hub in response.Items)
        {
            output.WriteLine($"  {hub.Dependents} dependents in {hub.DependentFiles} file(s): "
                + $"{hub.Identifier} [{hub.Type ?? "Unknown"}]{HumanLocation(hub)}");
        }
        if (!string.IsNullOrWhiteSpace(response.Hint)) output.WriteLine(response.Hint);
    }
}
