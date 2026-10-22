using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTunnel.Cli.Tunnel;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Security;
using MyTunnel.Contracts;
using MyTunnel.Gateway.Tunnels;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.ControlPlane;

/// <summary>Control-plane API against a real PostgreSQL database, including the tunnel path for DB-issued tokens.</summary>
public sealed class ControlPlaneApiTests
{
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<string> CreateTokenAsync(ApiClient client, string name = "laptop", int? expiresInDays = null)
    {
        using var response = await client.Http.PostAsJsonAsync("/api/tokens", new { name, expiresInDays });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("secret").GetString()!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    [PostgresFact]
    public async Task Register_sign_in_and_sign_out()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("Alice@Example.com");

        var me = await JsonAsync(await alice.Http.GetAsync("/api/me"));
        Assert.Equal("alice@example.com", me.GetProperty("email").GetString());

        using var logout = await alice.Http.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var after = await alice.Http.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        using var login = await alice.Http.PostAsJsonAsync("/api/auth/login", new { email = "alice@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var again = await alice.Http.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [PostgresFact]
    public async Task Logged_out_session_cookie_cannot_be_replayed()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var stolenCookies = alice.Cookies.GetCookieHeader(host.Gateway.BaseUri);

        await alice.Http.PostAsync("/api/auth/logout", null);

        using var attacker = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("Cookie", stolenCookies);
        using var response = await new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = host.Gateway.BaseUri }.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [PostgresFact]
    public async Task Registration_validates_input()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var client = host.CreateClient();
        using var _ = await host.CreateUserAsync("taken@example.com");

        foreach (var (email, password) in new[]
                 {
                     ("not-an-email", "long enough password"),
                     ("short@example.com", "short"),
                     ("TAKEN@example.com", "long enough password"),
                 })
        {
            using var response = await client.Http.PostAsJsonAsync("/api/auth/register", new { email, password });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_the_account()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var _ = await host.CreateUserAsync("bob@example.com");
        using var client = host.CreateClient();

        for (var i = 0; i < 10; i++)
        {
            using var wrong = await client.Http.PostAsJsonAsync("/api/auth/login", new { email = "bob@example.com", password = "wrong password!!" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Even the right password is refused while locked.
        using var locked = await client.Http.PostAsJsonAsync("/api/auth/login", new { email = "bob@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        // Unknown emails get the same generic failure as wrong passwords.
        using var unknown = await client.Http.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "whatever password" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [PostgresFact]
    public async Task Login_is_rate_limited_per_ip()
    {
        await using var host = await ControlPlaneHost.StartAsync(new Dictionary<string, string?> { ["Gateway:AuthRequestsPerMinutePerIp"] = "3" });
        using var client = host.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.Http.PostAsJsonAsync("/api/auth/login", new { email = "x@example.com", password = "whatever password" });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [PostgresFact]
    public async Task State_changing_requests_without_csrf_header_are_refused()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        alice.Http.DefaultRequestHeaders.Remove("X-MyTunnel-Csrf");

        using var response = await alice.Http.PostAsJsonAsync("/api/tokens", new { name = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var list = await alice.Http.GetAsync("/api/tokens");
        Assert.Empty((await JsonAsync(list)).EnumerateArray());
    }

    [PostgresFact]
    public async Task Unauthenticated_api_calls_are_refused()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var anonymous = host.CreateClient();
        foreach (var path in new[] { "/api/me", "/api/tokens", "/api/projects", "/api/tunnels", "/api/usage", "/api/audit" })
        {
            using var response = await anonymous.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Access_token_is_shown_once_stored_hashed_and_authenticates_a_tunnel()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice);
        Assert.StartsWith(Secrets.AccessTokenPrefix, secret);

        var list = await JsonAsync(await alice.Http.GetAsync("/api/tokens"));
        Assert.DoesNotContain(secret, list.GetRawText());

        await using (var scope = host.Gateway.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyTunnelDbContext>();
            var stored = await db.AccessTokens.SingleAsync();
            Assert.Equal(Secrets.Hash(secret), stored.SecretHash);
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(stored));
        }

        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, local.Origin, token: secret);
        var tunnels = await JsonAsync(await alice.Http.GetAsync("/api/tunnels"));
        Assert.Equal(tunnel.Registration.Subdomain, Assert.Single(tunnels.EnumerateArray()).GetProperty("subdomain").GetString());
    }

    [PostgresFact]
    public async Task Revoking_a_token_disconnects_its_tunnel_and_blocks_reconnection()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice);
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, local.Origin, token: secret);

        var tokenId = (await JsonAsync(await alice.Http.GetAsync("/api/tokens")))[0].GetProperty("id").GetString();
        using var revoke = await alice.Http.DeleteAsync($"/api/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        // The client is kicked, tries to reconnect, and is refused.
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() => tunnel.Run.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(ProtocolErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(0, host.Gateway.Services.GetRequiredService<TunnelRegistry>().Count);
    }

    [PostgresFact]
    public async Task Expired_token_is_rejected()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice, expiresInDays: 1);

        await using (var scope = host.Gateway.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MyTunnelDbContext>();
            await db.AccessTokens.ExecuteUpdateAsync(t => t.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() => RunningTunnel.StartAsync(host.Gateway, new Uri("http://localhost:1"), token: secret));
        Assert.Equal(ProtocolErrorCodes.Unauthorized, ex.Code);
    }

    [PostgresFact]
    public async Task Forged_token_with_valid_prefix_is_rejected()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice);
        var forged = secret[..^4] + "AAAA";

        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() => RunningTunnel.StartAsync(host.Gateway, new Uri("http://localhost:1"), token: forged));
        Assert.Equal(ProtocolErrorCodes.Unauthorized, ex.Code);
    }

    [PostgresFact]
    public async Task Users_cannot_see_or_modify_each_others_resources()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        using var mallory = await host.CreateUserAsync("mallory@example.com");

        var aliceSecret = await CreateTokenAsync(alice);
        var aliceTokenId = (await JsonAsync(await alice.Http.GetAsync("/api/tokens")))[0].GetProperty("id").GetString();
        var project = await JsonAsync(await alice.Http.PostAsJsonAsync("/api/projects", new { name = "Website" }));
        var projectId = project.GetProperty("id").GetString();
        var reservation = await JsonAsync(await alice.Http.PostAsJsonAsync($"/api/projects/{projectId}/subdomains", new { subdomain = "acme" }));
        var reservationId = reservation.GetProperty("id").GetString();
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, local.Origin, token: aliceSecret, subdomain: "acme");

        Assert.Empty((await JsonAsync(await mallory.Http.GetAsync("/api/tokens"))).EnumerateArray());
        Assert.Empty((await JsonAsync(await mallory.Http.GetAsync("/api/projects"))).EnumerateArray());
        Assert.Empty((await JsonAsync(await mallory.Http.GetAsync("/api/tunnels"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.DeleteAsync($"/api/tokens/{aliceTokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.DeleteAsync($"/api/projects/{projectId}/subdomains/{reservationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.PostAsJsonAsync($"/api/projects/{projectId}/subdomains", new { subdomain = "evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mallory.Http.DeleteAsync($"/api/tunnels/{tunnel.Registration.TunnelId}")).StatusCode);

        // Alice's things are intact.
        Assert.True((await JsonAsync(await alice.Http.GetAsync("/api/tokens")))[0].GetProperty("active").GetBoolean());
        Assert.Single((await JsonAsync(await alice.Http.GetAsync("/api/tunnels"))).EnumerateArray());
    }

    [PostgresFact]
    public async Task Reserved_subdomain_cannot_be_claimed_by_another_user_even_while_offline()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        using var mallory = await host.CreateUserAsync("mallory@example.com");
        var projectId = (await JsonAsync(await alice.Http.PostAsJsonAsync("/api/projects", new { name = "Site" }))).GetProperty("id").GetString();
        await JsonAsync(await alice.Http.PostAsJsonAsync($"/api/projects/{projectId}/subdomains", new { subdomain = "acme" }));

        // Mallory can neither reserve it nor tunnel on it, though Alice is not connected.
        var malloryProject = (await JsonAsync(await mallory.Http.PostAsJsonAsync("/api/projects", new { name = "Mine" }))).GetProperty("id").GetString();
        using var steal = await mallory.Http.PostAsJsonAsync($"/api/projects/{malloryProject}/subdomains", new { subdomain = "acme" });
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);

        var mallorySecret = await CreateTokenAsync(mallory);
        var ex = await Assert.ThrowsAsync<TunnelRejectedException>(() =>
            RunningTunnel.StartAsync(host.Gateway, new Uri("http://localhost:1"), token: mallorySecret, subdomain: "acme"));
        Assert.Equal(ProtocolErrorCodes.SubdomainUnavailable, ex.Code);

        // Alice can, and the dashboard shows it online.
        var aliceSecret = await CreateTokenAsync(alice);
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, local.Origin, token: aliceSecret, subdomain: "acme");
        var projects = await JsonAsync(await alice.Http.GetAsync("/api/projects"));
        Assert.True(projects[0].GetProperty("subdomains")[0].GetProperty("online").GetBoolean());
    }

    [PostgresFact]
    public async Task Reservation_rejects_invalid_and_service_reserved_names()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var projectId = (await JsonAsync(await alice.Http.PostAsJsonAsync("/api/projects", new { name = "Site" }))).GetProperty("id").GetString();
        foreach (var name in new[] { "api", "www", "a", "-x-", "has.dot", "UPPER!" })
        {
            using var response = await alice.Http.PostAsJsonAsync($"/api/projects/{projectId}/subdomains", new { subdomain = name });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task Device_login_issues_a_working_token_exactly_once()
    {
        await using var host = await ControlPlaneHost.StartAsync(new Dictionary<string, string?> { ["ControlPlane:DevicePollInterval"] = "00:00:00.010" });
        using var alice = await host.CreateUserAsync("alice@example.com");
        using var cli = new HttpClient { BaseAddress = host.Gateway.BaseUri };

        var start = await JsonAsync(await cli.PostAsJsonAsync("/api/cli/device/start", new { clientName = "test-machine" }));
        var deviceCode = start.GetProperty("deviceCode").GetString();
        var userCode = start.GetProperty("userCode").GetString()!;
        Assert.Matches("^[A-Z]{4}-[A-Z]{4}$", userCode);
        Assert.Contains("/activate?code=", start.GetProperty("verificationUriComplete").GetString());

        using (var pending = await cli.PostAsJsonAsync("/api/cli/device/token", new { deviceCode }))
        {
            Assert.Equal("authorization_pending", (await pending.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        var info = await JsonAsync(await alice.Http.GetAsync($"/api/device/{userCode}"));
        Assert.Equal("test-machine", info.GetProperty("clientName").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PostAsync($"/api/device/{userCode}/approve", null)).StatusCode);

        await Task.Delay(20);
        var issued = await JsonAsync(await cli.PostAsJsonAsync("/api/cli/device/token", new { deviceCode }));
        var token = issued.GetProperty("accessToken").GetString();
        Assert.Equal("alice@example.com", issued.GetProperty("email").GetString());

        using (var replay = await cli.PostAsJsonAsync("/api/cli/device/token", new { deviceCode }))
        {
            Assert.Equal("expired_token", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        }

        using var whoami = new HttpRequestMessage(HttpMethod.Get, "/api/cli/whoami");
        whoami.Headers.Authorization = new("Bearer", token);
        Assert.Equal("alice@example.com", (await JsonAsync(await cli.SendAsync(whoami))).GetProperty("email").GetString());

        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, new Uri("http://localhost:1"), token: token);
        Assert.NotEmpty(tunnel.Registration.Subdomain);
    }

    [PostgresFact]
    public async Task Denied_device_login_never_issues_a_token()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        using var cli = new HttpClient { BaseAddress = host.Gateway.BaseUri };
        var start = await JsonAsync(await cli.PostAsJsonAsync("/api/cli/device/start", new { }));

        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PostAsync($"/api/device/{start.GetProperty("userCode").GetString()}/deny", null)).StatusCode);
        using var result = await cli.PostAsJsonAsync("/api/cli/device/token", new { deviceCode = start.GetProperty("deviceCode").GetString() });
        Assert.Equal("access_denied", (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [PostgresFact]
    public async Task Changing_password_signs_out_other_sessions()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var laptop = await host.CreateUserAsync("alice@example.com");
        using var phone = host.CreateClient();
        await phone.Http.PostAsJsonAsync("/api/auth/login", new { email = "alice@example.com", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, (await phone.Http.GetAsync("/api/me")).StatusCode);

        using var change = await laptop.Http.PostAsJsonAsync("/api/account/password", new { currentPassword = "correct horse battery", newPassword = "an even better passphrase" });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await laptop.Http.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Http.GetAsync("/api/me")).StatusCode);
    }

    [PostgresFact]
    public async Task Usage_is_recorded_per_subdomain_and_audit_log_has_no_secrets()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice, name: "ci-runner");
        await using var local = await LocalApp.StartAsync();
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, local.Origin, token: secret);
        using var visitor = new HttpClient { BaseAddress = host.Gateway.BaseUri };

        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/echo") { Content = new StringContent("hello") };
            request.Headers.Host = tunnel.Host;
            (await visitor.SendAsync(request)).EnsureSuccessStatusCode();
        }

        using (var failing = new HttpRequestMessage(HttpMethod.Get, "/status/500"))
        {
            failing.Headers.Host = tunnel.Host;
            await visitor.SendAsync(failing);
        }

        var usage = Assert.Single((await JsonAsync(await alice.Http.GetAsync("/api/usage?days=7"))).EnumerateArray());
        Assert.Equal(tunnel.Registration.Subdomain, usage.GetProperty("subdomain").GetString());
        Assert.Equal(4, usage.GetProperty("requests").GetInt64());
        Assert.Equal(1, usage.GetProperty("errors").GetInt64());
        Assert.Equal(15, usage.GetProperty("bytesIn").GetInt64());
        Assert.True(usage.GetProperty("bytesOut").GetInt64() > 0);

        var audit = await JsonAsync(await alice.Http.GetAsync("/api/audit"));
        var types = audit.EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Contains("user.registered", types);
        Assert.Contains("token.created", types);
        Assert.DoesNotContain(secret, audit.GetRawText());
    }

    [PostgresFact]
    public async Task Tunnel_can_be_stopped_from_the_dashboard()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var alice = await host.CreateUserAsync("alice@example.com");
        var secret = await CreateTokenAsync(alice);
        await using var tunnel = await RunningTunnel.StartAsync(host.Gateway, new Uri("http://localhost:1"), token: secret);
        var reconnected = tunnel.Events.NextRegistration;

        using var stop = await alice.Http.DeleteAsync($"/api/tunnels/{tunnel.Registration.TunnelId}");
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        var registry = host.Gateway.Services.GetRequiredService<TunnelRegistry>();
        Assert.DoesNotContain(registry.Snapshot(), s => s.TunnelId == tunnel.Registration.TunnelId);

        // A running CLI reconnects (it was not revoked); the old session id is gone.
        var next = await reconnected.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(tunnel.Registration.TunnelId, next.TunnelId);
    }

    [PostgresFact]
    public async Task Readiness_reports_database_connectivity()
    {
        await using var host = await ControlPlaneHost.StartAsync();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.Http.GetAsync("/ready")).StatusCode);
    }
}
