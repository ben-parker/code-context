using System.Text.Json;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// A call that bound to nothing names a method without saying which. It is listed as a possible caller of every
/// method or function of that name its worker emits in the workspace, in a list of its own, so bound evidence stays
/// exactly as it was.
/// </summary>
public class PossibleCallerTests
{
    private const string Root = "/repo";

    private static CodeNode Node(string identifier, string name, string file, int line, string type = "Method", string workspace = "ws",
        string parser = "lang", bool external = false)
        => new()
        {
            Id = $"{parser}:{workspace}:{identifier}",
            Identifier = $"{parser}:{workspace}/{identifier}",
            Metadata = external ? new Dictionary<string, string> { ["external"] = "true" } : null,
            Name = name,
            Type = type,
            FilePath = $"{Root}/{file}",
            StartLine = line,
            EndLine = line + 2,
            Signature = name + "()",
        };

    private static CodeEdge Bound(string source, string target, int line)
        => new()
        {
            Id = $"lang:ws:{source}=[CALLS]=>lang:ws:{target}@{line}:0",
            SourceId = $"lang:ws:{source}",
            TargetId = $"lang:ws:{target}",
            Type = "CALLS",
            Metadata = new Dictionary<string, string> { ["line"] = line.ToString(), ["column"] = "0" },
        };

    private static CodeEdge Unbound(string source, string name, int line, string type = "CALLS", string workspace = "ws", string parser = "lang")
    {
        var metadata = new Dictionary<string, string> { ["line"] = line.ToString(), ["column"] = "4", ["unresolved"] = "true" };
        return new()
        {
            Id = $"{parser}:{workspace}:{source}=[{type}]=>{name}@{line}:4",
            SourceId = $"{parser}:{workspace}:{source}",
            TargetId = name,
            Type = type,
            Metadata = metadata,
        };
    }

    private static List<CodeNode> Nodes() =>
    [
        Node("Widget.Render", "Render", "Widget.cs", 3),
        Node("Widget.RenderProp", "Render", "Widget.cs", 9, type: "Property"),
        Node("Caller.Bound", "Bound", "Caller.cs", 1),
        Node("Caller.Guess", "Guess", "Caller.cs", 10),
        Node("Caller.Both", "Both", "Caller.cs", 20),
        Node("Caller.Heritage", "Heritage", "Caller.cs", 40, type: "Class"),
        Node("Other.Guess", "Guess", "Other.cs", 1, workspace: "ws2"),
        Node("other.js#guess", "guess", "other.js", 1, parser: "js"),
        Node("render.js#render", "render", "render.js", 1, type: "Function", parser: "js"),
        Node("render.js#caller", "caller", "render.js", 5, type: "Function", parser: "js"),
        Node("render.js#render2", "render", "render.js", 9, type: "Function", parser: "js"),
        Node("Lib.Render", "Render", "Lib.cs", 0, external: true),
    ];

    private static List<CodeEdge> BoundEdges() =>
    [
        Bound("Caller.Bound", "Widget.Render", 2),
        Bound("Caller.Both", "Widget.Render", 21),
    ];

    private static List<CodeEdge> UnboundEdges() =>
    [
        Unbound("Caller.Guess", "Render", 11),
        Unbound("Caller.Guess", "Render", 14),
        Unbound("Caller.Both", "Render", 22),
        // Not a call.
        Unbound("Caller.Heritage", "Render", 40, type: "EXTENDS"),
        // Another workspace's call of the name, and another worker's.
        Unbound("Other.Guess", "Render", 2, workspace: "ws2"),
        Unbound("other.js#guess", "Render", 2, workspace: "ws", parser: "js"),
        // A function's own call of its name, and another function's.
        Unbound("render.js#render", "render", 3, parser: "js"),
        Unbound("render.js#caller", "render", 6, parser: "js"),
    ];

    private static ContextService Service(bool withUnbound = true)
    {
        var edges = BoundEdges();
        if (withUnbound) edges.AddRange(UnboundEdges());
        var database = new InMemoryDatabase();
        Assert.True(database.TryCommitGeneration(1, Nodes(), edges, scope: null));
        return new ContextService(new InMemoryNodeRepository(database), new InMemoryEdgeRepository(database),
            new CodeContext.Core.Repositories.InMemoryFileMetadataRepository(), Options.Create(new CodeContextOptions { RootPath = Root }));
    }

    private static Task<CompactContextResponse> Compact(ContextService service, string identifier = "lang:ws/Widget.Render",
        int maxRelationships = 10, string? relation = null)
        => service.GetCompactContextAsync(identifier, maxRelationships: maxRelationships, relation: relation);

    [Fact]
    public async Task UnboundCallsOfTheName_AreListedApart_FromTheirOwnWorkspace_AndOnlyCalls()
    {
        var relationships = Assert.Single((await Compact(Service())).Matches).Relationships!;

        Assert.Equal(["lang:ws/Caller.Both", "lang:ws/Caller.Bound"],
            relationships.UsedBy!.Select(node => node.Identifier).Order(StringComparer.Ordinal));
        Assert.Equal(["lang:ws/Caller.Both", "lang:ws/Caller.Guess"],
            relationships.PossibleCallers!.Select(node => node.Identifier).Order(StringComparer.Ordinal));
        Assert.Equal(2, relationships.PossibleCallersCount);
        Assert.Equal(2, relationships.PossibleCallersReturnedCount);
        Assert.False(relationships.PossibleCallersTruncated);

        Assert.DoesNotContain("possibleCallEdges", JsonSerializer.Serialize(relationships, CodeContextJsonContext.Default.CompactRelationships),
            StringComparison.OrdinalIgnoreCase);

        var guess = relationships.PossibleCallers!.Single(node => node.Identifier == "lang:ws/Caller.Guess");
        Assert.Equal(["CALLS"], guess.Relations);
        Assert.Equal(2, guess.CallSiteCount);
        Assert.Equal([12, 15], guess.Lines);
        Assert.Null(guess.Bindings);
    }

    [Fact]
    public async Task BoundEvidence_IsExactlyWhatItWasWithoutTheUnboundCalls()
    {
        static string BoundJson(CompactContextResponse response)
        {
            var relationships = Assert.Single(response.Matches).Relationships!;
            relationships.PossibleCallers = null;
            relationships.PossibleCallersCount = null;
            relationships.PossibleCallersReturnedCount = null;
            return JsonSerializer.Serialize(response, CodeContextJsonContext.Default.CompactContextResponse);
        }

        Assert.Equal(BoundJson(await Compact(Service(withUnbound: false))), BoundJson(await Compact(Service())));
        Assert.Null(Assert.Single((await Compact(Service(withUnbound: false))).Matches).Relationships!.PossibleCallers);
    }

    [Fact]
    public async Task PossibleCallers_AreCountedAndTruncatedLikeTheOtherLists()
    {
        var relationships = Assert.Single((await Compact(Service(), maxRelationships: 1)).Matches).Relationships!;

        Assert.Single(relationships.PossibleCallers!);
        Assert.Equal(2, relationships.PossibleCallersCount);
        Assert.Equal(1, relationships.PossibleCallersReturnedCount);
        Assert.True(relationships.PossibleCallersTruncated);
        Assert.True(relationships.Truncated);
    }

    [Fact]
    public async Task ARelationFilter_WithoutCalls_LeavesThemOut()
    {
        var relationships = Assert.Single((await Compact(Service(), relation: "REFERENCES")).Matches).Relationships!;

        Assert.Empty(relationships.PossibleCallers!);
        Assert.Equal(0, relationships.PossibleCallersCount);
    }

    [Fact]
    public async Task OnlyAMethodOrFunction_HasPossibleCallers()
    {
        var relationships = Assert.Single((await Compact(Service(), "lang:ws/Widget.RenderProp")).Matches).Relationships!;

        Assert.Null(relationships.PossibleCallers);
    }

    /// <summary>A function is listed for each of its name's calls but its own, as usedBy leaves out the queried node.</summary>
    [Fact]
    public async Task AFunction_HasPossibleCallers_ButNotItself()
    {
        var service = Service();

        var render = Assert.Single((await Compact(service, "js:ws/render.js#render")).Matches).Relationships!;
        Assert.Equal(["js:ws/render.js#caller"], render.PossibleCallers!.Select(node => node.Identifier));
        var other = Assert.Single((await Compact(service, "js:ws/render.js#render2")).Matches).Relationships!;
        Assert.Equal(["js:ws/render.js#caller", "js:ws/render.js#render"],
            other.PossibleCallers!.Select(node => node.Identifier).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALibraryMethod_HasNoPossibleCallers()
    {
        var match = Assert.Single((await Service().GetCompleteContextAsync("lang:ws/Lib.Render", includeExternal: true)).Matches);

        Assert.Empty(match.Relationships.PossibleCallers);
    }

    [Fact]
    public async Task TheFullView_ListsThemApartToo()
    {
        var match = Assert.Single((await Service().GetCompleteContextAsync("lang:ws/Widget.Render")).Matches);

        Assert.Equal(["lang:ws:Caller.Both", "lang:ws:Caller.Guess"], match.Relationships.PossibleCallers.Select(node => node.Id));
        var json = JsonSerializer.Serialize(match.Relationships, CodeContextJsonContext.Default.ContextRelationships);
        Assert.Contains("\"possibleCallers\":[", json, StringComparison.Ordinal);
        Assert.DoesNotContain("possibleCallEdges", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["lang:ws:Caller.Both", "lang:ws:Caller.Bound"], match.Relationships.UsedBy.Select(node => node.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GraphQueries_NeverWalkAnUnboundCall()
    {
        var affected = await Service().GetAffectedAsync(new GraphAffectedRequest { Identifier = "lang:ws/Widget.Render" });

        Assert.Equal(["lang:ws/Caller.Both", "lang:ws/Caller.Bound"], affected.Items.Select(item => item.Identifier).Order(StringComparer.Ordinal));
    }
}
