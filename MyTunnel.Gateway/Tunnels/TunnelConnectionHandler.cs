using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using MyTunnel.Contracts;

namespace MyTunnel.Gateway.Tunnels;

/// <summary>Accepts CLI WebSocket connections on /tunnel, performs registration and runs the session.</summary>
public sealed class TunnelConnectionHandler(
    TunnelRegistry registry,
    ITunnelAuthenticator authenticator,
    ISubdomainClaimPolicy claimPolicy,
    IOptions<GatewayOptions> options,
    GatewayMetrics metrics,
    IUsageSink usage,
    IHostApplicationLifetime lifetime,
    ILogger<TunnelConnectionHandler> logger,
    ILoggerFactory loggerFactory)
{
    private const string GatewayPong = "Hello from MyTunnel Gateway!";

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("A WebSocket connection is required.");
            return;
        }

        var settings = options.Value;
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            KeepAliveInterval = settings.KeepAliveInterval,
            KeepAliveTimeout = settings.KeepAliveTimeout,
        });
        using var socket = new TunnelSocket(webSocket);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        var remoteIp = context.Connection.RemoteIpAddress;

        TunnelSession? session = null;
        try
        {
            session = await RegisterAsync(socket, settings, connectionCts.Token);
            if (session is null)
            {
                return;
            }

            logger.LogInformation(
                "Tunnel {Subdomain} ({TunnelId}) registered for user {UserId} from {RemoteIp}",
                session.Subdomain, session.TunnelId, session.UserId, remoteIp);
            await session.RunAsync(connectionCts.Token);
        }
        catch (ProtocolException ex)
        {
            logger.LogWarning("Closing tunnel connection from {RemoteIp}: protocol error {Code}: {Message}", remoteIp, ex.Code, ex.Message);
            await TrySendAsync(socket, new ErrorMessage(ex.Code, ex.Message));
            await socket.CloseAsync(
                ex.Code == ProtocolErrorCodes.MessageTooLarge ? WebSocketCloseStatus.MessageTooBig : WebSocketCloseStatus.PolicyViolation,
                ex.Code);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Gateway is shutting down");
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Connection lost; nothing to send.
        }
        finally
        {
            if (session is not null)
            {
                registry.Remove(session);
                logger.LogInformation("Tunnel {Subdomain} ({TunnelId}) disconnected after {Requests} requests",
                    session.Subdomain, session.TunnelId, session.RequestCount);
            }
        }
    }

    private async Task<TunnelSession?> RegisterAsync(TunnelSocket socket, GatewayOptions settings, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + settings.RegistrationTimeout;
        while (true)
        {
            // A timed-out WebSocket receive cannot be resumed, so the deadline races the receive instead of cancelling it.
            var receive = socket.ReceiveAsync(cancellationToken);
            TunnelFrame frame;
            try
            {
                frame = await receive.WaitAsync(deadline - DateTime.UtcNow, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or ArgumentOutOfRangeException)
            {
                _ = receive.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                metrics.Registration(ProtocolErrorCodes.RegistrationTimeout);
                await RejectAsync(socket, ProtocolErrorCodes.RegistrationTimeout, "No registration was received in time.");
                return null;
            }

            switch (frame.Kind)
            {
                case TunnelFrameKind.Closed:
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client");
                    return null;
                case TunnelFrameKind.Body:
                    throw new ProtocolException(ProtocolErrorCodes.NotRegistered, "Register before sending data.");
            }

            switch (frame.Message)
            {
                case PingMessage:
                    await socket.SendAsync(new PongMessage(GatewayPong), cancellationToken);
                    continue;
                case RegisterMessage register:
                    return await CompleteRegistrationAsync(socket, register, settings, cancellationToken);
                default:
                    throw new ProtocolException(ProtocolErrorCodes.NotRegistered, "Register before sending other messages.");
            }
        }
    }

    private async Task<TunnelSession?> CompleteRegistrationAsync(
        TunnelSocket socket, RegisterMessage register, GatewayOptions settings, CancellationToken cancellationToken)
    {
        if (register.ProtocolVersion is < TunnelProtocol.MinSupportedVersion or > TunnelProtocol.CurrentVersion)
        {
            return await RejectAsync(socket, ProtocolErrorCodes.UnsupportedProtocolVersion,
                $"This gateway supports protocol versions {TunnelProtocol.MinSupportedVersion}-{TunnelProtocol.CurrentVersion}; update the CLI.");
        }

        var principal = await authenticator.AuthenticateAsync(register.AuthToken, cancellationToken);
        if (principal is null)
        {
            return await RejectAsync(socket, ProtocolErrorCodes.Unauthorized, "Authentication failed. Run 'mytunnel login' or check your access token.");
        }

        var requested = register.Subdomain?.ToLowerInvariant();
        if (requested is not null)
        {
            if (!Subdomains.IsValid(requested) || settings.ReservedSubdomains.Contains(requested, StringComparer.OrdinalIgnoreCase))
            {
                return await RejectAsync(socket, ProtocolErrorCodes.InvalidSubdomain,
                    "Subdomains must be 3-63 lowercase letters, digits or hyphens and must not be reserved.");
            }

            if (!await claimPolicy.CanClaimAsync(principal, requested, cancellationToken))
            {
                return await RejectAsync(socket, ProtocolErrorCodes.SubdomainUnavailable, $"The subdomain '{requested}' is reserved by another account.");
            }
        }

        var sessionLogger = loggerFactory.CreateLogger<TunnelSession>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var subdomain = requested ?? Subdomains.Generate();
            if (requested is null && !await claimPolicy.CanClaimAsync(principal, subdomain, cancellationToken))
            {
                continue;
            }

            var session = new TunnelSession(Guid.NewGuid().ToString("N"), subdomain, principal, socket, settings, metrics, usage, sessionLogger);
            switch (registry.TryRegister(session, settings.MaxTunnelsPerUser, out var displaced))
            {
                case RegistrationOutcome.Registered:
                    if (displaced is not null)
                    {
                        logger.LogInformation("Tunnel {Subdomain} replaced by a new connection from the same user", subdomain);
                        await displaced.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Replaced by a newer connection");
                        displaced.Abort();
                    }

                    metrics.Registration("registered");
                    await socket.SendAsync(
                        new RegisteredMessage(
                            TunnelProtocol.CurrentVersion, session.TunnelId, subdomain, settings.BuildPublicUrl(subdomain),
                            settings.MaxRequestBodyBytes, settings.MaxResponseBodyBytes),
                        cancellationToken);
                    return session;
                case RegistrationOutcome.TunnelLimitReached:
                    return await RejectAsync(socket, ProtocolErrorCodes.TunnelLimitReached,
                        $"Your account already has {settings.MaxTunnelsPerUser} active tunnels.");
                case RegistrationOutcome.SubdomainUnavailable when requested is not null:
                    return await RejectAsync(socket, ProtocolErrorCodes.SubdomainUnavailable, $"The subdomain '{requested}' is in use.");
            }
        }

        return await RejectAsync(socket, ProtocolErrorCodes.SubdomainUnavailable, "Could not allocate a subdomain. Try again.");
    }

    private async Task<TunnelSession?> RejectAsync(TunnelSocket socket, string code, string message)
    {
        if (code != ProtocolErrorCodes.RegistrationTimeout)
        {
            metrics.Registration(code);
        }

        logger.LogInformation("Tunnel registration rejected: {Code}", code);
        await TrySendAsync(socket, new RegistrationRejectedMessage(code, message));
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, code);
        return null;
    }

    private static async Task TrySendAsync(TunnelSocket socket, TunnelMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(message, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}

/// <summary>Decides whether a principal may use a subdomain (for example, one reserved by another account).</summary>
public interface ISubdomainClaimPolicy
{
    ValueTask<bool> CanClaimAsync(TunnelPrincipal principal, string subdomain, CancellationToken cancellationToken);
}

/// <summary>No persistent reservations: any free subdomain may be claimed. Used until the control-plane database is configured.</summary>
public sealed class OpenSubdomainClaimPolicy : ISubdomainClaimPolicy
{
    public ValueTask<bool> CanClaimAsync(TunnelPrincipal principal, string subdomain, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
