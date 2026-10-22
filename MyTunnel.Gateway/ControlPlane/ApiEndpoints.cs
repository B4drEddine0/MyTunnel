using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Security;
using MyTunnel.ControlPlane.Services;
using MyTunnel.Gateway.Tunnels;

namespace MyTunnel.Gateway.ControlPlane;

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
public sealed record CreateTokenRequest(string? Name, int? ExpiresInDays);
public sealed record CreateProjectRequest(string? Name);
public sealed record ReserveSubdomainRequest(string? Subdomain);
public sealed record DeviceStartRequest(string? ClientName);
public sealed record DeviceTokenRequest(string? DeviceCode);

public sealed record UserDto(Guid Id, string Email, string? DisplayName, DateTimeOffset CreatedAt);
public sealed record TokenDto(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, bool Active);
public sealed record CreatedTokenDto(TokenDto Token, string Secret);
public sealed record SubdomainDto(Guid Id, string Subdomain, string PublicUrl, bool Online);
public sealed record ProjectDto(Guid Id, string Name, DateTimeOffset CreatedAt, IReadOnlyList<SubdomainDto> Subdomains);
public sealed record TunnelDto(string TunnelId, string Subdomain, string PublicUrl, DateTimeOffset ConnectedAt, long Requests, int InFlight, Guid? TokenId);
public sealed record UsageDto(DateOnly Day, string Subdomain, long Requests, long Errors, long BytesIn, long BytesOut);
public sealed record AuditDto(string Type, DateTimeOffset CreatedAt, string? IpAddress, string? Detail);

public static class ApiEndpoints
{
    public const string CsrfHeader = "X-MyTunnel-Csrf";

    public static void MapControlPlaneApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(NoStore);

        // Browser endpoints: unsafe methods require a custom header, which a cross-site form cannot send.
        var browser = api.MapGroup("").AddEndpointFilter(RequireCsrfHeader);
        browser.MapPost("/auth/register", RegisterAsync).RequireRateLimiting(GatewayApplication.AuthPolicy);
        browser.MapPost("/auth/login", LoginAsync).RequireRateLimiting(GatewayApplication.AuthPolicy);

        var dashboard = browser.MapGroup("").RequireAuthorization();
        dashboard.MapPost("/auth/logout", LogoutAsync);
        dashboard.MapGet("/me", MeAsync);
        dashboard.MapPost("/account/password", ChangePasswordAsync).RequireRateLimiting(GatewayApplication.AuthPolicy);

        dashboard.MapGet("/tokens", ListTokensAsync);
        dashboard.MapPost("/tokens", CreateTokenAsync).RequireRateLimiting(GatewayApplication.SensitivePolicy);
        dashboard.MapDelete("/tokens/{id:guid}", RevokeTokenAsync);

        dashboard.MapGet("/projects", ListProjectsAsync);
        dashboard.MapPost("/projects", CreateProjectAsync);
        dashboard.MapDelete("/projects/{id:guid}", DeleteProjectAsync);
        dashboard.MapPost("/projects/{id:guid}/subdomains", ReserveSubdomainAsync);
        dashboard.MapDelete("/projects/{id:guid}/subdomains/{reservationId:guid}", ReleaseSubdomainAsync);

        dashboard.MapGet("/tunnels", ListTunnels);
        dashboard.MapDelete("/tunnels/{tunnelId}", StopTunnelAsync);
        dashboard.MapGet("/usage", ListUsageAsync);
        dashboard.MapGet("/audit", ListAuditAsync);

        dashboard.MapGet("/device/{userCode}", GetDeviceAsync);
        dashboard.MapPost("/device/{userCode}/approve", (string userCode, HttpContext c, DeviceAuthorizationService s) => DecideDeviceAsync(userCode, true, c, s));
        dashboard.MapPost("/device/{userCode}/deny", (string userCode, HttpContext c, DeviceAuthorizationService s) => DecideDeviceAsync(userCode, false, c, s));

        // CLI endpoints: no cookies involved, so no CSRF header; authenticated by device code or bearer token.
        var cli = api.MapGroup("/cli");
        cli.MapPost("/device/start", StartDeviceAsync).RequireRateLimiting(GatewayApplication.SensitivePolicy);
        cli.MapPost("/device/token", ExchangeDeviceAsync).RequireRateLimiting(GatewayApplication.DevicePollPolicy);
        cli.MapGet("/whoami", WhoAmIAsync).RequireRateLimiting(GatewayApplication.DevicePollPolicy);
    }

    private static async ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return await next(context);
    }

    private static async ValueTask<object?> RequireCsrfHeader(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !request.Headers.ContainsKey(CsrfHeader))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: $"Missing {CsrfHeader} header.");
        }

        return await next(context);
    }

    private static string? Ip(HttpContext context) => context.Connection.RemoteIpAddress?.ToString();

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static UserDto ToDto(User user) => new(user.Id, user.Email, user.DisplayName, user.CreatedAt);

    private static TokenDto ToDto(AccessToken t, DateTimeOffset now) =>
        new(t.Id, t.Name, t.Prefix, t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt, t.RevokedAt is null && (t.ExpiresAt is null || t.ExpiresAt > now));

    // ---- Accounts ----------------------------------------------------------------------------

    private static async Task<IResult> RegisterAsync(RegisterRequest body, HttpContext context, AccountService accounts, CancellationToken ct)
    {
        if (accounts.ValidatePassword(body.Password) is { } passwordProblem)
        {
            return Invalid("password", passwordProblem);
        }

        var (user, error) = await accounts.RegisterAsync(body.Email ?? "", body.Password!, body.DisplayName, Ip(context), ct);
        return error switch
        {
            RegistrationError.InvalidEmail => Invalid("email", "Enter a valid email address."),
            RegistrationError.WeakPassword => Invalid("password", "Password is too weak."),
            RegistrationError.EmailTaken => Invalid("email", "An account with this email already exists."),
            _ => await SignInAsync(context, accounts, user!, ct, StatusCodes.Status201Created),
        };
    }

    private static async Task<IResult> LoginAsync(LoginRequest body, HttpContext context, AccountService accounts, CancellationToken ct)
    {
        var result = await accounts.AuthenticateAsync(body.Email ?? "", body.Password ?? "", Ip(context), ct);
        return result.Status switch
        {
            LoginStatus.Succeeded => await SignInAsync(context, accounts, result.User!, ct, StatusCodes.Status200OK),
            LoginStatus.LockedOut => Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many failed attempts. Try again in a few minutes."),
            _ => Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password."),
        };
    }

    private static async Task<IResult> SignInAsync(HttpContext context, AccountService accounts, User user, CancellationToken ct, int status)
    {
        var session = await accounts.CreateSessionAsync(user, context.Request.Headers.UserAgent.ToString(), Ip(context), ct);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ControlPlaneSetup.SessionClaim, session.Id.ToString())],
            ControlPlaneSetup.CookieScheme);
        await context.SignInAsync(ControlPlaneSetup.CookieScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = session.ExpiresAt });
        return Results.Json(ToDto(user), statusCode: status);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, AccountService accounts, CancellationToken ct)
    {
        await accounts.RevokeSessionAsync(context.User.GetSessionId(), context.User.GetUserId(), Ip(context), ct);
        await context.SignOutAsync(ControlPlaneSetup.CookieScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(HttpContext context, MyTunnelDbContext db, CancellationToken ct)
    {
        var user = await db.Users.FindAsync([context.User.GetUserId()], ct);
        return user is null ? Results.Unauthorized() : Results.Ok(ToDto(user));
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest body, HttpContext context, AccountService accounts, CancellationToken ct)
    {
        var problem = await accounts.ChangePasswordAsync(
            context.User.GetUserId(), body.CurrentPassword ?? "", body.NewPassword ?? "", context.User.GetSessionId(), Ip(context), ct);
        return problem is null ? Results.NoContent() : Invalid("password", problem);
    }

    // ---- Access tokens -----------------------------------------------------------------------

    private static async Task<IResult> ListTokensAsync(HttpContext context, AccessTokenService tokens, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return Results.Ok((await tokens.ListAsync(context.User.GetUserId(), ct)).Select(t => ToDto(t, now)));
    }

    private static async Task<IResult> CreateTokenAsync(CreateTokenRequest body, HttpContext context, AccessTokenService tokens, TimeProvider clock, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100)
        {
            return Invalid("name", "Name must be 1-100 characters.");
        }

        if (body.ExpiresInDays is < 1 or > 366)
        {
            return Invalid("expiresInDays", "Expiry must be between 1 and 366 days, or omitted for no expiry.");
        }

        var now = clock.GetUtcNow();
        var expires = body.ExpiresInDays is { } days ? now.AddDays(days) : (DateTimeOffset?)null;
        var created = await tokens.CreateAsync(context.User.GetUserId(), name, expires, Ip(context), ct);
        return created is null
            ? Invalid("name", "You have reached the maximum number of active tokens. Revoke one first.")
            : Results.Json(new CreatedTokenDto(ToDto(created.Token, now), created.Secret), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RevokeTokenAsync(Guid id, HttpContext context, AccessTokenService tokens, TunnelRegistry registry, CancellationToken ct)
    {
        if (!await tokens.RevokeAsync(context.User.GetUserId(), id, Ip(context), ct))
        {
            return Results.NotFound();
        }

        // Revocation takes effect immediately for tunnels that authenticated with this token.
        foreach (var session in registry.Snapshot().Where(s => s.Principal.CredentialId == id.ToString()))
        {
            await session.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Access token revoked");
            session.Abort();
        }

        return Results.NoContent();
    }

    // ---- Projects and reserved subdomains ----------------------------------------------------

    private static async Task<IResult> ListProjectsAsync(HttpContext context, ProjectService projects, TunnelRegistry registry, IOptions<GatewayOptions> gateway, CancellationToken ct)
    {
        var list = await projects.ListAsync(context.User.GetUserId(), ct);
        return Results.Ok(list.Select(p => ToDto(p, registry, gateway.Value)));
    }

    private static ProjectDto ToDto(Project p, TunnelRegistry registry, GatewayOptions gateway) =>
        new(p.Id, p.Name, p.CreatedAt, p.Subdomains
            .Select(s => new SubdomainDto(s.Id, s.Subdomain, gateway.BuildPublicUrl(s.Subdomain),
                registry.TryGet(s.Subdomain, out var session) && session.UserId == p.UserId.ToString()))
            .ToList());

    private static async Task<IResult> CreateProjectAsync(CreateProjectRequest body, HttpContext context, ProjectService projects, TunnelRegistry registry, IOptions<GatewayOptions> gateway, CancellationToken ct)
    {
        var (project, error) = await projects.CreateAsync(context.User.GetUserId(), body.Name ?? "", Ip(context), ct);
        return project is null ? Invalid("name", error!) : Results.Json(ToDto(project, registry, gateway.Value), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DeleteProjectAsync(Guid id, HttpContext context, ProjectService projects, CancellationToken ct) =>
        await projects.DeleteAsync(context.User.GetUserId(), id, Ip(context), ct) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> ReserveSubdomainAsync(Guid id, ReserveSubdomainRequest body, HttpContext context, ProjectService projects, IOptions<GatewayOptions> gateway, CancellationToken ct)
    {
        var (reservation, error) = await projects.ReserveAsync(context.User.GetUserId(), id, body.Subdomain ?? "", Ip(context), ct);
        return error switch
        {
            ReservationError.None => Results.Json(new SubdomainDto(reservation!.Id, reservation.Subdomain, gateway.Value.BuildPublicUrl(reservation.Subdomain), false), statusCode: StatusCodes.Status201Created),
            ReservationError.ProjectNotFound => Results.NotFound(),
            ReservationError.InvalidName => Invalid("subdomain", "Use 3-63 lowercase letters, digits or hyphens, not starting or ending with a hyphen."),
            ReservationError.Reserved => Invalid("subdomain", "That subdomain is reserved by the service."),
            ReservationError.Taken => Invalid("subdomain", "That subdomain is already reserved."),
            _ => Invalid("subdomain", "You have reached the maximum number of reserved subdomains."),
        };
    }

    private static async Task<IResult> ReleaseSubdomainAsync(Guid id, Guid reservationId, HttpContext context, ProjectService projects, CancellationToken ct) =>
        await projects.ReleaseAsync(context.User.GetUserId(), id, reservationId, Ip(context), ct) ? Results.NoContent() : Results.NotFound();

    // ---- Live tunnels, usage, audit ----------------------------------------------------------

    private static IResult ListTunnels(HttpContext context, TunnelRegistry registry, IOptions<GatewayOptions> gateway) =>
        Results.Ok(registry.ForUser(context.User.GetUserId().ToString())
            .OrderBy(s => s.Subdomain)
            .Select(s => new TunnelDto(s.TunnelId, s.Subdomain, gateway.Value.BuildPublicUrl(s.Subdomain), s.ConnectedAt, s.RequestCount, s.InFlightRequests,
                Guid.TryParse(s.Principal.CredentialId, out var tokenId) ? tokenId : null)));

    private static async Task<IResult> StopTunnelAsync(string tunnelId, HttpContext context, TunnelRegistry registry, AuditLog audit, MyTunnelDbContext db, CancellationToken ct)
    {
        var userId = context.User.GetUserId();
        var session = registry.ForUser(userId.ToString()).SingleOrDefault(s => s.TunnelId == tunnelId);
        if (session is null)
        {
            return Results.NotFound();
        }

        await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopped from the dashboard");
        session.Abort();
        audit.Record(AuditTypes.TunnelStopped, userId, Ip(context), session.Subdomain);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListUsageAsync(HttpContext context, UsageStore usage, UsageAggregator aggregator, TimeProvider clock, int? days, CancellationToken ct)
    {
        var window = Math.Clamp(days ?? 30, 1, 366);
        await aggregator.FlushAsync(ct); // include the last few seconds of traffic
        var since = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1 - window);
        var rows = await usage.ListAsync(context.User.GetUserId(), since, ct);
        return Results.Ok(rows.Select(u => new UsageDto(u.Day, u.Subdomain, u.Requests, u.Errors, u.BytesIn, u.BytesOut)));
    }

    private static async Task<IResult> ListAuditAsync(HttpContext context, MyTunnelDbContext db, CancellationToken ct)
    {
        var userId = context.User.GetUserId();
        var events = await db.AuditEvents.Where(a => a.UserId == userId).OrderByDescending(a => a.CreatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(events.Select(a => new AuditDto(a.Type, a.CreatedAt, a.IpAddress, a.Detail)));
    }

    // ---- Browser-approved CLI login ----------------------------------------------------------

    private static async Task<IResult> StartDeviceAsync(DeviceStartRequest? body, HttpContext context, DeviceAuthorizationService devices, IOptions<ControlPlaneOptions> options, CancellationToken ct)
    {
        var start = await devices.StartAsync(body?.ClientName, ct);
        var dashboard = options.Value.DashboardUrl?.TrimEnd('/') ?? $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Ok(new
        {
            deviceCode = start.DeviceCode,
            userCode = start.UserCode,
            verificationUri = $"{dashboard}/activate",
            verificationUriComplete = $"{dashboard}/activate?code={Uri.EscapeDataString(start.UserCode)}",
            expiresIn = (int)(start.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds,
            interval = (int)start.PollInterval.TotalSeconds,
        });
    }

    private static async Task<IResult> ExchangeDeviceAsync(DeviceTokenRequest body, DeviceAuthorizationService devices, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(body.DeviceCode) || body.DeviceCode.Length > 128)
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }

        var result = await devices.ExchangeAsync(body.DeviceCode, ct);
        return result.Status switch
        {
            DeviceTokenStatus.Issued => Results.Ok(new { accessToken = result.AccessToken, email = result.Email }),
            DeviceTokenStatus.Pending => Results.BadRequest(new { error = "authorization_pending" }),
            DeviceTokenStatus.SlowDown => Results.BadRequest(new { error = "slow_down" }),
            DeviceTokenStatus.Denied => Results.BadRequest(new { error = "access_denied" }),
            _ => Results.BadRequest(new { error = "expired_token" }),
        };
    }

    private static async Task<IResult> GetDeviceAsync(string userCode, DeviceAuthorizationService devices, CancellationToken ct)
    {
        var pending = await devices.FindPendingAsync(userCode, ct);
        return pending is null
            ? Results.NotFound()
            : Results.Ok(new { userCode = Secrets.FormatUserCode(pending.UserCode), clientName = pending.ClientName, createdAt = pending.CreatedAt, expiresAt = pending.ExpiresAt });
    }

    private static async Task<IResult> DecideDeviceAsync(string userCode, bool approve, HttpContext context, DeviceAuthorizationService devices) =>
        await devices.DecideAsync(userCode, context.User.GetUserId(), approve, Ip(context), context.RequestAborted)
            ? Results.NoContent()
            : Results.NotFound();

    private static async Task<IResult> WhoAmIAsync(HttpContext context, AccessTokenService tokens, CancellationToken ct)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        var accessToken = await tokens.ValidateAsync(token, ct);
        return accessToken is null
            ? Results.Unauthorized()
            : Results.Ok(new { email = accessToken.User.Email, tokenName = accessToken.Name, expiresAt = accessToken.ExpiresAt });
    }
}
