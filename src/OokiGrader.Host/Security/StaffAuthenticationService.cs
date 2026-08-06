using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Security;

public sealed record AuthenticatedStaff(
    string Id,
    string Username,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool MustChangePassword,
    DateTimeOffset SessionExpiresAt,
    string SessionHash);

public sealed record LoginSession(
    AuthenticatedStaff Staff,
    string SessionToken,
    string CsrfToken);

public enum LoginDisposition
{
    Succeeded,
    InvalidCredentials,
    Throttled,
    Disabled,
}

public sealed record LoginAttemptResult(
    LoginDisposition Disposition,
    LoginSession? Session = null);

public interface IStaffAuthenticationService
{
    Task<AuthenticatedStaff?> ResolveAsync(
        string sessionToken,
        CancellationToken cancellationToken = default);

    Task<LoginAttemptResult> LoginAsync(
        string username,
        string password,
        IPAddress? sourceAddress,
        string? userAgent,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        string sessionToken,
        string reason,
        string correlationId,
        CancellationToken cancellationToken = default);

    Task<string?> RotateCsrfAsync(
        string sessionToken,
        CancellationToken cancellationToken = default);

    Task<bool> ValidateCsrfAsync(
        string sessionToken,
        string csrfToken,
        CancellationToken cancellationToken = default);
}

public sealed class StaffAuthenticationService(
    OokiGraderDbContext db,
    IPasswordHasher passwordHasher,
    ISessionTokenService tokens,
    TimeProvider timeProvider,
    IConfiguration configuration) : IStaffAuthenticationService
{
    public async Task<AuthenticatedStaff?> ResolveAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hash = tokens.Hash(sessionToken);
        var session = await db.StaffSessions
            .Include(item => item.StaffUser)
            .ThenInclude(user => user.Roles)
            .SingleOrDefaultAsync(item => item.IdHash == hash, cancellationToken);

        if (session is null
            || session.RevokedAt is not null
            || session.IdleExpiresAt <= now
            || session.AbsoluteExpiresAt <= now
            || session.StaffUser.Status != "active")
        {
            return null;
        }

        var idleMinutes = configuration.GetValue("Security:SessionIdleMinutes", 30);
        if (session.LastSeenAt <= now.AddMinutes(-1))
        {
            session.LastSeenAt = now;
            session.IdleExpiresAt = Min(
                now.AddMinutes(Math.Clamp(idleMinutes, 5, 720)),
                session.AbsoluteExpiresAt);
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToAuthenticatedStaff(session);
    }

    public async Task<LoginAttemptResult> LoginAsync(
        string username,
        string password,
        IPAddress? sourceAddress,
        string? userAgent,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        var now = timeProvider.GetUtcNow();
        var user = await db.StaffUsers
            .Include(item => item.Roles)
            .SingleOrDefaultAsync(
                item => item.UsernameNormalized == normalizedUsername,
                cancellationToken);

        if (user is null)
        {
            // Preserve a memory-hard operation for unknown accounts so callers
            // cannot cheaply distinguish account existence by response timing.
            _ = await passwordHasher.HashAsync(
                string.IsNullOrEmpty(password) ? "invalid-password-placeholder" : password,
                cancellationToken);
            AddLoginAudit(
                now,
                actorStaffUserId: null,
                objectId: "unknown",
                outcome: "failed",
                reasonCode: "invalid_credentials",
                sourceAddress,
                correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(LoginDisposition.InvalidCredentials);
        }

        if (user.Status != "active")
        {
            AddLoginAudit(
                now,
                user.Id,
                user.Id,
                "failed",
                "account_disabled",
                sourceAddress,
                correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(LoginDisposition.InvalidCredentials);
        }

        if (user.MustChangePassword
            && (user.PasswordSetupExpiresAt <= now
                || user.PasswordSetupUsedAt is not null))
        {
            AddLoginAudit(
                now,
                user.Id,
                user.Id,
                "failed",
                user.PasswordSetupUsedAt is not null
                    ? "password_setup_already_used"
                    : "password_setup_expired",
                sourceAddress,
                correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(LoginDisposition.InvalidCredentials);
        }

        if (user.LockoutUntil > now)
        {
            AddLoginAudit(
                now,
                user.Id,
                user.Id,
                "throttled",
                "account_locked",
                sourceAddress,
                correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(LoginDisposition.Throttled);
        }

        if (!await passwordHasher.VerifyAsync(password, user.PasswordHash, cancellationToken))
        {
            user.FailedAttemptCount = checked(user.FailedAttemptCount + 1);
            if (user.FailedAttemptCount >= 5)
            {
                user.LockoutUntil = now.AddMinutes(15);
                user.FailedAttemptCount = 0;
            }

            AddLoginAudit(
                now,
                user.Id,
                user.Id,
                "failed",
                user.LockoutUntil > now
                    ? "lockout_started"
                    : "invalid_credentials",
                sourceAddress,
                correlationId);
            await db.SaveChangesAsync(cancellationToken);
            return new LoginAttemptResult(LoginDisposition.InvalidCredentials);
        }

        var pair = tokens.Create();
        var idleMinutes = Math.Clamp(
            configuration.GetValue("Security:SessionIdleMinutes", 30),
            5,
            720);
        var absoluteHours = Math.Clamp(
            configuration.GetValue("Security:SessionAbsoluteHours", 12),
            1,
            24);
        var absoluteExpiresAt = now.AddHours(absoluteHours);
        var session = new StaffSessionEntity
        {
            IdHash = pair.SessionTokenHash,
            StaffUserId = user.Id,
            CreatedAt = now,
            LastSeenAt = now,
            IdleExpiresAt = Min(now.AddMinutes(idleMinutes), absoluteExpiresAt),
            AbsoluteExpiresAt = absoluteExpiresAt,
            SourceIpPrefix = ToIpPrefix(sourceAddress),
            UserAgentHash = HashUserAgent(userAgent),
            CsrfSecretHash = pair.CsrfTokenHash,
        };
        user.FailedAttemptCount = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = now;
        if (user.MustChangePassword)
        {
            user.PasswordSetupUsedAt = now;
        }

        db.StaffSessions.Add(session);
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = user.Id,
            EventType = "auth.login",
            ObjectType = "staff_user",
            ObjectId = user.Id,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            SourceIpPrefix = ToIpPrefix(sourceAddress),
        });
        await db.SaveChangesAsync(cancellationToken);

        return new LoginAttemptResult(
            LoginDisposition.Succeeded,
            new LoginSession(
                ToAuthenticatedStaff(session, user),
                pair.SessionToken,
                pair.CsrfToken));
    }

    public async Task RevokeAsync(
        string sessionToken,
        string reason,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var sessionHash = tokens.Hash(sessionToken);
        var session = await db.StaffSessions
            .SingleOrDefaultAsync(item => item.IdHash == sessionHash, cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return;
        }

        session.RevokedAt = now;
        session.RevokeReason = reason[..Math.Min(reason.Length, 256)];
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = session.StaffUserId,
            EventType = "auth.logout",
            ObjectType = "staff_session",
            ObjectId = session.IdHash[..12],
            Outcome = "succeeded",
            CorrelationId = correlationId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string?> RotateCsrfAsync(
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var sessionHash = tokens.Hash(sessionToken);
        var session = await db.StaffSessions.SingleOrDefaultAsync(
            item => item.IdHash == sessionHash
                && item.RevokedAt == null
                && item.IdleExpiresAt > now
                && item.AbsoluteExpiresAt > now,
            cancellationToken);
        if (session is null)
        {
            return null;
        }

        var pair = tokens.Create();
        session.CsrfSecretHash = pair.CsrfTokenHash;
        await db.SaveChangesAsync(cancellationToken);
        return pair.CsrfToken;
    }

    public async Task<bool> ValidateCsrfAsync(
        string sessionToken,
        string csrfToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)
            || string.IsNullOrWhiteSpace(csrfToken))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var sessionHash = tokens.Hash(sessionToken);
        var expectedHash = await db.StaffSessions
            .Where(item => item.IdHash == sessionHash
                && item.RevokedAt == null
                && item.IdleExpiresAt > now
                && item.AbsoluteExpiresAt > now)
            .Select(item => item.CsrfSecretHash)
            .SingleOrDefaultAsync(cancellationToken);

        return expectedHash is not null && tokens.Verify(csrfToken, expectedHash);
    }

    public static string NormalizeUsername(string value) =>
        (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Trim()
            .ToUpperInvariant();

    private static AuthenticatedStaff ToAuthenticatedStaff(StaffSessionEntity session) =>
        ToAuthenticatedStaff(session, session.StaffUser);

    private static AuthenticatedStaff ToAuthenticatedStaff(
        StaffSessionEntity session,
        StaffUserEntity user) =>
        new(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Roles
                .Select(role => role.RoleName)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            user.MustChangePassword,
            Min(session.IdleExpiresAt, session.AbsoluteExpiresAt),
            session.IdHash);

    private static string? HashUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(userAgent)))
            .ToLowerInvariant();
    }

    private void AddLoginAudit(
        DateTimeOffset now,
        string? actorStaffUserId,
        string objectId,
        string outcome,
        string reasonCode,
        IPAddress? sourceAddress,
        string correlationId)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = actorStaffUserId,
            EventType = "auth.login",
            ObjectType = "staff_user",
            ObjectId = objectId,
            Outcome = outcome,
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            SourceIpPrefix = ToIpPrefix(sourceAddress),
        });
    }

    internal static string? ToIpPrefix(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            bytes[3] = 0;
            return $"{new IPAddress(bytes)}/24";
        }

        for (var index = 8; index < bytes.Length; index++)
        {
            bytes[index] = 0;
        }

        return $"{new IPAddress(bytes)}/64";
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;
}
