using Microsoft.EntityFrameworkCore;
using SalonCrm.Api.Data;
using SalonCrm.Api.Domain;

namespace SalonCrm.Api.Auth;

public static class SessionDefaults
{
    public const string Scheme = "Session";
    public const string CookieName = "salon_sid";
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);
}

public sealed record ValidSession(Session Session, User User);

public class SessionService(AppDbContext db, TimeProvider timeProvider)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    // Sessions that are not revoked, not expired, not idle for too long, and belong to an active user.
    public IQueryable<Session> ActiveSessions()
    {
        var now = Now;
        var idleLimit = now - SessionDefaults.IdleTimeout;
        return db.Sessions.Where(s =>
            s.RevokedAt == null && s.ExpiresAt > now && s.LastSeenAt > idleLimit && s.User.IsActive);
    }

    public async Task<(Session Session, string Token)> CreateAsync(
        User user, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = Now;
        var token = SessionTokens.Generate();
        var session = new Session
        {
            UserId = user.Id,
            TokenHash = SessionTokens.Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + SessionDefaults.AbsoluteLifetime,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, token);
    }

    public async Task<ValidSession?> ValidateAsync(string token, CancellationToken ct = default)
    {
        var hash = SessionTokens.Hash(token);
        var session = await ActiveSessions().AsNoTracking().Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null)
        {
            return null;
        }

        var now = Now;
        if (now - session.LastSeenAt >= SessionDefaults.TouchInterval)
        {
            await db.Sessions.Where(s => s.Id == session.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSeenAt, now), ct);
            session.LastSeenAt = now;
        }

        return new ValidSession(session, session.User);
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = Now;
        await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, Guid? exceptSessionId = null, CancellationToken ct = default)
    {
        var now = Now;
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.Id != exceptSessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
    }
}
