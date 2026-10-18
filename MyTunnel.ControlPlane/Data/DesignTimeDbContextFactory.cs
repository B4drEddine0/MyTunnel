using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyTunnel.ControlPlane.Data;

/// <summary>
/// Used only by <c>dotnet ef</c>. Generating a migration needs no database; applying one reads
/// <c>ConnectionStrings__MyTunnel</c> from the environment.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MyTunnelDbContext>
{
    public MyTunnelDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MyTunnel")
            ?? "Host=localhost;Database=mytunnel;Username=mytunnel";
        return new MyTunnelDbContext(new DbContextOptionsBuilder<MyTunnelDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
    }
}
