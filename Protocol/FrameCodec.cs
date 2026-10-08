using System.Buffers;
using System.Buffers.Binary;

namespace TempLab.Protocol;

/// <summary>
/// Length-prefixed frames: 4-byte little-endian uint32 length, then exactly that many bytes.
/// Reading never allocates more than <c>maxFrameBytes</c> because the header is validated first.
/// </summary>
public static class FrameCodec
{
    public const int HeaderBytes = 4;
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        var frame = new byte[HeaderBytes + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static byte[] EncodeEnvelope(Envelope envelope) => Encode(ProtocolJson.SerializeEnvelope(envelope));

    /// <summary>
    /// Reads one frame body. Returns null on a clean end of stream (EOF before any header byte).
    /// </summary>
    /// <exception cref="ProtocolException">Invalid/oversized length or EOF in the middle of a frame.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        int got = await ReadAtLeastAsync(stream, header, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < HeaderBytes)
            throw new ProtocolException(ProtocolErrorKind.Truncated, $"stream ended after {got} header bytes");

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0)
            throw new ProtocolException(ProtocolErrorKind.InvalidLength, "zero-length frame");
        if (length > (uint)maxFrameBytes)
            throw new ProtocolException(ProtocolErrorKind.Oversized, $"frame of {length} bytes exceeds limit {maxFrameBytes}");

        var body = new byte[length];
        got = await ReadAtLeastAsync(stream, body, ct).ConfigureAwait(false);
        if (got < body.Length)
            throw new ProtocolException(ProtocolErrorKind.Truncated, $"stream ended after {got} of {length} body bytes");
        return body;
    }

    public static async ValueTask<Envelope?> ReadEnvelopeAsync(Stream stream, int maxFrameBytes, CancellationToken ct)
    {
        var body = await ReadFrameAsync(stream, maxFrameBytes, ct).ConfigureAwait(false);
        return body is null ? null : ProtocolJson.DeserializeEnvelope(body);
    }

    /// <summary>Loops over partial reads until the buffer is full or EOF. Returns bytes read.</summary>
    private static async ValueTask<int> ReadAtLeastAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Incremental decoder for callers that receive arbitrary byte chunks (used by tests and tools):
    /// handles partial frames and several frames in one chunk.
    /// </summary>
    public sealed class Decoder(int maxFrameBytes = DefaultMaxFrameBytes)
    {
        private readonly ArrayBufferWriter<byte> _pending = new();

        public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> chunk)
        {
            _pending.Write(chunk);
            var frames = new List<byte[]>();
            int offset = 0;
            var data = _pending.WrittenSpan;
            while (data.Length - offset >= HeaderBytes)
            {
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
                if (length == 0) throw new ProtocolException(ProtocolErrorKind.InvalidLength, "zero-length frame");
                if (length > (uint)maxFrameBytes)
                    throw new ProtocolException(ProtocolErrorKind.Oversized, $"frame of {length} bytes exceeds limit {maxFrameBytes}");
                if (data.Length - offset - HeaderBytes < length) break;
                frames.Add(data.Slice(offset + HeaderBytes, (int)length).ToArray());
                offset += HeaderBytes + (int)length;
            }
            var rest = data[offset..].ToArray();
            _pending.Clear();
            _pending.Write(rest);
            return frames;
        }

        public int BufferedBytes => _pending.WrittenCount;
    }
}
