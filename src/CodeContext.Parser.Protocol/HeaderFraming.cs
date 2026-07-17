using System.Buffers;
using System.Buffers.Text;
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
    internal const int MaxHeaderBytes = 4 * 1024;

    public static async Task WriteFrameAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = Encoding.ASCII.GetBytes($"{ContentLengthHeader}: {payload.Length}\r\n\r\n");
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the <c>Content-Length</c> value out of a header block (bytes up to and
    /// including the terminating blank line). ASCII, no string allocation on the happy path.
    /// </summary>
    internal static int ParseContentLength(ReadOnlySpan<byte> headerBlock)
    {
        int? parsedLength = null;
        var rest = headerBlock;
        while (!rest.IsEmpty)
        {
            var newline = rest.IndexOf("\r\n"u8);
            var line = newline < 0 ? rest : rest[..newline];
            rest = newline < 0 ? [] : rest[(newline + 2)..];

            if (line.IsEmpty) continue;
            var separator = line.IndexOf((byte)':');
            if (separator <= 0) continue;

            var name = TrimAsciiSpace(line[..separator]);
            if (!Ascii.EqualsIgnoreCase(name, ContentLengthHeader)) continue;

            if (parsedLength is not null)
            {
                throw new ParserProtocolViolationException("Frame header contains multiple Content-Length fields.");
            }

            var value = TrimAsciiSpace(line[(separator + 1)..]);
            // Deliberate narrowing vs. the former int.TryParse: Utf8Parser is digits-with-
            // optional-sign and is not culture/format-flexible. In practice the framing spec is
            // digits-only and no worker emits a sign, so the two agree on every real input; a
            // framing test documents the exact behavior for '+' and '-'.
            if (Utf8Parser.TryParse(value, out int length, out int consumed) && consumed == value.Length)
            {
                parsedLength = length;
                continue;
            }
            throw new ParserProtocolViolationException(
                $"Unparseable Content-Length header: '{Encoding.ASCII.GetString(line)}'.");
        }

        if (parsedLength is not null) return parsedLength.Value;
        throw new ParserProtocolViolationException("Frame header is missing Content-Length.");
    }

    // ASCII-only trim (space and tab), a deliberate narrowing from the former Unicode
    // string.Trim(): frame headers are ASCII by spec, so no Unicode whitespace can legitimately
    // appear around a header value here.
    private static ReadOnlySpan<byte> TrimAsciiSpace(ReadOnlySpan<byte> span)
    {
        int start = 0;
        int end = span.Length;
        while (start < end && IsSpace(span[start])) start++;
        while (end > start && IsSpace(span[end - 1])) end--;
        return span[start..end];

        static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t';
    }
}

/// <summary>
/// A single framed payload rented from <see cref="ArrayPool{T}"/>. The <see cref="Payload"/>
/// span is valid until <see cref="Dispose"/>; the caller must fully consume it (e.g. deserialize)
/// before returning the lease. The backing buffer may be larger than <see cref="Length"/>.
/// </summary>
public readonly struct FrameLease : IDisposable
{
    private readonly byte[] _rented;

    internal FrameLease(byte[] rented, int length)
    {
        _rented = rented;
        Length = length;
    }

    /// <summary>Number of valid payload bytes.</summary>
    public int Length { get; }

    /// <summary>The payload bytes. Only valid until <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Payload => _rented.AsSpan(0, Length);

    public void Dispose()
    {
        // default(FrameLease) carries a null buffer (never rented) — make its Dispose a no-op
        // instead of throwing a NullReferenceException on the .Length access.
        if (_rented is { Length: > 0 })
        {
            ArrayPool<byte>.Shared.Return(_rented);
        }
    }
}

/// <summary>
/// Stateful reader for Content-Length framed payloads over one byte stream. Buffers reads
/// into a pooled buffer, scans for the <c>\r\n\r\n</c> header terminator, and carries surplus
/// bytes forward so pipelined frames delivered in a single read are handled without
/// over-consuming the stream. Not thread-safe: one reader per stream, driven by a single loop.
/// </summary>
public sealed class FrameReader : IDisposable
{
    private const int DefaultInitialBufferSize = 8 * 1024;

    private readonly Stream _input;
    private readonly int _maxPayloadBytes;
    private byte[] _buffer;
    private int _start; // start of unconsumed data in _buffer
    private int _end;   // end of unconsumed data in _buffer (valid region is [_start, _end))
    private bool _disposed;

    public FrameReader(Stream input, int maxPayloadBytes = HeaderFraming.DefaultMaxPayloadBytes)
        : this(input, maxPayloadBytes, DefaultInitialBufferSize)
    {
    }

    internal FrameReader(Stream input, int maxPayloadBytes, int initialBufferSize)
    {
        _input = input;
        _maxPayloadBytes = maxPayloadBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialBufferSize, 16));
    }

    /// <summary>
    /// Reads one framed payload. Returns null on clean EOF at a frame boundary.
    /// Throws <see cref="ParserProtocolViolationException"/> on malformed input.
    /// The returned lease owns a pooled buffer and must be disposed by the caller.
    /// </summary>
    public async ValueTask<FrameLease?> ReadFrameAsync(CancellationToken ct = default)
    {
        var payloadStart = await ReadPastHeaderAsync(ct).ConfigureAwait(false);
        if (payloadStart < 0)
        {
            return null; // clean EOF between frames
        }

        var contentLength = HeaderFraming.ParseContentLength(_buffer.AsSpan(_start, payloadStart - _start));
        if (contentLength < 0 || contentLength > _maxPayloadBytes)
        {
            throw new ParserProtocolViolationException(
                $"Frame payload length {contentLength} is outside [0, {_maxPayloadBytes}].");
        }

        _start = payloadStart; // consume the header (including the terminating blank line)

        var payload = ArrayPool<byte>.Shared.Rent(Math.Max(contentLength, 1));
        try
        {
            var buffered = Math.Min(contentLength, _end - _start);
            if (buffered > 0)
            {
                Array.Copy(_buffer, _start, payload, 0, buffered);
                _start += buffered;
            }

            var remaining = contentLength - buffered;
            if (remaining > 0)
            {
                try
                {
                    await _input.ReadExactlyAsync(payload.AsMemory(buffered, remaining), ct).ConfigureAwait(false);
                }
                catch (EndOfStreamException eos)
                {
                    throw new ParserProtocolViolationException("Stream ended inside a frame payload.", eos);
                }
            }

            ResetIfDrained();
            var lease = new FrameLease(payload, contentLength);
            payload = null!; // ownership transferred to the lease
            return lease;
        }
        finally
        {
            if (payload is not null)
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>
    /// Ensures a full header block is buffered and returns the index (into <c>_buffer</c>) of the
    /// first payload byte, i.e. just past the <c>\r\n\r\n</c> terminator. Returns -1 on clean EOF.
    /// </summary>
    private async ValueTask<int> ReadPastHeaderAsync(CancellationToken ct)
    {
        while (true)
        {
            var available = _end - _start;
            if (available >= 4)
            {
                var scanLen = Math.Min(available, HeaderFraming.MaxHeaderBytes);
                var index = _buffer.AsSpan(_start, scanLen).IndexOf("\r\n\r\n"u8);
                if (index >= 0)
                {
                    return _start + index + 4;
                }
            }

            if (available >= HeaderFraming.MaxHeaderBytes)
            {
                throw new ParserProtocolViolationException(
                    $"Frame header exceeded {HeaderFraming.MaxHeaderBytes} bytes; input is not Content-Length framed.");
            }

            EnsureReadSpace();
            var read = await _input.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end == _start)
                {
                    return -1; // clean EOF, nothing buffered
                }
                throw new ParserProtocolViolationException("Stream ended inside a frame header.");
            }
            _end += read;
        }
    }

    private void EnsureReadSpace()
    {
        if (_end < _buffer.Length)
        {
            return; // room to read at the tail
        }

        if (_start > 0)
        {
            // Compact: slide the unconsumed bytes to the front.
            var length = _end - _start;
            Array.Copy(_buffer, _start, _buffer, 0, length);
            _start = 0;
            _end = length;
            if (_end < _buffer.Length)
            {
                return;
            }
        }

        // Still full (a single header region larger than the buffer): grow.
        // Defensive only under production constants: DefaultInitialBufferSize (8192) already
        // exceeds MaxHeaderBytes (4096), so the header-size cap in ReadPastHeaderAsync trips
        // before an 8 KB buffer can fill without a terminator. Reached only via the internal
        // small-initial-buffer ctor, which the framing tests use to exercise this branch.
        var next = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
        Array.Copy(_buffer, _start, next, 0, _end - _start);
        _end -= _start;
        _start = 0;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    private void ResetIfDrained()
    {
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
