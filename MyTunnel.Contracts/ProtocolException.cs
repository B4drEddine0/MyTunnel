namespace MyTunnel.Contracts;

public static class ProtocolErrorCodes
{
    public const string InvalidMessage = "invalid_message";
    public const string MessageTooLarge = "message_too_large";
    public const string UnexpectedMessage = "unexpected_message";
    public const string FlowControlViolation = "flow_control_violation";
    public const string NotRegistered = "not_registered";
    public const string RegistrationTimeout = "registration_timeout";
    public const string UnsupportedProtocolVersion = "unsupported_protocol_version";
    public const string Unauthorized = "unauthorized";
    public const string InvalidSubdomain = "invalid_subdomain";
    public const string SubdomainUnavailable = "subdomain_unavailable";
    public const string TunnelLimitReached = "tunnel_limit_reached";
}

/// <summary>The peer violated the tunnel protocol. The connection should be closed.</summary>
public sealed class ProtocolException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}
