using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace MyTunnel.Tests.Infrastructure;

/// <summary>Two raw WebSockets connected over a loopback TCP socket, for transport-level tests.</summary>
public sealed class WebSocketPair : IDisposable
{
    private WebSocketPair(WebSocket client, WebSocket server)
    {
        Client = client;
        Server = server;
    }

    public WebSocket Client { get; }

    public WebSocket Server { get; }

    public static async Task<WebSocketPair> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var clientTcp = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await clientTcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var serverTcp = await accept;

            var client = WebSocket.CreateFromStream(clientTcp.GetStream(), isServer: false, subProtocol: null, Timeout.InfiniteTimeSpan);
            var server = WebSocket.CreateFromStream(serverTcp.GetStream(), isServer: true, subProtocol: null, Timeout.InfiniteTimeSpan);
            return new WebSocketPair(client, server);
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }
}
