using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyTunnel.Gateway;

namespace MyTunnel.Tests.Infrastructure;

/// <summary>Runs the real gateway on Kestrel with an ephemeral loopback port.</summary>
public sealed class GatewayHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private GatewayHost(WebApplication app, Uri baseUri)
    {
        _app = app;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    public IServiceProvider Services => _app.Services;

    public Uri WebSocketUri => new($"ws://{BaseUri.Authority}/tunnel");

    public static async Task<GatewayHost> StartAsync(IDictionary<string, string?>? settings = null, int port = 0)
    {
        var app = GatewayApplication.Create(
            ["--environment", "Testing"],
            builder =>
            {
                builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
                builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());
            });

        await GatewayApplication.MigrateIfConfiguredAsync(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>()
            .Addresses.First();
        return new GatewayHost(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
