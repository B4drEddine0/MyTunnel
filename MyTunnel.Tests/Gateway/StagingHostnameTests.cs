using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyTunnel.Contracts;
using MyTunnel.Gateway;
using MyTunnel.Gateway.Tunnels;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Gateway;

/// <summary>
/// The staging setup (deploy/Caddyfile.staging, docs/staging.md) serves an IP-embedding PublicDomain such as
/// 203-0-113-10.sslip.io with certificates for a few fixed tunnel subdomains only.
/// </summary>
public sealed class StagingHostnameTests
{
    private const string StagingDomain = "203-0-113-10.sslip.io";

    [Theory]
    [InlineData("demo.203-0-113-10.sslip.io", "demo")]
    [InlineData("DEMO.203-0-113-10.SSLIP.IO", "demo")]
    [InlineData("test.203-0-113-10.sslip.io", "test")]
    public void Fixed_staging_hosts_resolve_to_their_tunnel(string host, string expected)
    {
        Assert.True(Subdomains.TryExtract(host, StagingDomain, out var subdomain));
        Assert.Equal(expected, subdomain);
    }

    [Theory]
    [InlineData("203-0-113-10.sslip.io")]         // the gateway itself: dashboard, API, /tunnel
    [InlineData("a.demo.203-0-113-10.sslip.io")]  // nested labels are never tunnels
    [InlineData("demo.203.0.113.10.sslip.io")]    // dot notation is a different name than the dashed PublicDomain
    [InlineData("demo.198-51-100-7.sslip.io")]    // another IP's name
    public void Other_hosts_are_not_tunnel_hosts(string host)
    {
        Assert.False(Subdomains.TryExtract(host, StagingDomain, out _));
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("test")]
    public async Task Staging_domain_routes_fixed_subdomains_and_serves_the_gateway_on_the_bare_name(string name)
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:PublicDomain"] = StagingDomain,
            ["Gateway:PublicScheme"] = "https",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin, subdomain: name);
        Assert.Equal($"https://{name}.{StagingDomain}", tunnel.Registration.PublicUrl);

        using var http = new HttpClient { BaseAddress = gateway.BaseUri, Timeout = TimeSpan.FromSeconds(30) };

        using (var response = await tunnel.SendAsync(http, new HttpRequestMessage(HttpMethod.Get, "/echo/x"), $"{name}.{StagingDomain}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal($"{name}.{StagingDomain}", json.RootElement.GetProperty("headers").GetProperty("X-Forwarded-Host").GetString());
        }

        using (var response = await tunnel.SendAsync(http, new HttpRequestMessage(HttpMethod.Get, "/health"), StagingDomain))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("MyTunnel.Gateway", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_fixed_hostname_without_a_tunnel_gets_a_gateway_404()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:PublicDomain"] = StagingDomain,
        }));
        using var http = new HttpClient { BaseAddress = gateway.BaseUri };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = $"test.{StagingDomain}";
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tunnel_not_found", response.Headers.GetValues("X-MyTunnel-Error").Single());
    }

    [Fact]
    public void Staging_caddyfile_lists_only_fixed_claimable_hostnames_without_dns_credentials_or_on_demand_tls()
    {
        var caddyfile = File.ReadAllText(RepoFile("deploy/Caddyfile.staging"));
        var directives = string.Join('\n', caddyfile.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotContain("on_demand", directives);
        Assert.DoesNotContain("dns ", directives);
        Assert.DoesNotContain("CLOUDFLARE", directives);
        Assert.DoesNotContain("*.", directives);

        var siteLine = directives.Split('\n').Single(line => line.StartsWith("{$PUBLIC_DOMAIN}"));
        var labels = Regex.Matches(siteLine, @"([a-z0-9-]+)\.\{\$PUBLIC_DOMAIN\}").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["demo", "test"], labels);

        var reserved = new GatewayOptions().ReservedSubdomains;
        Assert.All(labels, label =>
        {
            Assert.True(SubdomainName.IsValid(label), $"'{label}' is not a valid tunnel subdomain");
            Assert.DoesNotContain(label, reserved);
        });
    }

    [Fact]
    public void Staging_override_only_swaps_the_caddy_config_and_publishes_nothing()
    {
        var lines = File.ReadAllLines(RepoFile("deploy/docker-compose.staging.yml"))
            .Where(line => !line.TrimStart().StartsWith('#') && line.Trim().Length > 0)
            .ToArray();

        var services = lines.Where(line => Regex.IsMatch(line, @"^  [a-z]+:\s*$")).Select(line => line.Trim().TrimEnd(':'));
        Assert.Equal(["caddy"], services);
        Assert.DoesNotContain(lines, line => line.Contains("ports"));
        Assert.Contains(lines, line => line.Trim() == "- ./Caddyfile.staging:/etc/caddy/Caddyfile:ro");
    }

    private static string RepoFile(string relativePath, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", relativePath));
}
