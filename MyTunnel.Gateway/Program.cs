
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => "MyTunnel Gateway is running!");

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    service = "MyTunnel.Gateway"
}));

app.MapGet("/proxy", async (HttpClient httpClient) =>
{
    var response = await httpClient.GetAsync("http://localhost:5019/");

    var content = await response.Content.ReadAsStringAsync();

    return Results.Text(content);
});


app.Map("/tunnel", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode =
            StatusCodes.Status400BadRequest;

        await context.Response.WriteAsync(
            "A WebSocket connection is required.");

        return;
    }

    using var webSocket =
        await context.WebSockets.AcceptWebSocketAsync();


    Console.WriteLine("CLI connected to the gateway.");

    var buffer = new byte[1024];

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed",
                    CancellationToken.None);

                break;
            }


            var message = System.Text.Encoding.UTF8.GetString(
                buffer, 0, result.Count);

            using var document = System.Text.Json.JsonDocument.Parse(message);

            var type = document.RootElement
                .GetProperty("type")
                .GetString();

            Console.WriteLine($"Received message type: {type}");

            if (type == "ping")
            {
                var reply = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "pong",
                    message = "Hello from MyTunnel Gateway!"
                });

                var replyBytes = System.Text.Encoding.UTF8.GetBytes(reply);

                await webSocket.SendAsync(
                    new ArraySegment<byte>(replyBytes),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);

                Console.WriteLine("Pong sent to CLI.");
            }

        }

    }
    finally
    {
        if (webSocket.State == WebSocketState.Open)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Connection closed by gateway",
                CancellationToken.None);
        }

        Console.WriteLine("WebSocket connection closed.");
    }

});


app.Run();
