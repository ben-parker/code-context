using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;

namespace CodeContext.Api.Commands;

/// <summary>
/// <c>query path</c>, <c>query affected</c>, <c>query hubs</c> and <c>query impact</c>: the graph endpoints, rendered like
/// <c>query</c>.
/// </summary>
public static partial class QueryCommandHandler
{
    /// <summary>Which query modes read each option; any other is refused rather than ignored.</summary>
    private static readonly (string Option, string[] Modes)[] OptionModes =
    [
        ("--depth", ["context", GraphQueryParameters.PathQuery, GraphQueryParameters.AffectedQuery, GraphQueryParameters.ImpactQuery]),
        ("--exact", ["context"]),
        ("--limit", ["context", GraphQueryParameters.AffectedQuery, GraphQueryParameters.HubsQuery, GraphQueryParameters.ImpactQuery]),
        ("--external", ["context", GraphQueryParameters.HubsQuery]),
        ("--within", [GraphQueryParameters.HubsQuery]),
        ("--type", [GraphQueryParameters.HubsQuery]),
        ("--diff", [GraphQueryParameters.ImpactQuery]),
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
        (GraphQueryParameters.ImpactQuery, "--depth") =>
            $"Most dependency hops followed from the changes (1-{GraphQueryLimits.MaxDepth}, default {GraphQueryLimits.DefaultAffectedDepth}).",
        (GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery, "--limit") =>
            $"Most entries listed (1-{GraphQueryLimits.MaxLimit}, default {GraphQueryLimits.DefaultLimit}).",
        (GraphQueryParameters.ImpactQuery, "--limit") =>
            $"Most entries in each list: changes, unmapped, dependents, test files (1-{GraphQueryLimits.MaxLimit}, default {GraphQueryLimits.DefaultLimit}).",
        (GraphQueryParameters.ImpactQuery, "--tests") =>
            "List dependents in test files and follow impact on from them (otherwise counted, and their files listed).",
        (GraphQueryParameters.PathQuery or GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery, "--tests") =>
            "Include symbols in test files (left out by default).",
        (GraphQueryParameters.PathQuery or GraphQueryParameters.AffectedQuery or GraphQueryParameters.HubsQuery or GraphQueryParameters.ImpactQuery, "--relation") =>
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
        if (graph.Query == GraphQueryParameters.ImpactQuery && settings.Identifiers.Count > 0)
            return "query impact takes no identifier: it reads a unified diff from standard input or --diff FILE.";
        if (settings.Identifiers.Count != expected || settings.Identifiers.Any(string.IsNullOrWhiteSpace))
        {
            return graph.Query switch
            {
                GraphQueryParameters.PathQuery => "query path needs two non-empty identifiers: <from> <to>.",
                GraphQueryParameters.AffectedQuery => "query affected needs one non-empty identifier.",
                GraphQueryParameters.ImpactQuery => "query impact takes no identifier.",
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

    /// <summary>
    /// The diff of <c>query impact</c>, from <c>--diff FILE</c> or standard input, read up to the API's limit and checked
    /// as the instance would check it; null, with the reason written, when it cannot be used. Nothing is run to make it.
    /// </summary>
    private static string? ReadDiff(GraphQuerySettings graph, QueryRuntime runtime)
    {
        string text;
        var source = graph.Diff is null or "-" ? "standard input" : graph.Diff;
        try
        {
            if (graph.Diff is null or "-")
            {
                if (!runtime.InputRedirected || runtime.Input is null)
                {
                    runtime.Error.WriteLine("query impact reads a unified diff from standard input or --diff FILE, and none was given: "
                        + "git diff $(git merge-base main HEAD) | codecontext query impact");
                    return null;
                }
                text = ReadBounded(runtime.Input);
            }
            else
            {
                if (new FileInfo(graph.Diff) is { Exists: true, Length: var length } && length > 4L * UnifiedDiffLimit)
                    throw new InvalidDataException($"it is {length:N0} bytes");
                using var reader = new StreamReader(graph.Diff, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = ReadBounded(reader);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            runtime.Error.WriteLine($"Could not read the diff from {source}: {ex.Message.TrimEnd('.')}.");
            return null;
        }
        try
        {
            CodeContext.Core.Diffs.UnifiedDiff.Parse(text);
        }
        catch (ArgumentException ex)
        {
            runtime.Error.WriteLine($"The diff from {source} is refused: {ex.Message}");
            return null;
        }
        return text;
    }

    private const int UnifiedDiffLimit = CodeContext.Core.Diffs.UnifiedDiff.MaxLength;

    /// <summary>All of <paramref name="reader"/>, refusing more than the API reads.</summary>
    private static string ReadBounded(TextReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[64 * 1024];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > UnifiedDiffLimit)
                throw new InvalidDataException($"it is longer than the {UnifiedDiffLimit:N0} characters read; split it by path (git diff <base> -- <dir>)");
        }
        return text.ToString();
    }

    /// <summary>The JSON body of an impact request: the diff and only the options given, so the defaults are the API's.</summary>
    private static string ImpactBody(QuerySettings settings, GraphQuerySettings graph)
    {
        using var buffer = new MemoryStream();
        // Not escaped beyond what JSON requires: the default writes '+' (every added line) and any non-ASCII character as
        // six bytes, and the body is read only as JSON, never as HTML.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("diff", graph.DiffText);
            if (graph.MaxDepth is { } depth) writer.WriteNumber("maxDepth", depth);
            if (!string.IsNullOrWhiteSpace(settings.Relation)) writer.WriteString("relation", settings.Relation);
            if (settings.IncludeTests) writer.WriteBoolean("includeTests", true);
            if (settings.Limit is { } limit) writer.WriteNumber("limit", limit);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<int> ExecuteGraphAsync(
        QuerySettings settings, InstanceRecord instance, QueryRuntime runtime, CancellationToken ct)
    {
        var graph = settings.Graph!;
        using var request = graph.Query == GraphQueryParameters.ImpactQuery
            ? new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{instance.Port}/api/graph/impact")
            {
                Content = new StringContent(ImpactBody(settings, graph), Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(
                HttpMethod.Get,
                $"http://localhost:{instance.Port}/api/graph/{graph.Query}?{GraphQueryString(settings, graph)}");
        HttpResponseMessage response;
        try
        {
            response = await runtime.Http.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout; the instance stops the walk when the request goes away.
            runtime.Error.WriteLine(
                $"{ModeName(graph.Query)} did not answer within {runtime.Http.Timeout.TotalSeconds:0} seconds and was stopped. "
                + (graph.Query == GraphQueryParameters.ImpactQuery
                    ? "On a very large graph narrow it: a smaller --depth, or a diff of fewer paths (git diff <base> -- <dir>)."
                    : "On a very large graph narrow it: --within DIR or --type for hubs, a smaller --depth for path and affected."));
            return 1;
        }
        using var answered = response;
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
            GraphQueryParameters.ImpactQuery => JsonSerializer.Serialize(
                await snapshot.Graph.GetImpactAsync(new GraphImpactRequest
                {
                    Diff = graph.DiffText!, MaxDepth = graph.MaxDepth ?? GraphQueryLimits.DefaultAffectedDepth,
                    Relation = relation, IncludeTests = settings.IncludeTests, Limit = settings.Limit ?? GraphQueryLimits.DefaultLimit,
                }, ct),
                options.GetTypeInfo(typeof(GraphImpactResponse))),
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
            case GraphQueryParameters.ImpactQuery:
            {
                var response = JsonSerializer.Deserialize(body, CodeContextJsonContext.Default.GraphImpactResponse);
                if (response is null) return EmptyResponse(runtime);
                if (settings.Json) output.Write(body);
                else if (settings.Human) WriteHumanImpact(response, output);
                else WriteAgentImpact(response, output);
                // Part of the diff is not covered: say so to a script too.
                return response.Complete ? 0 : 2;
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

    private static void WriteAgentNode(string prefix, CompactCodeNode node, TextWriter output, string? suffix = null, string? more = null)
    {
        output.Write(prefix);
        output.Write(Clean(node.Identifier));
        output.Write('\t');
        output.Write(Clean(node.Type ?? "Unknown"));
        WriteOptionalField(output, Location(node));
        WriteOptionalField(output, suffix);
        WriteOptionalField(output, more);
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

    private static void WriteAgentImpact(GraphImpactResponse response, TextWriter output)
    {
        WriteAgentCache(response.Cache, output);
        output.WriteLine($"impact\t{(response.Complete ? "complete" : "incomplete")}\t{response.ChangeCount} changed\t"
            + $"{response.TotalCount} affected in {response.FileCount} file(s)\tdepth {response.MaxDepth}"
            + (response.DepthLimited ? ", more beyond" : string.Empty) + $"\t{response.DiffFiles} file(s) in the diff");
        output.WriteLine($"content\t{response.ContentChecked} file(s) match the working tree"
            + (response.ContentUnchecked > 0 ? $"\t{response.ContentUnchecked} unchecked (the diff gives no blob id)" : string.Empty));

        output.WriteLine($"changed\t{response.ChangesReturned}/{response.ChangeCount}" + (response.ChangesTruncated ? "\ttruncated" : string.Empty));
        foreach (var change in response.Changes)
        {
            var lines = change.ChangedLines is { } changed ? $"lines {changed}" : "lines -";
            var removed = change.RemovedLines > 0 ? $", {change.RemovedLines} removed" : string.Empty;
            var tests = change.TestsHidden > 0 ? $" +{change.TestsHidden} in tests" : string.Empty;
            WriteAgentNode($"\t{change.Level}\t{lines}{removed}\t{change.Dependents} dependents{tests}\t", change, output,
                change.SameDeclaration is { Count: > 0 } copies ? $"same declaration as {string.Join(", ", copies)}" : null);
        }

        if (response.UnmappedCount > 0)
        {
            output.WriteLine($"unmapped\t{response.UnmappedReturned}/{response.UnmappedCount}" + (response.UnmappedTruncated ? "\ttruncated" : string.Empty)
                + $"\t{response.UnmappedLines} line(s)"
                + (response.UnmappedReasons is { Count: > 0 } reasons ? $"\t{string.Join(',', reasons.Select(pair => $"{pair.Key}={pair.Value}"))}" : string.Empty));
            foreach (var entry in response.Unmapped)
            {
                var where = entry.OldFile is { } old ? $"{entry.File} (from {old})" : entry.File;
                var lines = string.Join(' ', new[]
                {
                    entry.Lines is { } newLines ? $"lines {newLines}" : null,
                    entry.OldLines is { } oldLines ? $"old {oldLines}" : null,
                }.Where(part => part is not null));
                output.WriteLine($"\t{Clean(entry.Reason)}\t{Clean(where)}\t{(lines.Length > 0 ? lines : "-")}\t{Clean(entry.Detail)}");
            }
        }
        if (response.IgnoredLines > 0 || response.ModeOnlyFiles > 0)
            output.WriteLine($"ignored\t{response.IgnoredLines} blank or comment-only line(s)\t{response.ModeOnlyFiles} mode-only file(s)");

        output.WriteLine($"affected\t{response.ReturnedCount}/{response.TotalCount}" + (response.Truncated ? "\ttruncated" : string.Empty)
            + $"\tdepth {response.MaxDepth}" + (response.DepthLimited ? ", more beyond" : string.Empty) + $"\t{response.FileCount} file(s)");
        if (response.TestsHidden > 0) output.WriteLine($"testsHidden\t{response.TestsHidden}\t--tests lists them");
        foreach (var item in response.Items)
        {
            var how = item.Through is { } through ? $"{item.Relation} through {through}" : item.Relation;
            WriteAgentNode($"\t{item.Distance}\t{Clean(how)}\t", item, output, $"via {item.Via}", $"from {item.Seed}");
        }
        if (response.TestFileCount > 0)
        {
            output.WriteLine($"tests\t{response.TestFiles.Count}/{response.TestFileCount}" + (response.TestFilesTruncated ? "\ttruncated" : string.Empty));
            foreach (var file in response.TestFiles) output.WriteLine($"\t{Clean(file)}");
        }
        if (!string.IsNullOrWhiteSpace(response.Hint)) output.WriteLine($"hint\t{Clean(response.Hint)}");
        WriteCoverage(response.Coverage, "coverage\t{0}\t", response.Cache, output);
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

    private static void WriteHumanImpact(GraphImpactResponse response, TextWriter output)
    {
        WriteHumanCache(response.Cache, output);
        output.WriteLine(response.Complete
            ? $"Every changed line of the {response.DiffFiles} file(s) in the diff maps to a declaration."
            : $"Incomplete: part of the {response.DiffFiles} file(s) in the diff maps to no declaration; see Not mapped.");
        output.WriteLine($"Files matching the working tree: {response.ContentChecked}"
            + (response.ContentUnchecked > 0 ? $"; mapped unchecked (no blob id in the diff): {response.ContentUnchecked}" : string.Empty));
        output.WriteLine($"Changed declarations: {response.ChangesReturned}/{response.ChangeCount}{(response.ChangesTruncated ? ", truncated" : string.Empty)}");
        foreach (var change in response.Changes)
        {
            output.WriteLine($"  {change.Identifier} [{change.Type ?? "Unknown"}]{HumanLocation(change)}");
            var lines = change.ChangedLines is { } changed ? $"lines {changed}" : "removed lines only";
            output.WriteLine($"     {(change.Level == "container" ? "its own lines (not its members)" : "member")}: {lines}"
                + (change.RemovedLines > 0 ? $", {change.RemovedLines} removed" : string.Empty)
                + $"; {change.Dependents} dependent(s){(change.TestsHidden > 0 ? $" and {change.TestsHidden} in test files" : string.Empty)}");
        }
        if (response.UnmappedCount > 0)
        {
            output.WriteLine($"Not mapped: {response.UnmappedReturned}/{response.UnmappedCount}{(response.UnmappedTruncated ? ", truncated" : string.Empty)}, "
                + $"{response.UnmappedLines} line(s)");
            foreach (var entry in response.Unmapped)
            {
                var where = entry.OldFile is { } old ? $"{entry.File} (from {old})" : entry.File;
                var lines = entry.Lines is { } newLines ? $" lines {newLines}" : string.Empty;
                var oldLines = entry.OldLines is { } removed ? $" old lines {removed}" : string.Empty;
                output.WriteLine($"  - {where}{lines}{oldLines} [{entry.Reason}]: {entry.Detail}");
            }
        }
        if (response.IgnoredLines > 0 || response.ModeOnlyFiles > 0)
            output.WriteLine($"Ignored: {response.IgnoredLines} blank or comment-only line(s), {response.ModeOnlyFiles} mode-only file(s)");
        output.WriteLine($"Affected: {response.ReturnedCount}/{response.TotalCount}{(response.Truncated ? ", truncated" : string.Empty)}, "
            + $"within {response.MaxDepth} hop(s){(response.DepthLimited ? " (more beyond)" : string.Empty)}, in {response.FileCount} file(s)");
        if (response.TestsHidden > 0) output.WriteLine($"Test-file symbols affected: {response.TestsHidden} (--tests lists them)");
        foreach (var item in response.Items)
        {
            var through = item.Through is { } declaration ? $" through {declaration}" : string.Empty;
            output.WriteLine($"  {item.Distance}. {item.Identifier} [{item.Type ?? "Unknown"}]{HumanLocation(item)}");
            output.WriteLine($"     {item.Relation}{through} {item.Via}, from the change to {item.Seed}");
        }
        if (response.TestFileCount > 0)
        {
            output.WriteLine($"Test files in reach: {response.TestFiles.Count}/{response.TestFileCount}{(response.TestFilesTruncated ? ", truncated" : string.Empty)}");
            foreach (var file in response.TestFiles) output.WriteLine($"  - {file}");
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
