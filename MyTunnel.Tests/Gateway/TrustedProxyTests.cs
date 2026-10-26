using System.Text.Json;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Gateway;

public sealed class TrustedProxyTests
{
    [Fact]
    public async Task Client_ip_from_a_trusted_proxy_network_reaches_the_local_app()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:TrustedNetworks:0"] = "127.0.0.0/8",
            ["Gateway:PublicScheme"] = "https",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = new HttpClient { BaseAddress = gateway.BaseUri };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/echo");
        request.Headers.Host = tunnel.Host;
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await http.SendAsync(request);
        var headers = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("headers");

        Assert.Equal("203.0.113.9", headers.GetProperty("X-Forwarded-For").GetString());
        Assert.Equal("https", headers.GetProperty("X-Forwarded-Proto").GetString());
    }
}
