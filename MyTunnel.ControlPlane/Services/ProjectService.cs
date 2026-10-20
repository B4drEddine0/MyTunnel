using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane.Data;
using MyTunnel.Contracts;

namespace MyTunnel.ControlPlane.Services;

public enum ReservationError
{
    None,
    ProjectNotFound,
    InvalidName,
    Reserved,
    Taken,
    LimitReached,
}

/// <summary>Projects group a user's reserved subdomains. All queries are scoped to the calling user.</summary>
public sealed class ProjectService(MyTunnelDbContext db, AuditLog audit, IOptions<ControlPlaneOptions> options, TimeProvider clock)
{
    public Task<List<Project>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Projects.AsNoTracking()
            .Include(p => p.Subdomains.OrderBy(s => s.Subdomain))
            .Where(p => p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

    /// <returns>The project, or an error message.</returns>
    public async Task<(Project? Project, string? Error)> CreateAsync(Guid userId, string name, string? ipAddress, CancellationToken cancellationToken)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 100)
        {
            return (null, "Project name must be 1-100 characters.");
        }

        if (await db.Projects.CountAsync(p => p.UserId == userId, cancellationToken) >= options.Value.MaxProjectsPerUser)
        {
            return (null, $"You can have at most {options.Value.MaxProjectsPerUser} projects.");
        }

        var project = new Project { Id = Guid.CreateVersion7(), UserId = userId, Name = name, CreatedAt = clock.GetUtcNow() };
        db.Projects.Add(project);
        audit.Record(AuditTypes.ProjectCreated, userId, ipAddress, name);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (project, null);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return (null, "You already have a project with that name.");
        }
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid projectId, string? ipAddress, CancellationToken cancellationToken)
    {
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken);
        if (project is null)
        {
            return false;
        }

        db.Projects.Remove(project);
        audit.Record(AuditTypes.ProjectDeleted, userId, ipAddress, project.Name);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<(ReservedSubdomain? Reservation, ReservationError Error)> ReserveAsync(
        Guid userId, Guid projectId, string subdomain, string? ipAddress, CancellationToken cancellationToken)
    {
        subdomain = (subdomain ?? "").Trim().ToLowerInvariant();
        if (!SubdomainName.IsValid(subdomain))
        {
            return (null, ReservationError.InvalidName);
        }

        if (options.Value.ReservedSubdomains.Contains(subdomain, StringComparer.OrdinalIgnoreCase))
        {
            return (null, ReservationError.Reserved);
        }

        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.UserId == userId, cancellationToken))
        {
            return (null, ReservationError.ProjectNotFound);
        }

        if (await db.ReservedSubdomains.CountAsync(r => r.UserId == userId, cancellationToken) >= options.Value.MaxReservedSubdomainsPerUser)
        {
            return (null, ReservationError.LimitReached);
        }

        var reservation = new ReservedSubdomain
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            UserId = userId,
            Subdomain = subdomain,
            CreatedAt = clock.GetUtcNow(),
        };
        db.ReservedSubdomains.Add(reservation);
        audit.Record(AuditTypes.SubdomainReserved, userId, ipAddress, subdomain);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (reservation, ReservationError.None);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return (null, ReservationError.Taken);
        }
    }

    public async Task<bool> ReleaseAsync(Guid userId, Guid projectId, Guid reservationId, string? ipAddress, CancellationToken cancellationToken)
    {
        var reservation = await db.ReservedSubdomains.SingleOrDefaultAsync(
            r => r.Id == reservationId && r.ProjectId == projectId && r.UserId == userId, cancellationToken);
        if (reservation is null)
        {
            return false;
        }

        db.ReservedSubdomains.Remove(reservation);
        audit.Record(AuditTypes.SubdomainReleased, userId, ipAddress, reservation.Subdomain);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>True unless another user has reserved <paramref name="subdomain"/>.</summary>
    public async Task<bool> CanUseSubdomainAsync(Guid? userId, string subdomain, CancellationToken cancellationToken)
    {
        var owner = await db.ReservedSubdomains.AsNoTracking()
            .Where(r => r.Subdomain == subdomain)
            .Select(r => (Guid?)r.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        return owner is null || owner == userId;
    }
}
