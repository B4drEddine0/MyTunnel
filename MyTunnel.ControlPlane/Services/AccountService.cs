using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTunnel.ControlPlane.Data;

namespace MyTunnel.ControlPlane.Services;

public enum LoginStatus
{
    Succeeded,
    Failed,
    LockedOut,
}

public sealed record LoginResult(LoginStatus Status, User? User = null);

public enum RegistrationError
{
    None,
    InvalidEmail,
    WeakPassword,
    EmailTaken,
}

/// <summary>Accounts, password verification and server-side dashboard sessions.</summary>
public sealed class AccountService(
    MyTunnelDbContext db,
    IPasswordHasher<User> hasher,
    AuditLog audit,
    IOptions<ControlPlaneOptions> options,
    TimeProvider clock)
{
    private const int MaxPasswordLength = 256;
    private const int MaxFailedLogins = 10;
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(5);

    // Verified against when the email is unknown, so response time does not reveal which emails have accounts.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString()));

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
        {
            return false;
        }

        try
        {
            var address = new MailAddress(email.Trim());
            return address.Address == email.Trim() && address.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string? ValidatePassword(string? password)
    {
        var min = options.Value.MinPasswordLength;
        if (string.IsNullOrEmpty(password) || password.Length < min)
        {
            return $"Password must be at least {min} characters.";
        }

        return password.Length > MaxPasswordLength ? $"Password must be at most {MaxPasswordLength} characters." : null;
    }

    public async Task<(User? User, RegistrationError Error)> RegisterAsync(
        string email, string password, string? displayName, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!IsValidEmail(email))
        {
            return (null, RegistrationError.InvalidEmail);
        }

        if (ValidatePassword(password) is not null)
        {
            return (null, RegistrationError.WeakPassword);
        }

        var normalized = NormalizeEmail(email);
        if (await db.Users.AnyAsync(u => u.Email == normalized, cancellationToken))
        {
            return (null, RegistrationError.EmailTaken);
        }

        var name = displayName?.Trim();
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = normalized,
            PasswordHash = "",
            DisplayName = string.IsNullOrEmpty(name) ? null : name[..Math.Min(name.Length, 100)],
            CreatedAt = clock.GetUtcNow(),
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        audit.Record(AuditTypes.UserRegistered, user.Id, ipAddress);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            db.ChangeTracker.Clear();
            return (null, RegistrationError.EmailTaken);
        }

        return (user, RegistrationError.None);
    }

    /// <summary>Checks credentials and records the attempt. Accounts lock for a while after repeated failures.</summary>
    public async Task<LoginResult> AuthenticateAsync(string email, string password, string? ipAddress, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email ?? "");
        var user = await db.Users.SingleOrDefaultAsync(u => u.Email == normalized, cancellationToken);
        if (user is null || password is null || password.Length > MaxPasswordLength)
        {
            hasher.VerifyHashedPassword(null!, DummyHash.Value, password ?? "");
            audit.Record(AuditTypes.LoginFailed, null, ipAddress);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginResult(LoginStatus.Failed);
        }

        var windowStart = clock.GetUtcNow() - LockoutWindow;
        var recentFailures = await db.AuditEvents.CountAsync(
            a => a.UserId == user.Id && a.Type == AuditTypes.LoginFailed && a.CreatedAt > windowStart, cancellationToken);
        if (recentFailures >= MaxFailedLogins)
        {
            return new LoginResult(LoginStatus.LockedOut);
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed || user.DisabledAt is not null)
        {
            audit.Record(AuditTypes.LoginFailed, user.Id, ipAddress);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginResult(LoginStatus.Failed);
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }

        audit.Record(AuditTypes.LoginSucceeded, user.Id, ipAddress);
        await db.SaveChangesAsync(cancellationToken);
        return new LoginResult(LoginStatus.Succeeded, user);
    }

    public async Task<UserSession> CreateSessionAsync(User user, string? userAgent, string? ipAddress, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.SessionLifetime,
            UserAgent = userAgent is { Length: > 512 } ? userAgent[..512] : userAgent,
            IpAddress = ipAddress,
        };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>Returns the session's user if the session is live; null if it is unknown, expired or revoked.</summary>
    public async Task<User?> ValidateSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var session = await db.UserSessions.Include(s => s.User)
            .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now || session.User.DisabledAt is not null)
        {
            return null;
        }

        if (now - session.LastSeenAt > LastSeenResolution)
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return session.User;
    }

    public async Task RevokeSessionAsync(Guid sessionId, Guid userId, string? ipAddress, CancellationToken cancellationToken)
    {
        await db.UserSessions
            .Where(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, clock.GetUtcNow()), cancellationToken);
        audit.Record(AuditTypes.Logout, userId, ipAddress);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Changes the password and signs out every other session.</summary>
    public async Task<string?> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, Guid currentSessionId, string? ipAddress, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            return "The current password is incorrect.";
        }

        if (ValidatePassword(newPassword) is { } problem)
        {
            return problem;
        }

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        await db.UserSessions
            .Where(s => s.UserId == userId && s.Id != currentSessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, clock.GetUtcNow()), cancellationToken);
        audit.Record(AuditTypes.PasswordChanged, userId, ipAddress);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }
}
