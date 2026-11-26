using CodeContext.Api.Commands;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Commands;

public class StopCommandHandlerTests
{
    [Fact]
    public void GracefulExitTimeout_AllowsTheFinalFlushAndHostTeardown()
    {
        var settings = SnapshotServiceSettings.From(new CodeContextOptions(), userProfile: ".");
        Assert.True(StopCommandHandler.GracefulExitTimeout > settings.ShutdownFlushTimeout);
    }
}
