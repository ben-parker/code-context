using System.Text;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

public class HeaderFramingTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsPayload()
    {
        var stream = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");

        await HeaderFraming.WriteFrameAsync(stream, payload);
        stream.Position = 0;

        var read = await HeaderFraming.ReadFrameAsync(stream);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task ReadFrame_MultipleFramesBackToBack_ReadsEachInOrder()
    {
        var stream = new MemoryStream();
        await HeaderFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("first"));
        await HeaderFraming.WriteFrameAsync(stream, Encoding.UTF8.GetBytes("second"));
        stream.Position = 0;

        Assert.Equal("first", Encoding.UTF8.GetString((await HeaderFraming.ReadFrameAsync(stream))!));
        Assert.Equal("second", Encoding.UTF8.GetString((await HeaderFraming.ReadFrameAsync(stream))!));
    }

    [Fact]
    public async Task ReadFrame_CleanEof_ReturnsNull()
    {
        var read = await HeaderFraming.ReadFrameAsync(new MemoryStream());
        Assert.Null(read);
    }

    [Fact]
    public async Task ReadFrame_EofInsidePayload_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\nshort"));
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => HeaderFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrame_MissingContentLength_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("X-Whatever: 5\r\n\r\nhello"));
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => HeaderFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrame_DuplicateContentLength_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(
            "Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}"));
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => HeaderFraming.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrame_PayloadOverLimit_Throws()
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("Content-Length: 1000\r\n\r\n"));
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => HeaderFraming.ReadFrameAsync(stream, maxPayloadBytes: 100));
    }

    [Fact]
    public async Task ReadFrame_UnframedGarbage_ThrowsInsteadOfHanging()
    {
        // Garbage without any \r\n\r\n terminator must fail via the header size cap.
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 10_000)));
        await Assert.ThrowsAsync<ParserProtocolViolationException>(
            () => HeaderFraming.ReadFrameAsync(stream));
    }
}
