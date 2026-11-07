using CodeContext.Core.Services;
using CodeContext.Mcp;
using NSubstitute;

namespace CodeContext.Core.Tests.Services;

public class CodeContextToolsTests
{
    [Fact]
    public async Task GetContext_DefaultsToCompactView()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false)
            .Returns(new CompactContextResponse());

        var json = await CodeContextTools.GetContext(service, "Target");

        Assert.Contains("\"view\":\"compact\"", json);
        await service.Received(1).GetCompactContextAsync(
            "Target", null, 1, false, false, null, false, false, 5, 10, false);
        await service.DidNotReceive().GetCompleteContextAsync(
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task GetContext_FullViewIsExplicit()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompleteContextAsync(
                "Target", null, 1, false, false, false, false, false)
            .Returns(new CompleteContextResponse());

        var json = await CodeContextTools.GetContext(
            service, "Target", view: ContextResponseView.Full);

        Assert.Contains("\"matches\":[]", json);
        await service.Received(1).GetCompleteContextAsync(
            "Target", null, 1, false, false, false, false, false);
    }


    [Fact]
    public async Task GetContext_CarriesTheCacheNotice()
    {
        var service = Substitute.For<IContextService>();
        service.GetCompactContextAsync(
                "Target", null, 1, false, false, null, false, false, 5, 10, false)
            .Returns(new CompactContextResponse { Cache = new CacheNotice { State = "verifying", SnapshotAt = "2026-11-04T10:12:03.0000000Z" } });

        var json = await CodeContextTools.GetContext(service, "Target");

        Assert.Contains("\"cache\":{\"state\":\"verifying\",\"snapshotAt\":\"2026-11-04T10:12:03.0000000Z\"}", json);
    }

    [Fact]
    public async Task GetStatus_CarriesTheCacheObject()
    {
        var statusService = Substitute.For<IStatusService>();
        statusService.GetStatusAsync().Returns(new CodeContext.Core.Serialization.StatusResponseDto(
            new("1", "1", "0s", "now", "1MB", "healthy"),
            new(false, "scanning", null, null, @"C:\repo", new(), Cache: new("verifying", true, SnapshotSequence: 42)),
            new(0, 0, 0, new(), new(), "InMemory"),
            new(true, [], [], 0),
            new([], [], new()),
            new([], 0, "0ms", 1),
            false, 0, 0));

        var json = await CodeContextTools.GetStatus(statusService);

        Assert.Contains("\"cache\":{\"state\":\"verifying\",\"servingCached\":true,\"snapshotSequence\":42}", json);
    }
}
