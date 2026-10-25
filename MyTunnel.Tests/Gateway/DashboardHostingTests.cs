using System.Net;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.Gateway;

public sealed class DashboardHostingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mytunnel-dashboard").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Gateway_serves_dashboard_with_security_headers_and_spa_fallback()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), "<!doctype html><title>MyTunnel</title>");
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?> { ["Gateway:DashboardPath"] = _root }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = new HttpClient { BaseAddress = gateway.BaseUri };

        using var root = await http.GetAsync("/");
        Assert.Contains("<title>MyTunnel</title>", await root.Content.ReadAsStringAsync());
        Assert.Contains("frame-ancestors 'none'", root.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", root.Headers.GetValues("X-Content-Type-Options").Single());

        using var clientRoute = await http.GetAsync("/tokens");
        Assert.Contains("<title>MyTunnel</title>", await clientRoute.Content.ReadAsStringAsync());

        using var unknownApi = await http.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);

        // Tunnel responses are the developer's: the gateway must not inject its own CSP or serve the dashboard there.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = tunnel.Host;
        using var tunnelled = await http.SendAsync(request);
        Assert.Equal("Hello from my local application!", await tunnelled.Content.ReadAsStringAsync());
        Assert.False(tunnelled.Headers.Contains("Content-Security-Policy"));
    }
}
