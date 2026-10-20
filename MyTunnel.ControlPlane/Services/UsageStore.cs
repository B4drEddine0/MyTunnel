using Microsoft.EntityFrameworkCore;
using MyTunnel.ControlPlane.Data;

namespace MyTunnel.ControlPlane.Services;

public sealed record UsageDelta(Guid UserId, DateOnly Day, string Subdomain, long Requests, long Errors, long BytesIn, long BytesOut);

public sealed class UsageStore(MyTunnelDbContext db)
{
    /// <summary>Adds the deltas to the daily counters (insert or increment), in one transaction.</summary>
    public async Task AddAsync(IReadOnlyCollection<UsageDelta> deltas, CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var d in deltas)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO usage_daily (user_id, day, subdomain, requests, errors, bytes_in, bytes_out)
                VALUES ({d.UserId}, {d.Day}, {d.Subdomain}, {d.Requests}, {d.Errors}, {d.BytesIn}, {d.BytesOut})
                ON CONFLICT (user_id, day, subdomain) DO UPDATE SET
                    requests = usage_daily.requests + EXCLUDED.requests,
                    errors = usage_daily.errors + EXCLUDED.errors,
                    bytes_in = usage_daily.bytes_in + EXCLUDED.bytes_in,
                    bytes_out = usage_daily.bytes_out + EXCLUDED.bytes_out
                """, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task<List<UsageDaily>> ListAsync(Guid userId, DateOnly since, CancellationToken cancellationToken) =>
        db.UsageDaily.AsNoTracking()
            .Where(u => u.UserId == userId && u.Day >= since)
            .OrderBy(u => u.Day).ThenBy(u => u.Subdomain)
            .ToListAsync(cancellationToken);
}

/// <summary>Deletes data past its retention period. Safe to run repeatedly.</summary>
public sealed class RetentionService(MyTunnelDbContext db, Microsoft.Extensions.Options.IOptions<ControlPlaneOptions> options, TimeProvider clock)
{
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var settings = options.Value;
        var usageCutoff = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-settings.UsageRetentionDays));
        var deleted = 0;
        deleted += await db.UsageDaily.Where(u => u.Day < usageCutoff).ExecuteDeleteAsync(cancellationToken);
        deleted += await db.AuditEvents.Where(a => a.CreatedAt < now.AddDays(-settings.AuditRetentionDays)).ExecuteDeleteAsync(cancellationToken);
        deleted += await db.DeviceAuthorizations.Where(d => d.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(cancellationToken);
        deleted += await db.UserSessions.Where(s => s.ExpiresAt < now.AddDays(-30)).ExecuteDeleteAsync(cancellationToken);
        return deleted;
    }
}
