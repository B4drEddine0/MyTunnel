using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTunnel.ControlPlane.Data;
using MyTunnel.ControlPlane.Services;

namespace MyTunnel.ControlPlane;

public static class ControlPlaneServiceCollectionExtensions
{
    public static IServiceCollection AddControlPlane(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<MyTunnelDbContext>(db => db
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<AuditLog>();
        services.AddScoped<AccountService>();
        services.AddScoped<AccessTokenService>();
        services.AddScoped<DeviceAuthorizationService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<UsageStore>();
        services.AddScoped<RetentionService>();
        return services;
    }
}
