using System.Text.Json.Serialization;

namespace MyTunnel.Contracts;

/// <summary>
/// A JSON control message. Every message is a flat object whose <c>type</c> property selects the
/// concrete record. HTTP bodies never travel inside these messages; they use <see cref="BodyFrame"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(PongMessage), "pong")]
[JsonDerivedType(typeof(RegisterMessage), "register")]
[JsonDerivedType(typeof(RegisteredMessage), "registered")]
[JsonDerivedType(typeof(RegistrationRejectedMessage), "registration_rejected")]
[JsonDerivedType(typeof(TunnelHttpRequest), "http_request")]
[JsonDerivedType(typeof(TunnelHttpResponse), "http_response")]
[JsonDerivedType(typeof(CancelRequestMessage), "cancel")]
[JsonDerivedType(typeof(WindowUpdateMessage), "window")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record TunnelMessage
{
    /// <summary>Throws <see cref="ProtocolException"/> if the message is structurally valid JSON but semantically invalid.</summary>
    public virtual void Validate()
    {
    }

    private protected static void Require(bool condition, string detail)
    {
        if (!condition)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, detail);
        }
    }

    private protected static void RequireShortText(string? value, int maxLength, string field, bool required = true)
    {
        if (value is null)
        {
            Require(!required, $"'{field}' is required.");
            return;
        }

        Require(value.Length <= maxLength, $"'{field}' is too long.");
        Require(!required || value.Length > 0, $"'{field}' must not be empty.");
    }
}

/// <summary>Liveness probe. Unchanged from the original proof of concept, so unversioned clients still work.</summary>
public sealed record PingMessage(string? Message = null) : TunnelMessage
{
    public override void Validate() => RequireShortText(Message, 1024, "message", required: false);
}

public sealed record PongMessage(string? Message = null) : TunnelMessage
{
    public override void Validate() => RequireShortText(Message, 1024, "message", required: false);
}

/// <summary>First message a CLI sends to claim a tunnel. Must be answered with registered or registration_rejected.</summary>
public sealed record RegisterMessage(
    int ProtocolVersion,
    string? AuthToken = null,
    string? Subdomain = null,
    string? ClientVersion = null) : TunnelMessage
{
    public override void Validate()
    {
        Require(ProtocolVersion > 0, "'protocolVersion' must be positive.");
        RequireShortText(AuthToken, 512, "authToken", required: false);
        RequireShortText(Subdomain, 63, "subdomain", required: false);
        RequireShortText(ClientVersion, 64, "clientVersion", required: false);
    }

    // Records print every property by default; never let the credential reach a log line.
    public override string ToString() =>
        $"RegisterMessage {{ ProtocolVersion = {ProtocolVersion}, AuthToken = {(AuthToken is null ? "<none>" : "<redacted>")}, Subdomain = {Subdomain}, ClientVersion = {ClientVersion} }}";
}

public sealed record RegisteredMessage(
    int ProtocolVersion,
    string TunnelId,
    string Subdomain,
    string PublicUrl,
    long MaxRequestBodyBytes,
    long MaxResponseBodyBytes) : TunnelMessage
{
    public override void Validate()
    {
        RequireShortText(TunnelId, 64, "tunnelId");
        RequireShortText(Subdomain, 63, "subdomain");
        RequireShortText(PublicUrl, 2048, "publicUrl");
        Require(MaxRequestBodyBytes >= 0 && MaxResponseBodyBytes >= 0, "Body limits must not be negative.");
    }
}

public sealed record RegistrationRejectedMessage(string Code, string Message) : TunnelMessage
{
    public override void Validate()
    {
        RequireShortText(Code, 64, "code");
        RequireShortText(Message, 1024, "message");
    }
}

public sealed record HttpHeader(string Name, string Value);

/// <summary>
/// Start of a forwarded HTTP request (gateway to CLI). When <see cref="HasBody"/> is true the body follows
/// as binary frames for <see cref="RequestId"/>, terminated by a frame with the end-of-stream flag.
/// </summary>
public sealed record TunnelHttpRequest(
    Guid RequestId,
    string Method,
    string Target,
    IReadOnlyList<HttpHeader> Headers,
    bool HasBody) : TunnelMessage
{
    public override void Validate()
    {
        Require(RequestId != Guid.Empty, "'requestId' is required.");
        Require(HttpValidation.IsToken(Method) && Method.Length <= 32, "'method' is not a valid HTTP method token.");
        Require(HttpValidation.IsOriginFormTarget(Target), "'target' must be an origin-form path and query.");
        HttpValidation.ValidateHeaders(Headers);
    }
}

/// <summary>Start of an HTTP response (CLI to gateway). The body follows as binary frames when <see cref="HasBody"/> is true.</summary>
public sealed record TunnelHttpResponse(
    Guid RequestId,
    int StatusCode,
    IReadOnlyList<HttpHeader> Headers,
    bool HasBody) : TunnelMessage
{
    public override void Validate()
    {
        Require(RequestId != Guid.Empty, "'requestId' is required.");
        Require(StatusCode is >= 200 and <= 599, "'statusCode' must be a final status between 200 and 599.");
        HttpValidation.ValidateHeaders(Headers);
    }
}

/// <summary>Either side abandons a request; the receiver releases its resources and stops sending for it.</summary>
public sealed record CancelRequestMessage(Guid RequestId, string? Reason = null) : TunnelMessage
{
    public override void Validate()
    {
        Require(RequestId != Guid.Empty, "'requestId' is required.");
        RequireShortText(Reason, 256, "reason", required: false);
    }
}

/// <summary>Grants the peer permission to send <see cref="Bytes"/> more body bytes for a request.</summary>
public sealed record WindowUpdateMessage(Guid RequestId, int Bytes) : TunnelMessage
{
    public override void Validate()
    {
        Require(RequestId != Guid.Empty, "'requestId' is required.");
        Require(Bytes is > 0 and <= 16 * 1024 * 1024, "'bytes' is out of range.");
    }
}

public sealed record ErrorMessage(string Code, string Message, Guid? RequestId = null) : TunnelMessage
{
    public override void Validate()
    {
        RequireShortText(Code, 64, "code");
        RequireShortText(Message, 1024, "message");
    }
}
