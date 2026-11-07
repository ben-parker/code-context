using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Mcp;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// The <c>cache</c> notice on context responses (plan 3.10): present, in REST's and MCP's DTO alike,
/// only while answers come from an unverified snapshot; with no cache involved every response is the
/// same bytes as before the cache existed.
/// </summary>
public class CacheSurfaceTests
{
    private static InMemoryDatabase Graph()
    {
        var owned = new Dictionary<string, string> { ["parserId"] = "csharp", ["workspaceId"] = "default" };
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1,
        [
            new CodeNode { Id = "csharp:default:App.Widget", Identifier = "csharp:App.Widget", Name = "Widget", Type = "Class", Language = "csharp", FilePath = "src/Widget.cs", StartLine = 3, Metadata = new Dictionary<string, string>(owned) },
            new CodeNode { Id = "csharp:default:App.Caller", Identifier = "csharp:App.Caller", Name = "Caller", Type = "Class", Language = "csharp", FilePath = "src/Caller.cs", StartLine = 5, Metadata = new Dictionary<string, string>(owned) },
        ],
        [
            new CodeEdge { Id = "csharp:default:App.Caller->App.Widget", SourceId = "csharp:default:App.Caller", TargetId = "csharp:default:App.Widget", Type = "REFERENCES", Metadata = new Dictionary<string, string>(owned) },
        ], scope: null));
        return database;
    }

    private static ContextService Service(InMemoryDatabase database, ICacheStateProvider? cache)
        => new(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database), new InMemoryFileMetadataRepository(),
            cacheState: cache);

    private static async Task<List<string>> AllResponsesAsync(ContextService service)
    {
        var request = new MultiContextRequest { Identifiers = ["Widget", "Nope"] };
        return
        [
            await CodeContextTools.GetContext(service, "Widget"),
            await CodeContextTools.GetContext(service, "Nope"),
            await CodeContextTools.GetContext(service, "Widget", view: ContextResponseView.Full),
            await CodeContextTools.GetMultiContext(service, ["Widget", "Nope"]),
            await CodeContextTools.GetMultiContext(service, ["Widget"], view: ContextResponseView.Full),
            JsonSerializer.Serialize(await service.GetMultipleCompactContextAsync(request), CodeContextJsonContext.Default.ListCompactContextResponse),
        ];
    }

    private static CacheState Verifying()
    {
        var state = new CacheState(enabled: true, () => new CacheLoadOutcome(CacheLoadKind.Loaded, null,
            new DateTime(2026, 11, 4, 10, 12, 3, DateTimeKind.Utc).Ticks, 42, 100, ["csharp"]));
        state.RunLoadStep();
        return state;
    }

    [Fact]
    public async Task NoCacheInvolved_ResponsesAreTheSameBytes()
    {
        var database = Graph();
        var withoutProvider = await AllResponsesAsync(Service(database, null));
        var off = new CacheState(enabled: false);
        off.RunLoadStep();
        var verified = Verifying();
        verified.OnScanCommitted();
        var ignored = new CacheState(enabled: true, () => new CacheLoadOutcome(CacheLoadKind.Ignored, "refused"));
        ignored.RunLoadStep();

        foreach (var provider in new ICacheStateProvider[] { off, verified, ignored })
            Assert.Equal(withoutProvider, await AllResponsesAsync(Service(database, provider)));
        Assert.All(withoutProvider, json => Assert.DoesNotContain("\"cache\"", json));

        // Pinned: the compact response as 0.10.x wrote it.
        Assert.Equal(
            "{\"view\":\"compact\",\"matchMode\":\"exact\",\"substringSearchSkipped\":true,\"totalMatches\":1,\"returnedMatches\":1,"
            + "\"matches\":[{\"target\":{\"name\":\"Widget\",\"type\":\"Class\",\"file\":\"src/Widget.cs\",\"line\":4,\"identifier\":\"csharp:App.Widget\"},"
            + "\"relationships\":{\"usedBy\":[{\"name\":\"Caller\",\"type\":\"Class\",\"file\":\"src/Caller.cs\",\"line\":6,\"relations\":[\"REFERENCES\"],"
            + "\"occurrences\":1,\"callSiteCount\":0,\"identifier\":\"csharp:App.Caller\"}],\"usedByCount\":1,\"usedByReturnedCount\":1,"
            + "\"fileDependents\":[\"src/Caller.cs\"],\"fileDependentsCount\":1,\"fileDependentsReturnedCount\":1}}]}",
            withoutProvider[0]);
    }

    [Fact]
    public async Task WhileVerifying_EveryContextResponseCarriesTheNotice_InRestAndMcpAlike()
    {
        var service = Service(Graph(), Verifying());
        const string notice = "\"cache\":{\"state\":\"verifying\",\"snapshotAt\":\"2026-11-04T10:12:03.0000000Z\"}";
        var responses = await AllResponsesAsync(service);
        foreach (var json in responses) Assert.Contains(notice, json);
        // Compact: right after the counts, before the matches (plan 3.10).
        Assert.StartsWith("{\"view\":\"compact\",\"matchMode\":\"exact\",\"substringSearchSkipped\":true,\"totalMatches\":1,\"returnedMatches\":1," + notice + ",", responses[0]);
        // Full: first.
        Assert.StartsWith("{" + notice + ",\"matches\":", responses[2]);

        // The MCP tool serializes the very DTO the REST endpoint returns, through the same context.
        var compact = await service.GetCompactContextAsync("Widget");
        Assert.Equal(JsonSerializer.Serialize(compact, CodeContextJsonContext.Default.CompactContextResponse), responses[0]);
        // Counts and truncation are unchanged by the notice.
        Assert.Equal(1, compact.TotalMatches);
        Assert.Equal(1, compact.ReturnedMatches);
        Assert.False(compact.Truncated);
    }

    /// <summary>A provider whose notice changes after its first reading: a load or a verification during the store reads.</summary>
    private sealed class TransitionProvider(CacheNotice? first, CacheNotice? later) : ICacheStateProvider
    {
        private int _readings;

        public CacheStatusDto? GetStatus() => null;

        public CacheNotice? GetNotice() => _readings++ == 0 ? first : later;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATransitionDuringTheStoreReads_StillLabelsEveryContextResponse(bool loadedDuringTheReads)
    {
        // Loaded during the reads: nothing before them, a notice after (the state is set before the publish).
        // Verified during the reads: a notice before, nothing after. Either way the answer may hold snapshot facts.
        var notice = new CacheNotice { State = CacheState.Verifying, SnapshotAt = "2026-11-04T10:12:03.0000000Z" };
        var database = Graph();
        ContextService Fresh() => Service(database, loadedDuringTheReads ? new TransitionProvider(null, notice) : new TransitionProvider(notice, null));

        Assert.Equal(CacheState.Verifying, (await Fresh().GetCompactContextAsync("Widget")).Cache?.State);
        Assert.Equal(CacheState.Verifying, (await Fresh().GetCompactContextAsync("Nope")).Cache?.State);
        Assert.Equal(CacheState.Verifying, (await Fresh().GetCompleteContextAsync("Widget")).Cache?.State);
        Assert.Equal(CacheState.Verifying, (await Fresh().GetCompleteContextAsync("Nope")).Cache?.State);
        Assert.Contains("\"cache\":{\"state\":\"verifying\"", await CodeContextTools.GetContext(Fresh(), "Widget"));
        // Multi: each result takes its own readings.
        var service = Service(database, new TransitionProvider(null, notice));
        var multi = await service.GetMultipleCompactContextAsync(new MultiContextRequest { Identifiers = ["Widget", "Nope"] });
        Assert.All(multi, response => Assert.Equal(CacheState.Verifying, response.Cache?.State));
    }
}
