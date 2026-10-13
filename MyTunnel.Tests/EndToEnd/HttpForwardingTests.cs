using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyTunnel.Tests.Infrastructure;

namespace MyTunnel.Tests.EndToEnd;

/// <summary>
/// Real HTTP over real sockets: visitor -> gateway (Kestrel) -> WebSocket -> TunnelClient -> local app (Kestrel) and back.
/// </summary>
public sealed class HttpForwardingTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private LocalApp _local = null!;
    private RunningTunnel _tunnel = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _gateway = await GatewayHost.StartAsync(TestTokens.GatewaySettings(new Dictionary<string, string?>
        {
            ["Gateway:MaxRequestBodyBytes"] = (8 * 1024 * 1024).ToString(),
            ["Gateway:MaxResponseBodyBytes"] = (32 * 1024 * 1024).ToString(),
        }));
        _local = await LocalApp.StartAsync();
        _tunnel = await RunningTunnel.StartAsync(_gateway, _local.Origin);
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = _gateway.BaseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _tunnel.DisposeAsync();
        await _local.DisposeAsync();
        await _gateway.DisposeAsync();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null) =>
        _tunnel.SendAsync(_http, new HttpRequestMessage(method, path) { Content = content });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public void Registration_returns_public_url_for_the_subdomain()
    {
        Assert.Matches("^[a-z0-9]{12}$", _tunnel.Registration.Subdomain);
        Assert.Equal($"http://{_tunnel.Registration.Subdomain}.localhost", _tunnel.Registration.PublicUrl);
    }

    [Fact]
    public async Task Get_returns_the_local_application_response()
    {
        using var response = await SendAsync(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from my local application!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Path_query_and_headers_are_forwarded_and_host_is_rewritten()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/echo/a/b%20c?x=1&y=%2F");
        request.Headers.Add("X-Custom", "custom-value");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");
        request.Headers.Add("Forwarded", "for=6.6.6.6");
        using var response = await _tunnel.SendAsync(_http, request);
        var echo = await JsonAsync(response);

        Assert.Equal("/echo/a/b c", echo.GetProperty("path").GetString());
        Assert.Equal("?x=1&y=%2F", echo.GetProperty("query").GetString());
        Assert.Equal(_local.Origin.Authority, echo.GetProperty("host").GetString());

        var headers = echo.GetProperty("headers");
        Assert.Equal("custom-value", headers.GetProperty("X-Custom").GetString());
        Assert.Equal(_tunnel.Host, headers.GetProperty("X-Forwarded-Host").GetString());
        Assert.Equal("http", headers.GetProperty("X-Forwarded-Proto").GetString());
        // The spoofed value must be replaced by the address the gateway actually saw.
        Assert.Equal("127.0.0.1", headers.GetProperty("X-Forwarded-For").GetString());
        Assert.False(headers.TryGetProperty("Forwarded", out _));
    }

    [Fact]
    public async Task Json_post_body_round_trips()
    {
        using var response = await SendAsync(HttpMethod.Post, "/echo", new StringContent("""{"name":"café","n":42}""", Encoding.UTF8, "application/json"));
        var echo = await JsonAsync(response);
        Assert.Equal("POST", echo.GetProperty("method").GetString());
        Assert.Equal("""{"name":"café","n":42}""", echo.GetProperty("body").GetString());
        Assert.StartsWith("application/json", echo.GetProperty("headers").GetProperty("Content-Type").GetString());
    }

    [Fact]
    public async Task Form_post_body_round_trips()
    {
        using var response = await SendAsync(HttpMethod.Post, "/echo", new FormUrlEncodedContent([new("a", "1"), new("b", "x y")]));
        var echo = await JsonAsync(response);
        Assert.Equal("a=1&b=x+y", echo.GetProperty("body").GetString());
        Assert.Equal("application/x-www-form-urlencoded", echo.GetProperty("headers").GetProperty("Content-Type").GetString());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task Other_methods_are_forwarded(string method)
    {
        using var response = await SendAsync(new HttpMethod(method), "/echo", new StringContent("payload"));
        var echo = await JsonAsync(response);
        Assert.Equal(method, echo.GetProperty("method").GetString());
        Assert.Equal("payload", echo.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Head_returns_headers_without_body()
    {
        using var response = await SendAsync(HttpMethod.Head, "/echo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Status_codes_pass_through(int code)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/status/{code}");
        Assert.Equal(code, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("X-MyTunnel-Error"));
    }

    [Fact]
    public async Task Redirects_are_returned_not_followed()
    {
        using var response = await SendAsync(HttpMethod.Get, "/redirect");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/target", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Multiple_set_cookie_headers_are_preserved()
    {
        using var response = await SendAsync(HttpMethod.Get, "/cookies");
        Assert.Equal(["a=1; Path=/", "b=2; Path=/"], response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task Compressed_responses_pass_through_untouched()
    {
        using var response = await SendAsync(HttpMethod.Get, "/gzip");
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal("compressed payload", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Large_upload_streams_intact()
    {
        var payload = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 17);
        using var response = await SendAsync(HttpMethod.Post, "/hash", new ByteArrayContent(payload));
        var result = await JsonAsync(response);
        Assert.Equal(payload.Length, result.GetProperty("length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), result.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Chunked_upload_without_content_length_streams_intact()
    {
        var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        var content = new StreamContent(new NonSeekableStream(payload));
        using var response = await SendAsync(HttpMethod.Post, "/hash", content);
        var result = await JsonAsync(response);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), result.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Large_download_streams_intact()
    {
        const int size = 20 * 1024 * 1024 + 3;
        using var response = await SendAsync(HttpMethod.Get, $"/large?bytes={size}");
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(size, body.Length);
        Assert.True(body.AsSpan().SequenceEqual(LocalApp.Pattern(size)));
    }

    [Fact]
    public async Task Concurrent_requests_are_correlated_correctly()
    {
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            // Mix fast and slow requests so responses complete out of order.
            var path = i % 5 == 0 ? $"/echo/slow?id={i}" : $"/echo/fast?id={i}";
            using var response = await SendAsync(HttpMethod.Post, path, new StringContent($"body-{i}"));
            var echo = await JsonAsync(response);
            Assert.Equal($"?id={i}", echo.GetProperty("query").GetString());
            Assert.Equal($"body-{i}", echo.GetProperty("body").GetString());
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Gateway_paths_on_a_tunnel_host_reach_the_local_app()
    {
        // /tunnel and /health on a tunnel host must be the developer's routes, never the gateway's.
        using var response = await SendAsync(HttpMethod.Get, "/echo/tunnel");
        Assert.Equal("/echo/tunnel", (await JsonAsync(response)).GetProperty("path").GetString());

        using var health = await SendAsync(HttpMethod.Get, "/health");
        Assert.Equal(HttpStatusCode.NotFound, health.StatusCode);
        Assert.False(health.Headers.Contains("X-MyTunnel-Error"));
    }

    [Fact]
    public async Task Gateway_root_still_serves_gateway_endpoints()
    {
        using var response = await _http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_subdomain_returns_404()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        using var response = await _tunnel.SendAsync(_http, request, host: "doesnotexist.localhost");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tunnel_not_found", response.Headers.GetValues("X-MyTunnel-Error").Single());
    }

    [Fact]
    public async Task Requests_are_logged_by_the_client()
    {
        using var response = await SendAsync(HttpMethod.Get, "/status/204");
        // The client logs after it finishes sending, which can be just after the visitor has the response.
        for (var i = 0; i < 50 && !_tunnel.Events.Log.Contains("GET /status/204 204"); i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains("GET /status/204 204", _tunnel.Events.Log);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
