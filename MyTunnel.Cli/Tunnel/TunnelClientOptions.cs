using System.Net;
using System.Reflection;

namespace MyTunnel.Cli.Tunnel;

public sealed class TunnelClientOptions
{
    /// <summary>Gateway base URL (http/https); the WebSocket endpoint is derived from it.</summary>
    public required Uri GatewayUri { get; init; }

    /// <summary>The only origin requests are ever forwarded to.</summary>
    public required Uri LocalOrigin { get; init; }

    public string? AuthToken { get; init; }

    public string? Subdomain { get; init; }

    /// <summary>How long the local application may take to return response headers.</summary>
    public TimeSpan LocalResponseTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxConcurrentRequests { get; init; } = 64;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public static string ClientVersion { get; } =
        typeof(TunnelClientOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Uri GetTunnelWebSocketUri(Uri gatewayUri)
    {
        var builder = new UriBuilder(gatewayUri)
        {
            Scheme = gatewayUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = gatewayUri.AbsolutePath.TrimEnd('/') + Contracts.TunnelProtocol.TunnelPath,
            Query = "",
            Fragment = "",
        };
        return builder.Uri;
    }

    public static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>Returns an error message, or null when the gateway URL is acceptable.</summary>
    public static string? ValidateGatewayUri(Uri gatewayUri)
    {
        if (gatewayUri.Scheme != Uri.UriSchemeHttps && gatewayUri.Scheme != Uri.UriSchemeHttp)
        {
            return "The gateway URL must start with https:// (or http:// for a local development gateway).";
        }

        // Credentials must never cross a network unencrypted.
        if (gatewayUri.Scheme == Uri.UriSchemeHttp && !IsLoopback(gatewayUri))
        {
            return "Refusing to connect to a remote gateway over unencrypted http://. Use https://.";
        }

        return null;
    }
}
