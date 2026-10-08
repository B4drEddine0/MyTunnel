using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane.Data;
using MyTunnel.Contracts;
using MyTunnel.Gateway.ControlPlane;
using MyTunnel.Gateway.Tunnels;

namespace MyTunnel.Gateway;

public static class GatewayApplication
{
    public const string AuthPolicy = "auth";
    public const string SensitivePolicy = "sensitive";
    public const string DevicePollPolicy = "device-poll";
    private const string TunnelConnectPolicy = "tunnel-connect";

    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.Services.AddOptions<GatewayOptions>()
            .Bind(builder.Configuration.GetSection(GatewayOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.PublicDomain), "Gateway:PublicDomain is required.")
            .Validate(o => o.PublicScheme is "http" or "https", "Gateway:PublicScheme must be http or https.")
            .Validate(o => o.MaxConcurrentRequestsPerTunnel > 0 && o.MaxTunnelsPerUser > 0, "Gateway limits must be positive.")
            .ValidateOnStart();

        builder.Services.AddSingleton<TunnelRegistry>();
        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddSingleton<TunnelConnectionHandler>();
        builder.Services.AddSingleton<ITunnelAuthenticator, StaticTokenAuthenticator>();
        builder.Services.AddSingleton<ISubdomainClaimPolicy, OpenSubdomainClaimPolicy>();
        builder.Services.AddSingleton<IUsageSink, NullUsageSink>();

        var gatewaySettings = builder.Configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();
        var controlPlaneEnabled = builder.AddGatewayControlPlane(gatewaySettings);

        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(TunnelConnectPolicy, context => PerIp(context, gatewaySettings.TunnelConnectionsPerMinutePerIp));
            limiter.AddPolicy(AuthPolicy, context => PerIp(context, gatewaySettings.AuthRequestsPerMinutePerIp));
            limiter.AddPolicy(SensitivePolicy, context => PerIp(context, 20));
            limiter.AddPolicy(DevicePollPolicy, context => PerIp(context, 60));
        });

        builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            // Only honour X-Forwarded-* from proxies the operator lists explicitly.
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var proxy in gatewaySettings.TrustedProxies)
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in gatewaySettings.TrustedNetworks)
            {
                forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(15));

        var app = builder.Build();
        var settings = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        if (settings.AllowAnonymousTunnels && !(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
        {
            throw new InvalidOperationException("Gateway:AllowAnonymousTunnels may only be enabled in the Development environment.");
        }

        if (app.Environment.IsProduction() && settings.PublicScheme != "https")
        {
            // Session cookies and tokens must never be issued for plain-HTTP public URLs.
            throw new InvalidOperationException("In Production, Gateway:PublicScheme must be https (terminate TLS at the reverse proxy).");
        }

        if (settings.TrustedProxies.Length > 0 || settings.TrustedNetworks.Length > 0)
        {
            app.UseForwardedHeaders();
        }

        app.UseWebSockets();
        app.UseRateLimiter();
        // Before authentication: tunnel hosts are served entirely by the developer's application.
        app.UseMiddleware<TunnelRoutingMiddleware>();

        // Gateway-host responses only (tunnel responses belong to the developer's app and were already sent).
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
            if (settings.PublicScheme == "https")
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }

            return next(context);
        });

        var dashboard = DashboardFiles(app, settings);
        if (dashboard is not null)
        {
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = dashboard });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = dashboard });
        }

        if (controlPlaneEnabled)
        {
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControlPlaneApi();
        }

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "Healthy",
            service = "MyTunnel.Gateway"
        }));

        app.MapGet("/ready", async (IHostApplicationLifetime lifetime, IServiceProvider services, CancellationToken ct) =>
        {
            if (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                return Results.Json(new { status = "ShuttingDown" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (controlPlaneEnabled)
            {
                await using var scope = services.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<MyTunnelDbContext>().Database.CanConnectAsync(ct))
                {
                    return Results.Json(new { status = "DatabaseUnavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            return Results.Ok(new { status = "Ready" });
        });

        app.Map(TunnelProtocol.TunnelPath, (HttpContext context, TunnelConnectionHandler handler) => handler.HandleAsync(context))
            .RequireRateLimiting(TunnelConnectPolicy);

        if (dashboard is null)
        {
            app.MapGet("/", () => "MyTunnel Gateway is running!");
        }
        else
        {
            // Client-side routes (/tokens, /activate, ...) load the SPA; unknown API paths stay 404.
            app.MapFallback(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || !HttpMethods.IsGet(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(dashboard.GetFileInfo("index.html"));
            });
        }

        return app;
    }

    /// <summary>The built dashboard: <c>Gateway:DashboardPath</c>, else wwwroot, if it contains index.html.</summary>
    private static Microsoft.Extensions.FileProviders.IFileProvider? DashboardFiles(WebApplication app, GatewayOptions settings)
    {
        var path = settings.DashboardPath ?? app.Environment.WebRootPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(Path.Combine(path, "index.html")))
        {
            return null;
        }

        return new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(path));
    }

    /// <summary>
    /// Applies pending EF Core migrations when <c>Database:MigrateOnStartup</c> is true (the Development default).
    /// Production deployments run migrations as an explicit release step instead; see docs/deployment.md.
    /// </summary>
    public static async Task MigrateIfConfiguredAsync(WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup")
            && !string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString(ControlPlaneSetup.ConnectionStringName)))
        {
            await app.MigrateControlPlaneDatabaseAsync();
        }
    }

    private static RateLimitPartition<string> PerIp(HttpContext context, int permitsPerMinute) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permitsPerMinute, Window = TimeSpan.FromMinutes(1) });
}
