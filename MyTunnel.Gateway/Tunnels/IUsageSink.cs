namespace MyTunnel.Gateway.Tunnels;

/// <summary>Receives one call per completed public request. Must be cheap and must not throw.</summary>
public interface IUsageSink
{
    void Record(TunnelPrincipal principal, string subdomain, int statusCode, long bytesIn, long bytesOut);
}

public sealed class NullUsageSink : IUsageSink
{
    public void Record(TunnelPrincipal principal, string subdomain, int statusCode, long bytesIn, long bytesOut)
    {
    }
}
