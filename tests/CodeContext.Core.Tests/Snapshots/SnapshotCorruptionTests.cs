using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// A file that is not exactly what this user's host wrote for this root is refused, with the check
/// that caught it: truncation at every length of a small file and sampled lengths of a large one,
/// every single-byte change, and each header and host meta field on its own.
/// </summary>
public class SnapshotCorruptionTests
{
    /// <summary>
    /// Every length, through the checks <see cref="SnapshotFile.Open"/> runs before mapping (in memory,
    /// so every length is affordable), and a sample of lengths through real files.
    /// </summary>
    [Fact]
    public void EveryTruncation_OfASmallFile_IsRefused()
    {
        var bytes = SmallFile();
        var name = SnapshotFile.FormatName(1, 0x5d0c9a1e);
        Assert.Equal(SnapshotRejection.None, SnapshotFile.Precheck(bytes, name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);
        for (var length = 0; length < bytes.Length; length++)
            Assert.Equal(length < SnapshotFormat.MinimumFileLength ? SnapshotRejection.TooShort : SnapshotRejection.LengthMismatch,
                SnapshotFile.Precheck(bytes[..length], name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        foreach (var length in new[] { 0, 1, 255, 256, 295, 296, bytes.Length / 2, bytes.Length - 40, bytes.Length - 32, bytes.Length - 1 })
        {
            File.WriteAllBytes(path, bytes[..length]);
            var result = SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect());
            Assert.Equal(length < SnapshotFormat.MinimumFileLength ? SnapshotRejection.TooShort : SnapshotRejection.LengthMismatch, result.Rejection);
        }
    }

    [Fact]
    public void SampledTruncations_OfALargeFile_AreRefused()
    {
        var database = SnapshotTestSupport.Commit(new SnapshotGraphGenerator(77).Generate(maxNodes: 400, maxEdges: 1500));
        var bytes = SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database)));
        Assert.True(bytes.Length > 50_000, $"the large file is only {bytes.Length} bytes");
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        var random = new Random(5);
        var lengths = Enumerable.Range(0, 60).Select(_ => random.Next(bytes.Length))
            .Concat([bytes.Length - 1, bytes.Length - SnapshotFormat.HmacSize, bytes.Length - SnapshotFormat.FooterSize, 296, 295]);
        foreach (var length in lengths)
        {
            File.WriteAllBytes(path, bytes[..length]);
            Assert.False(SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Loaded, $"truncated to {length}");
        }
    }

    /// <summary>
    /// Every byte of the file, each changed by flipping one bit (bit <c>offset % 8</c>), is refused by
    /// the checks before mapping; a sample through real files gives the same outcome. One flipped bit
    /// per byte, not every value of every byte.
    /// </summary>
    [Fact]
    public void EveryByte_WithOneBitFlipped_IsRefused()
    {
        var bytes = SmallFile();
        var name = SnapshotFile.FormatName(1, 0x5d0c9a1e);
        for (var offset = 0; offset < bytes.Length; offset++)
        {
            var damaged = (byte[])bytes.Clone();
            damaged[offset] ^= (byte)(1 << (offset % 8));
            var (rejection, _) = SnapshotFile.Precheck(damaged, name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect());
            Assert.True(rejection != SnapshotRejection.None, $"a change at byte {offset} was accepted");
        }

        // A sample through real files, with the same outcome.
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        foreach (var offset in new[] { 0, 9, 30, 60, 300, bytes.Length / 2, bytes.Length - 40, bytes.Length - 1 })
        {
            var damaged = (byte[])bytes.Clone();
            damaged[offset] ^= 0x10;
            File.WriteAllBytes(path, damaged);
            Assert.False(SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Loaded, $"byte {offset}");
        }
    }

    [Fact]
    public void WrongKey_IsRefusedByAuthentication()
        => AssertRefused(SmallFile(), SnapshotRejection.Authentication, key: SnapshotTestSupport.OtherKey);

    [Fact]
    public void FormatVersion_IsChecked()
        => AssertRefused(Patch32(SmallFile(), SnapshotFormat.HeaderFormatVersion, 2), SnapshotRejection.FormatVersion);

    [Fact]
    public void EndianTag_IsChecked()
        => AssertRefused(Patch32(SmallFile(), SnapshotFormat.HeaderEndianTag, 0x04030201), SnapshotRejection.EndianTag);

    [Fact]
    public void HeaderSize_IsChecked()
        => AssertRefused(Patch32(SmallFile(), SnapshotFormat.HeaderHeaderSize, 128), SnapshotRejection.HeaderSize);

    [Fact]
    public void Magic_IsChecked()
    {
        var bytes = SmallFile();
        bytes[0] = (byte)'X';
        AssertRefused(bytes, SnapshotRejection.Magic);
    }

    [Fact]
    public void LengthField_IsChecked()
    {
        var bytes = SmallFile();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(SnapshotFormat.HeaderFileLength), (ulong)bytes.Length + 8);
        AssertRefused(bytes, SnapshotRejection.LengthMismatch);
    }

    [Fact]
    public void FooterMagic_IsChecked()
    {
        var bytes = SmallFile();
        bytes[bytes.Length - SnapshotFormat.FooterSize] ^= 0xFF;
        AssertRefused(bytes, SnapshotRejection.Footer);
    }

    [Fact]
    public void HostVersion_IsChecked()
        => AssertRefused(SmallFile(), SnapshotRejection.HostVersion,
            expected: SnapshotTestSupport.Expect() with { HostVersion = SnapshotTestSupport.HostVersion + "1" });

    [Fact]
    public void RootPath_IsChecked_WithThePathCaseRule()
    {
        AssertRefused(SmallFile(), SnapshotRejection.RootPath, expected: SnapshotTestSupport.Expect() with { RootPath = "C:\\other" });
        // Case-insensitive paths: a case variant of the root is the same root.
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, SmallFile());
        Assert.True(SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect() with { RootPath = "c:\\REPO" }).Loaded);
        // Case-sensitive paths: it is not.
        File.WriteAllBytes(path, SmallFile(caseInsensitive: false));
        Assert.Equal(SnapshotRejection.RootPath,
            SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect(caseInsensitive: false) with { RootPath = "c:\\REPO" }).Rejection);
    }

    [Fact]
    public void PathCaseFlag_IsChecked()
        => AssertRefused(SmallFile(caseInsensitive: false), SnapshotRejection.PathCase);

    [Fact]
    public void NameAndHeaderSequence_MustAgree()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot(sequence: 9);
        File.WriteAllBytes(path, SmallFile());   // written as sequence 1
        Assert.Equal(SnapshotRejection.NameMismatch, SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);
    }

    [Theory]
    [InlineData("graph.0000000001.5d0c9a1e.tmp")]
    [InlineData("graph.0000000001.5d0c9a1e.ccsnap.tmp")]
    [InlineData("graph.1.5d0c9a1e.ccsnap")]
    [InlineData("graph.0000000001.5D0C9A1E.ccsnap")]
    [InlineData("worker-csharp.0000000001.5d0c9a1e.state")]
    [InlineData("graph.000000000x.5d0c9a1e.ccsnap")]
    public void NonSnapshotNames_AreNotOpened(string name)
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.File(name);
        File.WriteAllBytes(path, SmallFile());
        Assert.Equal(SnapshotRejection.Name, SnapshotLoader.Load(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);
    }

    [Fact]
    public void MissingFile_IsAnIoRefusal()
    {
        using var temp = new SnapshotTempDirectory();
        Assert.Equal(SnapshotRejection.Io, SnapshotLoader.Load(temp.Snapshot(), SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);
    }

    /// <summary>
    /// Deny-write is asserted on Windows only, where sharing is enforced (on Unix it is advisory, and
    /// <see cref="UnixSnapshotGrantingGroupOrOtherWrite_IsRefused_OnUnix"/> covers what replaces it).
    /// Releasing the file when disposed holds everywhere.
    /// </summary>
    [Fact]
    public void OpenSnapshot_DeniesWriters_AndReleasesTheFileWhenDisposed()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, SmallFile());
        using (var file = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!)
        {
            if (OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.NotEmpty(file.Shards);
        }
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // ---- P1: the length cap ----

    [Fact]
    public void FileLongerThanTheCap_IsRefusedBeforeHashing()
    {
        Assert.Equal(1L << 30, SnapshotFormat.MaxFileLength);
        var bytes = SmallFile();
        var name = SnapshotFile.FormatName(1, 0x5d0c9a1e);
        Assert.Equal(SnapshotRejection.None, SnapshotFile.Precheck(bytes, name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect(), bytes.Length).Rejection);
        Assert.Equal(SnapshotRejection.TooLong, SnapshotFile.Precheck(bytes, name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect(), bytes.Length - 1).Rejection);
        // The cap comes before the header: a file with a wrong key is refused as too long, not unauthenticated.
        Assert.Equal(SnapshotRejection.TooLong, SnapshotFile.Precheck(bytes, name, SnapshotTestSupport.OtherKey, SnapshotTestSupport.Expect(), 296).Rejection);

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, bytes);
        var opened = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect(), bytes.Length - 1);
        Assert.Null(opened.File);
        Assert.Equal(SnapshotRejection.TooLong, opened.Rejection);
        File.Delete(path);   // the refused file was released
    }

    [Fact]
    public void CaptureLongerThanTheCap_WritesNothing_AndSaysWhy()
    {
        var input = SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(SmallDatabase()));
        var length = SnapshotTestSupport.WriteBytes(input).Length;

        using var stream = new MemoryStream();
        var result = SnapshotWriter.Write(stream, input, SnapshotTestSupport.Key, length - 1);
        Assert.False(result.IsWritten);
        Assert.Contains("limit", result.NotWritten, StringComparison.Ordinal);
        Assert.Equal(length, result.Length);
        Assert.Empty(result.Written);
        Assert.Equal(0, stream.Length);

        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        Assert.False(SnapshotWriter.WriteFile(path, input, SnapshotTestSupport.Key, length - 1).IsWritten);
        Assert.False(File.Exists(path));
        Assert.True(SnapshotWriter.WriteFile(path, input, SnapshotTestSupport.Key, length).IsWritten);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    // ---- P7: writer and region hygiene ----

    [Fact]
    public void WriteFile_DeletesItsPartialFile_WhenWritingFails()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        var input = SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(SmallDatabase()));
        Assert.Throws<IOException>(() => SnapshotWriter.WriteFile(path, input, SnapshotTestSupport.Key, SnapshotFormat.MaxFileLength,
            stream => new FailingStream(stream, failAfter: 300)));
        Assert.False(File.Exists(path));

        // A path that already exists is never touched.
        File.WriteAllBytes(path, [1, 2, 3]);
        Assert.Throws<IOException>(() => SnapshotWriter.WriteFile(path, input, SnapshotTestSupport.Key));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndSafeAgainstConcurrentCalls()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, SmallFile());
        for (var round = 0; round < 50; round++)
        {
            var file = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!;
            var region = file.Region;
            System.Threading.Tasks.Parallel.For(0, 8, i =>
            {
                if (i % 2 == 0) region.Dispose();
                else file.Dispose();
            });
            file.Dispose();
            region.Dispose();
            Assert.Throws<ObjectDisposedException>(() => region.ReadU32(0));
        }
        File.Delete(path);
    }

    [Fact]
    public void RefusalReasons_QuoteFileTextTruncatedAndEscaped()
    {
        var hostile = "\u001b[31m" + new string('v', 1000) + "\u202e";
        var database = SmallDatabase();
        var input = SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database));
        var hostileVersion = new SnapshotWriteInput
        {
            Shards = input.Shards, Host = input.Host with { HostVersion = hostile }, Sequence = 1, CreatedUtcTicks = input.CreatedUtcTicks,
        };
        var hostileRoot = new SnapshotWriteInput
        {
            Shards = input.Shards, Host = input.Host with { RootPath = "C:\\r\n" + hostile }, Sequence = 1, CreatedUtcTicks = input.CreatedUtcTicks,
        };
        var name = SnapshotFile.FormatName(1, 0x5d0c9a1e);
        foreach (var (written, rejection) in new[] { (hostileVersion, SnapshotRejection.HostVersion), (hostileRoot, SnapshotRejection.RootPath) })
        {
            var (actual, reason) = SnapshotFile.Precheck(SnapshotTestSupport.WriteBytes(written), name, SnapshotTestSupport.Key, SnapshotTestSupport.Expect());
            Assert.Equal(rejection, actual);
            Assert.DoesNotContain(reason!, char.IsControl);
            Assert.DoesNotContain('\u202e', reason!);
            Assert.Contains("\\u001b", reason, StringComparison.Ordinal);
            Assert.Contains("...", reason, StringComparison.Ordinal);
            Assert.True(reason!.Length < 320, $"the reason is {reason.Length} characters");
        }
    }

    [Fact]
    public void Open_RefusesAKeyOfTheWrongLength()
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, SmallFile());
        Assert.Throws<ArgumentException>(() => SnapshotFile.Open(path, new byte[16], SnapshotTestSupport.Expect()));
        Assert.Throws<ArgumentException>(() => SnapshotLoader.Load(path, [], SnapshotTestSupport.Expect()));
    }

    [Fact]
    public void SectionAboveTheLargestArray_IsAShardLimit_NotAnotherException()
    {
        SnapshotWriter.EnsureSectionFits(Array.MaxLength);
        var exception = Assert.Throws<InvalidOperationException>(() => SnapshotWriter.EnsureSectionFits(Array.MaxLength + 1L));
        Assert.Contains("largest array", exception.Message, StringComparison.Ordinal);
        // 536,870,912 u32 entries: the section a reviewer saw fail outside the writer's catch.
        Assert.Throws<InvalidOperationException>(() => SnapshotWriter.EnsureSectionFits(536_870_912L * 4));
    }

    // ---- P2: Unix ownership and modes (each returns early on Windows, where the profile ACL and deny-write apply) ----

    [Fact]
    public void Snapshot_IsCreatedUserOnly_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        SnapshotTestSupport.WriteFile(path, SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(SmallDatabase())));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void UnixSnapshotGrantingGroupOrOtherWrite_IsRefused_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, SmallFile());
        foreach (var extra in new[] { UnixFileMode.GroupWrite, UnixFileMode.OtherWrite })
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);
            Assert.Equal(SnapshotRejection.SharedWritable, SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).Rejection);
        }
        // A group-readable file of this user's is claimed: opened, and its mode set to 0600.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using (var file = SnapshotFile.Open(path, SnapshotTestSupport.Key, SnapshotTestSupport.Expect()).File!) Assert.NotEmpty(file.Shards);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void UnixKeyGrantingGroupOrOtherAccess_IsRefused_OnUnix()
    {
        if (OperatingSystem.IsWindows()) return;   // Unix only
        using var temp = new SnapshotTempDirectory();
        var path = temp.File("cache.key");
        SnapshotKey.LoadOrCreate(path);
        foreach (var extra in new[] { UnixFileMode.GroupRead, UnixFileMode.OtherRead, UnixFileMode.OtherWrite })
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);
            Assert.Contains("group or others", Assert.Throws<UnauthorizedAccessException>(() => SnapshotKey.LoadOrCreate(path)).Message, StringComparison.Ordinal);
        }
    }

    private sealed class FailingStream(Stream inner, long failAfter) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_written + buffer.Length > failAfter) throw new IOException("The disk is full (simulated).");
            inner.Write(buffer);
            _written += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static InMemoryDatabase SmallDatabase()
    {
        var key = new ShardKey("csharp", "default");
        return SnapshotTestSupport.Commit((key,
            [new CodeNode { Id = "csharp:default:A", Identifier = "csharp:A", Name = "Alpha", Type = "Class", FilePath = "/r/A.cs", Metadata = SnapshotTestSupport.Owned(key) }],
            [new CodeEdge { Id = "csharp:default:A=[CALLS]=>x", SourceId = "csharp:default:A", TargetId = "x", Type = "CALLS", Metadata = SnapshotTestSupport.Owned(key) }]));
    }

    internal static byte[] SmallFile(bool caseInsensitive = true)
    {
        var key = new ShardKey("csharp", "default");
        var database = SnapshotTestSupport.Commit((key,
            [
                new CodeNode { Id = "csharp:default:A", Identifier = "csharp:A", Name = "Alpha", Type = "Class", FilePath = "/r/A.cs", Metadata = SnapshotTestSupport.Owned(key) },
                new CodeNode { Id = "csharp:default:B", Identifier = "csharp:B", Name = "Beta", Type = "Method", FilePath = "/r/A.cs", Metadata = SnapshotTestSupport.Owned(key, ("external", "true")) },
            ],
            [
                new CodeEdge { Id = "csharp:default:A=[CALLS]=>csharp:default:B@3:4", SourceId = "csharp:default:A", TargetId = "csharp:default:B", Type = "CALLS", Metadata = SnapshotTestSupport.Owned(key, ("line", "3"), ("column", "4")) },
                new CodeEdge { Id = "csharp:default:verbatim", SourceId = "csharp:default:B", TargetId = "elsewhere:C", Type = "REFERENCES", Metadata = SnapshotTestSupport.Owned(key, ("line", "07")) },
            ]));
        return SnapshotTestSupport.WriteBytes(SnapshotTestSupport.Input(SnapshotTestSupport.ShardsOf(database), caseInsensitive: caseInsensitive));
    }

    private static byte[] Patch32(byte[] bytes, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        SnapshotTestSupport.Resign(bytes);
        return bytes;
    }

    private static void AssertRefused(byte[] bytes, SnapshotRejection rejection, byte[]? key = null, SnapshotExpectations? expected = null)
    {
        using var temp = new SnapshotTempDirectory();
        var path = temp.Snapshot();
        File.WriteAllBytes(path, bytes);
        var result = SnapshotLoader.Load(path, key ?? SnapshotTestSupport.Key, expected ?? SnapshotTestSupport.Expect());
        Assert.Equal(rejection, result.Rejection);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }
}
