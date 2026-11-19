using System;
using System.IO;
using System.Linq;
using System.Threading;
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
    public void ConcurrentFirstUse_AgreesOnOneKey()
    {
        // Many rounds of many callers released together: a creator that can replace another's key
        // (a check-then-rename publish) shows up within a few rounds; one that cannot never does.
        const int Rounds = 200;
        const int Callers = 16;
        for (var round = 0; round < Rounds; round++)
        {
            using var temp = new SnapshotTempDirectory();
            var path = temp.File("cache.key");
            using var start = new Barrier(Callers);
            var threads = new Thread[Callers];
            var keys = new byte[Callers][];
            var errors = new Exception?[Callers];
            for (var i = 0; i < Callers; i++)
            {
                var index = i;
                threads[i] = new Thread(() =>
                {
                    start.SignalAndWait();
                    try { keys[index] = SnapshotKey.LoadOrCreate(path); }
                    catch (Exception ex) { errors[index] = ex; }
                });
                threads[i].Start();
            }
            foreach (var thread in threads) thread.Join();
            Assert.All(errors, Assert.Null);
            var onDisk = File.ReadAllBytes(path);
            Assert.All(keys, key => Assert.Equal(onDisk, key));
            Assert.Equal(["cache.key"], Directory.GetFiles(temp.Path).Select(Path.GetFileName));
        }
    }

    [Fact]
    public void ReaderOfAKeyBeingWritten_WaitsForTheWholeKey()
    {
        // A first user that lost the race may open the winner's file before the key is in it: it must
        // wait for the whole key, not refuse the file or take a short read.
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        var key = Enumerable.Range(1, SnapshotKey.Length).Select(b => (byte)b).ToArray();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        byte[] read;
        using (var writer = new FileStream(path, options))
        {
            writer.Write(key, 0, 7);
            writer.Flush();
            var reader = Task.Run(() => SnapshotKey.LoadOrCreate(path));
            Thread.Sleep(200);
            Assert.False(reader.IsCompleted);
            writer.Write(key, 7, key.Length - 7);
            writer.Flush();
            read = reader.GetAwaiter().GetResult();
        }
        Assert.Equal(key, read);
    }

    [Fact]
    public void FileThatIsNotAKey_IsRefused()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        // Created user-only, as a key would be: with a typical umask (022) File.WriteAllBytes makes a
        // 0644 file, which the mode check refuses before the length is ever looked at.
        File.WriteAllBytes(path, new byte[5]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
