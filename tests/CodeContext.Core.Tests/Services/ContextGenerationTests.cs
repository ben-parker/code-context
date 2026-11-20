using System.Reflection;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Context queries read one generation: every store read an answer makes (resolving the target, its relationships,
/// test evidence, file relationships) comes from the map published when the answer began, whatever commits land
/// meanwhile, and the answers of one multi query share it.
/// </summary>
public class ContextGenerationTests
{
    private static Dictionary<string, string> Owned() => new() { ["parserId"] = "csharp", ["workspaceId"] = "a" };

    private static CodeNode Node(string id, string identifier, string file = "/repo/src/X.cs") => new()
    {
        Id = id, Identifier = identifier, Name = identifier.Split('.')[^1], Type = "Method", FilePath = file, Metadata = Owned(),
    };

    private static CodeEdge Edge(string source, string target) => new()
    {
        Id = $"{source}>{target}", SourceId = source, TargetId = target, Type = "CALLS", Metadata = Owned(),
    };

    /// <summary>Generation 1: A calls B (id b1). Generation 2: C calls B (id b2), and A is gone.</summary>
    private static void CommitFirst(InMemoryDatabase database)
        => Assert.True(database.TryCommitGeneration(1,
            [Node("csharp:a:b1", "csharp:Demo.B", "/repo/src/B.cs"), Node("csharp:a:A", "csharp:Demo.A", "/repo/src/A.cs")],
            [Edge("csharp:a:A", "csharp:a:b1")], scope: null));

    private static void CommitSecond(InMemoryDatabase database)
        => Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1,
            [Node("csharp:a:b2", "csharp:Demo.B", "/repo/src/B.cs"), Node("csharp:a:C", "csharp:Demo.C", "/repo/src/C.cs")],
            [Edge("csharp:a:C", "csharp:a:b2")], scope: null));

    private static ContextService Service(InMemoryDatabase database, ICodeNodeRepository? nodes = null, ICacheStateProvider? cache = null)
        => new(nodes ?? new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = "/repo" }), cacheState: cache);

    private static string UsedBy(CompactContextResponse response)
        => string.Join(",", Assert.Single(response.Matches).Relationships?.UsedBy?.Select(node => node.Identifier) ?? []);

    [Fact]
    public async Task AContextAnswerWhoseReadsStraddleACommit_IsOneGeneration()
    {
        var database = new InMemoryDatabase();
        CommitFirst(database);
        // The commit lands right after the answer has resolved its target, before it reads who uses it.
        var service = Service(database, new SteppingNodeRepository(new InMemoryNodeRepository(database), afterRead: 1,
            () => CommitSecond(database)));

        var answer = await service.GetCompactContextAsync("csharp:Demo.B", depth: 1);

        Assert.Equal("csharp:Demo.A", UsedBy(answer));
        Assert.Null(database.Pinned);
    }

    [Fact]
    public async Task TheAnswersOfOneMultiQuery_ShareOneGeneration()
    {
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database, new SteppingNodeRepository(new InMemoryNodeRepository(database), afterRead: 1,
            () => CommitSecond(database)));

        var answers = await service.GetMultipleCompactContextAsync(new MultiContextRequest { Identifiers = ["csharp:Demo.B", "csharp:Demo.B"] });

        Assert.Equal(["csharp:Demo.A", "csharp:Demo.A"], answers.Select(UsedBy));
        // The next query reads the generation published since.
        Assert.Equal("csharp:Demo.C", UsedBy(await service.GetCompactContextAsync("csharp:Demo.B", depth: 1)));
    }

    [Fact]
    public async Task AFullContextAnswerWhoseReadsStraddleACommit_IsOneGeneration()
    {
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database, new SteppingNodeRepository(new InMemoryNodeRepository(database), afterRead: 1,
            () => CommitSecond(database)));

        var answer = await service.GetCompleteContextAsync("csharp:Demo.B", depth: 1, includeTests: true);

        Assert.Equal(["csharp:Demo.A"], Assert.Single(answer.Matches).Relationships.UsedBy.Select(node => node.Identifier));
    }

    [Fact]
    public async Task EveryQueryOfTheServices_ReadsTheStoreOnlyThroughOnePin()
    {
        // Each method of the query interfaces, called once: none may read the store outside a pin, so a method added
        // later that forgets it fails here.
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database);
        var unpinned = 0;
        database.UnpinnedReadForTests = () => Interlocked.Increment(ref unpinned);

        var calls = new Dictionary<string, Func<Task>>
        {
            [nameof(IContextService.GetCompactContextAsync)] = () => service.GetCompactContextAsync("Demo.B", depth: 2, includeTests: true, includeRelated: true, includeMetrics: true),
            [nameof(IContextService.GetCompleteContextAsync)] = () => service.GetCompleteContextAsync("B", depth: 2),
            [nameof(IContextService.GetMultipleCompactContextAsync)] = () => service.GetMultipleCompactContextAsync(new MultiContextRequest { Identifiers = ["B", "src/A.cs"] }),
            [nameof(IContextService.GetMultipleContextAsync)] = () => service.GetMultipleContextAsync(new MultiContextRequest { Identifiers = ["B", "A"] }),
            [nameof(IGraphQueryService.FindPathAsync)] = () => service.FindPathAsync(new GraphPathRequest { From = "A", To = "B" }),
            [nameof(IGraphQueryService.GetAffectedAsync)] = () => service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "B" }),
            [nameof(IGraphQueryService.GetHubsAsync)] = () => service.GetHubsAsync(new GraphHubsRequest()),
            [nameof(IGraphQueryService.GetImpactAsync)] = () => service.GetImpactAsync(
                new GraphImpactRequest { Diff = "--- a/src/B.cs\n+++ b/src/B.cs\n@@ -1 +1 @@\n-a\n+b\n" }),
        };
        var methods = typeof(IContextService).GetMethods().Concat(typeof(IGraphQueryService).GetMethods())
            .Select(method => method.Name).Distinct().Order().ToList();
        Assert.Equal(methods, calls.Keys.Order().ToList());

        foreach (var (name, call) in calls)
        {
            await call();
            Assert.True(unpinned == 0, $"{name} read the store {unpinned} time(s) outside a pin.");
            Assert.Null(database.Pinned);
        }
    }

    [Fact]
    public async Task APinEndsWithItsQuery_AndWorkStartedAfterwardDoesNotSeeIt()
    {
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database);

        await service.GetCompactContextAsync("csharp:Demo.B");
        CommitSecond(database);

        Assert.Null(database.Pinned);
        Assert.Null(await Task.Run(() => database.Pinned));
        Assert.Equal("csharp:Demo.C", UsedBy(await service.GetCompactContextAsync("csharp:Demo.B", depth: 1)));
    }

    [Fact]
    public async Task AGraphQueryInsideAPinnedRead_ReadsThatPinsGeneration()
    {
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database);
        using (database.PinPublished())
        {
            CommitSecond(database);
            var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = "csharp:Demo.B" });
            Assert.Equal(["csharp:Demo.A"], affected.Items.Select(item => item.Identifier));
        }
        Assert.Null(database.Pinned);
    }

    [Fact]
    public void ACompositeBuiltAsACommitClearsTheCache_IsNotKeptCached()
    {
        // A reader builds the composite of generation 1 and passes its version check; a commit publishes generation 2
        // and clears the cache before the reader stores its composite. That composite must not stay cached, holding a
        // replaced generation alive until the next read.
        var database = new InMemoryDatabase();
        CommitFirst(database);
        object? built = null;
        database.BeforeAdjacencyStoreForTests = () =>
        {
            database.BeforeAdjacencyStoreForTests = null;
            CommitSecond(database);
        };

        built = database.GetAdjacency();

        Assert.NotSame(built, database.CachedAdjacencyForTests);
        Assert.Null(database.CachedAdjacencyForTests);
    }

    [Fact]
    public async Task AnAnswerFromTheLoadedSnapshot_IsLabelledWhenTheScanCommitsAsItBegins()
    {
        // The pinned generation is the store read: the "before" cache notice must be read before it, or a scan that
        // commits and reports verified between the pin and that read leaves facts of the snapshot unlabelled.
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database, cache: new VerifiedOnNotice(flipOnCall: 1, () => CommitSecond(database)));

        var compact = await service.GetCompactContextAsync("csharp:Demo.B", depth: 1);

        Assert.True(UsedBy(compact) != "csharp:Demo.A" || compact.Cache is not null, "snapshot facts answered unlabelled");

        database = new InMemoryDatabase();
        CommitFirst(database);
        service = Service(database, cache: new VerifiedOnNotice(flipOnCall: 1, () => CommitSecond(database)));

        var full = await service.GetCompleteContextAsync("csharp:Demo.B", depth: 1);

        var users = Assert.Single(full.Matches).Relationships.UsedBy.Select(node => node.Identifier).ToList();
        Assert.True(users is not ["csharp:Demo.A"] || full.Cache is not null, "snapshot facts answered unlabelled");
    }

    [Fact]
    public async Task EveryAnswerOfAMultiQueryFromTheLoadedSnapshot_IsLabelled()
    {
        // The multi query pins the snapshot's generation; the scan commits and reports verified while its first answer
        // is read, so the later notices say nothing, yet every answer comes from the snapshot.
        var database = new InMemoryDatabase();
        CommitFirst(database);
        var service = Service(database, cache: new VerifiedOnNotice(flipOnCall: 2, () => CommitSecond(database)));

        var compact = await service.GetMultipleCompactContextAsync(new MultiContextRequest { Identifiers = ["csharp:Demo.B", "csharp:Demo.B"] });

        Assert.All(compact, answer => Assert.True(UsedBy(answer) != "csharp:Demo.A" || answer.Cache is not null, "snapshot facts answered unlabelled"));

        database = new InMemoryDatabase();
        CommitFirst(database);
        service = Service(database, cache: new VerifiedOnNotice(flipOnCall: 2, () => CommitSecond(database)));

        var full = await service.GetMultipleContextAsync(new MultiContextRequest { Identifiers = ["csharp:Demo.B", "csharp:Demo.B"] });

        Assert.All(full, answer => Assert.True(
            Assert.Single(answer.Matches).Relationships.UsedBy.Select(node => node.Identifier).ToList() is not ["csharp:Demo.A"]
                || answer.Cache is not null, "snapshot facts answered unlabelled"));
    }

    /// <summary>
    /// Serves the loaded snapshot until its <paramref name="flipOnCall"/>-th notice, where the scan commits (the step)
    /// and is then reported, in the host's order: from that call on, nothing is labelled.
    /// </summary>
    private sealed class VerifiedOnNotice(int flipOnCall, Action commit) : ICacheStateProvider
    {
        private int _calls;
        private bool _verified;

        public CacheStatusDto? GetStatus() => null;

        public CacheNotice? GetNotice()
        {
            if (++_calls == flipOnCall)
            {
                commit();
                _verified = true;
            }
            return _verified ? null : new CacheNotice { State = CacheState.Verifying };
        }
    }

    /// <summary>A node repository that runs a step once, right after its <paramref name="afterRead"/>-th read.</summary>
    private sealed class SteppingNodeRepository(ICodeNodeRepository inner, int afterRead, Action step) : ICodeNodeRepository
    {
        private int _reads;

        private async Task<T> Read<T>(Task<T> read)
        {
            var result = await read;
            if (Interlocked.Increment(ref _reads) == afterRead) step();
            return result;
        }

        public Task<CodeNode?> GetByIdAsync(string id) => Read(inner.GetByIdAsync(id));
        public Task<CodeNode?> GetByIdentifierAsync(string identifier) => Read(inner.GetByIdentifierAsync(identifier));
        public Task<IReadOnlyList<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false) => Read(inner.FindByNameAsync(name, type, exact));
        public Task<IReadOnlyList<CodeNode>> FindByFilePathAsync(string filePath, string? type = null) => Read(inner.FindByFilePathAsync(filePath, type));
        public Task<IReadOnlyList<CodeNode>> GetAllAsync() => Read(inner.GetAllAsync());
        public Task<IReadOnlyList<string>> GetFilePathsAsync() => Read(inner.GetFilePathsAsync());
        public Task UpsertAsync(CodeNode node) => inner.UpsertAsync(node);
        public Task DeleteAsync(string id, CancellationToken cancellationToken) => inner.DeleteAsync(id, cancellationToken);
    }
}
