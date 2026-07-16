using CodeContext.Core;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeContext.Api.Endpoints;

public static class CodeContextEndpoints
{
    public static IEndpointRouteBuilder MapCodeContextEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1")
            .WithOpenApi()
            .WithTags("CodeContext");

        // API status endpoint
        app.MapGet("/api/status", async (IStatusService statusService) =>
        {
            var status = await statusService.GetStatusAsync();
            return Results.Ok(status);
        })
        .WithName("GetStatus")
        .WithSummary("Get API status")
        .WithDescription("Returns comprehensive system health and debugging information.")
        .Produces<StatusResponseDto>(StatusCodes.Status200OK);

        // Main context endpoint
        app.MapGet("/api/context/complete", async (
            IContextService contextService,
            string identifier,
            string? type = null,
            int depth = 1,
            bool includeTests = false,
            bool includeContent = false,
            bool? exact = null,
            bool includeRelated = false,
            bool includeMetrics = false,
            int maxMatches = 5,
            int maxRelationships = 10,
            int maxCallSites = 3,
            int maxTestFiles = 5,
            int maxTestMethods = 5,
            bool expandAmbiguous = false,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null,
            string? relation = null,
            string view = "compact") =>
        {
            try
            {
                var responseView = ParseView(view);
                if (responseView == ContextResponseView.Full)
                {
                    if (!string.IsNullOrEmpty(relation))
                        throw new ArgumentException(
                            "relation is only supported for view=compact.", nameof(relation));
                    var full = await contextService.GetCompleteContextAsync(
                        identifier, type, depth, includeTests, includeContent, exact ?? false,
                        includeRelated, includeMetrics, maxTestFiles, maxTestMethods, containingType,
                        @namespace, signature, sourceFile);
                    return Results.Ok(full);
                }

                var compact = await contextService.GetCompactContextAsync(
                    identifier, type, depth, includeTests, includeContent, exact,
                    includeRelated, includeMetrics, maxMatches, maxRelationships, expandAmbiguous,
                    maxCallSites, maxTestFiles, maxTestMethods, containingType, @namespace, signature,
                    sourceFile, relation);
                return Results.Ok(compact);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = new
                    {
                        code = "CONTEXT_ERROR",
                        message = ex.Message,
                        details = new
                        {
                            identifier, type, depth, includeTests, includeContent, exact,
                            includeRelated, includeMetrics, maxMatches, maxRelationships,
                            maxCallSites, maxTestFiles, maxTestMethods, expandAmbiguous, containingType,
                            @namespace, signature, sourceFile, relation, view
                        }
                    }
                });
            }
        })
        .WithName("GetCompleteContext")
        .WithSummary("Get complete context for an identifier")
        .WithDescription("Returns comprehensive context information for a code construct. Searches by name or file path. " +
                        "Without 'type' parameter, searches across all entity types and returns best match or multiple matches. " +
                        "With 'type' parameter, searches only within specified type. " +
                        "Returns array of matches to handle ambiguity.")
        // 206 is a schema-generation anchor; the document transformer folds it into
        // the v1 200-response union and removes the synthetic response.
        .Produces<CompleteContextResponse>(StatusCodes.Status206PartialContent)
        .Produces<CompactContextResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        // Multi-context endpoint
        app.MapPost("/api/context/multi", async (
            IContextService contextService,
            MultiContextRequest request) =>
        {
            try
            {
                if (request.View == ContextResponseView.Full)
                {
                    return Results.Ok(await contextService.GetMultipleContextAsync(request));
                }

                return Results.Ok(await contextService.GetMultipleCompactContextAsync(request));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = new
                    {
                        code = "MULTI_CONTEXT_ERROR",
                        message = ex.Message,
                        details = request
                    }
                });
            }
        })
        .WithName("GetMultipleContext")
        .WithSummary("Get context for multiple identifiers")
        .WithDescription("Returns context for multiple code constructs as a round-trip optimization. " +
                        "It reduces HTTP requests, not response-token size.")
        .Accepts<MultiContextRequest>("application/json")
        .Produces<List<CompleteContextResponse>>(StatusCodes.Status206PartialContent)
        .Produces<List<CompactContextResponse>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest);

        // Index refresh endpoint. All work is queued on the index coordinator — the
        // endpoint owns no background tasks and mutations stay ordered.
        app.MapPost("/api/index/refresh", async (
            IIndexCoordinator coordinator,
            string? path = null) =>
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    var operationId = await coordinator.TryRequestFullRescanAsync();
                    if (operationId is null)
                    {
                        return Results.Conflict(new { error = new { code = "SCAN_IN_PROGRESS", message = "A scan is already running." } });
                    }

                    return Results.Accepted(value: new RefreshStartedResponseDto(
                        "Full rescan started. Poll /api/status until indexing.status == \"ready\" and indexing.operationId >= this operationId.",
                        operationId.Value));
                }
                else
                {
                    // Refresh specific file (waits for that file to be processed).
                    await coordinator.RefreshFileAsync(path);
                    return Results.Ok(new RefreshFileResponseDto($"Refreshed: {path}"));
                }
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return Results.Json(
                    new { error = new { code = "SHUTTING_DOWN", message = "The instance is shutting down." } },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new
                {
                    error = new
                    {
                        code = "REFRESH_ERROR",
                        message = ex.Message,
                        details = new { path }
                    }
                });
            }
        })
        .WithName("RefreshIndex")
        .WithSummary("Refresh the code index")
        .WithDescription("Triggers a re-parse of the specified file or the entire codebase. " +
                        "If path is provided, only that file is refreshed synchronously. " +
                        "If path is omitted, a full background rescan starts (202) and the " +
                        "response carries an operationId observable via /api/status; " +
                        "returns 409 if a scan is already in progress.")
        .Produces<RefreshFileResponseDto>(StatusCodes.Status200OK)
        .Produces<RefreshStartedResponseDto>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict);

        // Optional parser-native syntax trees supplement the normalized graph for
        // syntax-specific questions. They are generated on demand and never stored.
        app.MapPost("/api/syntax-tree", async (
            ILanguageWorkerService workers,
            NativeSyntaxTreeRequestDto request,
            CancellationToken ct) =>
        {
            try
            {
                var result = await workers.GetNativeSyntaxTreeAsync(
                    request.FilePath, request.Start, request.Length, request.MaxDepth, ct);
                var view = request.View.ToLowerInvariant();
                if (view == "full") return Results.Ok(result with { View = "full" });
                if (view != "compact")
                    throw new ArgumentException("view must be 'compact' or 'full'.", nameof(request.View));
                return Results.Ok(result with
                {
                    Tree = CompactNativeTree(result.Tree),
                    View = "compact"
                });
            }
            catch (NotSupportedException ex)
            {
                return Results.Json(
                    new { error = new { code = "NATIVE_TREE_UNSUPPORTED", message = ex.Message } },
                    statusCode: StatusCodes.Status501NotImplemented);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(
                    new { error = new { code = "FILE_NOT_INDEXED", message = ex.Message } });
            }
            catch (ParserWorkerUnavailableException ex)
            {
                return Results.Json(
                    new { error = new { code = "PARSER_UNAVAILABLE", message = ex.Message } },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (ParserWorkerFailedException ex)
            {
                return Results.Json(
                    new { error = new { code = "PARSER_FAILED", message = ex.Message } },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (JsonRpcRemoteException ex)
            {
                return Results.BadRequest(
                    new { error = new { code = "NATIVE_TREE_ERROR", message = ex.Message } });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(
                    new { error = new { code = "INVALID_NATIVE_TREE_REQUEST", message = ex.Message } });
            }
        })
        .WithName("GetNativeSyntaxTree")
        .WithSummary("Get a language-native syntax tree")
        .WithDescription("Returns an on-demand, parser-specific syntax tree for an indexed file. " +
                        "Use the normalized context endpoints for cross-language relationships; " +
                        "use this endpoint for exact syntax, tokens, nesting, and language-specific constructs.")
        .Accepts<NativeSyntaxTreeRequestDto>("application/json")
        .Produces<NativeSyntaxTreeResult>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status501NotImplemented)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // Liveness endpoint: answers as soon as the host is up, independent of indexing state.
        app.MapGet("/healthz", () => Results.Ok(new HealthzResponseDto("up")))
        .WithName("Healthz")
        .WithSummary("Liveness probe")
        .WithDescription("Returns 200 as soon as the HTTP host is accepting requests. " +
                        "Use /api/status to determine whether indexing has completed.")
        .Produces<HealthzResponseDto>(StatusCodes.Status200OK);

        // Graceful shutdown endpoint (localhost-only binding makes this acceptable for a local
        // dev tool). Requires this instance's ID so a stale registry record — or a client
        // aiming at a reused port — cannot stop an unrelated instance.
        app.MapPost("/api/shutdown", (
            IHostApplicationLifetime lifetime,
            Microsoft.Extensions.Options.IOptions<CodeContextOptions> options,
            string? instanceId = null) =>
        {
            if (string.IsNullOrEmpty(instanceId)
                || !string.Equals(instanceId, options.Value.InstanceId, StringComparison.Ordinal))
            {
                return Results.Json(
                    new { error = new { code = "INSTANCE_ID_MISMATCH", message = "Pass this instance's instanceId (see `codecontext list --json`) to shut it down." } },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                lifetime.StopApplication();
            });
            return Results.Accepted(value: new ShutdownResponseDto("shutting down"));
        })
        .WithName("Shutdown")
        .WithSummary("Gracefully stop this instance")
        .WithDescription("Requests a graceful shutdown of this CodeContext instance. " +
                        "Requires the target instance's instanceId as a query parameter. " +
                        "The instance unregisters itself from the instance registry on exit.")
        .Produces<ShutdownResponseDto>(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static JsonElement CompactNativeTree(JsonElement tree)
        => JsonSerializer.SerializeToElement(CompactNativeNode(tree));

    private static JsonObject CompactNativeNode(JsonElement node)
    {
        var compact = new JsonObject
        {
            ["kind"] = node.TryGetProperty("kind", out var kind) ? kind.GetString() : "Unknown",
        };
        if (TryGetCompactSpan(node, out var start, out var length))
            compact["span"] = new JsonArray(start, length);
        if (node.TryGetProperty("text", out var text)) compact["text"] = text.GetString();
        else if (node.TryGetProperty("valueText", out var valueText) && valueText.GetString() is { Length: > 0 } value)
            compact["text"] = value;

        CopyTrueMarker(node, compact, "childrenTruncated");
        CopyTrueMarker(node, compact, "textTruncated");
        CopyTrueMarker(node, compact, "isMissing");
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var child in children.EnumerateArray()) array.Add(CompactNativeNode(child));
            compact["children"] = array;
        }
        return compact;
    }

    private static bool TryGetCompactSpan(JsonElement node, out int start, out int length)
    {
        if (node.TryGetProperty("span", out var span)
            && span.TryGetProperty("start", out var spanStart)
            && span.TryGetProperty("length", out var spanLength))
        {
            start = spanStart.GetInt32();
            length = spanLength.GetInt32();
            return true;
        }
        if (node.TryGetProperty("start", out var tsStart)
            && node.TryGetProperty("end", out var tsEnd))
        {
            start = tsStart.GetInt32();
            length = tsEnd.GetInt32() - start;
            return true;
        }
        start = length = 0;
        return false;
    }

    private static void CopyTrueMarker(JsonElement source, JsonObject target, string name)
    {
        if (source.TryGetProperty(name, out var marker) && marker.ValueKind == JsonValueKind.True)
            target[name] = true;
    }

    private static ContextResponseView ParseView(string? view)
        => view?.ToLowerInvariant() switch
        {
            null or "" or "compact" => ContextResponseView.Compact,
            "full" => ContextResponseView.Full,
            _ => throw new ArgumentException("view must be 'compact' or 'full'.", nameof(view))
        };
}
