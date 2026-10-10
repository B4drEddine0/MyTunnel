namespace MyTunnel.Gateway.Tunnels;

/// <summary>Responses the gateway itself produces for visitors when a tunnelled request cannot be completed.</summary>
public static class GatewayErrors
{
    public const string ErrorHeader = "X-MyTunnel-Error";

    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Headers already went out; the only honest signal left is to abort the connection.
            context.Abort();
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.Headers[ErrorHeader] = code;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync($"MyTunnel: {message}\n");
    }
}
