namespace MyTunnel.ControlPlane;

public sealed class ControlPlaneOptions
{
    public const string SectionName = "ControlPlane";

    /// <summary>
    /// Base URL of the dashboard, used in CLI login links. Defaults to the gateway's own URL, which serves the
    /// built dashboard; set it to the Vite dev server (http://localhost:5173) during frontend development.
    /// </summary>
    public string? DashboardUrl { get; set; }

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);

    public TimeSpan DeviceCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DevicePollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Lifetime of tokens issued through browser login. Tokens created in the dashboard choose their own.</summary>
    public TimeSpan CliTokenLifetime { get; set; } = TimeSpan.FromDays(90);

    public int MaxActiveTokensPerUser { get; set; } = 50;

    public int MaxProjectsPerUser { get; set; } = 20;

    public int MaxReservedSubdomainsPerUser { get; set; } = 10;

    public int MinPasswordLength { get; set; } = 10;

    /// <summary>Subdomains that can never be reserved (kept in sync with the gateway's list).</summary>
    public string[] ReservedSubdomains { get; set; } = [];

    public int UsageRetentionDays { get; set; } = 400;

    public int AuditRetentionDays { get; set; } = 365;
}
