using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Security;

namespace MyTunnel.ControlPlane.Services;

public sealed record CreatedAccessToken(AccessToken Token, string Secret);

public sealed class AccessTokenService(MyTunnelDbContext db, AuditLog audit, IOptions<ControlPlaneOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    /// <summary>Creates a token. The plaintext secret is returned exactly once and never stored.</summary>
    /// <returns>Null when the user already has the maximum number of active tokens.</returns>
    public async Task<CreatedAccessToken?> CreateAsync(
        Guid userId, string name, DateTimeOffset? expiresAt, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var active = await db.AccessTokens.CountAsync(
            t => t.UserId == userId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now), cancellationToken);
        if (active >= options.Value.MaxActiveTokensPerUser)
        {
            return null;
        }

        var (secret, prefix, hash) = Secrets.CreateAccessToken();
        var token = new AccessToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name.Trim(),
            Prefix = prefix,
            SecretHash = hash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        db.AccessTokens.Add(token);
        audit.Record(AuditTypes.TokenCreated, userId, ipAddress, token.Name);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedAccessToken(token, secret);
    }

    public Task<List<AccessToken>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        db.AccessTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Revokes one of the user's own tokens. Returns false if it does not exist or belongs to someone else.</summary>
    public async Task<bool> RevokeAsync(Guid userId, Guid tokenId, string? ipAddress, CancellationToken cancellationToken)
    {
        var token = await db.AccessTokens.SingleOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        if (token.RevokedAt is null)
        {
            token.RevokedAt = clock.GetUtcNow();
            audit.Record(AuditTypes.TokenRevoked, userId, ipAddress, token.Name);
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>Returns the token (with its user) if the secret is valid, unexpired, unrevoked and the user is enabled.</summary>
    public async Task<AccessToken?> ValidateAsync(string? secret, CancellationToken cancellationToken)
    {
        if (!Secrets.TryGetAccessTokenPrefix(secret, out var prefix))
        {
            return null;
        }

        var token = await db.AccessTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.Prefix == prefix, cancellationToken);
        var now = clock.GetUtcNow();
        if (token is null
            || !Secrets.Matches(secret!, token.SecretHash)
            || token.RevokedAt is not null
            || token.ExpiresAt <= now
            || token.User.DisabledAt is not null)
        {
            return null;
        }

        if (token.LastUsedAt is null || now - token.LastUsedAt > LastUsedResolution)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return token;
    }
}
