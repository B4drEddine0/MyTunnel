using System.Net.WebSockets;
using System.Security.Cryptography;
using MyTunnel.Contracts;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Contracts;

public sealed class TransportTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    [Fact]
    public void Body_frame_round_trips()
    {
        var id = Guid.NewGuid();
        var buffer = new byte[BodyFrame.MaxFrameSize];
        var length = BodyFrame.Write(buffer, id, "abc"u8, endOfStream: true);

        var frame = BodyFrame.Parse(buffer.AsMemory(0, length));
        Assert.Equal(id, frame.RequestId);
        Assert.True(frame.EndOfStream);
        Assert.Equal("abc"u8.ToArray(), frame.Payload.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 1, 0 })]
    [InlineData(new byte[] { 9, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    [InlineData(new byte[] { 1, 0x80, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    [InlineData(new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Malformed_body_frames_are_rejected(byte[] frame)
    {
        Assert.Throws<ProtocolException>(() => BodyFrame.Parse(frame));
    }

    [Fact]
    public async Task Fragmented_text_message_is_reassembled()
    {
        using var pair = await WebSocketPair.CreateAsync();
        var receiver = new TunnelSocket(pair.Server);

        var json = TunnelMessageSerializer.Serialize(new PingMessage(new string('x', 1000)));
        for (var offset = 0; offset < json.Length; offset += 37)
        {
            var count = Math.Min(37, json.Length - offset);
            await pair.Client.SendAsync(json.AsMemory(offset, count), WebSocketMessageType.Text, offset + count == json.Length, Timeout);
        }

        var frame = await receiver.ReceiveAsync(Timeout);
        Assert.Equal(TunnelFrameKind.Message, frame.Kind);
        Assert.Equal(1000, Assert.IsType<PingMessage>(frame.Message).Message!.Length);
    }

    [Fact]
    public async Task Oversized_text_message_is_rejected()
    {
        using var pair = await WebSocketPair.CreateAsync();
        var receiver = new TunnelSocket(pair.Server);

        var chunk = new byte[16 * 1024];
        var send = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    await pair.Client.SendAsync(chunk, WebSocketMessageType.Text, endOfMessage: false, Timeout);
                }
            }
            catch (WebSocketException)
            {
            }
        });

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => receiver.ReceiveAsync(Timeout));
        Assert.Equal(ProtocolErrorCodes.MessageTooLarge, ex.Code);
        receiver.Abort();
        await send;
    }

    [Fact]
    public async Task Close_frame_is_reported()
    {
        using var pair = await WebSocketPair.CreateAsync();
        var receiver = new TunnelSocket(pair.Server);
        await pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", Timeout);

        var frame = await receiver.ReceiveAsync(Timeout);
        Assert.Equal(TunnelFrameKind.Closed, frame.Kind);
    }

    [Fact]
    public async Task Body_larger_than_window_streams_with_flow_control()
    {
        using var pair = await WebSocketPair.CreateAsync();
        var sender = new TunnelSocket(pair.Client);
        var receiverSocket = new TunnelSocket(pair.Server);
        var requestId = Guid.NewGuid();
        var payload = RandomNumberGenerator.GetBytes(TunnelProtocol.InitialWindowBytes * 5 + 123);
        var window = new SendWindow(TunnelProtocol.InitialWindowBytes);

        var receiver = new BodyReceiver(long.MaxValue, grant => receiverSocket.SendAsync(new WindowUpdateMessage(requestId, grant)));

        // Sender side loop: route window updates arriving from the receiver.
        var senderLoop = Task.Run(async () =>
        {
            while (true)
            {
                var frame = await sender.ReceiveAsync(Timeout);
                if (frame.Kind != TunnelFrameKind.Message)
                {
                    return;
                }

                window.Grant(((WindowUpdateMessage)frame.Message!).Bytes);
            }
        });

        // Receiver side loop: push body frames into the receiver.
        var receiverLoop = Task.Run(async () =>
        {
            while (true)
            {
                var frame = await receiverSocket.ReceiveAsync(Timeout);
                Assert.Equal(TunnelFrameKind.Body, frame.Kind);
                Assert.Equal(BodyWriteResult.Accepted, receiver.Write(frame.Body.Payload.Span, frame.Body.EndOfStream));
                if (frame.Body.EndOfStream)
                {
                    return;
                }
            }
        });

        var send = BodySender.SendAsync(sender, requestId, new MemoryStream(payload), window, long.MaxValue, Timeout);

        using var copy = new MemoryStream();
        await receiver.Stream.CopyToAsync(copy, Timeout);
        Assert.Equal(payload.Length, await send);
        await receiverLoop;
        Assert.Equal(payload, copy.ToArray());

        await receiverSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done");
        await senderLoop;
    }

    [Fact]
    public void Receiver_rejects_data_beyond_granted_window()
    {
        var receiver = new BodyReceiver(long.MaxValue, _ => Task.CompletedTask);
        Assert.Equal(BodyWriteResult.Accepted, receiver.Write(new byte[TunnelProtocol.InitialWindowBytes], false));
        Assert.Equal(BodyWriteResult.FlowControlViolation, receiver.Write(new byte[1], false));
        Assert.Equal(BodyWriteResult.Ignored, receiver.Write(new byte[1], false));
    }

    [Fact]
    public async Task Receiver_enforces_body_limit()
    {
        var receiver = new BodyReceiver(10, _ => Task.CompletedTask);
        Assert.Equal(BodyWriteResult.Accepted, receiver.Write(new byte[10], false));
        Assert.Equal(BodyWriteResult.LimitExceeded, receiver.Write(new byte[1], false));
        await Assert.ThrowsAsync<BodyLimitExceededException>(() => receiver.Stream.CopyToAsync(Stream.Null));
    }

    [Fact]
    public async Task Sender_enforces_body_limit()
    {
        using var pair = await WebSocketPair.CreateAsync();
        var sender = new TunnelSocket(pair.Client);
        await Assert.ThrowsAsync<BodyLimitExceededException>(() =>
            BodySender.SendAsync(sender, Guid.NewGuid(), new MemoryStream(new byte[100]), new SendWindow(1000), 50, Timeout));
    }

    [Fact]
    public async Task Failed_window_wakes_waiting_sender()
    {
        var window = new SendWindow(0);
        var acquire = window.AcquireAsync(10, Timeout).AsTask();
        Assert.False(acquire.IsCompleted);
        window.Fail(new InvalidOperationException("gone"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquire);
    }
}
