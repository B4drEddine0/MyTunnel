using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MyTunnel.Cli.Tunnel;
using MyTunnel.Contracts;

namespace MyTunnel.Tests.Infrastructure;

/// <summary>Gateway + local app + tunnel client wiring shared by the end-to-end tests.</summary>
public static class TestTokens
{
    public const string UserA = "user-a";
    public const string UserB = "user-b";
    public const string TokenA = "test-token-for-user-a";
    public const string TokenB = "test-token-for-user-b";

    public static Dictionary<string, string?> GatewaySettings(IDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:PublicDomain"] = "localhost",
            ["Gateway:PublicScheme"] = "http",
            ["Gateway:StaticTokens:0:UserId"] = UserA,
            ["Gateway:StaticTokens:0:TokenSha256"] = Sha256(TokenA),
            ["Gateway:StaticTokens:1:UserId"] = UserB,
            ["Gateway:StaticTokens:1:TokenSha256"] = Sha256(TokenB),
        };

        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return settings;
    }

    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class RecordingEvents : ITunnelClientEvents
{
    private TaskCompletionSource<RegisteredMessage> _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Log { get; } = new();

    public int RegistrationCount;

    public Task<RegisteredMessage> NextRegistration => _registered.Task;

    public void Connecting(Uri gatewayUri) => Log.Enqueue("connecting");

    public void Registered(RegisteredMessage registration)
    {
        Interlocked.Increment(ref RegistrationCount);
        Log.Enqueue($"registered {registration.Subdomain}");
        var current = _registered;
        _registered = new TaskCompletionSource<RegisteredMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        current.TrySetResult(registration);
    }

    public void RequestCompleted(string method, string path, int statusCode, TimeSpan elapsed) => Log.Enqueue($"{method} {path} {statusCode}");

    public void Disconnected(string reason) => Log.Enqueue($"disconnected: {reason}");

    public void Reconnecting(TimeSpan delay, int attempt) => Log.Enqueue($"reconnecting {attempt}");

    public void Warning(string message) => Log.Enqueue($"warning: {message}");
}

/// <summary>A running <see cref="TunnelClient"/> and a way to call its public URL.</summary>
public sealed class RunningTunnel : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpMessageInvoker _localHttp = LocalRequestBuilder.CreateLocalInvoker();

    private RunningTunnel(GatewayHost gateway, TunnelClientOptions options)
    {
        Gateway = gateway;
        Client = new TunnelClient(options, _localHttp, Events);
        FirstRegistration = Events.NextRegistration;
        Run = Task.Run(() => Client.RunAsync(_cts.Token));
    }

    private Task<RegisteredMessage> FirstRegistration { get; }

    public GatewayHost Gateway { get; }

    public TunnelClient Client { get; }

    public RecordingEvents Events { get; } = new();

    public Task Run { get; }

    public RegisteredMessage Registration { get; private set; } = null!;

    public string Host => $"{Registration.Subdomain}.localhost";

    public static async Task<RunningTunnel> StartAsync(
        GatewayHost gateway, Uri localOrigin, string? token = TestTokens.TokenA, string? subdomain = null, TimeSpan? localTimeout = null)
    {
        var tunnel = new RunningTunnel(gateway, new TunnelClientOptions
        {
            GatewayUri = gateway.BaseUri,
            LocalOrigin = localOrigin,
            AuthToken = token,
            Subdomain = subdomain,
            LocalResponseTimeout = localTimeout ?? TimeSpan.FromSeconds(30),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(100),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(500),
        });

        var first = await Task.WhenAny(tunnel.FirstRegistration, tunnel.Run).WaitAsync(TimeSpan.FromSeconds(15));
        if (first == tunnel.Run)
        {
            await tunnel.Run; // surfaces TunnelRejectedException
            throw new InvalidOperationException("Tunnel client stopped before registering.");
        }

        tunnel.Registration = await tunnel.FirstRegistration;
        return tunnel;
    }

    /// <summary>Sends a request to the tunnel's public host via the gateway.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string? host = null)
    {
        request.Headers.Host = host ?? Host;
        return http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is TunnelRejectedException or OperationCanceledException)
        {
        }

        _localHttp.Dispose();
    }
}
