using MyTunnel.Contracts;

namespace MyTunnel.Cli.Tunnel;

/// <summary>Progress notifications from <see cref="TunnelClient"/>, so the client stays independent of the console.</summary>
public interface ITunnelClientEvents
{
    void Connecting(Uri gatewayUri);

    void Registered(RegisteredMessage registration);

    void RequestCompleted(string method, string path, int statusCode, TimeSpan elapsed);

    void Disconnected(string reason);

    void Reconnecting(TimeSpan delay, int attempt);

    void Warning(string message);
}

/// <summary>The gateway refused the registration. Retrying with the same settings will not help.</summary>
public sealed class TunnelRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
