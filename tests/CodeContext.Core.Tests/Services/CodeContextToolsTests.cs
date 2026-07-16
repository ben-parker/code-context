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
}
