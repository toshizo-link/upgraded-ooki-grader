using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Contracts;
using OokiGrader.Host.Security;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bootstrap = endpoints.MapGroup("/api/v1/bootstrap")
            .WithTags("Bootstrap");
        bootstrap.MapGet("/status", GetBootstrapStatus)
            .AllowAnonymous();
        bootstrap.MapPost("/complete", CompleteBootstrap)
            .AllowAnonymous();

        var auth = endpoints.MapGroup("/api/v1/auth")
            .WithTags("Authentication");
        auth.MapPost("/login", Login)
            .AllowAnonymous()
            .RequireRateLimiting("login");
        auth.MapPost("/logout", Logout);
        auth.MapGet("/me", CurrentUser);
        auth.MapGet("/csrf", IssueCsrf);
        auth.MapPost("/change-password", ChangePassword);
        return endpoints;
    }

    private static async Task<IResult> GetBootstrapStatus(
        HttpContext context,
        IBootstrapService bootstrap,
        CancellationToken cancellationToken)
    {
        var hostLocal = IsHostLocal(context.Connection.RemoteIpAddress);
        return Results.Ok(await bootstrap.GetStatusAsync(hostLocal, cancellationToken));
    }

    private static async Task<IResult> CompleteBootstrap(
        HttpContext context,
        [FromBody] CompleteBootstrapRequest request,
        IBootstrapService bootstrap,
        CancellationToken cancellationToken)
    {
        if (!IsHostLocal(context.Connection.RemoteIpAddress))
        {
            return Results.NotFound();
        }

        var result = await bootstrap.CompleteAsync(request, cancellationToken);
        return result.Succeeded
            ? Results.NoContent()
            : Problem(
                result.ErrorCode == "BOOTSTRAP_COMPLETED"
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status422UnprocessableEntity,
                result.ErrorCode!,
                result.ErrorMessage!);
    }

    private static async Task<IResult> Login(
        HttpContext context,
        [FromBody] LoginRequest request,
        IStaffAuthenticationService authentication,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrEmpty(request.Password)
            || request.Username.Length > 200
            || request.Password.Length > 1024)
        {
            return Problem(
                StatusCodes.Status401Unauthorized,
                "INVALID_CREDENTIALS",
                "ユーザー名またはパスワードを確認してください。");
        }

        var result = await authentication.LoginAsync(
            request.Username,
            request.Password,
            context.Connection.RemoteIpAddress,
            context.Request.Headers.UserAgent.FirstOrDefault(),
            context.TraceIdentifier,
            cancellationToken);

        if (result.Disposition == LoginDisposition.Throttled)
        {
            return Problem(
                StatusCodes.Status429TooManyRequests,
                "LOGIN_THROTTLED",
                "しばらく待ってから、もう一度お試しください。");
        }

        if (result.Disposition != LoginDisposition.Succeeded || result.Session is null)
        {
            return Problem(
                StatusCodes.Status401Unauthorized,
                "INVALID_CREDENTIALS",
                "ユーザー名またはパスワードを確認してください。");
        }

        var secure = configuration.GetValue("Security:RequireSecureCookies", true);
        var cookieName = OokiSessionAuthenticationHandler.GetCookieName(configuration);
        context.Response.Cookies.Append(
            cookieName,
            result.Session.SessionToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = result.Session.Staff.SessionExpiresAt,
                IsEssential = true,
            });
        context.Response.Headers.CacheControl = "no-store";
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(
        HttpContext context,
        IStaffAuthenticationService authentication,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var cookieName = OokiSessionAuthenticationHandler.GetCookieName(configuration);
        if (context.Request.Cookies.TryGetValue(cookieName, out var token))
        {
            await authentication.RevokeAsync(
                token,
                "user_logout",
                context.TraceIdentifier,
                cancellationToken);
        }

        context.Response.Cookies.Delete(
            cookieName,
            new CookieOptions
            {
                Secure = configuration.GetValue("Security:RequireSecureCookies", true),
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
        return Results.NoContent();
    }

    private static async Task<IResult> CurrentUser(
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (id is null)
        {
            return Results.Unauthorized();
        }

        var schoolName = await db.SiteSettings
            .AsNoTracking()
            .Select(settings => settings.SchoolName)
            .SingleAsync(cancellationToken);
        var expiresAt = DateTimeOffset.TryParse(
            principal.FindFirstValue("session_expires_at"),
            out var expiry)
            ? expiry
            : DateTimeOffset.UtcNow;

        return Results.Ok(new
        {
            id,
            username = principal.Identity?.Name ?? string.Empty,
            displayName = principal.FindFirstValue("display_name") ?? string.Empty,
            roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value),
            mustChangePassword = principal.FindFirstValue(
                "must_change_password") == "true",
            schoolName,
            environmentName = environment.EnvironmentName,
            sessionExpiresAt = expiresAt,
        });
    }

    private static async Task<IResult> IssueCsrf(
        HttpContext context,
        IStaffAuthenticationService authentication,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var cookieName = OokiSessionAuthenticationHandler.GetCookieName(configuration);
        if (!context.Request.Cookies.TryGetValue(cookieName, out var sessionToken))
        {
            return Results.Unauthorized();
        }

        var csrfToken = await authentication.RotateCsrfAsync(
            sessionToken,
            cancellationToken);
        return csrfToken is null
            ? Results.Unauthorized()
            : Results.Ok(new CsrfTokenResponse(csrfToken));
    }

    private static async Task<IResult> ChangePassword(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ChangePasswordBody request,
        OokiGraderDbContext db,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null
            || string.IsNullOrEmpty(request.CurrentPassword)
            || request.CurrentPassword.Length > 1_024)
        {
            return Results.Unauthorized();
        }

        var passwordErrors = PasswordPolicy.Validate(request.NewPassword);
        if (passwordErrors.Count > 0)
        {
            return Problem(
                StatusCodes.Status422UnprocessableEntity,
                "PASSWORD_POLICY",
                string.Join(' ', passwordErrors));
        }

        var user = await db.StaffUsers
            .Include(item => item.Sessions)
            .SingleOrDefaultAsync(
                item => item.Id == userId && item.Status == "active",
                cancellationToken);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        if (!await passwordHasher.VerifyAsync(
                request.CurrentPassword,
                user.PasswordHash,
                cancellationToken))
        {
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                ActorStaffUserId = user.Id,
                EventType = "auth.password_change",
                ObjectType = "staff_user",
                ObjectId = user.Id,
                Outcome = "failed",
                ReasonCode = "current_password_invalid",
                CorrelationId = context.TraceIdentifier,
                SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                    context.Connection.RemoteIpAddress),
            });
            await db.SaveChangesAsync(cancellationToken);
            return Problem(
                StatusCodes.Status401Unauthorized,
                "CURRENT_PASSWORD_INVALID",
                "現在のパスワードを確認してください。");
        }

        if (await passwordHasher.VerifyAsync(
                request.NewPassword,
                user.PasswordHash,
                cancellationToken))
        {
            return Problem(
                StatusCodes.Status422UnprocessableEntity,
                "PASSWORD_UNCHANGED",
                "現在とは異なるパスワードを指定してください。");
        }

        user.PasswordHash = await passwordHasher.HashAsync(
            request.NewPassword,
            cancellationToken);
        user.PasswordAlgorithm = "argon2id";
        user.PasswordAlgorithmVersion = 1;
        user.CredentialChangedAt = now;
        user.MustChangePassword = false;
        user.PasswordSetupExpiresAt = null;
        user.PasswordSetupUsedAt = null;
        user.FailedAttemptCount = 0;
        user.LockoutUntil = null;
        var currentSessionHash = principal.FindFirstValue("session_hash");
        foreach (var session in user.Sessions.Where(session =>
                     session.RevokedAt is null
                     && session.IdHash != currentSessionHash))
        {
            session.RevokedAt = now;
            session.RevokeReason = "password_changed";
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = user.Id,
            EventType = "auth.password_change",
            ObjectType = "staff_user",
            ObjectId = user.Id,
            Outcome = "succeeded",
            ReasonCode = "self_service",
            CorrelationId = context.TraceIdentifier,
            SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                context.Connection.RemoteIpAddress),
        });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(
            statusCode: status,
            type: $"https://ooki-grader.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
            title: status == StatusCodes.Status401Unauthorized
                ? "ログインできません"
                : "リクエストを処理できません",
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool IsHostLocal(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    private sealed record ChangePasswordBody(
        string CurrentPassword,
        string NewPassword);
}
