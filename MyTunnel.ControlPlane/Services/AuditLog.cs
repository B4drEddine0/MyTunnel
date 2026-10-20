using MyTunnel.ControlPlane.Data;

namespace MyTunnel.ControlPlane.Services;

public static class AuditTypes
{
    public const string UserRegistered = "user.registered";
    public const string LoginSucceeded = "user.login_succeeded";
    public const string LoginFailed = "user.login_failed";
    public const string Logout = "user.logout";
    public const string PasswordChanged = "user.password_changed";
    public const string TokenCreated = "token.created";
    public const string TokenRevoked = "token.revoked";
    public const string DeviceApproved = "device.approved";
    public const string DeviceDenied = "device.denied";
    public const string ProjectCreated = "project.created";
    public const string ProjectDeleted = "project.deleted";
    public const string SubdomainReserved = "subdomain.reserved";
    public const string SubdomainReleased = "subdomain.released";
    public const string TunnelStopped = "tunnel.stopped";
}

/// <summary>Adds audit rows to the current unit of work so they commit atomically with the action they describe.</summary>
public sealed class AuditLog(MyTunnelDbContext db, TimeProvider clock)
{
    public void Record(string type, Guid? userId, string? ipAddress, string? detail = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Type = type,
            UserId = userId,
            IpAddress = ipAddress,
            Detail = detail is { Length: > 256 } ? detail[..256] : detail,
            CreatedAt = clock.GetUtcNow(),
        });
}
