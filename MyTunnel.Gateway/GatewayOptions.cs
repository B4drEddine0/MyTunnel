namespace MyTunnel.Gateway;

public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Tunnels are served at <c>{subdomain}.{PublicDomain}</c>. "localhost" works for local development.</summary>
    public string PublicDomain { get; set; } = "localhost";

    /// <summary>Scheme visitors use; "https" in production where TLS terminates in front of or at the gateway.</summary>
    public string PublicScheme { get; set; } = "http";

    /// <summary>Port shown in public URLs; leave unset for the scheme's default port.</summary>
    public int? PublicPort { get; set; }

    public TimeSpan RegistrationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a visitor waits for the CLI to return response headers.</summary>
    public TimeSpan ResponseHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    public long MaxResponseBodyBytes { get; set; } = 100 * 1024 * 1024;

    public int MaxConcurrentRequestsPerTunnel { get; set; } = 64;

    public int MaxTunnelsPerUser { get; set; } = 5;

    /// <summary>New tunnel WebSocket connections allowed per client IP per minute.</summary>
    public int TunnelConnectionsPerMinutePerIp { get; set; } = 30;

    /// <summary>Login, registration and password-change attempts allowed per client IP per minute.</summary>
    public int AuthRequestsPerMinutePerIp { get; set; } = 10;

    /// <summary>Public requests allowed per tunnel per minute.</summary>
    public int RequestsPerMinutePerTunnel { get; set; } = 1200;

    /// <summary>Directory containing the built dashboard (index.html). Defaults to wwwroot.</summary>
    public string? DashboardPath { get; set; }

    /// <summary>CIDR ranges of reverse proxies (e.g. the Docker network "172.18.0.0/16") whose forwarding headers are trusted.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>IP addresses of reverse proxies whose X-Forwarded-For/Proto headers are trusted.</summary>
    public string[] TrustedProxies { get; set; } = [];

    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Subdomains that can never be claimed by a tunnel and are served by the gateway itself.</summary>
    public string[] ReservedSubdomains { get; set; } =
        ["www", "api", "app", "admin", "dashboard", "gateway", "tunnel", "status", "docs", "mail", "static", "auth", "login"];

    /// <summary>Development only: lets a CLI register without a token. Rejected at startup outside Development.</summary>
    public bool AllowAnonymousTunnels { get; set; }

    /// <summary>
    /// Static credentials for development and tests, as SHA-256 hex digests of the token.
    /// Production deployments authenticate with database-backed access tokens instead.
    /// </summary>
    public List<StaticTokenOptions> StaticTokens { get; set; } = [];

    public string BuildPublicUrl(string subdomain) =>
        PublicPort is { } port
            ? $"{PublicScheme}://{subdomain}.{PublicDomain}:{port}"
            : $"{PublicScheme}://{subdomain}.{PublicDomain}";
}

public sealed class StaticTokenOptions
{
    public string UserId { get; set; } = "";

    public string TokenSha256 { get; set; } = "";
}
