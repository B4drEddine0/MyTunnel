using System.Buffers;
using System.Net.WebSockets;

namespace MyTunnel.Contracts;

public enum TunnelFrameKind
{
    Message,
    Body,
    Closed,
}

/// <summary>One complete WebSocket message. <see cref="Body"/> payload is only valid until the next receive.</summary>
public readonly record struct TunnelFrame(TunnelFrameKind Kind, TunnelMessage? Message, BodyFrame Body)
{
    public static readonly TunnelFrame Closed = new(TunnelFrameKind.Closed, null, default);
}

/// <summary>
/// Message-level wrapper around a WebSocket: reassembles fragmented messages, enforces size limits,
/// parses frames and serialises concurrent sends (a WebSocket permits only one send at a time).
/// </summary>
public sealed class TunnelSocket : IDisposable
{
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _receiveBuffer = new byte[Math.Max(TunnelProtocol.MaxControlMessageBytes, BodyFrame.MaxFrameSize)];

    // Cancelling an in-progress WebSocket send corrupts the stream, so writes only observe this
    // connection-wide token. Callers' tokens only bound the wait for the send lock.
    private readonly CancellationTokenSource _connectionCts = new();

    public TunnelSocket(WebSocket socket) => _socket = socket;

    public WebSocketState State => _socket.State;

    public Task SendAsync(TunnelMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = TunnelMessageSerializer.Serialize(message);
        if (bytes.Length > TunnelProtocol.MaxControlMessageBytes)
        {
            throw new InvalidOperationException("Control message exceeds the protocol maximum.");
        }

        return SendRawAsync(bytes, WebSocketMessageType.Text, cancellationToken);
    }

    public async Task SendBodyAsync(Guid requestId, ReadOnlyMemory<byte> payload, bool endOfStream, CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BodyFrame.HeaderSize + payload.Length);
        try
        {
            var length = BodyFrame.Write(buffer, requestId, payload.Span, endOfStream);
            await SendRawAsync(buffer.AsMemory(0, length), WebSocketMessageType.Binary, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendRawAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(data, type, endOfMessage: true, _connectionCts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Receives one complete message. Must not be called concurrently.</summary>
    /// <exception cref="ProtocolException">The peer sent an oversized or malformed message.</exception>
    public async Task<TunnelFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (true)
        {
            if (count == _receiveBuffer.Length)
            {
                throw new ProtocolException(ProtocolErrorCodes.MessageTooLarge, "Message is too large.");
            }

            var result = await _socket.ReceiveAsync(_receiveBuffer.AsMemory(count), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return TunnelFrame.Closed;
            }

            count += result.Count;
            var limit = result.MessageType == WebSocketMessageType.Text
                ? TunnelProtocol.MaxControlMessageBytes
                : BodyFrame.MaxFrameSize;
            if (count > limit)
            {
                throw new ProtocolException(ProtocolErrorCodes.MessageTooLarge, "Message is too large.");
            }

            if (!result.EndOfMessage)
            {
                continue;
            }

            return result.MessageType == WebSocketMessageType.Text
                ? new TunnelFrame(TunnelFrameKind.Message, TunnelMessageSerializer.Deserialize(_receiveBuffer.AsSpan(0, count)), default)
                : new TunnelFrame(TunnelFrameKind.Body, null, BodyFrame.Parse(_receiveBuffer.AsMemory(0, count)));
        }
    }

    /// <summary>Sends a close frame (best effort); aborts if that is not possible. Never throws.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string description)
    {
        using var timeout = new CancellationTokenSource(CloseTimeout);
        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseOutputAsync(status, description, timeout.Token);
                    return;
                }
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The peer is gone or unresponsive; aborting below is all that is left to do.
        }

        Abort();
    }

    /// <summary>Tears the connection down immediately, failing pending sends and receives.</summary>
    public void Abort()
    {
        try
        {
            _connectionCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _socket.Abort();
    }

    public void Dispose()
    {
        _connectionCts.Dispose();
        _socket.Dispose();
    }
}
