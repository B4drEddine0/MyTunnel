using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyTunnel.Contracts;
using MyTunnel.Gateway.Tunnels;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Gateway;

/// <summary>A hostile or buggy client speaking directly to /tunnel.</summary>
public sealed class ProtocolAbuseTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task<(ClientWebSocket Raw, TunnelSocket Socket)> ConnectAsync(GatewayHost gateway)
    {
        var raw = new ClientWebSocket();
        await raw.ConnectAsync(gateway.WebSocketUri, Timeout);
        return (raw, new TunnelSocket(raw));
    }

    private static async Task<RegisteredMessage> RegisterAsync(TunnelSocket socket)
    {
        await socket.SendAsync(new RegisterMessage(TunnelProtocol.CurrentVersion, TestTokens.TokenA));
        var frame = await socket.ReceiveAsync(Timeout);
        return Assert.IsType<RegisteredMessage>(frame.Message);
    }

    /// <summary>Reads until the gateway closes, returning the messages it sent first.</summary>
    private static async Task<List<TunnelMessage>> ReadUntilClosedAsync(TunnelSocket socket)
    {
        var messages = new List<TunnelMessage>();
        try
        {
            while (true)
            {
                var frame = await socket.ReceiveAsync(Timeout);
                if (frame.Kind == TunnelFrameKind.Closed)
                {
                    return messages;
                }

                if (frame.Message is not null)
                {
                    messages.Add(frame.Message);
                }
            }
        }
        catch (WebSocketException)
        {
            return messages;
        }
    }

    [Fact]
    public async Task Connection_without_registration_times_out()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:RegistrationTimeout"] = "00:00:00.500",
        }));
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;

        var messages = await ReadUntilClosedAsync(socket);
        var rejected = Assert.IsType<RegistrationRejectedMessage>(Assert.Single(messages));
        Assert.Equal(ProtocolErrorCodes.RegistrationTimeout, rejected.Code);
    }

    [Fact]
    public async Task Ping_before_registration_is_still_answered()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;

        await socket.SendAsync(new PingMessage("hi"));
        var pong = Assert.IsType<PongMessage>((await socket.ReceiveAsync(Timeout)).Message);
        Assert.Equal("Hello from MyTunnel Gateway!", pong.Message);

        // And registration still works afterwards.
        await RegisterAsync(socket);
    }

    [Fact]
    public async Task Messages_before_registration_are_rejected()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;

        await socket.SendAsync(new TunnelHttpResponse(Guid.NewGuid(), 200, [], false));
        var error = Assert.IsType<ErrorMessage>(Assert.Single(await ReadUntilClosedAsync(socket)));
        Assert.Equal(ProtocolErrorCodes.NotRegistered, error.Code);
    }

    [Fact]
    public async Task Unsupported_protocol_version_is_rejected()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;

        await socket.SendAsync(new RegisterMessage(TunnelProtocol.CurrentVersion + 1, TestTokens.TokenA));
        var rejected = Assert.IsType<RegistrationRejectedMessage>(Assert.Single(await ReadUntilClosedAsync(socket)));
        Assert.Equal(ProtocolErrorCodes.UnsupportedProtocolVersion, rejected.Code);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("""{"type":"http_request"}""")]
    [InlineData("""{"type":"teleport"}""")]
    public async Task Malformed_messages_close_the_connection_and_unregister(string payload)
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;
        await RegisterAsync(socket);

        await raw.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, Timeout);
        var error = Assert.IsType<ErrorMessage>(Assert.Single(await ReadUntilClosedAsync(socket)));
        Assert.Equal(ProtocolErrorCodes.InvalidMessage, error.Code);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, raw.CloseStatus);

        var registry = gateway.Services.GetRequiredService<TunnelRegistry>();
        for (var i = 0; i < 100 && registry.Count > 0; i++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task Client_may_not_send_requests_to_the_gateway()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;
        await RegisterAsync(socket);

        await socket.SendAsync(new TunnelHttpRequest(Guid.NewGuid(), "GET", "/", [], false));
        var error = Assert.IsType<ErrorMessage>(Assert.Single(await ReadUntilClosedAsync(socket)));
        Assert.Equal(ProtocolErrorCodes.UnexpectedMessage, error.Code);
    }

    [Fact]
    public async Task Oversized_message_closes_with_message_too_big()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;
        await RegisterAsync(socket);

        try
        {
            await raw.SendAsync(new byte[TunnelProtocol.MaxControlMessageBytes + 10], WebSocketMessageType.Text, true, Timeout);
        }
        catch (WebSocketException)
        {
        }

        await ReadUntilClosedAsync(socket);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, raw.CloseStatus);
    }

    [Fact]
    public async Task Response_for_an_unknown_request_is_ignored_and_body_without_headers_is_rejected()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var (raw, socket) = await ConnectAsync(gateway);
        using var _ = raw;
        var registration = await RegisterAsync(socket);

        // A response nobody asked for cannot be injected into another request.
        await socket.SendAsync(new TunnelHttpResponse(Guid.NewGuid(), 200, [], false));
        await socket.SendAsync(new PingMessage());
        Assert.IsType<PongMessage>((await socket.ReceiveAsync(Timeout)).Message);

        // Start a real request, then send body before headers.
        using var http = new HttpClient { BaseAddress = gateway.BaseUri };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = $"{registration.Subdomain}.localhost";
        var visitor = http.SendAsync(request);
        var forwarded = Assert.IsType<TunnelHttpRequest>((await socket.ReceiveAsync(Timeout)).Message);
        await socket.SendBodyAsync(forwarded.RequestId, "x"u8.ToArray(), endOfStream: true);

        var error = Assert.IsType<ErrorMessage>(Assert.Single(await ReadUntilClosedAsync(socket)));
        Assert.Equal(ProtocolErrorCodes.UnexpectedMessage, error.Code);
        using var response = await visitor;
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Tunnel_connection_rate_limit_is_enforced_per_ip()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:TunnelConnectionsPerMinutePerIp"] = "2",
        }));

        for (var i = 0; i < 2; i++)
        {
            using var ok = new ClientWebSocket();
            await ok.ConnectAsync(gateway.WebSocketUri, Timeout);
        }

        using var limited = new ClientWebSocket();
        limited.Options.CollectHttpResponseDetails = true;
        await Assert.ThrowsAsync<WebSocketException>(() => limited.ConnectAsync(gateway.WebSocketUri, Timeout));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.HttpStatusCode);
    }
}
