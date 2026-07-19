using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeContext.Core.Instances;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;

namespace CodeContext.Api.Commands;

public sealed record QuerySettings(
    IReadOnlyList<string> Identifiers,
    string Path,
    int Depth,
    bool IncludeTests,
    string? Relation,
    bool Exact,
    bool Json,
    bool Multi,
    bool Human = false);

internal sealed record QueryRuntime(
    IInstanceRegistry Registry,
    HttpClient Http,
    Func<string, CancellationToken, Task<DetachedStartResult>> StartDetachedAsync,
    TextWriter Output,
    TextWriter Error,
    TimeProvider TimeProvider,
    Func<TimeSpan, CancellationToken, Task> DelayAsync,
    TimeSpan ReadinessTimeout,
    Func<string, bool>? DirectoryExists = null);

public static class QueryCommandHandler
{
    public static async Task<int> ExecuteAsync(QuerySettings settings, CancellationToken ct)
    {
        var registry = new InstanceRegistry();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var orchestrator = new DetachedStartOrchestrator(registry);
        var runtime = new QueryRuntime(
            registry,
            http,
            (root, token) => orchestrator.StartAsync(root, null, 120, null, token),
            Console.Out,
            Console.Error,
            TimeProvider.System,
            Task.Delay,
            TimeSpan.FromSeconds(30));

        return await ExecuteAsync(settings, runtime, ct);
    }

    internal static async Task<int> ExecuteAsync(
        QuerySettings settings,
        QueryRuntime runtime,
        CancellationToken ct)
    {
        if (settings.Identifiers.Count == 0 || settings.Identifiers.Any(string.IsNullOrWhiteSpace))
        {
            runtime.Error.WriteLine("At least one non-empty identifier is required.");
            return 1;
        }

        if (settings.Depth < 0)
        {
            runtime.Error.WriteLine("Depth must be non-negative.");
            return 1;
        }

        if (settings.Json && settings.Human)
        {
            runtime.Error.WriteLine("--json and --human cannot be combined.");
            return 1;
        }

        string lookupPath;
        try
        {
            lookupPath = StartCommandHandler.NormalizePath(settings.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            runtime.Error.WriteLine($"Invalid path '{settings.Path}': {ex.Message}");
            return 1;
        }

        var directoryExists = runtime.DirectoryExists ?? Directory.Exists;
        if (!directoryExists(lookupPath))
        {
            runtime.Error.WriteLine($"Path does not exist: {lookupPath}");
            return 1;
        }

        InstanceRecord? discovered;
        try
        {
            discovered = runtime.Registry.FindForPath(lookupPath);
        }
        catch (Exception ex)
        {
            runtime.Error.WriteLine($"Failed to discover a CodeContext instance: {ex.Message}");
            return 1;
        }

        InstanceRecord instance;
        if (discovered is null)
        {
            runtime.Error.WriteLine($"No running instance found. Starting CodeContext for {lookupPath}...");
            try
            {
                var started = await runtime.StartDetachedAsync(lookupPath, ct);
                if (!started.Success || started.Instance is null)
                {
                    runtime.Error.WriteLine(started.ErrorMessage ?? "Automatic detached startup failed.");
                    return 4;
                }
                instance = started.Instance;
                if (started.WasStarted)
                {
                    runtime.Error.WriteLine(
                        $"CodeContext started on port {instance.Port} (pid {instance.Pid}). Waiting for indexing...");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                runtime.Error.WriteLine($"Automatic detached startup failed: {ex.Message}");
                return 4;
            }
        }
        else
        {
            instance = discovered;
        }

        ReadinessOutcome readiness;
        try
        {
            readiness = await ReadinessWaiter.WaitUntilReadyAsync(
                instance,
                lookupPath,
                runtime.Http,
                runtime.Error,
                runtime.TimeProvider,
                runtime.DelayAsync,
                runtime.ReadinessTimeout,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.Error.WriteLine($"Failed to read instance status: {ex.Message}");
            return 1;
        }
        if (readiness.Result == ReadinessResult.Timeout)
        {
            runtime.Error.WriteLine(
                $"Indexing did not become ready within " +
                $"{runtime.ReadinessTimeout.TotalSeconds:0} seconds. Run " +
                $"codecontext status --path {QuotePath(lookupPath)} for details.");
            return 3;
        }
        if (readiness.Result == ReadinessResult.Invalid)
        {
            return 1;
        }

        try
        {
            return settings.Multi
                ? await ExecuteMultiAsync(settings, instance, runtime, ct)
                : await ExecuteSingleAsync(settings, instance, runtime, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            runtime.Error.WriteLine($"Query failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ExecuteSingleAsync(
        QuerySettings settings,
        InstanceRecord instance,
        QueryRuntime runtime,
        CancellationToken ct)
    {
        var query = new StringBuilder()
            .Append("identifier=").Append(Uri.EscapeDataString(settings.Identifiers[0]))
            .Append("&depth=").Append(settings.Depth);
        if (settings.IncludeTests) query.Append("&includeTests=true");
        if (!string.IsNullOrWhiteSpace(settings.Relation))
            query.Append("&relation=").Append(Uri.EscapeDataString(settings.Relation));
        if (settings.Exact) query.Append("&exact=true");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"http://localhost:{instance.Port}/api/context/complete?{query}");
        return await SendAndRenderAsync(request, settings, runtime, ct);
    }

    private static async Task<int> ExecuteMultiAsync(
        QuerySettings settings,
        InstanceRecord instance,
        QueryRuntime runtime,
        CancellationToken ct)
    {
        var requestBody = new MultiContextRequest
        {
            Identifiers = settings.Identifiers.ToList(),
            Depth = settings.Depth,
            IncludeTests = settings.IncludeTests,
            Exact = settings.Exact ? true : null,
            RelationshipTypes = ParseRelations(settings.Relation),
        };
        var body = JsonSerializer.Serialize(
            requestBody, CodeContextJsonContext.Default.MultiContextRequest);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://localhost:{instance.Port}/api/context/multi")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendAndRenderAsync(request, settings, runtime, ct);
    }

    private static async Task<int> SendAndRenderAsync(
        HttpRequestMessage request,
        QuerySettings settings,
        QueryRuntime runtime,
        CancellationToken ct)
    {
        using var response = await runtime.Http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            runtime.Error.WriteLine(
                $"Query API rejected the request ({(int)response.StatusCode} {response.ReasonPhrase}): {body}");
            return 1;
        }

        IReadOnlyList<CompactContextResponse> results;
        if (settings.Multi)
        {
            results = JsonSerializer.Deserialize(
                body, CodeContextJsonContext.Default.ListCompactContextResponse) ?? [];
            if (results.Count != settings.Identifiers.Count)
            {
                runtime.Error.WriteLine(
                    $"Query API returned {results.Count} results for {settings.Identifiers.Count} identifiers.");
                return 1;
            }
        }
        else
        {
            var result = JsonSerializer.Deserialize(
                body, CodeContextJsonContext.Default.CompactContextResponse);
            if (result is null)
            {
                runtime.Error.WriteLine("Query API returned an empty response.");
                return 1;
            }
            results = [result];
        }

        if (settings.Json)
        {
            runtime.Output.Write(body);
        }
        else
        {
            for (var index = 0; index < results.Count; index++)
            {
                if (settings.Multi && settings.Human)
                {
                    if (index > 0) runtime.Output.WriteLine();
                    runtime.Output.WriteLine($"== {settings.Identifiers[index]} ==");
                }
                else if (settings.Multi)
                {
                    runtime.Output.WriteLine($"query\t{Clean(settings.Identifiers[index])}");
                }

                if (settings.Human)
                    WriteHumanResult(results[index], settings.Identifiers[index], runtime.Output);
                else
                    WriteAgentResult(results[index], settings.Identifiers[index], runtime.Output);
            }
        }

        return results.Any(result => result.TotalMatches == 0 || result.Matches.Count == 0) ? 2 : 0;
    }

    private static void WriteAgentResult(
        CompactContextResponse response,
        string requestedIdentifier,
        TextWriter output)
    {
        if (response.TotalMatches == 0 || response.Matches.Count == 0)
        {
            output.WriteLine($"missing\t{Clean(requestedIdentifier)}");
            return;
        }

        if (response.Ambiguous
            || response.TotalMatches > 1
            || response.Truncated
            || !string.Equals(response.MatchMode, "exact", StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine(
                $"matches\t{response.ReturnedMatches}/{response.TotalMatches}\t{Clean(response.MatchMode)}" +
                (response.Truncated ? "\ttruncated" : string.Empty));
            if (!string.IsNullOrWhiteSpace(response.DisambiguationHint))
                output.WriteLine($"hint\t{Clean(response.DisambiguationHint)}");
        }

        foreach (var match in response.Matches)
        {
            var target = match.Target;
            output.Write("target\t");
            output.Write(Clean(target.Identifier));
            output.Write('\t');
            output.Write(Clean(target.Type ?? "Unknown"));
            WriteOptionalField(output, Location(target));
            WriteOptionalField(output, AgentSignature(target));
            output.WriteLine();

            WriteAgentRelationships(match.Relationships, output);
            if (match.Testing is not null) WriteAgentTesting(match.Testing, output);
        }
    }

    private static void WriteAgentRelationships(CompactRelationships? relationships, TextWriter output)
    {
        if (relationships is null || !HasAgentRelationships(relationships))
        {
            output.WriteLine("relationships\tnone");
            return;
        }

        WriteAgentNodeCategory("uses", relationships.Uses, relationships.UsesReturnedCount,
            relationships.UsesCount, relationships.UsesTruncated, output);
        WriteAgentNodeCategory("usedBy", relationships.UsedBy, relationships.UsedByReturnedCount,
            relationships.UsedByCount, relationships.UsedByTruncated, output);
        WriteAgentNodeCategory("transitiveUses", relationships.TransitiveUses,
            relationships.TransitiveUsesReturnedCount, relationships.TransitiveUsesCount,
            relationships.TransitiveUsesTruncated, output);
        WriteAgentNodeCategory("transitiveUsedBy", relationships.TransitiveUsedBy,
            relationships.TransitiveUsedByReturnedCount, relationships.TransitiveUsedByCount,
            relationships.TransitiveUsedByTruncated, output);
        WriteAgentFileCategory("fileDependencies", relationships.FileDependencies,
            relationships.FileDependenciesReturnedCount, relationships.FileDependenciesCount,
            relationships.FileDependenciesTruncated, output);
        WriteAgentFileCategory("fileDependents", relationships.FileDependents,
            relationships.FileDependentsReturnedCount, relationships.FileDependentsCount,
            relationships.FileDependentsTruncated, output);
        WriteAgentNodeCategory("relatedItems", relationships.RelatedItems,
            relationships.RelatedItemsReturnedCount, relationships.RelatedItemsCount,
            relationships.RelatedItemsTruncated, output);
    }

    private static bool HasAgentRelationships(CompactRelationships relationships)
        => CategoryHasValues(relationships.Uses, relationships.UsesCount, relationships.UsesTruncated)
            || CategoryHasValues(relationships.UsedBy, relationships.UsedByCount, relationships.UsedByTruncated)
            || CategoryHasValues(relationships.TransitiveUses, relationships.TransitiveUsesCount,
                relationships.TransitiveUsesTruncated)
            || CategoryHasValues(relationships.TransitiveUsedBy, relationships.TransitiveUsedByCount,
                relationships.TransitiveUsedByTruncated)
            || CategoryHasValues(relationships.FileDependencies, relationships.FileDependenciesCount,
                relationships.FileDependenciesTruncated)
            || CategoryHasValues(relationships.FileDependents, relationships.FileDependentsCount,
                relationships.FileDependentsTruncated)
            || CategoryHasValues(relationships.RelatedItems, relationships.RelatedItemsCount,
                relationships.RelatedItemsTruncated);

    private static bool CategoryHasValues<T>(IReadOnlyList<T>? items, int? total, bool truncated)
        => items is { Count: > 0 } || total is > 0 || truncated;

    private static void WriteAgentNodeCategory(
        string name,
        IReadOnlyList<CompactCodeNode>? nodes,
        int? returned,
        int? total,
        bool truncated,
        TextWriter output)
    {
        var returnedCount = returned ?? nodes?.Count ?? 0;
        var totalCount = total ?? nodes?.Count ?? 0;
        if (returnedCount == 0 && totalCount == 0 && !truncated) return;

        output.WriteLine($"{name}\t{returnedCount}/{totalCount}{(truncated ? "\ttruncated" : string.Empty)}");
        foreach (var node in nodes ?? [])
        {
            output.Write('\t');
            output.Write(node.Relations is { Count: > 0 } ? string.Join(',', node.Relations) : "-");
            output.Write('\t');
            output.Write(Clean(node.Name ?? node.Identifier));
            WriteOptionalField(output, node.Type);
            WriteOptionalField(output, Location(node));
            output.WriteLine();
        }
    }

    private static void WriteAgentFileCategory(
        string name,
        IReadOnlyList<string>? files,
        int? returned,
        int? total,
        bool truncated,
        TextWriter output)
    {
        var returnedCount = returned ?? files?.Count ?? 0;
        var totalCount = total ?? files?.Count ?? 0;
        if (returnedCount == 0 && totalCount == 0 && !truncated) return;

        output.WriteLine($"{name}\t{returnedCount}/{totalCount}{(truncated ? "\ttruncated" : string.Empty)}");
        foreach (var file in files ?? []) output.WriteLine($"\t{Clean(file)}");
    }

    private static void WriteAgentTesting(CompactTesting testing, TextWriter output)
    {
        output.Write(
            $"tests\t{testing.TestFilesReturnedCount}/{testing.TestFileCount}" +
            (testing.TestFilesTruncated ? "\ttruncated" : string.Empty) +
            (testing.DirectlyTested ? "\tdirect" : testing.IsTested ? "\tevidence" : "\tnone"));
        output.Write($"\trefs={testing.TestReferenceCount}\timpl={testing.TestImplementerCount}" +
            $"\theuristic={testing.HeuristicMatchCount}");
        output.WriteLine();

        foreach (var file in testing.TestFiles)
        {
            output.Write(
                $"\t{Clean(file.File)}\t{file.TestMethodsReturnedCount}/{file.TestCount}" +
                (file.TestMethodsTruncated ? "\ttruncated" : string.Empty));
            if (file.Evidence.Count > 0) output.Write($"\t{string.Join(',', file.Evidence)}");
            output.WriteLine();
            foreach (var method in file.TestMethods)
            {
                output.Write($"\t\t{Clean(method.Name ?? method.Identifier)}\t{Clean(method.Type ?? "Unknown")}");
                WriteOptionalField(output, Location(method));
                output.WriteLine();
            }
        }
    }

    private static string? Location(CompactCodeNode node)
        => string.IsNullOrEmpty(node.File)
            ? null
            : $"{node.File}{(node.Line > 0 ? $":{node.Line}" : string.Empty)}";

    private static string? AgentSignature(CompactCodeNode node)
        => string.IsNullOrEmpty(node.Signature)
            || node.Identifier.EndsWith(node.Signature, StringComparison.Ordinal)
            ? null
            : node.Signature;

    private static void WriteOptionalField(TextWriter output, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        output.Write('\t');
        output.Write(Clean(value));
    }

    private static string Clean(string? value)
        => (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteHumanResult(
        CompactContextResponse response,
        string requestedIdentifier,
        TextWriter output)
    {
        if (response.TotalMatches == 0 || response.Matches.Count == 0)
        {
            output.WriteLine($"No match: {requestedIdentifier}");
            return;
        }

        if (response.Ambiguous || response.TotalMatches > 1)
        {
            output.WriteLine(
                $"Matches: {response.ReturnedMatches}/{response.TotalMatches} " +
                $"({response.MatchMode}{(response.Truncated ? ", truncated" : string.Empty)})");
            if (!string.IsNullOrWhiteSpace(response.DisambiguationHint))
                output.WriteLine($"Hint: {response.DisambiguationHint}");
        }

        for (var index = 0; index < response.Matches.Count; index++)
        {
            if (index > 0) output.WriteLine();
            var match = response.Matches[index];
            var target = match.Target;
            output.WriteLine($"{target.Name ?? "<unnamed>"} [{target.Type ?? "Unknown"}]");
            output.WriteLine($"  Identifier: {target.Identifier}");
            if (!string.IsNullOrEmpty(target.File))
                output.WriteLine($"  Location: {target.File}{(target.Line > 0 ? $":{target.Line}" : string.Empty)}");
            if (!string.IsNullOrEmpty(target.Signature))
                output.WriteLine($"  Signature: {target.Signature}");

            WriteRelationships(match.Relationships, output);
            if (match.Testing is not null) WriteTesting(match.Testing, output);
        }
    }

    private static void WriteRelationships(CompactRelationships? relationships, TextWriter output)
    {
        if (relationships is null || !HasRelationships(relationships))
        {
            output.WriteLine("  Relationships: none");
            return;
        }

        WriteNodeCategory("Uses", relationships.Uses, relationships.UsesReturnedCount,
            relationships.UsesCount, relationships.UsesTruncated, output);
        WriteNodeCategory("Used by", relationships.UsedBy, relationships.UsedByReturnedCount,
            relationships.UsedByCount, relationships.UsedByTruncated, output);
        WriteNodeCategory("Transitive uses", relationships.TransitiveUses,
            relationships.TransitiveUsesReturnedCount, relationships.TransitiveUsesCount,
            relationships.TransitiveUsesTruncated, output);
        WriteNodeCategory("Transitive used by", relationships.TransitiveUsedBy,
            relationships.TransitiveUsedByReturnedCount, relationships.TransitiveUsedByCount,
            relationships.TransitiveUsedByTruncated, output);
        WriteFileCategory("File dependencies", relationships.FileDependencies,
            relationships.FileDependenciesReturnedCount, relationships.FileDependenciesCount,
            relationships.FileDependenciesTruncated, output);
        WriteFileCategory("File dependents", relationships.FileDependents,
            relationships.FileDependentsReturnedCount, relationships.FileDependentsCount,
            relationships.FileDependentsTruncated, output);
        WriteNodeCategory("Related items", relationships.RelatedItems,
            relationships.RelatedItemsReturnedCount, relationships.RelatedItemsCount,
            relationships.RelatedItemsTruncated, output);
        if (relationships.Truncated)
            output.WriteLine("  Relationship result is truncated.");
    }

    private static bool HasRelationships(CompactRelationships relationships)
        => relationships.UsesCount is not null
            || relationships.UsedByCount is not null
            || relationships.TransitiveUsesCount is not null
            || relationships.TransitiveUsedByCount is not null
            || relationships.FileDependenciesCount is not null
            || relationships.FileDependentsCount is not null
            || relationships.RelatedItemsCount is not null
            || relationships.Uses is { Count: > 0 }
            || relationships.UsedBy is { Count: > 0 }
            || relationships.TransitiveUses is { Count: > 0 }
            || relationships.TransitiveUsedBy is { Count: > 0 }
            || relationships.FileDependencies is { Count: > 0 }
            || relationships.FileDependents is { Count: > 0 }
            || relationships.RelatedItems is { Count: > 0 };

    private static void WriteNodeCategory(
        string name,
        IReadOnlyList<CompactCodeNode>? nodes,
        int? returned,
        int? total,
        bool truncated,
        TextWriter output)
    {
        if (nodes is null && total is null) return;
        var returnedCount = returned ?? nodes?.Count ?? 0;
        var totalCount = total ?? nodes?.Count ?? 0;
        output.WriteLine($"  {name} ({returnedCount}/{totalCount}{(truncated ? ", truncated" : string.Empty)}):");
        foreach (var node in nodes ?? [])
        {
            var relations = node.Relations is { Count: > 0 }
                ? $" [{string.Join(',', node.Relations)}]"
                : string.Empty;
            var location = string.IsNullOrEmpty(node.File)
                ? string.Empty
                : $" — {node.File}{(node.Line > 0 ? $":{node.Line}" : string.Empty)}";
            output.WriteLine(
                $"    - {node.Name ?? node.Identifier} [{node.Type ?? "Unknown"}]{relations}{location}");
        }
    }

    private static void WriteFileCategory(
        string name,
        IReadOnlyList<string>? files,
        int? returned,
        int? total,
        bool truncated,
        TextWriter output)
    {
        if (files is null && total is null) return;
        var returnedCount = returned ?? files?.Count ?? 0;
        var totalCount = total ?? files?.Count ?? 0;
        output.WriteLine($"  {name} ({returnedCount}/{totalCount}{(truncated ? ", truncated" : string.Empty)}):");
        foreach (var file in files ?? []) output.WriteLine($"    - {file}");
    }

    private static void WriteTesting(CompactTesting testing, TextWriter output)
    {
        var direct = testing.DirectlyTested ? ", directly tested" : string.Empty;
        output.WriteLine(
            $"  Tests ({testing.TestFilesReturnedCount}/{testing.TestFileCount}" +
            $"{(testing.TestFilesTruncated ? ", truncated" : string.Empty)}): " +
            $"{(testing.IsTested ? "evidence found" : "no evidence")}{direct}; " +
            $"references {testing.TestReferenceCount}, implementers {testing.TestImplementerCount}, " +
            $"heuristic {testing.HeuristicMatchCount}");
        foreach (var file in testing.TestFiles)
        {
            output.WriteLine(
                $"    - {file.File} ({file.TestMethodsReturnedCount}/{file.TestCount}" +
                $"{(file.TestMethodsTruncated ? ", truncated" : string.Empty)})" +
                $"{(file.Evidence.Count > 0 ? $" [{string.Join(',', file.Evidence)}]" : string.Empty)}");
        }
    }

    private static List<string> ParseRelations(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string QuotePath(string path)
        => path.Any(char.IsWhiteSpace) ? $"\"{path}\"" : path;
}
