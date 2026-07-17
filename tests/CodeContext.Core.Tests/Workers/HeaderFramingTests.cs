using System.Text;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

public class HeaderFramingTests
{
    private static byte[] Frame(string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var stream = new MemoryStream();
        HeaderFraming.WriteFrameAsync(stream, body).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    private static async Task<string?> ReadOne(FrameReader reader)
    {
        var lease = await reader.ReadFrameAsync();
        if (lease is not { } frame) return null;
        using (frame)
        {
            return Encoding.UTF8.GetString(frame.Payload);
        }
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsPayload()
    {
        var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        await HeaderFraming.WriteFrameAsync(stream, payload);
        stream.Position = 0;

        using var reader = new FrameReader(stream);
        var lease = await reader.ReadFrameAsync();
        Assert.NotNull(lease);
        using var frame = lease.Value;
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    [Fact]
    public async Task ReadFrame_MultipleFramesBackToBack_ReadsEachInOrder()
    {
        var stream = new MemoryStream();
        await HeaderFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("first"));
        await HeaderFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("second"));
        stream.Position = 0;

        using var reader = new FrameReader(stream);
        Assert.Equal("first", await ReadOne(reader));
        Assert.Equal("second", await ReadOne(reader));
        Assert.Null(await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_ManyPipelinedFramesInOneBuffer_ReadsAllInOrder()
    {
        // All frames delivered in a single read: exercises carry-over between frames.
        using var buffer = new MemoryStream();
        var expected = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            var text = $"payload-number-{i}";
            expected.Add(text);
            buffer.Write(Frame(text));
        }
        buffer.Position = 0;
        // A single-chunk stream that hands the whole blob back in one ReadAsync.
        using var stream = new ChunkedStream(buffer.ToArray(), chunkSize: int.MaxValue);

        using var reader = new FrameReader(stream);
        foreach (var text in expected)
        {
            Assert.Equal(text, await ReadOne(reader));
        }
        Assert.Null(await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_HeaderSplitAcrossArbitraryReadBoundaries_Reassembles()
    {
        var frames = Frame("first-payload").Concat(Frame("second-payload")).ToArray();
        // One byte per read: the header terminator and payload cross a boundary every step.
        using var stream = new ChunkedStream(frames, chunkSize: 1);

        using var reader = new FrameReader(stream);
        Assert.Equal("first-payload", await ReadOne(reader));
        Assert.Equal("second-payload", await ReadOne(reader));
        Assert.Null(await ReadOne(reader));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    public async Task ReadFrame_InterleavedPartialReads_Reassembles(int chunkSize)
    {
        var frames = Frame("alpha").Concat(Frame("beta")).Concat(Frame("gamma")).ToArray();
        using var stream = new ChunkedStream(frames, chunkSize);

        using var reader = new FrameReader(stream);
        Assert.Equal("alpha", await ReadOne(reader));
        Assert.Equal("beta", await ReadOne(reader));
        Assert.Equal("gamma", await ReadOne(reader));
        Assert.Null(await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_HeaderExactlyAtBufferBoundary_Reassembles()
    {
        // Force a tiny internal buffer so the header terminator straddles a compaction/grow.
        var frames = Frame("payload-a").Concat(Frame("payload-b")).ToArray();
        using var stream = new ChunkedStream(frames, chunkSize: int.MaxValue);

        using var reader = new FrameReader(stream, HeaderFraming.DefaultMaxPayloadBytes, initialBufferSize: 24);
        Assert.Equal("payload-a", await ReadOne(reader));
        Assert.Equal("payload-b", await ReadOne(reader));
        Assert.Null(await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_CleanEof_ReturnsNull()
    {
        using var reader = new FrameReader(new MemoryStream());
        Assert.Null(await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_EofInsidePayload_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\nshort"));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_EofInsideHeader_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 5\r\n"));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_MissingContentLength_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("X-Whatever: 5\r\n\r\nhello"));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_DuplicateContentLength_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}"));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_ExtraHeadersBeforeContentLength_AreIgnored()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "X-Trace: abc\r\ncontent-length: 5\r\n\r\nhello"));
        using var reader = new FrameReader(stream);
        Assert.Equal("hello", await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_PayloadOverLimit_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 1000\r\n\r\n"));
        using var reader = new FrameReader(stream, maxPayloadBytes: 100);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_UnframedGarbage_ThrowsInsteadOfHanging()
    {
        // Garbage without any \r\n\r\n terminator must fail via the header size cap.
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 10_000)));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_ZeroLengthPayload_ReturnsEmpty()
    {
        var stream = new MemoryStream();
        await HeaderFraming.WriteFrameAsync(stream, Array.Empty<byte>());
        await HeaderFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("after"));
        stream.Position = 0;

        using var reader = new FrameReader(stream);
        Assert.Equal("", await ReadOne(reader));
        Assert.Equal("after", await ReadOne(reader));
    }

    /// <summary>A stream that returns at most <c>chunkSize</c> bytes per read, to exercise split reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = data.Length - _position;
            if (remaining == 0) return 0;
            var n = Math.Min(Math.Min(count, chunkSize), remaining);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var remaining = data.Length - _position;
            if (remaining == 0) return ValueTask.FromResult(0);
            var n = Math.Min(Math.Min(buffer.Length, chunkSize), remaining);
            data.AsSpan(_position, n).CopyTo(buffer.Span);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
