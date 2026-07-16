using System.Text;

namespace CodeContext.Parser.Protocol;

/// <summary>
/// Content-Length framing (LSP-style) over a byte stream:
/// <c>Content-Length: N\r\n\r\n{payload}</c>. Headers are ASCII; the payload is UTF-8.
/// </summary>
public static class HeaderFraming
{
    public const int DefaultMaxPayloadBytes = 64 * 1024 * 1024;

    private const string ContentLengthHeader = "Content-Length";
    private const int MaxHeaderBytes = 4 * 1024;

    public static async Task WriteFrameAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = Encoding.ASCII.GetBytes($"{ContentLengthHeader}: {payload.Length}\r\n\r\n");
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one framed payload. Returns null on clean EOF at a frame boundary.
    /// Throws <see cref="ParserProtocolViolationException"/> on malformed input.
    /// </summary>
    public static async Task<byte[]?> ReadFrameAsync(
        Stream input, int maxPayloadBytes = DefaultMaxPayloadBytes, CancellationToken ct = default)
    {
        var headerBytes = await ReadHeaderBlockAsync(input, ct).ConfigureAwait(false);
        if (headerBytes is null)
        {
            return null;
        }

        var contentLength = ParseContentLength(Encoding.ASCII.GetString(headerBytes));
        if (contentLength < 0 || contentLength > maxPayloadBytes)
        {
            throw new ParserProtocolViolationException(
                $"Frame payload length {contentLength} is outside [0, {maxPayloadBytes}].");
        }

        var payload = new byte[contentLength];
        try
        {
            await input.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException eos)
        {
            throw new ParserProtocolViolationException("Stream ended inside a frame payload.", eos);
        }
        return payload;
    }

    /// <summary>Reads bytes until the blank-line header terminator; null on immediate EOF.</summary>
    private static async Task<byte[]?> ReadHeaderBlockAsync(Stream input, CancellationToken ct)
    {
        var buffer = new List<byte>(64);
        var single = new byte[1];

        while (true)
        {
            var read = await input.ReadAsync(single, ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (buffer.Count == 0)
                {
                    return null; // clean EOF between frames
                }
                throw new ParserProtocolViolationException("Stream ended inside a frame header.");
            }

            buffer.Add(single[0]);
            if (buffer.Count > MaxHeaderBytes)
            {
                throw new ParserProtocolViolationException(
                    $"Frame header exceeded {MaxHeaderBytes} bytes; input is not Content-Length framed.");
            }

            if (buffer.Count >= 4 &&
                buffer[^4] == (byte)'\r' && buffer[^3] == (byte)'\n' &&
                buffer[^2] == (byte)'\r' && buffer[^1] == (byte)'\n')
            {
                return buffer.ToArray();
            }
        }
    }

    private static int ParseContentLength(string headerBlock)
    {
        int? parsedLength = null;
        foreach (var line in headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;

            var name = line[..separator].Trim();
            if (!name.Equals(ContentLengthHeader, StringComparison.OrdinalIgnoreCase)) continue;

            if (parsedLength is not null)
            {
                throw new ParserProtocolViolationException("Frame header contains multiple Content-Length fields.");
            }
            if (int.TryParse(line[(separator + 1)..].Trim(), out var length))
            {
                parsedLength = length;
                continue;
            }
            throw new ParserProtocolViolationException($"Unparseable Content-Length header: '{line}'.");
        }
        if (parsedLength is not null) return parsedLength.Value;
        throw new ParserProtocolViolationException("Frame header is missing Content-Length.");
    }
}
