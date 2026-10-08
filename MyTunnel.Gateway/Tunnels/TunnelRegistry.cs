using System.Diagnostics.CodeAnalysis;

namespace MyTunnel.Gateway.Tunnels;

public enum RegistrationOutcome
{
    Registered,
    SubdomainUnavailable,
    TunnelLimitReached,
}

/// <summary>In-memory map of live tunnels, keyed by subdomain. Only connected sessions are ever present.</summary>
public sealed class TunnelRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TunnelSession> _bySubdomain = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bySubdomain.Count;
            }
        }
    }

    /// <summary>
    /// Claims the session's subdomain. If the same user already holds it (typically a reconnect after a
    /// connection the gateway has not yet noticed is dead), the old session is returned in
    /// <paramref name="displaced"/> and must be closed by the caller.
    /// </summary>
    public RegistrationOutcome TryRegister(TunnelSession session, int maxTunnelsPerUser, out TunnelSession? displaced)
    {
        lock (_gate)
        {
            displaced = null;
            if (_bySubdomain.TryGetValue(session.Subdomain, out var existing))
            {
                if (existing.UserId != session.UserId)
                {
                    return RegistrationOutcome.SubdomainUnavailable;
                }

                displaced = existing;
            }

            var replaced = displaced;
            var userTunnels = _bySubdomain.Values.Count(s => s.UserId == session.UserId && s != replaced);
            if (userTunnels >= maxTunnelsPerUser)
            {
                displaced = null;
                return RegistrationOutcome.TunnelLimitReached;
            }

            _bySubdomain[session.Subdomain] = session;
            return RegistrationOutcome.Registered;
        }
    }

    public bool IsTaken(string subdomain)
    {
        lock (_gate)
        {
            return _bySubdomain.ContainsKey(subdomain);
        }
    }

    public bool TryGet(string subdomain, [NotNullWhen(true)] out TunnelSession? session)
    {
        lock (_gate)
        {
            return _bySubdomain.TryGetValue(subdomain, out session);
        }
    }

    /// <summary>Removes the entry only if it still points at this session, so a replacement is never evicted.</summary>
    public void Remove(TunnelSession session)
    {
        lock (_gate)
        {
            if (_bySubdomain.TryGetValue(session.Subdomain, out var current) && current == session)
            {
                _bySubdomain.Remove(session.Subdomain);
            }
        }
    }

    public IReadOnlyList<TunnelSession> Snapshot()
    {
        lock (_gate)
        {
            return [.. _bySubdomain.Values];
        }
    }

    public IReadOnlyList<TunnelSession> ForUser(string userId)
    {
        lock (_gate)
        {
            return [.. _bySubdomain.Values.Where(s => s.UserId == userId)];
        }
    }
}
