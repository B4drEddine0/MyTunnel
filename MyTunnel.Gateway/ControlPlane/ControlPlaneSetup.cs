using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Services;
using MyTunnel.Gateway.Tunnels;

namespace MyTunnel.Gateway.ControlPlane;

public static class ControlPlaneSetup
{
    public const string CookieScheme = "mytunnel";
    public const string SessionClaim = "mytunnel:session";
    public const string ConnectionStringName = "MyTunnel";

    /// <summary>
    /// Enables accounts, tokens and the API when <c>ConnectionStrings:MyTunnel</c> is configured.
    /// Without it the gateway runs in standalone development mode (static tokens, no dashboard API).
    /// </summary>
    public static bool AddGatewayControlPlane(this WebApplicationBuilder builder, GatewayOptions gateway)
    {
        var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var services = builder.Services;
        services.AddControlPlane(connectionString);
        services.AddOptions<ControlPlaneOptions>()
            .Bind(builder.Configuration.GetSection(ControlPlaneOptions.SectionName))
            .PostConfigure<IOptions<GatewayOptions>>((controlPlane, gatewayOptions) =>
                controlPlane.ReservedSubdomains = gatewayOptions.Value.ReservedSubdomains);

        // Later registrations win: these replace the standalone defaults.
        services.AddSingleton<StaticTokenAuthenticator>();
        services.AddSingleton<ITunnelAuthenticator, DbTunnelAuthenticator>();
        services.AddSingleton<ISubdomainClaimPolicy, DbSubdomainClaimPolicy>();
        services.AddSingleton<UsageAggregator>();
        services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<UsageAggregator>());
        services.AddHostedService(sp => sp.GetRequiredService<UsageAggregator>());
        services.AddHostedService<RetentionWorker>();

        services.AddDataProtection()
            .SetApplicationName("MyTunnel")
            .PersistKeysToDbContext<MyTunnelDbContext>();

        var https = gateway.PublicScheme == "https";
        services.AddAuthentication(CookieScheme).AddCookie(CookieScheme, cookie =>
        {
            // __Host- cookies are host-only, Secure and Path=/, so tunnel subdomains can never read or overwrite them.
            cookie.Cookie.Name = https ? "__Host-mytunnel" : "mytunnel";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = https ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.Cookie.Path = "/";
            cookie.SlidingExpiration = false;
            cookie.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
                // The cookie only names a server-side session; every request checks it is still live.
                OnValidatePrincipal = async context =>
                {
                    var sessionId = context.Principal?.FindFirstValue(SessionClaim);
                    var accounts = context.HttpContext.RequestServices.GetRequiredService<AccountService>();
                    if (!Guid.TryParse(sessionId, out var id) || await accounts.ValidateSessionAsync(id, context.HttpContext.RequestAborted) is null)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieScheme);
                    }
                },
            };
        });
        services.AddAuthorization();
        return true;
    }

    public static async Task MigrateControlPlaneDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MyTunnelDbContext>().Database.MigrateAsync();
    }

    public static Guid GetUserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static Guid GetSessionId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(SessionClaim)!);
}
