using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MyTunnel.Gateway.Tunnels;

/// <summary>
/// Sends requests for <c>{subdomain}.{PublicDomain}</c> through the matching tunnel. Requests for any other
/// host (the gateway's own domain, reserved subdomains) continue to the gateway's endpoints. A tunnel host
/// never falls through to gateway endpoints, so a tunnel cannot be used to reach /tunnel or the API.
/// </summary>
public sealed class TunnelRoutingMiddleware : IDisposable
{
    private readonly RequestDelegate _next;
    private readonly TunnelRegistry _registry;
    private readonly GatewayOptions _options;
    private readonly PartitionedRateLimiter<string> _perTunnelLimiter;

    public TunnelRoutingMiddleware(RequestDelegate next, TunnelRegistry registry, IOptions<GatewayOptions> options)
    {
        _next = next;
        _registry = registry;
        _options = options.Value;
        _perTunnelLimiter = PartitionedRateLimiter.Create<string, string>(subdomain =>
            RateLimitPartition.GetFixedWindowLimiter(subdomain, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = _options.RequestsPerMinutePerTunnel,
                Window = TimeSpan.FromMinutes(1),
            }));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!Subdomains.TryExtract(context.Request.Host.Host, _options.PublicDomain, out var subdomain)
            || _options.ReservedSubdomains.Contains(subdomain, StringComparer.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!_registry.TryGet(subdomain, out var session))
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status404NotFound, "tunnel_not_found",
                $"Tunnel '{subdomain}' is not online. Start it with 'mytunnel http <port>'.");
            return;
        }

        if (context.WebSockets.IsWebSocketRequest)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status501NotImplemented, "websocket_not_supported",
                "WebSocket upgrades through tunnels are not supported yet.");
            return;
        }

        using var lease = _perTunnelLimiter.AttemptAcquire(subdomain);
        if (!lease.IsAcquired)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status429TooManyRequests, "tunnel_rate_limited",
                "This tunnel has exceeded its request rate limit. Try again shortly.");
            return;
        }

        await session.ForwardAsync(context);
    }

    public void Dispose() => _perTunnelLimiter.Dispose();
}
