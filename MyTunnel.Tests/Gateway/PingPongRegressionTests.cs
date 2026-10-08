using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Gateway;

/// <summary>
/// Locks in the Step 17 milestone: a client that sends the original unversioned
/// <c>{"type":"ping","message":...}</c> JSON receives a JSON <c>pong</c>.
/// </summary>
public sealed class PingPongRegressionTests
{
    [Fact]
    public async Task Legacy_ping_receives_pong()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(gateway.WebSocketUri, timeout.Token);

        var ping = """{"type":"ping","message":"Hello from MyTunnel CLI!"}"""u8.ToArray();
        await socket.SendAsync(ping, WebSocketMessageType.Text, true, timeout.Token);

        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);

        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        using var reply = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
        Assert.Equal("pong", reply.RootElement.GetProperty("type").GetString());
        Assert.Equal("Hello from MyTunnel Gateway!", reply.RootElement.GetProperty("message").GetString());

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var http = new HttpClient { BaseAddress = gateway.BaseUri };

        using var response = await http.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
    }
}
