using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

internal static class AdminEndpoints
{
    private const long DefaultWarningBytes = 144_955_146_240;
    private const long DefaultCleanupTargetBytes = 155_692_564_480;
    private const long DefaultHardLimitBytes = 161_061_273_600;
    private const string JobsListRoute = "GET:/api/v1/admin/jobs";
    private const string DeletionsListRoute = "GET:/api/v1/admin/deletions";
    private const string AuditEventsListRoute = "GET:/api/v1/admin/audit-events";

    public static IEndpointRouteBuilder MapAdminEndpoints(
        this IEndpointRouteBuilder endpoints,
        string dataRoot)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .RequireAuthorization("administrator");

        group.MapGet("/health", GetHealthAsync);
        group.MapGet("/storage", (
            OokiGraderDbContext database,
            CancellationToken cancellationToken) =>
            GetStorageAsync(database, dataRoot, cancellationToken));
        group.MapGet("/jobs", GetJobsAsync);
        group.MapPost("/jobs/{jobId}:retry", RetryJobAsync);
        group.MapPost("/jobs/{jobId}:cancel", CancelJobAsync);
        group.MapPost("/retention:run", EnqueueRetentionAsync);
        group.MapGet("/deletions", GetDeletionHistoryAsync);
        group.MapGet("/audit-events", GetAuditEventsAsync);
        group.MapGet("/settings/site", GetSiteSettingsAsync);
        group.MapPatch("/settings/site", PatchSiteSettingsAsync);
        group.MapPost("/maintenance:enter", (
            HttpContext context,
            OokiGraderDbContext database,
            IAuditSink audit,
            CancellationToken cancellationToken) =>
            SetMaintenanceModeAsync(
                context,
                database,
                audit,
                enabled: true,
                cancellationToken));
        group.MapPost("/maintenance:exit", (
            HttpContext context,
            OokiGraderDbContext database,
            IAuditSink audit,
            CancellationToken cancellationToken) =>
            SetMaintenanceModeAsync(
                context,
                database,
                audit,
                enabled: false,
                cancellationToken));

        return endpoints;
    }

    private static async Task<IResult> GetHealthAsync(
        OokiGraderDbContext database,
        BackupHealthService backupHealthService,
        HostCertificateHealthService certificateHealthService,
        IConfiguration configuration,
        OokiGrader.Ai.Abstractions.IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var settings = await database.SiteSettings
            .AsNoTracking()
            .SingleAsync(cancellationToken);

        bool databaseHealthy;
        try
        {
            databaseHealthy = await database.Database
                .CanConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException)
        {
            databaseHealthy = false;
        }

        var storage = ReadDriveMetrics(settings.DataRoot);
        var fileStoreHealthy = Directory.Exists(settings.DataRoot);
        var reserveHealthy = storage.FreeBytes is null
            || storage.FreeBytes >= settings.PhysicalFreeReserveBytes;
        var failedJobs = await database.BackgroundJobs
            .AsNoTracking()
            .CountAsync(
                job => job.State == "failed" || job.State == "blocked",
                cancellationToken);
        var queueState = failedJobs == 0 ? "healthy" : "degraded";
        var pendingMigrations = databaseHealthy
            ? (await database.Database
                .GetPendingMigrationsAsync(cancellationToken))
                .ToArray()
            : [];
        var schemaHealthy = databaseHealthy && pendingMigrations.Length == 0;
        var activeAiProfile = await database.AiTaskProfiles
            .AsNoTracking()
            .Include(profile => profile.AiConnection)
            .Where(profile => profile.Active)
            .OrderByDescending(profile => profile.ActivatedAt)
            .ThenBy(profile => profile.Id)
            .Select(profile => new
            {
                profile.ModelId,
                ConnectionState = profile.AiConnection.State,
                ProbeState = profile.AiConnection.LastCapabilityProbeState,
                ProbeAt = profile.AiConnection.LastCapabilityProbeAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        var aiConfigured = await database.AiConnections
            .AsNoTracking()
            .AnyAsync(cancellationToken);
        var capabilities = await CapabilitiesEndpoints.ReadAsync(
            database,
            configuration,
            promptCatalog,
            cancellationToken);
        var aiFeatureEnabled =
            capabilities.Ai.TemplateGeneration.Enabled
            || capabilities.Ai.NameTranscription.Enabled
            || capabilities.Ai.SemanticGrading.Enabled;
        var aiProfileReady =
            capabilities.Ai.TemplateGeneration.Ready
            || capabilities.Ai.NameTranscription.Ready
            || capabilities.Ai.SemanticGrading.Ready;
        var ambiguousAiRequests = await database.AiRequests
            .AsNoTracking()
            .CountAsync(
                request => request.PossibleDuplicate
                    || (request.State == "dispatching"
                        && request.UpdatedAt < checkedAt.AddMinutes(-15)),
                cancellationToken);
        var budgetBlockedRequests = await database.AiRequests
            .AsNoTracking()
            .CountAsync(
                request => request.State == "budget_blocked",
                cancellationToken);
        var aiProbeFresh = activeAiProfile?.ProbeAt is not null
            && checkedAt - activeAiProfile.ProbeAt.Value
                <= TimeSpan.FromHours(24);
        var aiState = !aiFeatureEnabled
            ? "unknown"
            : activeAiProfile is null
            ? aiConfigured ? "degraded" : "unknown"
            : activeAiProfile.ConnectionState != "active"
                || activeAiProfile.ProbeState != "passed"
                ? "unavailable"
                : !aiProfileReady
                    ? "unavailable"
                    : !aiProbeFresh
                    || ambiguousAiRequests > 0
                    || budgetBlockedRequests > 0
                    ? "degraded"
                    : "healthy";
        var aiDetail = !aiFeatureEnabled
            ? "Gemini AI 機能はホスト設定で無効です。"
            : activeAiProfile is null
            ? aiConfigured
                ? "利用できる AI 機能設定がありません。"
                : "AI 接続プロファイルはまだ構成されていません。"
            : ambiguousAiRequests > 0
                ? $"{ambiguousAiRequests} 件の AI 送信結果を手動確認してください。"
                : budgetBlockedRequests > 0
                    ? $"{budgetBlockedRequests} 件が予算上限で停止しています。"
                    : !aiProbeFresh
                        ? "AI 能力確認を再実行してください。"
                        : !aiProfileReady
                            ? "有効なプロファイルが現在の認証情報またはプロンプト版と一致しません。再評価してください。"
                        : aiState == "unavailable"
                            ? "AI 接続または能力確認が利用できません。"
                            : null;
        var geminiBatch = await ReadGeminiBatchHealthAsync(
            database,
            checkedAt,
            cancellationToken);
        var certificate = certificateHealthService.Read();
        var backup = await backupHealthService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        var coreState = databaseHealthy
            && schemaHealthy
            && fileStoreHealthy
            && reserveHealthy
            && certificate.State != "unavailable"
            ? failedJobs == 0 ? "healthy" : "degraded"
            : "unavailable";
        var overallState = coreState == "unavailable"
            ? coreState
            : coreState == "degraded"
                || backup.State != "healthy"
                || aiState is "degraded" or "unavailable"
                || geminiBatch.State is "degraded" or "unavailable"
                || certificate.State == "degraded"
                ? "degraded"
                : "healthy";

        return Results.Ok(new
        {
            overallState,
            maintenanceMode = settings.MaintenanceMode,
            currentModel = activeAiProfile?.ModelId,
            certificateExpiresAt = certificate.ExpiresAt,
            lastBackupAt = backup.LastVerifiedAt,
            lastCleanupAt = await LastRetentionRunAsync(database, cancellationToken),
            components = new object[]
            {
                Component(
                    "database",
                    "データベース",
                    databaseHealthy ? "healthy" : "unavailable",
                    databaseHealthy ? null : "データベースへ接続できません。",
                    checkedAt),
                Component(
                    "databaseSchema",
                    "データベーススキーマ",
                    schemaHealthy ? "healthy" : "unavailable",
                    schemaHealthy
                        ? null
                        : pendingMigrations.Length == 0
                            ? "データベーススキーマを確認できません。"
                            : $"{pendingMigrations.Length} 件の未適用マイグレーションがあります。",
                    checkedAt,
                    schemaHealthy ? null : "database_schema_not_current"),
                Component(
                    "fileStore",
                    "ファイル保管領域",
                    fileStoreHealthy ? "healthy" : "unavailable",
                    fileStoreHealthy ? null : "データ保管領域を確認できません。",
                    checkedAt),
                Component(
                    "physicalStorage",
                    "物理ストレージ",
                    reserveHealthy ? "healthy" : "unavailable",
                    reserveHealthy
                        ? null
                        : "緊急用の空き容量 5 GiB を下回っています。",
                    checkedAt),
                Component(
                    "backgroundWorkers",
                    "バックグラウンド処理",
                    queueState,
                    failedJobs == 0
                        ? null
                        : $"{failedJobs} 件の失敗または停止ジョブがあります。",
                    checkedAt),
                Component(
                    "aiProvider",
                    "Gemini 接続",
                    aiState,
                    aiDetail,
                    checkedAt,
                    aiState == "healthy"
                        ? null
                        : activeAiProfile is null
                            ? "ai_profile_not_active"
                            : "ai_provider_attention_required"),
                GeminiBatchComponent(geminiBatch, checkedAt),
                Component(
                    "backup",
                    "バックアップ",
                    backup.State,
                    backup.Detail,
                    backup.CheckedAt,
                    backup.ErrorCode),
                Component(
                    "certificate",
                    "HTTPS 証明書",
                    certificate.State,
                    certificate.Detail,
                    certificate.CheckedAt,
                    certificate.ErrorCode),
                Component(
                    "clock",
                    "システム時刻",
                    "unknown",
                    "OS の時刻同期状態はホスト監視で確認してください。",
                    checkedAt,
                    "clock_sync_not_observed"),
                Component(
                    "update",
                    "アプリケーション更新",
                    "unknown",
                    "署名済み更新チャネルはまだ構成されていません。",
                    checkedAt,
                    "update_channel_not_configured"),
            },
        });
    }

    private static async Task<IResult> GetStorageAsync(
        OokiGraderDbContext database,
        string dataRoot,
        CancellationToken cancellationToken)
    {
        var settings = await database.SiteSettings
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        var availableObjects = database.FileObjects
            .AsNoTracking()
            .Where(file => file.State == "available");
        var managedBytes = await availableObjects
            .Where(file => file.ManagedScanBytes)
            .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
        var originalsBytes = await availableObjects
            .Where(file => file.StorageClass == "ManagedScanOriginal")
            .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
        var derivativesBytes = await availableObjects
            .Where(file => file.StorageClass == "ManagedScanDerived")
            .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
        var templatesBytes = await availableObjects
            .Where(file => file.StorageClass == "TemplateSource")
            .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
        var reportsBytes = await availableObjects
            .Where(file => file.StorageClass == "ResultReport")
            .SumAsync(file => (long?)file.Bytes, cancellationToken) ?? 0;
        var oldestRetainedAt = await database.FileReferences
            .AsNoTracking()
            .Where(reference =>
                reference.FileObject.ManagedScanBytes
                && reference.FileObject.State == "available")
            .MinAsync(
                reference => (DateTimeOffset?)reference.RetentionAnchorAt,
                cancellationToken);
        var drive = ReadDriveMetrics(dataRoot);
        var lastDeletion = await database.DeletionManifests
            .AsNoTracking()
            .Where(manifest => manifest.State == "completed")
            .OrderByDescending(manifest => manifest.CompletedAt ?? manifest.CreatedAt)
            .Select(manifest => new
            {
                manifest.DeletedObjectCount,
                manifest.Reason,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return Results.Ok(new
        {
            managedBytes,
            quotaBytes = settings.ManagedScanHardLimitBytes > 0
                ? settings.ManagedScanHardLimitBytes
                : DefaultHardLimitBytes,
            warningBytes = settings.ManagedScanWarningBytes > 0
                ? settings.ManagedScanWarningBytes
                : DefaultWarningBytes,
            proactiveCleanupBytes = settings.ManagedScanCleanupTargetBytes > 0
                ? settings.ManagedScanCleanupTargetBytes
                : DefaultCleanupTargetBytes,
            lowWaterBytes = settings.ManagedScanCleanupTargetBytes > 0
                ? settings.ManagedScanCleanupTargetBytes
                : DefaultCleanupTargetBytes,
            physicalFreeBytes = drive.FreeBytes ?? 0,
            physicalTotalBytes = drive.TotalBytes,
            originalsBytes,
            derivativesBytes,
            templatesBytes,
            reportsBytes,
            logsBytes = DirectoryBytes(Path.Combine(dataRoot, "logs")),
            temporaryBytes = DirectoryBytes(Path.Combine(dataRoot, "incoming")),
            quarantineBytes = DirectoryBytes(Path.Combine(dataRoot, "quarantine")),
            oldestRetainedAt,
            nextCleanupAt = NextCleanupAt(settings.TimeZone),
            lastDeletionCount = lastDeletion?.DeletedObjectCount ?? 0,
            lastDeletionReason = lastDeletion?.Reason,
        });
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetJobsAsync(
        HttpContext context,
        OokiGraderDbContext database,
        string? state,
        string? cursor,
        int? pageSize,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var query = database.BackgroundJobs.AsNoTracking();
        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState is not null)
        {
            if (normalizedState.Length > 64)
            {
                return Results.BadRequest();
            }

            query = query.Where(job => job.State == normalizedState);
        }

        var filterBinding = CursorPagination.Bind(
            ("sort", "-createdAt,-id"),
            ("state", normalizedState));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                JobsListRoute,
                filterBinding,
                out JobCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(job =>
                job.CreatedAt < position.CreatedAt
                || (job.CreatedAt == position.CreatedAt
                    && string.Compare(job.Id, position.Id) < 0));
        }

        var items = await query
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Take(take + 1)
            .Select(job => new
            {
                id = job.Id,
                jobType = job.Type,
                state = job.State,
                attempt = job.AttemptCount,
                maxAttempts = job.MaxAttempts,
                progressBasisPoints = job.ProgressBasisPoints,
                createdAt = job.CreatedAt,
                nextAttemptAt = job.NextAttemptAt,
                sanitizedError = job.SafeErrorDetail ?? job.ErrorCode,
                revision = job.Revision,
            })
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(take);
        }

        var nextCursor = items.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                JobsListRoute,
                filterBinding,
                hasMore,
                new JobCursorPosition(
                    items[^1].createdAt,
                    items[^1].id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private static Task<IResult> RetryJobAsync(
        string jobId,
        HttpContext context,
        OokiGraderDbContext database,
        IAuditSink audit,
        CancellationToken cancellationToken) =>
        ChangeJobStateAsync(
            jobId,
            context,
            database,
            audit,
            "queued",
            ["failed", "blocked", "cancelled"],
            "job.retry",
            cancellationToken);

    private static Task<IResult> CancelJobAsync(
        string jobId,
        HttpContext context,
        OokiGraderDbContext database,
        IAuditSink audit,
        CancellationToken cancellationToken) =>
        ChangeJobStateAsync(
            jobId,
            context,
            database,
            audit,
            "cancelled",
            ["queued", "retry_waiting", "blocked"],
            "job.cancel",
            cancellationToken);

    private static async Task<IResult> ChangeJobStateAsync(
        string jobId,
        HttpContext context,
        OokiGraderDbContext database,
        IAuditSink audit,
        string destinationState,
        string[] allowedSourceStates,
        string auditEventType,
        CancellationToken cancellationToken)
    {
        var job = await database.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status404NotFound,
                "JOB_NOT_FOUND",
                "ジョブが見つかりません",
                "指定されたジョブは存在しません。");
        }

        if (!allowedSourceStates.Contains(job.State, StringComparer.Ordinal))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "JOB_STATE_CONFLICT",
                "現在の状態では操作できません",
                $"状態が {job.State} のジョブにはこの操作を実行できません。");
        }

        job.State = destinationState;
        job.NextAttemptAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
        if (destinationState == "queued")
        {
            job.CompletedAt = null;
        }
        else
        {
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        await database.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            new AuditWrite(
                auditEventType,
                "backgroundJob",
                job.Id,
                "success",
                ApiHelpers.StaffId(context.User),
                context.TraceIdentifier),
            cancellationToken);
        return Results.Accepted(
            $"/api/v1/admin/jobs/{job.Id}",
            new { id = job.Id, state = job.State, revision = job.Revision });
    }

    private static async Task<IResult> EnqueueRetentionAsync(
        HttpContext context,
        IBackgroundJobStore jobs,
        IAuditSink audit,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var result = await jobs.EnqueueAsync(
            new EnqueueJobRequest(
                "retention.reconcile",
                1,
                $"retention:manual:{now:yyyyMMddHHmmss}",
                JsonSerializer.Serialize(new { reason = "manual", requestedAt = now }),
                Priority: 100,
                MaxAttempts: 3,
                CorrelationId: context.TraceIdentifier),
            cancellationToken);
        await audit.AppendAsync(
            new AuditWrite(
                "retention.run.requested",
                "backgroundJob",
                result.JobId,
                "success",
                ApiHelpers.StaffId(context.User),
                context.TraceIdentifier),
            cancellationToken);
        return Results.Accepted(
            $"/api/v1/admin/jobs/{result.JobId}",
            new
            {
                jobId = result.JobId,
                state = result.State.ToString().ToLowerInvariant(),
            });
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetDeletionHistoryAsync(
        HttpContext context,
        OokiGraderDbContext database,
        string? state,
        string? cursor,
        int? pageSize,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var query = database.DeletionManifests.AsNoTracking();
        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState is not null)
        {
            if (normalizedState.Length > 64)
            {
                return Results.BadRequest();
            }

            query = query.Where(manifest => manifest.State == normalizedState);
        }

        var filterBinding = CursorPagination.Bind(
            ("sort", "-completedOrCreatedAt,-id"),
            ("state", normalizedState));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                DeletionsListRoute,
                filterBinding,
                out DeletionCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(manifest =>
                (manifest.CompletedAt ?? manifest.CreatedAt)
                    < position.CompletedOrCreatedAt
                || ((manifest.CompletedAt ?? manifest.CreatedAt)
                        == position.CompletedOrCreatedAt
                    && string.Compare(manifest.Id, position.Id) < 0));
        }

        var items = await query
            .OrderByDescending(manifest =>
                manifest.CompletedAt ?? manifest.CreatedAt)
            .ThenByDescending(manifest => manifest.Id)
            .Take(take + 1)
            .Select(manifest => new
            {
                id = manifest.Id,
                backgroundJobId = manifest.BackgroundJobId,
                reason = manifest.Reason,
                state = manifest.State,
                cutoffAt = manifest.CutoffAt,
                plannedObjectCount = manifest.PlannedObjectCount,
                plannedReferenceCount = manifest.PlannedReferenceCount,
                plannedBytes = manifest.PlannedBytes,
                deletedObjectCount = manifest.DeletedObjectCount,
                missingObjectCount = manifest.MissingObjectCount,
                releasedReferenceCount = manifest.ReleasedReferenceCount,
                deletedBytes = manifest.DeletedBytes,
                failureCount = manifest.FailureCount,
                safeErrorDetail = manifest.SafeErrorDetail,
                createdAt = manifest.CreatedAt,
                startedAt = manifest.StartedAt,
                completedAt = manifest.CompletedAt,
                revision = manifest.Revision,
            })
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(take);
        }

        var nextCursor = items.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                DeletionsListRoute,
                filterBinding,
                hasMore,
                new DeletionCursorPosition(
                    items[^1].completedAt ?? items[^1].createdAt,
                    items[^1].id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetAuditEventsAsync(
        HttpContext context,
        OokiGraderDbContext database,
        string? eventType,
        string? objectType,
        string? objectId,
        string? cursor,
        int? pageSize,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var query = database.AuditEvents.AsNoTracking();
        var normalizedEventType = CursorPagination.TrimToNull(eventType);
        var normalizedObjectType = CursorPagination.TrimToNull(objectType);
        var normalizedObjectId = CursorPagination.TrimToNull(objectId);
        if (normalizedEventType?.Length > 128
            || normalizedObjectType?.Length > 128
            || normalizedObjectId?.Length > 256)
        {
            return Results.BadRequest();
        }

        if (normalizedEventType is not null)
        {
            query = query.Where(item => item.EventType == normalizedEventType);
        }

        if (normalizedObjectType is not null)
        {
            query = query.Where(item => item.ObjectType == normalizedObjectType);
        }

        if (normalizedObjectId is not null)
        {
            query = query.Where(item => item.ObjectId == normalizedObjectId);
        }

        var filterBinding = CursorPagination.Bind(
            ("eventType", normalizedEventType),
            ("objectId", normalizedObjectId),
            ("objectType", normalizedObjectType),
            ("sort", "-occurredAt,-id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                AuditEventsListRoute,
                filterBinding,
                out AuditCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(item =>
                item.OccurredAt < position.OccurredAt
                || (item.OccurredAt == position.OccurredAt
                    && string.Compare(item.Id, position.Id) < 0));
        }

        var items = await query
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Id)
            .Take(take + 1)
            .Select(item => new
            {
                id = item.Id,
                occurredAt = item.OccurredAt,
                actorStaffUserId = item.ActorStaffUserId,
                eventType = item.EventType,
                objectType = item.ObjectType,
                objectId = item.ObjectId,
                outcome = item.Outcome,
                reasonCode = item.ReasonCode,
                correlationId = item.CorrelationId,
                sourceIpPrefix = item.SourceIpPrefix,
                safeMetadataJson = item.SafeMetadataJson,
                action = item.EventType,
                timestamp = item.OccurredAt,
                localDisplayTime = item.OccurredAt,
                actorDisplayName = database.StaffUsers
                    .Where(user => user.Id == item.ActorStaffUserId)
                    .Select(user => user.DisplayName)
                    .FirstOrDefault(),
                summary = item.SafeMetadataJson ?? item.ReasonCode,
            })
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(take);
        }

        var nextCursor = items.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                AuditEventsListRoute,
                filterBinding,
                hasMore,
                new AuditCursorPosition(
                    items[^1].occurredAt,
                    items[^1].id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record JobCursorPosition(
        DateTimeOffset CreatedAt,
        string Id);

    private sealed record DeletionCursorPosition(
        DateTimeOffset CompletedOrCreatedAt,
        string Id);

    private sealed record AuditCursorPosition(
        DateTimeOffset OccurredAt,
        string Id);

    private static async Task<IResult> GetSiteSettingsAsync(
        OokiGraderDbContext database,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var settings = await database.SiteSettings
            .AsNoTracking()
            .SingleAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(response, settings.Revision);
        return Results.Ok(ToSiteSettings(settings));
    }

    private static async Task<IResult> PatchSiteSettingsAsync(
        SiteSettingsPatch request,
        HttpContext context,
        OokiGraderDbContext database,
        IAuditSink audit,
        CancellationToken cancellationToken)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.Revision,
                out var expectedRevision))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "REVISION_REQUIRED",
                "更新条件が必要です",
                "If-Match または revision を指定してください。");
        }

        var settings = await database.SiteSettings
            .SingleAsync(cancellationToken);
        if (settings.Revision != expectedRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_MISMATCH",
                "設定が更新されています",
                "最新の設定を読み込み直してから保存してください。");
        }

        if (request.SchoolName is not null)
        {
            settings.SchoolName = request.SchoolName.Trim();
        }

        if (request.TimeZone is not null)
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZone);
            }
            catch (TimeZoneNotFoundException)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status400BadRequest,
                    "INVALID_TIME_ZONE",
                    "タイムゾーンが正しくありません",
                    "ホストで利用できるタイムゾーンを指定してください。");
            }

            settings.TimeZone = request.TimeZone;
        }

        await database.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            new AuditWrite(
                "site.settings.updated",
                "siteSettings",
                settings.Id,
                "success",
                ApiHelpers.StaffId(context.User),
                context.TraceIdentifier),
            cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, settings.Revision);
        return Results.Ok(ToSiteSettings(settings));
    }

    private static async Task<IResult> SetMaintenanceModeAsync(
        HttpContext context,
        OokiGraderDbContext database,
        IAuditSink audit,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var settings = await database.SiteSettings.SingleAsync(cancellationToken);
        settings.MaintenanceMode = enabled;
        await database.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            new AuditWrite(
                enabled ? "maintenance.entered" : "maintenance.exited",
                "siteSettings",
                settings.Id,
                "success",
                ApiHelpers.StaffId(context.User),
                context.TraceIdentifier),
            cancellationToken);
        return Results.Ok(new
        {
            maintenanceMode = settings.MaintenanceMode,
            revision = settings.Revision,
        });
    }

    private static object ToSiteSettings(
        Infrastructure.Persistence.Entities.SiteSettingsEntity settings) =>
        new
        {
            schoolName = settings.SchoolName,
            timeZone = settings.TimeZone,
            locale = settings.Locale,
            managedScanHardLimitBytes = settings.ManagedScanHardLimitBytes,
            managedScanCleanupTargetBytes = settings.ManagedScanCleanupTargetBytes,
            managedScanWarningBytes = settings.ManagedScanWarningBytes,
            physicalFreeReserveBytes = settings.PhysicalFreeReserveBytes,
            scanRetentionCalendarMonths = settings.ScanRetentionCalendarMonths,
            maintenanceMode = settings.MaintenanceMode,
            revision = settings.Revision,
        };

    internal static async Task<GeminiBatchHealth> ReadGeminiBatchHealthAsync(
        OokiGraderDbContext database,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var recentCutoff = checkedAt.AddHours(-24);
        var staleCutoff = checkedAt.AddMinutes(-30);

        var batches = await database.AiBatches
            .AsNoTracking()
            .Where(item =>
                item.State == "prepared"
                || item.State == "uploading"
                || item.State == "submitting"
                || item.State == "submitted"
                || item.State == "reconcile_required"
                || item.State == "pending"
                || item.State == "running"
                || item.State == "delayed"
                || item.State == "manual_review"
                || item.PossibleDuplicate
                || item.UpdatedAt >= recentCutoff)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Active = group.Count(item =>
                    item.State == "prepared"
                    || item.State == "uploading"
                    || item.State == "submitting"
                    || item.State == "submitted"
                    || item.State == "reconcile_required"
                    || item.State == "pending"
                    || item.State == "running"
                    || item.State == "delayed"
                    || item.State == "manual_review"),
                ManualReview = group.Count(item =>
                    item.State == "manual_review"),
                Reconciliation = group.Count(item =>
                    item.State == "reconcile_required"),
                Delayed = group.Count(item => item.State == "delayed"),
                PossibleDuplicate = group.Count(item =>
                    item.PossibleDuplicate),
                Stale = group.Count(item =>
                    (item.State == "prepared"
                        || item.State == "uploading"
                        || item.State == "submitting"
                        || item.State == "submitted"
                        || item.State == "reconcile_required"
                        || item.State == "pending"
                        || item.State == "running")
                    && item.UpdatedAt < staleCutoff),
                RecentFailed = group.Count(item =>
                    item.State == "failed"
                    && item.UpdatedAt >= recentCutoff),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var jobs = await database.BackgroundJobs
            .AsNoTracking()
            .Where(item =>
                (item.Type == AiBatchJobWorker.PrepareJobType
                    || item.Type == AiBatchJobWorker.SubmitJobType
                    || item.Type == AiBatchJobWorker.PollJobType
                    || item.Type == AiBatchJobWorker.ReconcileJobType)
                && (item.State == "queued"
                    || item.State == "retry_waiting"
                    || item.State == "leased"
                    || item.State == "blocked"
                    || (item.State == "failed"
                        && item.UpdatedAt >= recentCutoff)))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Queued = group.Count(item => item.State == "queued"),
                RetryWaiting = group.Count(item =>
                    item.State == "retry_waiting"),
                Leased = group.Count(item => item.State == "leased"),
                Blocked = group.Count(item => item.State == "blocked"),
                RecentFailed = group.Count(item =>
                    item.State == "failed"
                    && item.UpdatedAt >= recentCutoff),
                Stale = group.Count(item =>
                    (item.State == "queued"
                        && item.NextAttemptAt < staleCutoff)
                    || (item.State == "leased"
                        && item.LeaseExpiresAt < staleCutoff)),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var activeBatches = batches?.Active ?? 0;
        var manualReviewBatches = batches?.ManualReview ?? 0;
        var reconciliationBatches = batches?.Reconciliation ?? 0;
        var delayedBatches = batches?.Delayed ?? 0;
        var possibleDuplicateBatches = batches?.PossibleDuplicate ?? 0;
        var staleBatches = batches?.Stale ?? 0;
        var recentFailedBatches = batches?.RecentFailed ?? 0;
        var queuedJobs = jobs?.Queued ?? 0;
        var retryWaitingJobs = jobs?.RetryWaiting ?? 0;
        var leasedJobs = jobs?.Leased ?? 0;
        var blockedJobs = jobs?.Blocked ?? 0;
        var recentFailedJobs = jobs?.RecentFailed ?? 0;
        var staleJobs = jobs?.Stale ?? 0;

        var unavailable = manualReviewBatches > 0
            || possibleDuplicateBatches > 0
            || blockedJobs > 0;
        var degraded = reconciliationBatches > 0
            || delayedBatches > 0
            || staleBatches > 0
            || retryWaitingJobs > 0
            || recentFailedBatches > 0
            || recentFailedJobs > 0
            || staleJobs > 0;
        var state = unavailable
            ? "unavailable"
            : degraded
                ? "degraded"
                : "healthy";
        var detail = unavailable
            ? $"手動確認 {manualReviewBatches} 件、重複可能性 {possibleDuplicateBatches} 件、停止ジョブ {blockedJobs} 件。"
            : degraded
                ? $"再照合 {reconciliationBatches} 件、遅延 {delayedBatches} 件、再試行 {retryWaitingJobs} 件、直近24時間の失敗 {recentFailedBatches + recentFailedJobs} 件。"
                : activeBatches + queuedJobs + leasedJobs > 0
                    ? $"処理中バッチ {activeBatches} 件、待機または実行中ジョブ {queuedJobs + leasedJobs} 件。"
                    : "一括処理キューは正常です。";

        return new GeminiBatchHealth(
            state,
            detail,
            unavailable
                ? "gemini_batch_manual_attention_required"
                : degraded
                    ? "gemini_batch_degraded"
                    : null,
            activeBatches,
            manualReviewBatches,
            reconciliationBatches,
            delayedBatches,
            possibleDuplicateBatches,
            staleBatches,
            recentFailedBatches,
            queuedJobs,
            retryWaitingJobs,
            leasedJobs,
            blockedJobs,
            recentFailedJobs,
            staleJobs);
    }

    private static object GeminiBatchComponent(
        GeminiBatchHealth health,
        DateTimeOffset checkedAt) =>
        new
        {
            name = "geminiBatch",
            displayName = "Gemini 一括処理",
            health.State,
            health.Detail,
            checkedAt,
            health.ErrorCode,
            metrics = new
            {
                health.ActiveBatches,
                health.ManualReviewBatches,
                health.ReconciliationBatches,
                health.DelayedBatches,
                health.PossibleDuplicateBatches,
                health.StaleBatches,
                health.RecentFailedBatches,
                health.QueuedJobs,
                health.RetryWaitingJobs,
                health.LeasedJobs,
                health.BlockedJobs,
                health.RecentFailedJobs,
                health.StaleJobs,
                recentFailureWindowHours = 24,
                staleAfterMinutes = 30,
            },
        };

    internal sealed record GeminiBatchHealth(
        string State,
        string Detail,
        string? ErrorCode,
        int ActiveBatches,
        int ManualReviewBatches,
        int ReconciliationBatches,
        int DelayedBatches,
        int PossibleDuplicateBatches,
        int StaleBatches,
        int RecentFailedBatches,
        int QueuedJobs,
        int RetryWaitingJobs,
        int LeasedJobs,
        int BlockedJobs,
        int RecentFailedJobs,
        int StaleJobs);

    private static object Component(
        string name,
        string displayName,
        string state,
        string? detail,
        DateTimeOffset checkedAt,
        string? errorCode = null) =>
        new { name, displayName, state, detail, checkedAt, errorCode };

    private static async Task<DateTimeOffset?> LastRetentionRunAsync(
        OokiGraderDbContext database,
        CancellationToken cancellationToken) =>
        await database.AuditEvents
            .AsNoTracking()
            .Where(item =>
                item.EventType == "retention.run.completed"
                || item.EventType == "retention.run.requested")
            .MaxAsync(item => (DateTimeOffset?)item.OccurredAt, cancellationToken);

    private static (long? FreeBytes, long? TotalBytes) ReadDriveMetrics(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return (null, null);
            }

            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return (null, null);
        }
    }

    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    })
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTimeOffset NextCleanupAt(string timeZoneId)
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
            var nextLocal = new DateTime(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                3,
                0,
                0,
                DateTimeKind.Unspecified);
            if (localNow.TimeOfDay >= TimeSpan.FromHours(3))
            {
                nextLocal = nextLocal.AddDays(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(nextLocal, timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(3);
        }
    }

    private sealed record SiteSettingsPatch(
        string? SchoolName,
        string? TimeZone,
        long? Revision);
}
