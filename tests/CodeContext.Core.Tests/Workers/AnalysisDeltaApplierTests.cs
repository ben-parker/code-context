using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Workers;

public class AnalysisDeltaApplierTests
{
    private readonly IGenerationalGraphStore _store;
    private readonly AnalysisDeltaApplier _applier;

    public AnalysisDeltaApplierTests()
    {
        _store = (IGenerationalGraphStore)new InMemoryRepositoryFactory(
            NullLogger<InMemoryRepositoryFactory>.Instance).CreateGraphRepository();
        _applier = new AnalysisDeltaApplier(_store, NullLogger<AnalysisDeltaApplier>.Instance);
    }

    private static AnalysisDelta Delta(
        long generation,
        IReadOnlyList<ProtocolNode> nodes,
        bool replacesWorkspace = true,
        IReadOnlyList<string>? replacesFiles = null,
        string workspaceId = "ws-1",
        string parserId = "fake",
        long? requestId = null,
        bool isLastForRequest = true)
        => new(
            ParserId: parserId, ParserVersion: "1.0", WorkspaceId: workspaceId,
            Generation: generation, RequestId: requestId ?? generation,
            ReplacesWorkspace: replacesWorkspace, ReplacesFiles: replacesFiles ?? [],
            Nodes: nodes, Edges: [], IsLastForRequest: isLastForRequest);

    private static ProtocolNode Node(string id, string file)
        => new(id, id, id, "class", "fake", file);

    [Fact]
    public async Task Apply_CommitsNodesWithOwnershipMetadata()
    {
        var applied = await _applier.ApplyAsync(Delta(1, [Node("fake:ws-1:A", "a.fake")]));

        Assert.True(applied);
        Assert.Equal(1, _store.GetStatistics().NodeCount);
        var repo = (ICodeGraphRepository)_store;
        var node = (await repo.GetGraphAsync())!.Nodes.Single();
        Assert.Equal("fake", node.Metadata!["parserId"]);
        Assert.Equal("ws-1", node.Metadata["workspaceId"]);
        Assert.Equal("class", node.Type);
    }

    [Fact]
    public async Task Apply_StaleGeneration_IsRejected()
    {
        await _applier.ApplyAsync(Delta(2, [Node("fake:ws-1:A", "a.fake")]));

        var applied = await _applier.ApplyAsync(Delta(1, [Node("fake:ws-1:B", "b.fake")]));

        Assert.False(applied);
        Assert.Equal(1, _store.GetStatistics().NodeCount); // still only A
    }

    [Fact]
    public async Task Apply_IncrementalDelta_ReplacesOnlyListedFilesFacts()
    {
        await _applier.ApplyAsync(Delta(1, [Node("fake:ws-1:A", "a.fake"), Node("fake:ws-1:B", "b.fake")]));

        // Replace a.fake's facts (new node) and b.fake's facts (with nothing = deletion).
        var applied = await _applier.ApplyAsync(Delta(2, [Node("fake:ws-1:A2", "a.fake")],
            replacesWorkspace: false, replacesFiles: ["a.fake", "b.fake"]));

        Assert.True(applied);
        var repo = (ICodeGraphRepository)_store;
        var ids = (await repo.GetGraphAsync())!.Nodes.Select(n => n.Id).ToList();
        Assert.Equal(["fake:ws-1:A2"], ids);
    }

    [Fact]
    public async Task Apply_NeverTouchesOtherParsersFacts()
    {
        // Another writer's facts (no worker ownership metadata) already in the store.
        await _store.TryCommitGenerationAsync(1,
            [new CodeNode { Id = "csharp:Other", Name = "Other", Type = "class", Language = "csharp", FilePath = "o.cs" }],
            [], replacesScope: null);

        await _applier.ApplyAsync(Delta(1, [Node("fake:ws-1:A", "a.fake")], replacesWorkspace: true));

        var repo = (ICodeGraphRepository)_store;
        var ids = (await repo.GetGraphAsync())!.Nodes.Select(n => n.Id).OrderBy(x => x).ToList();
        Assert.Equal(["csharp:Other", "fake:ws-1:A"], ids);
    }

    [Fact]
    public async Task Apply_SeparateWorkspaces_TrackGenerationsIndependently()
    {
        await _applier.ApplyAsync(Delta(5, [Node("fake:ws-1:A", "a.fake")], workspaceId: "ws-1"));

        // Generation 1 is fresh for ws-2 even though ws-1 is already at 5.
        var applied = await _applier.ApplyAsync(Delta(1, [Node("fake:ws-2:B", "b.fake")], workspaceId: "ws-2"));

        Assert.True(applied);
        Assert.Equal(2, _store.GetStatistics().NodeCount);
    }

    [Fact]
    public async Task Apply_StreamedGeneration_IsInvisibleUntilFinalChunkThenCommitsAtomically()
    {
        await _applier.ApplyAsync(Delta(1, [Node("fake:ws-1:old", "old.fake")]));

        var firstAccepted = await _applier.ApplyAsync(Delta(
            2, [Node("fake:ws-1:A", "a.fake")], requestId: 22, isLastForRequest: false));

        Assert.True(firstAccepted);
        var repo = (ICodeGraphRepository)_store;
        Assert.Equal(["fake:ws-1:old"], (await repo.GetGraphAsync())!.Nodes.Select(n => n.Id));

        var finalAccepted = await _applier.ApplyAsync(Delta(
            2, [Node("fake:ws-1:B", "b.fake")], requestId: 22, isLastForRequest: true));

        Assert.True(finalAccepted);
        var ids = (await repo.GetGraphAsync())!.Nodes.Select(n => n.Id).OrderBy(id => id).ToList();
        Assert.Equal(["fake:ws-1:A", "fake:ws-1:B"], ids);
    }

    [Fact]
    public async Task Apply_NewerGenerationSupersedesIncompleteOlderRequest()
    {
        await _applier.ApplyAsync(Delta(
            2, [Node("fake:ws-1:old", "old.fake")], requestId: 20, isLastForRequest: false));
        await _applier.ApplyAsync(Delta(
            3, [Node("fake:ws-1:new", "new.fake")], requestId: 30, isLastForRequest: true));

        var staleFinal = await _applier.ApplyAsync(Delta(
            2, [Node("fake:ws-1:late", "late.fake")], requestId: 20, isLastForRequest: true));

        Assert.False(staleFinal);
        var repo = (ICodeGraphRepository)_store;
        Assert.Equal(["fake:ws-1:new"], (await repo.GetGraphAsync())!.Nodes.Select(n => n.Id));
    }
}
