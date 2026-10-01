
using System.Net;
using System.Net.WebSockets;

if (args.Length == 0)
{
    Console.WriteLine("MyTunnel CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  MyTunnel.Cli http <port>");
    return;
}

if (args[0] != "http")
{
    Console.WriteLine($"Unknown command: {args[0]}");
    return;
}

if (args.Length != 2 ||
    !int.TryParse(args[1], out var port) ||
    port < 1 ||
    port > 65535)
{
    Console.WriteLine("Usage: MyTunnel.Cli http <port>");
    Console.WriteLine("Please provide a valid port number.");
    return;
}

var gatewayUrl = Environment.GetEnvironmentVariable("MYTUNNEL_GATEWAY")
                 ?? "http://localhost:5018";

Console.WriteLine($"Local port: {port}");

var localUrl = $"http://localhost:{port}";

Console.WriteLine($"Local application: {localUrl}");

using var localHttpClient = new HttpClient();

try
{
    var localResponse = await localHttpClient.GetAsync(localUrl);

    if (localResponse.IsSuccessStatusCode)
    {
        Console.WriteLine("Local application is reachable!");
    }
    else
    {
        Console.WriteLine(
            $"Local application returned HTTP {(int)localResponse.StatusCode}.");
        return;
    }
}
catch (HttpRequestException)
{
    Console.WriteLine("Could not connect to the local application.");
    return;
}

Console.WriteLine($"Gateway: {gatewayUrl}");

using var httpClient = new HttpClient();


using var webSocket = new ClientWebSocket();

try
{
    var gatewayWebSocketUrl =
        $"{gatewayUrl.TrimEnd('/')}/tunnel"
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");

    Console.WriteLine("Connecting to the gateway...");

    await webSocket.ConnectAsync(
        new Uri(gatewayWebSocketUrl),
        CancellationToken.None);


    Console.WriteLine("WebSocket connection established!");
    Console.WriteLine($"State: {webSocket.State}");

    Console.WriteLine("Tunnel connection is active.");

    var message = System.Text.Json.JsonSerializer.Serialize(new
    {
        type = "ping",
        message = "Hello from MyTunnel CLI!"
    });

    var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);

    Console.WriteLine($"Sent: {message}");

    await webSocket.SendAsync(
        new ArraySegment<byte>(messageBytes),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None);

    var receiveBuffer = new byte[1024];

    var result = await webSocket.ReceiveAsync(
        new ArraySegment<byte>(receiveBuffer),
        CancellationToken.None);

    if (result.MessageType == WebSocketMessageType.Text)
    {
        var reply = System.Text.Encoding.UTF8.GetString(
        receiveBuffer, 0, result.Count);

        using var document = System.Text.Json.JsonDocument.Parse(reply);

        var type = document.RootElement
            .GetProperty("type")
            .GetString();

        var replyMessage = document.RootElement
            .GetProperty("message")
            .GetString();

        Console.WriteLine($"Received message type: {type}");
        Console.WriteLine($"Gateway says: {replyMessage}");
    }

    
    Console.WriteLine("Press Ctrl+C to stop.");

    await Task.Delay(Timeout.Infinite);

}
catch (WebSocketException ex)
{
    Console.WriteLine($"WebSocket connection failed: {ex.Message}");
}
