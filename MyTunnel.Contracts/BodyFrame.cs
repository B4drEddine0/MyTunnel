using System.Buffers.Binary;

namespace MyTunnel.Contracts;

/// <summary>
/// Binary WebSocket message carrying one chunk of an HTTP body.
/// Layout: [kind:1][flags:1][requestId:16][payload:0..MaxBodyChunkBytes].
/// The request id uses the RFC 4122 big-endian byte order.
/// </summary>
public readonly record struct BodyFrame(Guid RequestId, bool EndOfStream, ReadOnlyMemory<byte> Payload)
{
    public const byte KindBody = 1;
    public const byte FlagEndOfStream = 0x01;
    public const int HeaderSize = 18;
    public const int MaxFrameSize = HeaderSize + TunnelProtocol.MaxBodyChunkBytes;

    public static int Write(Span<byte> destination, Guid requestId, ReadOnlySpan<byte> payload, bool endOfStream)
    {
        if (payload.Length > TunnelProtocol.MaxBodyChunkBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Body chunk exceeds the protocol maximum.");
        }

        destination[0] = KindBody;
        destination[1] = endOfStream ? FlagEndOfStream : (byte)0;
        requestId.TryWriteBytes(destination.Slice(2, 16), bigEndian: true, out _);
        payload.CopyTo(destination[HeaderSize..]);
        return HeaderSize + payload.Length;
    }

    /// <summary>Parses a frame. The returned payload aliases <paramref name="frame"/>.</summary>
    public static BodyFrame Parse(ReadOnlyMemory<byte> frame)
    {
        var span = frame.Span;
        if (span.Length < HeaderSize)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Binary frame is too short.");
        }

        if (span.Length > MaxFrameSize)
        {
            throw new ProtocolException(ProtocolErrorCodes.MessageTooLarge, "Binary frame is too large.");
        }

        if (span[0] != KindBody)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Unknown binary frame kind.");
        }

        if ((span[1] & ~FlagEndOfStream) != 0)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Unknown binary frame flags.");
        }

        var requestId = new Guid(span.Slice(2, 16), bigEndian: true);
        if (requestId == Guid.Empty)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Binary frame has no request id.");
        }

        return new BodyFrame(requestId, (span[1] & FlagEndOfStream) != 0, frame[HeaderSize..]);
    }
}
