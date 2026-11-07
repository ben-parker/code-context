using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// The per-user key: created once with user-only permissions, then read back unchanged. Every test
/// passes a path inside its own temp directory; none touches the user profile.
/// </summary>
public class SnapshotKeyTests
{
    [Fact]
    public void FirstUse_CreatesA32ByteKey_ThatLaterUsesRead()
    {
        using var temp = new SnapshotTempDirectory();
        var path = Path.Combine(temp.Path, "home", ".codecontext", "cache.key");
        var created = SnapshotKey.LoadOrCreate(path);
        Assert.Equal(SnapshotKey.Length, created.Length);
        Assert.Equal(SnapshotKey.Length, new FileInfo(path).Length);
        Assert.Equal(created, SnapshotKey.LoadOrCreate(path));
        Assert.Equal(created, File.ReadAllBytes(path));
        Assert.Equal(["cache.key"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        Assert.NotEqual(new byte[SnapshotKey.Length], created);
    }

    [Fact]
    public void Key_IsUserOnly_OnUnix()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        SnapshotKey.LoadOrCreate(path);
        if (OperatingSystem.IsWindows()) return;   // the profile's ACL applies on Windows
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task ConcurrentFirstUse_AgreesOnOneKey()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => SnapshotKey.LoadOrCreate(path))));
        Assert.All(keys, key => Assert.Equal(keys[0], key));
        Assert.Equal(["cache.key"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void FileThatIsNotAKey_IsRefused()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        File.WriteAllBytes(path, new byte[5]);
        Assert.Throws<InvalidDataException>(() => SnapshotKey.LoadOrCreate(path));
        Assert.Equal(5, new FileInfo(path).Length);
    }

    [Fact]
    public void DefaultPath_IsTheUserProfileKey()
    {
        var path = SnapshotKey.DefaultPath();
        Assert.Equal("cache.key", Path.GetFileName(path));
        Assert.Equal(".codecontext", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Writer_RefusesAKeyOfTheWrongLength()
        => Assert.Throws<ArgumentException>(() => SnapshotWriter.Write(new MemoryStream(),
            SnapshotTestSupport.Input([]), new byte[16]));
}
