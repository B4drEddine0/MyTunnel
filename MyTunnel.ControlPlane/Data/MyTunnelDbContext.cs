using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace MyTunnel.ControlPlane.Data;

public sealed class MyTunnelDbContext(DbContextOptions<MyTunnelDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<AccessToken> AccessTokens => Set<AccessToken>();

    public DbSet<DeviceAuthorization> DeviceAuthorizations => Set<DeviceAuthorization>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ReservedSubdomain> ReservedSubdomains => Set<ReservedSubdomain>();

    public DbSet<UsageDaily> UsageDaily => Set<UsageDaily>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    /// <summary>ASP.NET Core Data Protection keys, so auth cookies survive restarts and work across instances.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.Email).HasMaxLength(254);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(512);
            e.Property(u => u.DisplayName).HasMaxLength(100);
        });

        model.Entity<UserSession>(e =>
        {
            e.ToTable("user_sessions");
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.UserId, s.ExpiresAt });
            e.Property(s => s.UserAgent).HasMaxLength(512);
            e.Property(s => s.IpAddress).HasMaxLength(64);
        });

        model.Entity<AccessToken>(e =>
        {
            e.ToTable("access_tokens");
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Prefix).HasMaxLength(32);
            e.HasIndex(t => t.Prefix).IsUnique();
            e.HasIndex(t => t.UserId);
            e.Property(t => t.SecretHash).HasMaxLength(32);
        });

        model.Entity<DeviceAuthorization>(e =>
        {
            e.ToTable("device_authorizations");
            e.HasIndex(d => d.DeviceCodeHash).IsUnique();
            e.HasIndex(d => d.UserCode).IsUnique();
            e.Property(d => d.UserCode).HasMaxLength(16);
            e.Property(d => d.ClientName).HasMaxLength(100);
            e.HasOne(d => d.ApprovedByUser).WithMany().HasForeignKey(d => d.ApprovedByUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.ExpiresAt);
        });

        model.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasOne(p => p.User).WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.Name).HasMaxLength(100);
            e.HasIndex(p => new { p.UserId, p.Name }).IsUnique();
        });

        model.Entity<ReservedSubdomain>(e =>
        {
            e.ToTable("reserved_subdomains");
            e.HasOne(r => r.Project).WithMany(p => p.Subdomains).HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(r => r.Subdomain).HasMaxLength(63);
            e.HasIndex(r => r.Subdomain).IsUnique();
        });

        model.Entity<UsageDaily>(e =>
        {
            e.ToTable("usage_daily");
            e.HasKey(u => new { u.UserId, u.Day, u.Subdomain });
            e.Property(u => u.Subdomain).HasMaxLength(63);
            e.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.Day);
        });

        model.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.Property(a => a.Type).HasMaxLength(64);
            e.Property(a => a.IpAddress).HasMaxLength(64);
            e.Property(a => a.Detail).HasMaxLength(256);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(a => new { a.UserId, a.CreatedAt });
            e.HasIndex(a => a.CreatedAt);
        });

        model.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
