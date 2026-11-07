using System.IO;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The Unix claim decision of plan 3.2 rule 19 as a pure predicate, so it runs on every platform: a
/// file already exactly 0600 is this user's (only its owner, or root, could open it for reading) and
/// needs no fchmod, which keeps a snapshot readable on a read-only mount; any other mode is claimed.
/// </summary>
public class SnapshotUnixClaimTests
{
    private const UnixFileMode UserRead = UnixFileMode.UserRead;
    private const UnixFileMode UserWrite = UnixFileMode.UserWrite;

    [Fact]
    public void ExactlyUserReadWrite_NeedsNoClaim()
        => Assert.False(SnapshotFile.NeedsClaim(UserRead | UserWrite));

    [Theory]
    [InlineData(UnixFileMode.UserRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.SetUser)]
    [InlineData(UnixFileMode.None)]
    public void AnyOtherMode_IsClaimed(UnixFileMode mode)
        => Assert.True(SnapshotFile.NeedsClaim(mode));
}
