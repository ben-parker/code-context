using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Phase 4 review regression: structural containment edges (HAS_METHOD/HAS_FIELD/
/// HAS_PROPERTY/contains) describe nesting, not usage — they must not appear in a
/// context response's Uses/UsedBy lists, while real CALLS/EXTENDS edges still do.
/// </summary>
public class ContainmentEdgeFilteringTests
{
    [Fact]
    public async Task GetCompleteContext_ExcludesContainmentEdgesFromUsesAndUsedBy()
    {
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        await factory.InitializeAsync("test");
        var nodeRepo = factory.CreateNodeRepository();
        var edgeRepo = factory.CreateEdgeRepository();

        await nodeRepo.UpsertAsync(new CodeNode { Id = "ts:widget#Widget", Name = "Widget", Type = "Class", FilePath = "widget.ts" });
        await nodeRepo.UpsertAsync(new CodeNode { Id = "ts:widget#Widget.render", Name = "render", Type = "Method", FilePath = "widget.ts" });
        await nodeRepo.UpsertAsync(new CodeNode { Id = "ts:base#Base", Name = "Base", Type = "Class", FilePath = "base.ts" });
        await nodeRepo.UpsertAsync(new CodeNode { Id = "ts:app#App", Name = "App", Type = "Class", FilePath = "app.ts" });

        // Containment: Widget has its own method. Usage: Widget extends Base; App calls Widget.render.
        await edgeRepo.UpsertAsync(new CodeEdge { Id = "e1", SourceId = "ts:widget#Widget", TargetId = "ts:widget#Widget.render", Type = "HAS_METHOD" });
        await edgeRepo.UpsertAsync(new CodeEdge { Id = "e2", SourceId = "ts:widget#Widget", TargetId = "ts:base#Base", Type = "EXTENDS" });
        await edgeRepo.UpsertAsync(new CodeEdge { Id = "e3", SourceId = "ts:app#App", TargetId = "ts:widget#Widget", Type = "CALLS" });

        var service = new ContextService(nodeRepo, edgeRepo, new InMemoryFileMetadataRepository());
        var response = await service.GetCompleteContextAsync("Widget", exact: true);

        var match = Assert.Single(response.Matches);
        // Real usage relationships survive:
        Assert.Contains(match.Relationships.Uses, n => n.Name == "Base");
        Assert.Contains(match.Relationships.UsedBy, n => n.Name == "App");
        // The class's own member is structure, not usage:
        Assert.DoesNotContain(match.Relationships.Uses, n => n.Name == "render");

        // And from the member's perspective, its container is not a "user".
        var memberResponse = await service.GetCompleteContextAsync("render", exact: true);
        var memberMatch = Assert.Single(memberResponse.Matches);
        Assert.DoesNotContain(memberMatch.Relationships.UsedBy, n => n.Name == "Widget");
    }
}
