using System.Collections.Concurrent;
using MyTunnel.ControlPlane.Services;
using MyTunnel.Gateway.Tunnels;

namespace MyTunnel.Gateway.ControlPlane;

/// <summary>
/// Validates personal access tokens from the database. Falls back to the static/anonymous development
/// authenticator only for tokens that are not in the access-token format.
/// </summary>
public sealed class DbTunnelAuthenticator(IServiceScopeFactory scopes, StaticTokenAuthenticator fallback) : ITunnelAuthenticator
{
    public async ValueTask<TunnelPrincipal?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        if (token is null || !MyTunnel.ControlPlane.Security.Secrets.TryGetAccessTokenPrefix(token, out _))
        {
            return await fallback.AuthenticateAsync(token, cancellationToken);
        }

        await using var scope = scopes.CreateAsyncScope();
        var accessToken = await scope.ServiceProvider.GetRequiredService<AccessTokenService>().ValidateAsync(token, cancellationToken);
        return accessToken is null ? null : new TunnelPrincipal(accessToken.UserId.ToString(), accessToken.Id.ToString());
    }
}

/// <summary>Refuses subdomains another account has reserved.</summary>
public sealed class DbSubdomainClaimPolicy(IServiceScopeFactory scopes) : ISubdomainClaimPolicy
{
    public async ValueTask<bool> CanClaimAsync(TunnelPrincipal principal, string subdomain, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var userId = Guid.TryParse(principal.UserId, out var id) ? id : (Guid?)null;
        return await scope.ServiceProvider.GetRequiredService<ProjectService>().CanUseSubdomainAsync(userId, subdomain, cancellationToken);
    }
}

/// <summary>
/// Aggregates per-request usage in memory and flushes it to <c>usage_daily</c> periodically and at shutdown,
/// so the database sees one upsert per (user, day, subdomain) per interval instead of one write per request.
/// </summary>
public sealed class UsageAggregator(IServiceScopeFactory scopes, TimeProvider clock, ILogger<UsageAggregator> logger)
    : BackgroundService, IUsageSink
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);
    private ConcurrentDictionary<(Guid User, DateOnly Day, string Subdomain), Counters> _pending = new();

    public void Record(TunnelPrincipal principal, string subdomain, int statusCode, long bytesIn, long bytesOut)
    {
        if (!Guid.TryParse(principal.UserId, out var userId))
        {
            return; // Development/static principals have no account to bill.
        }

        var key = (userId, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), subdomain);
        var counters = _pending.GetOrAdd(key, _ => new Counters());
        Interlocked.Increment(ref counters.Requests);
        if (statusCode >= 500)
        {
            Interlocked.Increment(ref counters.Errors);
        }

        Interlocked.Add(ref counters.BytesIn, bytesIn);
        Interlocked.Add(ref counters.BytesOut, bytesOut);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = Interlocked.Exchange(ref _pending, new ConcurrentDictionary<(Guid User, DateOnly Day, string Subdomain), Counters>());
        if (batch.IsEmpty)
        {
            return;
        }

        var deltas = batch.Select(e => new UsageDelta(e.Key.User, e.Key.Day, e.Key.Subdomain,
            e.Value.Requests, e.Value.Errors, e.Value.BytesIn, e.Value.BytesOut)).ToList();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UsageStore>().AddAsync(deltas, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Usage is best-effort telemetry; losing one interval is preferable to unbounded retry buffers.
            logger.LogWarning(ex, "Failed to flush {Count} usage records", deltas.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(FlushInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    private sealed class Counters
    {
        public long Requests;
        public long Errors;
        public long BytesIn;
        public long BytesOut;
    }
}

/// <summary>Runs <see cref="RetentionService"/> once an hour.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<RetentionService>().PurgeAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Retention purge removed {Count} rows", deleted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Retention purge failed");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
