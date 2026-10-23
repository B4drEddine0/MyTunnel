using System.Net.Http.Json;
using MyTunnel.Cli;
using MyTunnel.Cli.Configuration;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.ControlPlane;

/// <summary>Drives the real CLI commands against a gateway with the control plane enabled.</summary>
public sealed class CliLoginTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "mytunnel-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [PostgresFact]
    public async Task Browser_login_stores_a_token_that_status_and_http_accept()
    {
        await using var host = await ControlPlaneHost.StartAsync(new Dictionary<string, string?> { ["ControlPlane:DevicePollInterval"] = "00:00:01" });
        using var alice = await host.CreateUserAsync("alice@example.com");
        var gateway = host.Gateway.BaseUri.GetLeftPart(UriPartial.Authority);

        // The "browser": when the CLI opens the activation link, approve the code as the signed-in user.
        Uri? opened = null;
        bool OpenBrowser(Uri uri)
        {
            opened = uri;
            var code = Uri.UnescapeDataString(uri.Query.Split("code=")[1]);
            _ = Task.Run(async () => (await alice.Http.PostAsync($"/api/device/{code}/approve", null)).EnsureSuccessStatusCode());
            return true;
        }

        var output = new StringWriter();
        var error = new StringWriter();
        var cli = new CliApp(output, error, new CliSettings(_configDir), OpenBrowser);

        var exit = await cli.RunAsync(["login", "--gateway", gateway], CancellationToken.None);
        Assert.True(exit == ExitCodes.Success, output + error.ToString());
        Assert.NotNull(opened);
        Assert.Contains("Logged in as alice@example.com", output.ToString());
        var stored = new CliSettings(_configDir).Credentials.Load();
        Assert.StartsWith("mt_", stored);
        Assert.DoesNotContain(stored!, output.ToString());

        var statusOut = new StringWriter();
        Assert.Equal(ExitCodes.Success, await new CliApp(statusOut, error, new CliSettings(_configDir)).RunAsync(["status", "--gateway", gateway], CancellationToken.None));
        Assert.Contains("alice@example.com", statusOut.ToString());

        // The stored token is reported as rejected once it is revoked from the dashboard.
        var tokens = await alice.Http.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/tokens");
        (await alice.Http.DeleteAsync($"/api/tokens/{tokens![0]["id"]}")).EnsureSuccessStatusCode();
        var revokedOut = new StringWriter();
        Assert.Equal(ExitCodes.Error, await new CliApp(revokedOut, error, new CliSettings(_configDir)).RunAsync(["status", "--gateway", gateway], CancellationToken.None));
        Assert.Contains("rejected", revokedOut.ToString());
    }

    [Fact]
    public async Task Login_against_a_gateway_without_accounts_explains_the_alternative()
    {
        await using var gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings());
        var error = new StringWriter();
        var cli = new CliApp(new StringWriter(), error, new CliSettings(_configDir), _ => true);

        Assert.Equal(ExitCodes.Error, await cli.RunAsync(["login", "--gateway", gateway.BaseUri.GetLeftPart(UriPartial.Authority)], CancellationToken.None));
        Assert.Contains("--token", error.ToString());
    }
}
