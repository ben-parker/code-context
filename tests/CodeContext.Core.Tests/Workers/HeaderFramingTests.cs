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

    [Fact]
    public async Task ReadFrame_HeaderLargerThanInitialBuffer_CompactsAndGrows()
    {
        // Buffer starts at 16 bytes. Frame 1's payload fully buffers and spills into frame 2's
        // header (forcing a compaction when frame 2 is read), and frame 2's header is far larger
        // than 16 bytes (forcing the EnsureReadSpace grow branch, which production constants never
        // reach because DefaultInitialBufferSize 8192 > MaxHeaderBytes 4096).
        var frame1 = Frame("hi");
        var bigHeaderFrame = Encoding.ASCII.GetBytes(
            "Content-Length: 5\r\nX-Padding: " + new string('q', 60) + "\r\n\r\nworld");
        var bytes = frame1.Concat(bigHeaderFrame).ToArray();
        // int.MaxValue chunkSize hands back as much as the buffer allows per read, so frame 1's
        // payload and the first slice of frame 2 arrive in the same fill.
        using var stream = new ChunkedStream(bytes, chunkSize: int.MaxValue);

        using var reader = new FrameReader(stream, HeaderFraming.DefaultMaxPayloadBytes, initialBufferSize: 16);
        Assert.Equal("hi", await ReadOne(reader));
        Assert.Equal("world", await ReadOne(reader));
        Assert.Null(await ReadOne(reader));
    }

    [Fact]
    public async Task ReadFrame_CancellationDuringPayloadRead_ThrowsAndReturnsBuffers()
    {
        // Header + partial payload arrive in the first read; the token is canceled before the
        // remainder read. The rented payload buffer must return to the pool (via the finally),
        // and a fresh reader over a normal frame must still work (no pool corruption).
        var cts = new CancellationTokenSource();
        var headerAndPartial = Encoding.ASCII.GetBytes("Content-Length: 20\r\n\r\npartial"); // 7 of 20 payload bytes
        using var stream = new CancelAfterFirstReadStream(headerAndPartial, cts);
        using var reader = new FrameReader(stream);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await reader.ReadFrameAsync(cts.Token));

        var good = new MemoryStream();
        await HeaderFraming.WriteFrameAsync(good, Encoding.UTF8.GetBytes("ok"));
        good.Position = 0;
        using var reader2 = new FrameReader(good);
        Assert.Equal("ok", await ReadOne(reader2));
    }

    [Fact]
    public async Task ReadFrame_NegativeContentLength_Throws()
    {
        // Utf8Parser accepts the '-' sign, so parsing succeeds at -5; the [0, max] range check rejects it.
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: -5\r\n\r\n"));
        using var reader = new FrameReader(stream);
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            async () => await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ReadFrame_PlusSignContentLength_ParsesLikeUtf8Parser()
    {
        // Documents the deliberate narrowing vs. the former int.TryParse. Utf8Parser accepts an
        // optional leading '+' for signed integers, so "+5" still parses to 5 and the frame reads
        // normally — the two parsers agree here. (Digits-only is the real-world framing spec.)
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: +5\r\n\r\nhello"));
        using var reader = new FrameReader(stream);
        Assert.Equal("hello", await ReadOne(reader));
    }

    [Fact]
    public void FrameLease_Default_DisposeIsNoOp()
    {
        // default(FrameLease) never rented a buffer; disposing it must not throw.
        default(FrameLease).Dispose();
    }

    /// <summary>
    /// Returns header + partial payload on the first read (arming the CTS as a side effect); any
    /// later read observes the canceled token and throws, deterministically hitting the payload
    /// remainder read.
    /// </summary>
    private sealed class CancelAfterFirstReadStream(byte[] first, CancellationTokenSource cts) : Stream
    {
        private int _reads;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (_reads++ == 0)
            {
                first.AsSpan().CopyTo(buffer.Span);
                cts.Cancel();
                return ValueTask.FromResult(first.Length);
            }
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unexpected read after cancellation.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
