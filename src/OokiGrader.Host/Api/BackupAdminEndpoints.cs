using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

internal static class BackupAdminEndpoints
{
    private const string BackupListRoute = "GET:/api/v1/admin/backups";

    public static IEndpointRouteBuilder MapBackupAdminEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .RequireAuthorization("administrator");

        group.MapGet("/backups", ListBackupsAsync);
        group.MapGet("/backups/{backupId}", GetBackupAsync);
        group.MapPost("/backups", CreateBackupAsync)
            .RequireIdempotency();
        group.MapPost("/backups/{backupId}:verify", VerifyBackupAsync)
            .RequireIdempotency();
        group.MapGet(
            "/backups/{backupId}/restore-plan",
            ValidateRestorePlanAsync);

        return endpoints;
    }

    private static async Task<IResult> ListBackupsAsync(
        HttpContext context,
        OokiGraderDbContext database,
        BackupHealthService healthService,
        string? state,
        string? cursor,
        int? pageSize,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var query = database.BackupRecords.AsNoTracking();
        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState?.Length > 32)
        {
            return Results.BadRequest();
        }

        if (normalizedState is not null)
        {
            query = query.Where(record => record.State == normalizedState);
        }

        var binding = CursorPagination.Bind(
            ("sort", "-requestedAt,-id"),
            ("state", normalizedState));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                BackupListRoute,
                binding,
                out BackupCursor position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrWhiteSpace(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(record =>
                record.RequestedAt < position.RequestedAt
                || (record.RequestedAt == position.RequestedAt
                    && string.Compare(
                        record.Id,
                        position.Id,
                        StringComparison.Ordinal) < 0));
        }

        var items = await query
            .OrderByDescending(record => record.RequestedAt)
            .ThenByDescending(record => record.Id)
            .Take(take + 1)
            .Select(record => new
            {
                id = record.Id,
                backupPolicyId = record.BackupPolicyId,
                backgroundJobId = record.BackgroundJobId,
                trigger = record.Trigger,
                state = record.State,
                destinationRelativePath = record.DestinationRelativePath,
                manifestSha256 = record.ManifestSha256,
                databaseSha256 = record.DatabaseSha256,
                databaseBytes = record.DatabaseBytes,
                objectCount = record.ObjectCount,
                objectBytes = record.ObjectBytes,
                secretEnvelopeCount = record.SecretEnvelopeCount,
                secretEnvelopeBytes = record.SecretEnvelopeBytes,
                databaseMigrationId = record.DatabaseMigrationId,
                applicationVersion = record.ApplicationVersion,
                integrityResult = record.IntegrityResult,
                requestedAt = record.RequestedAt,
                startedAt = record.StartedAt,
                completedAt = record.CompletedAt,
                verifiedAt = record.VerifiedAt,
                lastVerificationAt = record.LastVerificationAt,
                errorCode = record.ErrorCode,
                safeErrorDetail = record.SafeErrorDetail,
                revision = record.Revision,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(take);
        }

        var nextCursor = items.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                BackupListRoute,
                binding,
                hasMore,
                new BackupCursor(
                    items[^1].requestedAt,
                    items[^1].id));
        var health = await healthService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
            configuration = new
            {
                health.Configuration.Enabled,
                health.Configuration.Configured,
                health.Configuration.EncryptionConfirmed,
                destinationAccessible =
                    health.Configuration.DestinationAccessible,
                destinationRootPath =
                    health.Configuration.DestinationRootPath,
                health.Configuration.IncludeManagedScans,
                scheduleLocalTime =
                    $"{health.Configuration.ScheduleLocalHour:D2}:" +
                    $"{health.Configuration.ScheduleLocalMinute:D2}",
                health.NextScheduledAt,
                componentState = health.State,
                health.ErrorCode,
                health.Detail,
            },
        });
    }

    private sealed record BackupCursor(
        DateTimeOffset RequestedAt,
        string Id);

    private static async Task<IResult> GetBackupAsync(
        string backupId,
        HttpContext context,
        OokiGraderDbContext database,
        CancellationToken cancellationToken)
    {
        var record = await database.BackupRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == backupId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return BackupNotFound(context);
        }

        return Results.Ok(new
        {
            id = record.Id,
            backupPolicyId = record.BackupPolicyId,
            backgroundJobId = record.BackgroundJobId,
            trigger = record.Trigger,
            state = record.State,
            destinationRelativePath = record.DestinationRelativePath,
            manifestSha256 = record.ManifestSha256,
            databaseSha256 = record.DatabaseSha256,
            databaseBytes = record.DatabaseBytes,
            objectCount = record.ObjectCount,
            objectBytes = record.ObjectBytes,
            secretEnvelopeCount = record.SecretEnvelopeCount,
            secretEnvelopeBytes = record.SecretEnvelopeBytes,
            databaseMigrationId = record.DatabaseMigrationId,
            databaseDataVersion = record.DatabaseDataVersion,
            applicationVersion = record.ApplicationVersion,
            integrityResult = record.IntegrityResult,
            requestedAt = record.RequestedAt,
            startedAt = record.StartedAt,
            completedAt = record.CompletedAt,
            verifiedAt = record.VerifiedAt,
            lastVerificationAt = record.LastVerificationAt,
            errorCode = record.ErrorCode,
            safeErrorDetail = record.SafeErrorDetail,
            revision = record.Revision,
        });
    }

    private static async Task<IResult> CreateBackupAsync(
        HttpContext context,
        BackupJobCoordinator coordinator,
        IAuditSink audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coordinator.EnqueueManualAsync(
                    ApiHelpers.StaffId(context.User),
                    context.TraceIdentifier,
                    cancellationToken)
                .ConfigureAwait(false);
            await audit.AppendAsync(
                    new AuditWrite(
                        "backup.requested",
                        "backup",
                        result.BackupId,
                        "success",
                        ApiHelpers.StaffId(context.User),
                        context.TraceIdentifier,
                        SafeMetadataJson: System.Text.Json.JsonSerializer.Serialize(
                            new
                            {
                                trigger = "manual",
                                managedScansIncluded =
                                    coordinator.Configuration
                                        .IncludeManagedScans,
                            })),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(
                $"/api/v1/admin/backups/{result.BackupId}",
                new
                {
                    backupId = result.BackupId,
                    jobId = result.JobId,
                    state = result.State,
                });
        }
        catch (BackupOperationException exception) when (
            exception.IsConfigurationError)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                exception.ErrorCode.ToUpperInvariant(),
                "バックアップを開始できません",
                exception.SafeDetail);
        }
    }

    private static async Task<IResult> VerifyBackupAsync(
        string backupId,
        HttpContext context,
        BackupJobCoordinator coordinator,
        IAuditSink audit,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await coordinator.EnqueueVerificationAsync(
                    backupId,
                    ApiHelpers.StaffId(context.User),
                    context.TraceIdentifier,
                    cancellationToken)
                .ConfigureAwait(false);
            await audit.AppendAsync(
                    new AuditWrite(
                        "backup.verification.requested",
                        "backup",
                        backupId,
                        "success",
                        ApiHelpers.StaffId(context.User),
                        context.TraceIdentifier),
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(
                $"/api/v1/admin/backups/{backupId}",
                new
                {
                    backupId,
                    jobId = result.JobId,
                    state = result.State,
                });
        }
        catch (KeyNotFoundException)
        {
            return BackupNotFound(context);
        }
        catch (InvalidOperationException exception)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "BACKUP_NOT_VERIFIABLE",
                "バックアップを検証できません",
                exception.Message);
        }
        catch (ArgumentException)
        {
            return BackupNotFound(context);
        }
    }

    private static async Task<IResult> ValidateRestorePlanAsync(
        string backupId,
        HttpContext context,
        OokiGraderDbContext database,
        IBackupArchiveService archiveService,
        CancellationToken cancellationToken)
    {
        var record = await database.BackupRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == backupId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return BackupNotFound(context);
        }

        if (record.DestinationRelativePath is null
            || record.ManifestSha256 is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "BACKUP_NOT_VERIFIABLE",
                "バックアップを検証できません",
                "検証済みマニフェストがまだ作成されていません。");
        }

        try
        {
            var plan = await archiveService.ValidateRestorePlanAsync(
                    record.Id,
                    record.DestinationRelativePath,
                    record.ManifestSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            return plan.CanRestore
                ? Results.Ok(plan)
                : ApiHelpers.Problem(
                    context,
                    StatusCodes.Status422UnprocessableEntity,
                    (plan.ErrorCode ?? "BACKUP_RESTORE_PLAN_INVALID")
                        .ToUpperInvariant(),
                    "復元計画を検証できません",
                    plan.SafeErrorDetail
                    ?? "バックアップの整合性を確認してください。");
        }
        catch (BackupOperationException exception) when (
            exception.IsConfigurationError)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                exception.ErrorCode.ToUpperInvariant(),
                "復元計画を検証できません",
                exception.SafeDetail);
        }
    }

    private static IResult BackupNotFound(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status404NotFound,
            "BACKUP_NOT_FOUND",
            "バックアップが見つかりません",
            "指定されたバックアップ記録は存在しません。");
}
