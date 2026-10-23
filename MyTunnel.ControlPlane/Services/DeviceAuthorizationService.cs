using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Security;

namespace MyTunnel.ControlPlane.Services;

public sealed record DeviceAuthorizationStart(string DeviceCode, string UserCode, DateTimeOffset ExpiresAt, TimeSpan PollInterval);

public enum DeviceTokenStatus
{
    Pending,
    SlowDown,
    Expired,
    Denied,
    Issued,
}

public sealed record DeviceTokenResult(DeviceTokenStatus Status, string? AccessToken = null, string? Email = null);

/// <summary>
/// Browser-approved CLI login, following the RFC 8628 shape: the CLI gets a secret device code and shows the
/// user a short user code; the user approves it while signed in to the dashboard; the CLI's next poll receives
/// a newly minted access token. The token is created at collection time, so no plaintext token is ever stored.
/// </summary>
public sealed class DeviceAuthorizationService(
    MyTunnelDbContext db,
    AccessTokenService tokens,
    AuditLog audit,
    IOptions<ControlPlaneOptions> options,
    TimeProvider clock)
{
    public async Task<DeviceAuthorizationStart> StartAsync(string? clientName, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var deviceCode = Secrets.CreateOpaque();
        var now = clock.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            var authorization = new DeviceAuthorization
            {
                Id = Guid.CreateVersion7(),
                DeviceCodeHash = Secrets.Hash(deviceCode),
                UserCode = Secrets.CreateUserCode(),
                ClientName = Sanitize(clientName),
                CreatedAt = now,
                ExpiresAt = now + settings.DeviceCodeLifetime,
            };
            db.DeviceAuthorizations.Add(authorization);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new DeviceAuthorizationStart(deviceCode, Secrets.FormatUserCode(authorization.UserCode), authorization.ExpiresAt, settings.DevicePollInterval);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation() && attempt < 3)
            {
                // User-code collision (1 in 20^8); pick another.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Looks up a pending, unexpired request so the dashboard can show what is being approved.</summary>
    public Task<DeviceAuthorization?> FindPendingAsync(string userCode, CancellationToken cancellationToken)
    {
        var code = Secrets.NormalizeUserCode(userCode);
        var now = clock.GetUtcNow();
        return db.DeviceAuthorizations.AsNoTracking().SingleOrDefaultAsync(
            d => d.UserCode == code && d.ExpiresAt > now && d.ApprovedAt == null && d.DeniedAt == null && d.ConsumedAt == null,
            cancellationToken);
    }

    public async Task<bool> DecideAsync(string userCode, Guid userId, bool approve, string? ipAddress, CancellationToken cancellationToken)
    {
        var code = Secrets.NormalizeUserCode(userCode);
        var now = clock.GetUtcNow();
        var authorization = await db.DeviceAuthorizations.SingleOrDefaultAsync(
            d => d.UserCode == code && d.ExpiresAt > now && d.ApprovedAt == null && d.DeniedAt == null && d.ConsumedAt == null,
            cancellationToken);
        if (authorization is null)
        {
            return false;
        }

        if (approve)
        {
            authorization.ApprovedAt = now;
            authorization.ApprovedByUserId = userId;
        }
        else
        {
            authorization.DeniedAt = now;
        }

        audit.Record(approve ? AuditTypes.DeviceApproved : AuditTypes.DeviceDenied, userId, ipAddress, authorization.ClientName);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Called by the polling CLI. Issues the token exactly once after approval.</summary>
    public async Task<DeviceTokenResult> ExchangeAsync(string deviceCode, CancellationToken cancellationToken)
    {
        var hash = Secrets.Hash(deviceCode ?? "");
        var authorization = await db.DeviceAuthorizations.Include(d => d.ApprovedByUser)
            .SingleOrDefaultAsync(d => d.DeviceCodeHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        if (authorization is null || authorization.ConsumedAt is not null || authorization.ExpiresAt <= now)
        {
            return new DeviceTokenResult(DeviceTokenStatus.Expired);
        }

        if (authorization.DeniedAt is not null)
        {
            return new DeviceTokenResult(DeviceTokenStatus.Denied);
        }

        if (authorization.ApprovedByUser is not { } user)
        {
            var tooSoon = authorization.LastPolledAt is { } last && now - last < options.Value.DevicePollInterval * 0.8;
            authorization.LastPolledAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new DeviceTokenResult(tooSoon ? DeviceTokenStatus.SlowDown : DeviceTokenStatus.Pending);
        }

        // Claim the request atomically so two concurrent polls cannot both receive a token.
        var claimed = await db.DeviceAuthorizations
            .Where(d => d.Id == authorization.Id && d.ConsumedAt == null)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.ConsumedAt, now), cancellationToken);
        if (claimed != 1)
        {
            return new DeviceTokenResult(DeviceTokenStatus.Expired);
        }

        var name = $"CLI login{(authorization.ClientName is { } client ? $" ({client})" : "")}";
        var created = await tokens.CreateAsync(user.Id, name, now + options.Value.CliTokenLifetime, ipAddress: null, cancellationToken);
        return created is null
            ? new DeviceTokenResult(DeviceTokenStatus.Denied)
            : new DeviceTokenResult(DeviceTokenStatus.Issued, created.Secret, user.Email);
    }

    private static string? Sanitize(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return null;
        }

        var clean = new string(clientName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }
}
