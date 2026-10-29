using MyTunnel.Cli;
using MyTunnel.Cli.Configuration;
using MyTunnel.Cli.Tunnel;
using MyTunnel.Contracts;

namespace MyTunnel.Tests.Cli;

public sealed class LocalRequestBuilderTests
{
    private static readonly Uri Origin = new("http://localhost:5019");

    [Theory]
    [InlineData("/", "http://localhost:5019/")]
    [InlineData("/a/b?c=d", "http://localhost:5019/a/b?c=d")]
    [InlineData("//evil.example/x", "http://localhost:5019//evil.example/x")]
    [InlineData("/@evil.example", "http://localhost:5019/@evil.example")]
    [InlineData("/..%2F..%2Fetc", "http://localhost:5019/..%2F..%2Fetc")]
    public void Targets_always_resolve_to_the_local_origin(string target, string expected)
    {
        var uri = LocalRequestBuilder.BuildUri(Origin, target);
        Assert.Equal(expected, uri.AbsoluteUri);
        Assert.Equal("localhost", uri.Host);
        Assert.Equal(5019, uri.Port);
    }

    [Theory]
    [InlineData("http://evil.example/")]
    [InlineData("evil.example/")]
    [InlineData("")]
    [InlineData("/a b")]
    [InlineData("/a#frag")]
    public void Non_origin_form_targets_are_refused(string target)
    {
        Assert.Throws<ProtocolException>(() => LocalRequestBuilder.BuildUri(Origin, target));
    }

    [Fact]
    public void Hop_by_hop_headers_are_dropped_and_host_is_rewritten()
    {
        var request = new TunnelHttpRequest(Guid.NewGuid(), "POST", "/x",
        [
            new HttpHeader("Connection", "keep-alive, X-Secret-Hop"),
            new HttpHeader("X-Secret-Hop", "drop me"),
            new HttpHeader("Keep-Alive", "timeout=5"),
            new HttpHeader("Transfer-Encoding", "chunked"),
            new HttpHeader("Upgrade", "websocket"),
            new HttpHeader("Host", "attacker.example"),
            new HttpHeader("Content-Type", "text/plain"),
            new HttpHeader("X-Keep", "yes"),
        ], HasBody: true);

        using var message = LocalRequestBuilder.BuildRequest(Origin, request, new MemoryStream());
        Assert.Equal("localhost:5019", message.Headers.Host);
        Assert.False(message.Headers.Contains("X-Secret-Hop"));
        Assert.False(message.Headers.Contains("Keep-Alive"));
        Assert.False(message.Headers.Contains("Upgrade"));
        Assert.Null(message.Headers.TransferEncodingChunked);
        Assert.Equal("yes", message.Headers.GetValues("X-Keep").Single());
        Assert.Equal("text/plain", message.Content!.Headers.ContentType!.MediaType);
    }
}

public sealed class GatewayUriPolicyTests
{
    [Theory]
    [InlineData("https://tunnel.example.com", null)]
    [InlineData("http://localhost:5018", null)]
    [InlineData("http://127.0.0.1:5018", null)]
    [InlineData("http://[::1]:5018", null)]
    [InlineData("http://tunnel.example.com", "unencrypted")]
    [InlineData("http://192.168.1.10:5018", "unencrypted")]
    [InlineData("ftp://tunnel.example.com", "must start with https")]
    public void Credentials_are_only_sent_over_tls_or_loopback(string url, string? expectedProblem)
    {
        var problem = TunnelClientOptions.ValidateGatewayUri(new Uri(url));
        if (expectedProblem is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(expectedProblem, problem);
        }
    }

    [Theory]
    [InlineData("http://localhost:5018", "ws://localhost:5018/tunnel")]
    [InlineData("https://tunnel.example.com", "wss://tunnel.example.com/tunnel")]
    [InlineData("https://example.com/base/", "wss://example.com/base/tunnel")]
    public void WebSocket_uri_is_derived_from_gateway_url(string gateway, string expected)
    {
        Assert.Equal(expected, TunnelClientOptions.GetTunnelWebSocketUri(new Uri(gateway)).AbsoluteUri);
    }
}

public sealed class CliAppTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "mytunnel-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    private async Task<int> RunAsync(params string[] args)
    {
        var app = new CliApp(_out, _err, new CliSettings(_configDir));
        return await app.RunAsync(args, CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, recursive: true);
        }
    }

    [Fact]
    public async Task No_arguments_prints_usage()
    {
        Assert.Equal(ExitCodes.Usage, await RunAsync());
        Assert.Contains("mytunnel http <port>", _out.ToString());
    }

    [Fact]
    public async Task Help_succeeds()
    {
        Assert.Equal(ExitCodes.Success, await RunAsync("--help"));
    }

    [Fact]
    public async Task Version_prints_protocol()
    {
        Assert.Equal(ExitCodes.Success, await RunAsync("version"));
        Assert.Contains($"protocol {TunnelProtocol.CurrentVersion}", _out.ToString());
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("http")]
    [InlineData("http", "abc")]
    [InlineData("http", "0")]
    [InlineData("http", "65536")]
    [InlineData("http", "5019", "--subdomain", "A!")]
    [InlineData("http", "5019", "--timeout", "0")]
    [InlineData("http", "5019", "--bogus", "1")]
    [InlineData("http", "5019", "--gateway", "http://remote.example.com")]
    [InlineData("http", "5019", "--subdomain")]
    public async Task Invalid_arguments_return_usage_error(params string[] args)
    {
        Assert.Equal(ExitCodes.Usage, await RunAsync(args));
        Assert.NotEmpty(_err.ToString());
    }

    [Fact]
    public async Task Config_set_gateway_persists_and_is_used()
    {
        Assert.Equal(ExitCodes.Success, await RunAsync("config", "set", "gateway", "https://tunnel.example.com/"));
        var (gateway, source) = new CliSettings(_configDir).ResolveGateway(null);
        Assert.Equal("https://tunnel.example.com", gateway);
        Assert.EndsWith("config.json", source);

        Assert.Equal(ExitCodes.Usage, await RunAsync("config", "set", "gateway", "http://remote.example.com"));
    }

    [Fact]
    public async Task Login_stores_token_protected_and_logout_removes_it()
    {
        Assert.Equal(ExitCodes.Success, await RunAsync("login", "--token", "mt_secret_value"));
        var settings = new CliSettings(_configDir);
        Assert.Equal("mt_secret_value", settings.Credentials.Load());

        var stored = Directory.GetFiles(_configDir).Single();
        if (OperatingSystem.IsWindows())
        {
            // DPAPI: the file on disk must not contain the token in plain text.
            Assert.DoesNotContain("mt_secret_value", File.ReadAllText(stored));
        }
        else
        {
            // No OS keyring is assumed elsewhere: the file must be readable and writable by its owner only.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(stored));
        }

        Assert.DoesNotContain("mt_secret_value", _out.ToString());

        Assert.Equal(ExitCodes.Success, await RunAsync("logout"));
        Assert.Null(settings.Credentials.Load());
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void File_credential_store_tightens_permissions_of_an_existing_file()
    {
        var path = Path.Combine(_configDir, "credentials");
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new FileCredentialStore(path).Save("mt_secret_value");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal("mt_secret_value", new FileCredentialStore(path).Load());
    }

    [Fact]
    public void Command_line_parses_options_and_flags()
    {
        var parsed = CommandLine.Parse(["5019", "--https", "--subdomain", "demo", "--timeout=5"]);
        Assert.Equal(["5019"], parsed.Positionals);
        Assert.True(parsed.Has("https"));
        Assert.Equal("demo", parsed.Get("subdomain"));
        Assert.Equal("5", parsed.Get("timeout"));
    }
}

/// <summary>For behaviour that only exists on Linux and macOS, such as Unix file modes.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only behaviour; runs in the Linux CI job.";
        }
    }
}
