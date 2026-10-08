using System.Security.Cryptography;
using MyTunnel.Contracts;

namespace MyTunnel.Gateway.Tunnels;

public static class Subdomains
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static bool IsValid(string? subdomain) => SubdomainName.IsValid(subdomain);

    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, 12);

    /// <summary>
    /// Extracts the tunnel label from a Host value when it is exactly one label below <paramref name="publicDomain"/>.
    /// </summary>
    public static bool TryExtract(string host, string publicDomain, out string subdomain)
    {
        subdomain = "";
        var suffixLength = publicDomain.Length + 1;
        if (host.Length <= suffixLength
            || !host.EndsWith(publicDomain, StringComparison.OrdinalIgnoreCase)
            || host[^suffixLength] != '.')
        {
            return false;
        }

        var label = host[..^suffixLength];
        if (label.Contains('.'))
        {
            return false;
        }

        subdomain = label.ToLowerInvariant();
        return true;
    }
}
