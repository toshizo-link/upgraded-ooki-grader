using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Security;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static partial class StaffEndpoints
{
    private const int MaximumPageSize = 200;
    private const string StaffListRoute = "GET:/api/v1/staff";

    public static IEndpointRouteBuilder MapStaffEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var staff = endpoints.MapGroup("/api/v1/staff")
            .WithTags("Staff")
            .RequireAuthorization("administrator");
        staff.MapGet("/", ListStaff);
        staff.MapPost("/", CreateStaff);
        staff.MapGet("/{staffId}", GetStaff);
        staff.MapPatch("/{staffId}", PatchStaff);
        staff.MapPost("/{staffId}:disable", DisableStaff);
        staff.MapPost("/{staffId}:enable", EnableStaff);
        staff.MapPost("/{staffId}:resetPassword", ResetPassword);

        endpoints.MapGet("/api/v1/roles", ListRoles)
            .WithTags("Staff")
            .RequireAuthorization("administrator");
        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates these predicates to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListStaff(
        HttpContext context,
        string? search,
        string? status,
        string? cursor,
        int? pageSize,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (search?.Length > 200
            || status is not (null or "" or "active" or "disabled"))
        {
            return Results.BadRequest();
        }

        var take = Math.Clamp(pageSize ?? 50, 1, MaximumPageSize);
        var query = db.StaffUsers
            .AsNoTracking()
            .Include(user => user.Roles)
            .AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(user => user.Status == status);
        }

        var normalizedSearch = CursorPagination.TrimToNull(search);
        if (normalizedSearch is not null)
        {
            var normalized =
                StaffAuthenticationService.NormalizeUsername(normalizedSearch);
            query = query.Where(user =>
                user.DisplayName.Contains(normalizedSearch)
                || user.Username.Contains(normalizedSearch)
                || user.UsernameNormalized.Contains(normalized));
        }

        var normalizedStatus = CursorPagination.TrimToNull(status);
        var filterBinding = CursorPagination.Bind(
            ("search", normalizedSearch),
            ("sort", "status,displayName,id"),
            ("status", normalizedStatus));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                StaffListRoute,
                filterBinding,
                out StaffCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Status)
                || position.Status.Length > 64
                || string.IsNullOrEmpty(position.DisplayName)
                || position.DisplayName.Length > 500
                || string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(user =>
                string.Compare(user.Status, position.Status) > 0
                || (user.Status == position.Status
                    && (string.Compare(
                            user.DisplayName,
                            position.DisplayName) > 0
                        || (user.DisplayName == position.DisplayName
                            && string.Compare(user.Id, position.Id) > 0))));
        }

        var users = await query
            .OrderBy(user => user.Status)
            .ThenBy(user => user.DisplayName)
            .ThenBy(user => user.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = users.Count > take;
        if (hasMore)
        {
            users.RemoveAt(take);
        }

        var nextCursor = users.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                StaffListRoute,
                filterBinding,
                hasMore,
                new StaffCursorPosition(
                    users[^1].Status,
                    users[^1].DisplayName,
                    users[^1].Id));
        return Results.Ok(new
        {
            items = users.Select(ToResponse),
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record StaffCursorPosition(
        string Status,
        string DisplayName,
        string Id);

    private static async Task<IResult> GetStaff(
        string staffId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await db.StaffUsers
            .AsNoTracking()
            .Include(item => item.Roles)
            .SingleOrDefaultAsync(item => item.Id == staffId, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> CreateStaff(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateStaffRequest request,
        OokiGraderDbContext db,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var normalizedUsername =
            StaffAuthenticationService.NormalizeUsername(request.Username);
        var roles = NormalizeRoles(request.Roles);
        var errors = ValidateIdentity(
            request.Username,
            normalizedUsername,
            request.DisplayName,
            roles);
        errors.AddRange(PasswordPolicy.Validate(request.InitialPassword));
        if (errors.Count > 0
            || !await RolesExistAsync(db, roles, cancellationToken))
        {
            return Invalid(
                context,
                "STAFF_INVALID",
                errors.Count > 0
                    ? string.Join(' ', errors)
                    : "指定された役割を確認してください。");
        }

        if (await db.StaffUsers.AnyAsync(
                user => user.UsernameNormalized == normalizedUsername,
                cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "USERNAME_ALREADY_EXISTS",
                "職員アカウントを作成できません",
                "このユーザー名はすでに使用されています。");
        }

        var now = timeProvider.GetUtcNow();
        var actorId = ApiHelpers.StaffId(principal);
        var user = new StaffUserEntity
        {
            Id = UlidId.New(now),
            Username = request.Username.Trim(),
            UsernameNormalized = normalizedUsername,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = await passwordHasher.HashAsync(
                request.InitialPassword,
                cancellationToken),
            PasswordAlgorithm = "argon2id",
            PasswordAlgorithmVersion = 1,
            Status = "active",
            CredentialChangedAt = now,
            MustChangePassword = true,
            PasswordSetupExpiresAt = now.AddHours(24),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StaffUsers.Add(user);
        foreach (var role in roles)
        {
            db.StaffUserRoles.Add(new StaffUserRoleEntity
            {
                StaffUserId = user.Id,
                RoleName = role,
                GrantedByStaffUserId = actorId,
                GrantedAt = now,
            });
        }

        AddAudit(
            db,
            now,
            context,
            principal,
            "staff.created",
            user.Id,
            "administrator_created",
            new { user.Username, user.DisplayName, roles });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "STAFF_CONFLICT",
                "職員アカウントを作成できません",
                "ユーザー名または役割が競合しました。");
        }

        await db.Entry(user).Collection(item => item.Roles)
            .LoadAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
        return Results.Created($"/api/v1/staff/{user.Id}", ToResponse(user));
    }

    private static async Task<IResult> PatchStaff(
        string staffId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] PatchStaffRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await db.StaffUsers
            .Include(item => item.Roles)
            .SingleOrDefaultAsync(item => item.Id == staffId, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        var precondition = CheckRevision(context, user.Revision, request.Revision);
        if (precondition is not null)
        {
            return precondition;
        }

        var username = request.Username ?? user.Username;
        var displayName = request.DisplayName ?? user.DisplayName;
        var roles = request.Roles is null
            ? user.Roles.Select(role => role.RoleName).ToArray()
            : NormalizeRoles(request.Roles);
        var normalizedUsername =
            StaffAuthenticationService.NormalizeUsername(username);
        var errors = ValidateIdentity(
            username,
            normalizedUsername,
            displayName,
            roles);
        if (errors.Count > 0
            || !await RolesExistAsync(db, roles, cancellationToken))
        {
            return Invalid(
                context,
                "STAFF_INVALID",
                errors.Count > 0
                    ? string.Join(' ', errors)
                    : "指定された役割を確認してください。");
        }

        if (await db.StaffUsers.AnyAsync(
                item => item.Id != user.Id
                    && item.UsernameNormalized == normalizedUsername,
                cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "USERNAME_ALREADY_EXISTS",
                "職員アカウントを更新できません",
                "このユーザー名はすでに使用されています。");
        }

        var removesAdministrator = user.Roles.Any(
                role => role.RoleName == "administrator")
            && !roles.Contains("administrator", StringComparer.Ordinal);
        if (user.Status == "active"
            && removesAdministrator
            && !await HasAnotherEnabledAdministratorAsync(
                db,
                user.Id,
                cancellationToken))
        {
            return LastAdministrator(context);
        }

        var now = timeProvider.GetUtcNow();
        var actorId = ApiHelpers.StaffId(principal);
        var before = new
        {
            user.Username,
            user.DisplayName,
            roles = user.Roles
                .Select(role => role.RoleName)
                .Order(StringComparer.Ordinal)
                .ToArray(),
        };
        user.Username = username.Trim();
        user.UsernameNormalized = normalizedUsername;
        user.DisplayName = displayName.Trim();
        if (request.Roles is not null)
        {
            var existing = user.Roles
                .ToDictionary(role => role.RoleName, StringComparer.Ordinal);
            db.StaffUserRoles.RemoveRange(
                user.Roles.Where(role => !roles.Contains(
                    role.RoleName,
                    StringComparer.Ordinal)));
            foreach (var role in roles.Where(role => !existing.ContainsKey(role)))
            {
                db.StaffUserRoles.Add(new StaffUserRoleEntity
                {
                    StaffUserId = user.Id,
                    RoleName = role,
                    GrantedByStaffUserId = actorId,
                    GrantedAt = now,
                });
            }
        }

        AddAudit(
            db,
            now,
            context,
            principal,
            "staff.updated",
            user.Id,
            "administrator_updated",
            new
            {
                before,
                after = new
                {
                    username = user.Username,
                    displayName = user.DisplayName,
                    roles,
                },
            });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(context);
        }
        catch (DbUpdateException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "STAFF_CONFLICT",
                "職員アカウントを更新できません",
                "最新のアカウント情報を読み込み直してください。");
        }

        await db.Entry(user).Collection(item => item.Roles)
            .LoadAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
        return Results.Ok(ToResponse(user));
    }

    private static Task<IResult> DisableStaff(
        string staffId,
        HttpContext context,
        ClaimsPrincipal principal,
        StaffActionRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetStatus(
            staffId,
            "disabled",
            context,
            principal,
            request,
            db,
            timeProvider,
            cancellationToken);

    private static Task<IResult> EnableStaff(
        string staffId,
        HttpContext context,
        ClaimsPrincipal principal,
        StaffActionRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetStatus(
            staffId,
            "active",
            context,
            principal,
            request,
            db,
            timeProvider,
            cancellationToken);

    private static async Task<IResult> SetStatus(
        string staffId,
        string status,
        HttpContext context,
        ClaimsPrincipal principal,
        StaffActionRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await db.StaffUsers
            .Include(item => item.Roles)
            .Include(item => item.Sessions)
            .SingleOrDefaultAsync(item => item.Id == staffId, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        var precondition = CheckRevision(context, user.Revision, request.Revision);
        if (precondition is not null)
        {
            return precondition;
        }

        if (!ValidReason(request.ReasonCode))
        {
            return Invalid(context, "REASON_REQUIRED", "変更理由を指定してください。");
        }

        if (status == "disabled"
            && user.Status == "active"
            && user.Roles.Any(role => role.RoleName == "administrator")
            && !await HasAnotherEnabledAdministratorAsync(
                db,
                user.Id,
                cancellationToken))
        {
            return LastAdministrator(context);
        }

        if (user.Status == status)
        {
            ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
            return Results.Ok(ToResponse(user));
        }

        var now = timeProvider.GetUtcNow();
        user.Status = status;
        user.FailedAttemptCount = 0;
        user.LockoutUntil = null;
        if (status == "disabled")
        {
            RevokeSessions(user.Sessions, now, "account_disabled");
        }

        AddAudit(
            db,
            now,
            context,
            principal,
            status == "active" ? "staff.enabled" : "staff.disabled",
            user.Id,
            request.ReasonCode!,
            new { status });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(context);
        }

        ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ResetPassword(
        string staffId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ResetPasswordRequest request,
        OokiGraderDbContext db,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var passwordErrors = PasswordPolicy.Validate(request.NewPassword);
        if (passwordErrors.Count > 0 || !ValidReason(request.ReasonCode))
        {
            return Invalid(
                context,
                "PASSWORD_RESET_INVALID",
                passwordErrors.Count > 0
                    ? string.Join(' ', passwordErrors)
                    : "変更理由を指定してください。");
        }

        var user = await db.StaffUsers
            .Include(item => item.Roles)
            .Include(item => item.Sessions)
            .SingleOrDefaultAsync(item => item.Id == staffId, cancellationToken);
        if (user is null)
        {
            return Results.NotFound();
        }

        var precondition = CheckRevision(context, user.Revision, request.Revision);
        if (precondition is not null)
        {
            return precondition;
        }

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = await passwordHasher.HashAsync(
            request.NewPassword,
            cancellationToken);
        user.PasswordAlgorithm = "argon2id";
        user.PasswordAlgorithmVersion = 1;
        user.CredentialChangedAt = now;
        user.MustChangePassword = true;
        user.PasswordSetupExpiresAt = now.AddMinutes(30);
        user.PasswordSetupUsedAt = null;
        user.FailedAttemptCount = 0;
        user.LockoutUntil = null;
        RevokeSessions(user.Sessions, now, "administrator_password_reset");
        AddAudit(
            db,
            now,
            context,
            principal,
            "staff.password_reset",
            user.Id,
            request.ReasonCode!,
            new { sessionsRevoked = user.Sessions.Count });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(context);
        }

        ApiHelpers.SetRevisionEtag(context.Response, user.Revision);
        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> ListRoles(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var roles = await db.Roles
            .AsNoTracking()
            .OrderBy(role => role.Name)
            .Select(role => new
            {
                role.Name,
                role.DisplayName,
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            items = roles,
            nextCursor = (string?)null,
            totalApproximate = roles.Count,
        });
    }

    private static object ToResponse(StaffUserEntity user) =>
        new
        {
            user.Id,
            user.Username,
            user.DisplayName,
            user.Status,
            roles = user.Roles
                .Select(role => role.RoleName)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            user.LastLoginAt,
            user.LockoutUntil,
            user.CredentialChangedAt,
            user.MustChangePassword,
            user.PasswordSetupExpiresAt,
            user.CreatedAt,
            user.UpdatedAt,
            user.Revision,
        };

    private static List<string> ValidateIdentity(
        string? username,
        string normalizedUsername,
        string? displayName,
        string[] roles)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(username)
            || normalizedUsername.Length is < 1 or > 200)
        {
            errors.Add("ユーザー名を1〜200文字で入力してください。");
        }

        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.Trim().Length > 300)
        {
            errors.Add("表示名を1〜300文字で入力してください。");
        }

        if (roles.Length == 0)
        {
            errors.Add("役割を1つ以上指定してください。");
        }

        return errors;
    }

    private static string[] NormalizeRoles(IReadOnlyList<string>? roles) =>
        (roles ?? [])
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<bool> RolesExistAsync(
        OokiGraderDbContext db,
        string[] roles,
        CancellationToken cancellationToken) =>
        roles.Length > 0
        && await db.Roles.CountAsync(
            role => roles.Contains(role.Name),
            cancellationToken) == roles.Length;

    private static Task<bool> HasAnotherEnabledAdministratorAsync(
        OokiGraderDbContext db,
        string excludedStaffId,
        CancellationToken cancellationToken) =>
        db.StaffUsers.AnyAsync(
            user => user.Id != excludedStaffId
                && user.Status == "active"
                && user.Roles.Any(role => role.RoleName == "administrator"),
            cancellationToken);

    private static void RevokeSessions(
        IEnumerable<StaffSessionEntity> sessions,
        DateTimeOffset now,
        string reason)
    {
        foreach (var session in sessions.Where(item => item.RevokedAt is null))
        {
            session.RevokedAt = now;
            session.RevokeReason = reason;
        }
    }

    private static IResult? CheckRevision(
        HttpContext context,
        long actualRevision,
        long? bodyRevision)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                bodyRevision,
                out var expectedRevision))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "REVISION_REQUIRED",
                "更新条件が必要です",
                "最新の職員情報を読み込み直してください。");
        }

        return actualRevision == expectedRevision
            ? null
            : Stale(context);
    }

    private static IResult Stale(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status412PreconditionFailed,
            "REVISION_STALE",
            "職員情報が更新されています",
            "最新の職員情報を読み込み直してください。");

    private static IResult LastAdministrator(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "LAST_ADMINISTRATOR_REQUIRED",
            "最後の管理者は変更できません",
            "有効な管理者を少なくとも1人残してください。");

    private static IResult Invalid(
        HttpContext context,
        string code,
        string detail) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            code,
            "職員情報を保存できません",
            detail);

    private static bool ValidReason(string? value) =>
        value is { Length: > 0 and <= 100 }
        && ReasonPattern().IsMatch(value);

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        HttpContext context,
        ClaimsPrincipal principal,
        string eventType,
        string staffId,
        string reasonCode,
        object safeMetadata)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddTicks(1)),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = "staff_user",
            ObjectId = staffId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = context.TraceIdentifier,
            SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                context.Connection.RemoteIpAddress),
            SafeMetadataJson = JsonSerializer.Serialize(safeMetadata),
        });
    }

    private sealed record CreateStaffRequest(
        string Username,
        string DisplayName,
        string InitialPassword,
        IReadOnlyList<string>? Roles);

    private sealed record PatchStaffRequest(
        long? Revision,
        string? Username,
        string? DisplayName,
        IReadOnlyList<string>? Roles);

    private sealed record StaffActionRequest(
        long? Revision,
        string? ReasonCode);

    private sealed record ResetPasswordRequest(
        long? Revision,
        string NewPassword,
        string? ReasonCode);
}
