using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using MyTunnel.Cli.Tunnel;
using MyTunnel.Contracts;
using MyTunnel.Gateway.Tunnels;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.EndToEnd;

public sealed class TunnelLifecycleTests
{
    private static HttpClient CreateHttp(GatewayHost gateway) => new() { BaseAddress = gateway.BaseUri, Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string host, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        return await http.SendAsync(request);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting until {because}.");
    }

    [Fact]
    public async Task Registration_without_token_is_rejected()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() =>
            RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"), token: null));
        Assert.Equal(ProtocolErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Registration_with_wrong_token_is_rejected()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() =>
            RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"), token: "not-a-valid-token"));
        Assert.Equal(ProtocolErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(0, gateway.Services.GetRequiredService<TunnelRegistry>().Count);
    }

    [Fact]
    public async Task Anonymous_tunnels_work_only_when_enabled()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:AllowAnonymousTunnels"] = "true",
        }));
        await using var tunnel = await RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"), token: null);
        Assert.NotEmpty(tunnel.Registration.Subdomain);
    }

    [Fact]
    public async Task Another_user_cannot_claim_an_active_subdomain()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var local = await LocalApp.StartAsync();
        await using var owner = await RunningTunnel.StartAsync(gateway, local.Origin, TestTokens.TokenA, subdomain: "demo-app");

        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() =>
            RunningTunnel.StartAsync(gateway, local.Origin, TestTokens.TokenB, subdomain: "demo-app"));
        Assert.Equal(ProtocolErrorCodes.SubdomainUnavailable, ex.Code);

        // The owner's tunnel is untouched.
        using var http = CreateHttp(gateway);
        using var response = await GetAsync(http, "demo-app.localhost", "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Same_user_reconnecting_replaces_the_stale_session()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var local = await LocalApp.StartAsync();
        await using var first = await RunningTunnel.StartAsync(gateway, local.Origin, subdomain: "takeover");
        await using var second = await RunningTunnel.StartAsync(gateway, local.Origin, subdomain: "takeover");

        Assert.Equal("takeover", second.Registration.Subdomain);
        var registry = gateway.Services.GetRequiredService<TunnelRegistry>();
        Assert.True(registry.TryGet("takeover", out var session));
        Assert.Equal(second.Registration.TunnelId, session.TunnelId);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-bad")]
    [InlineData("has.dot")]
    [InlineData("UPPER_case")]
    [InlineData("api")]
    [InlineData("www")]
    public async Task Invalid_or_reserved_subdomains_are_rejected(string subdomain)
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() =>
            RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"), subdomain: subdomain));
        Assert.Equal(ProtocolErrorCodes.InvalidSubdomain, ex.Code);
    }

    [Fact]
    public async Task Per_user_tunnel_limit_is_enforced()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:MaxTunnelsPerUser"] = "2",
        }));
        await using var one = await RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"));
        await using var two = await RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"));
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() => RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1")));
        Assert.Equal(ProtocolErrorCodes.TunnelLimitReached, ex.Code);

        // Other users are unaffected.
        await using var other = await RunningTunnel.StartAsync(gateway, new Uri("http://localhost:1"), TestTokens.TokenB);
    }

    [Fact]
    public async Task Stopping_the_client_unregisters_the_tunnel()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var local = await LocalApp.StartAsync();
        var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        var host = tunnel.Host;
        await tunnel.DisposeAsync();

        var registry = gateway.Services.GetRequiredService<TunnelRegistry>();
        await WaitUntilAsync(() => registry.Count == 0, "the tunnel is removed");

        using var http = CreateHttp(gateway);
        using var response = await GetAsync(http, host, "/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Local_application_offline_returns_502()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var tunnel = await RunningTunnel.StartAsync(gateway, new Uri($"http://127.0.0.1:{UnusedPort()}"));
        using var http = CreateHttp(gateway);

        using var response = await GetAsync(http, tunnel.Host, "/anything");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("local_unreachable", response.Headers.GetValues("X-MyTunnel-Error").Single());
        Assert.Contains("Could not reach the local application", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slow_local_application_times_out_at_the_gateway()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:ResponseHeadersTimeout"] = "00:00:01",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        using var response = await GetAsync(http, tunnel.Host, "/slow?ms=10000");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("tunnel_timeout", response.Headers.GetValues("X-MyTunnel-Error").Single());

        // The tunnel stays healthy for later requests.
        using var after = await GetAsync(http, tunnel.Host, "/");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Slow_local_application_times_out_at_the_client()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin, localTimeout: TimeSpan.FromSeconds(1));
        using var http = CreateHttp(gateway);

        using var response = await GetAsync(http, tunnel.Host, "/slow?ms=10000");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("local_timeout", response.Headers.GetValues("X-MyTunnel-Error").Single());
    }

    [Fact]
    public async Task Oversized_request_body_is_rejected_with_413()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:MaxRequestBodyBytes"] = "100000",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        using var declared = new HttpRequestMessage(HttpMethod.Post, "/hash") { Content = new ByteArrayContent(new byte[200_000]) };
        declared.Headers.Host = tunnel.Host;
        using var response = await http.SendAsync(declared);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // Chunked upload with no Content-Length is cut off once it crosses the limit.
        using var chunked = new HttpRequestMessage(HttpMethod.Post, "/hash")
        {
            Content = new StreamContent(new UnknownLengthStream(new byte[200_000])),
        };
        chunked.Headers.Host = tunnel.Host;
        chunked.Headers.TransferEncodingChunked = true;
        try
        {
            using var chunkedResponse = await http.SendAsync(chunked);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        }
        catch (HttpRequestException)
        {
            // Kestrel may also reset the connection while the client is still uploading; either way nothing over the limit is accepted.
        }
    }

    [Fact]
    public async Task Oversized_response_is_aborted()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:MaxResponseBodyBytes"] = "1000000",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetAsync(http, tunnel.Host, "/large?bytes=5000000"));

        using var after = await GetAsync(http, tunnel.Host, "/");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Concurrency_limit_returns_429()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:MaxConcurrentRequestsPerTunnel"] = "2",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        var slow = Enumerable.Range(0, 2).Select(_ => GetAsync(http, tunnel.Host, "/slow?ms=1500")).ToArray();
        await Task.Delay(300);
        using var rejected = await GetAsync(http, tunnel.Host, "/");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        foreach (var response in await Task.WhenAll(slow))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }
    }

    [Fact]
    public async Task Request_rate_limit_per_tunnel_returns_429()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:RequestsPerMinutePerTunnel"] = "3",
        }));
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        for (var i = 0; i < 3; i++)
        {
            using var ok = await GetAsync(http, tunnel.Host, "/");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await GetAsync(http, tunnel.Host, "/");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("tunnel_rate_limited", limited.Headers.GetValues("X-MyTunnel-Error").Single());
    }

    [Fact]
    public async Task Client_disconnect_fails_in_flight_requests_with_502()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        await using var local = await LocalApp.StartAsync();
        var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        using var http = CreateHttp(gateway);

        var inFlight = GetAsync(http, tunnel.Host, "/slow?ms=10000");
        var registry = gateway.Services.GetRequiredService<TunnelRegistry>();
        await WaitUntilAsync(() => registry.Snapshot().Single().InFlightRequests == 1, "the request reaches the tunnel");
        await tunnel.DisposeAsync();

        using var response = await inFlight.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Client_reconnects_with_the_same_subdomain_after_gateway_restart()
    {
        var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var port = gateway.BaseUri.Port;
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(gateway, local.Origin);
        var subdomain = tunnel.Registration.Subdomain;
        var reRegistered = tunnel.Events.NextRegistration;

        await gateway.DisposeAsync();
        await using var restarted = await GatewayHost.StartAsync(TestTokens.GatewaySettings(), port);

        var registration = await reRegistered.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(subdomain, registration.Subdomain);
        Assert.Contains(tunnel.Events.Log, line => line.StartsWith("reconnecting", StringComparison.Ordinal));

        using var http = CreateHttp(restarted);
        using var response = await GetAsync(http, $"{subdomain}.localhost", "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Reconnect_delay_grows_exponentially_with_jitter_and_is_capped()
    {
        var client = new TunnelClient(
            new TunnelClientOptions
            {
                GatewayUri = new Uri("http://localhost"),
                LocalOrigin = new Uri("http://localhost:1"),
                InitialReconnectDelay = TimeSpan.FromSeconds(1),
                MaxReconnectDelay = TimeSpan.FromSeconds(30),
            },
            new HttpMessageInvoker(new HttpClientHandler()),
            new RecordingEvents());

        for (var i = 0; i < 100; i++)
        {
            var first = client.GetReconnectDelay(1);
            Assert.InRange(first.TotalSeconds, 0.5, 1.0);
            var fourth = client.GetReconnectDelay(4);
            Assert.InRange(fourth.TotalSeconds, 4.0, 8.0);
            var late = client.GetReconnectDelay(50);
            Assert.InRange(late.TotalSeconds, 15.0, 30.0);
        }
    }

    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
