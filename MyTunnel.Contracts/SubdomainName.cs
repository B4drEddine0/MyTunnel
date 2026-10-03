using System.Text.RegularExpressions;

namespace MyTunnel.Contracts;

/// <summary>Syntax rule for tunnel subdomains, shared by the CLI, gateway and control plane.</summary>
public static partial class SubdomainName
{
    /// <summary>A single DNS label: 3-63 lowercase letters, digits and inner hyphens.</summary>
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    public static bool IsValid(string? subdomain) => subdomain is not null && LabelPattern().IsMatch(subdomain);
}
