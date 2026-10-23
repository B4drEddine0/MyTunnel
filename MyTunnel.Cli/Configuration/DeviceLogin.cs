using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyTunnel.Cli.Configuration;

public sealed record DeviceLoginResult(bool Succeeded, string Message, string? AccessToken = null);

/// <summary>Client side of the browser-approved login (RFC 8628 shape) against <c>/api/cli/device/*</c>.</summary>
public sealed class DeviceLogin(HttpClient http, TextWriter output, Func<Uri, bool> openBrowser)
{
    public static bool OpenInDefaultBrowser(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public async Task<DeviceLoginResult> RunAsync(bool launchBrowser, CancellationToken cancellationToken)
    {
        using var startResponse = await http.PostAsJsonAsync("api/cli/device/start", new { clientName = Environment.MachineName }, cancellationToken);
        if (startResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return new DeviceLoginResult(false, "This gateway has no account system. Use 'mytunnel login --token <token>' instead.");
        }

        if (!startResponse.IsSuccessStatusCode)
        {
            return new DeviceLoginResult(false, $"The gateway refused to start a login (HTTP {(int)startResponse.StatusCode}).");
        }

        var start = await startResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var deviceCode = start.GetProperty("deviceCode").GetString()!;
        var userCode = start.GetProperty("userCode").GetString()!;
        var interval = TimeSpan.FromSeconds(Math.Max(1, start.GetProperty("interval").GetInt32()));
        var deadline = DateTime.UtcNow.AddSeconds(start.GetProperty("expiresIn").GetInt32());
        var verification = new Uri(start.GetProperty("verificationUriComplete").GetString()!);

        output.WriteLine("To sign in, confirm this code in your browser:");
        output.WriteLine();
        output.WriteLine($"    {userCode}");
        output.WriteLine();
        if (!(launchBrowser && openBrowser(verification)))
        {
            output.WriteLine($"Open {verification.AbsoluteUri}");
        }

        output.WriteLine("Waiting for approval (Ctrl+C to cancel)...");
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken);
            using var poll = await http.PostAsJsonAsync("api/cli/device/token", new { deviceCode }, cancellationToken);
            var body = await poll.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (poll.IsSuccessStatusCode)
            {
                return new DeviceLoginResult(true, $"Logged in as {body.GetProperty("email").GetString()}.", body.GetProperty("accessToken").GetString());
            }

            switch (body.TryGetProperty("error", out var error) ? error.GetString() : null)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    return new DeviceLoginResult(false, "The login request was denied.");
                default:
                    return new DeviceLoginResult(false, "The login request expired. Run 'mytunnel login' again.");
            }
        }

        return new DeviceLoginResult(false, "The login request expired. Run 'mytunnel login' again.");
    }
}
