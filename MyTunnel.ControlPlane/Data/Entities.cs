namespace MyTunnel.ControlPlane.Data;

public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Lower-cased, trimmed; unique.</summary>
    public required string Email { get; set; }

    public required string PasswordHash { get; set; }

    public string? DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }
}

/// <summary>A signed-in dashboard session. The cookie holds only the id; revoking the row signs the browser out.</summary>
public sealed class UserSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>Personal access token used by the CLI and automation. Only a SHA-256 verifier of the secret is stored.</summary>
public sealed class AccessToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>Public, non-secret lookup key embedded in the token (e.g. "mt_ab12cd34_...").</summary>
    public required string Prefix { get; set; }

    public required byte[] SecretHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Browser-approved CLI login (modelled on RFC 8628 device authorization).</summary>
public sealed class DeviceAuthorization
{
    public Guid Id { get; set; }

    public required byte[] DeviceCodeHash { get; set; }

    /// <summary>Short code the user confirms in the browser, stored without the dash, e.g. "BCDFGHJK".</summary>
    public required string UserCode { get; set; }

    public string? ClientName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    public User? ApprovedByUser { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? DeniedAt { get; set; }

    /// <summary>Set when the CLI collected its token; the request can never be used again.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    public DateTimeOffset? LastPolledAt { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; } = null!;

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<ReservedSubdomain> Subdomains { get; set; } = [];
}

/// <summary>A subdomain only its owner may register a tunnel on.</summary>
public sealed class ReservedSubdomain
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Project Project { get; set; } = null!;

    /// <summary>Denormalised owner for the hot-path ownership check at tunnel registration.</summary>
    public Guid UserId { get; set; }

    public required string Subdomain { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Per-user, per-subdomain, per-day request counters (aggregated in memory by the gateway, flushed periodically).</summary>
public sealed class UsageDaily
{
    public Guid UserId { get; set; }

    public DateOnly Day { get; set; }

    public required string Subdomain { get; set; }

    public long Requests { get; set; }

    public long Errors { get; set; }

    public long BytesIn { get; set; }

    public long BytesOut { get; set; }
}

public sealed class AuditEvent
{
    public long Id { get; set; }

    /// <summary>Null when the account was deleted or the event was not tied to an account (e.g. failed login for an unknown email).</summary>
    public Guid? UserId { get; set; }

    public required string Type { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>Small, non-sensitive context such as a token name or subdomain. Never secrets.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
