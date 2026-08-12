using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Api;

public static class BulkTranscriptExportEndpoints
{
    public const string CreateRateLimitPolicy = "bulk-transcript-export-create";
    internal const int MaximumActiveExportsPerActor = 2;
    internal const int MaximumActiveExportsPerSite = 4;
    internal const int ActiveLimitRetryAfterSeconds = 60;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static IEndpointRouteBuilder MapBulkTranscriptExportEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/transcript-exports:preview",
                Preview)
            .WithTags("Reports")
            .RequireAuthorization("results")
            .RequireRateLimiting("search")
            .AllowNonIdempotentMutation();
        endpoints.MapPost(
                "/api/v1/transcript-exports",
                Create)
            .WithTags("Reports")
            .RequireAuthorization("teacher")
            .RequireRateLimiting(CreateRateLimitPolicy)
            .RequireIdempotency();
        endpoints.MapGet(
                "/api/v1/transcript-exports/{exportId}",
                GetStatus)
            .WithTags("Reports")
            .RequireAuthorization("results")
            .RequireRateLimiting("search");
        endpoints.MapGet(
                "/api/v1/transcript-exports/{exportId}/file",
                Download)
            .WithTags("Reports")
            .RequireAuthorization("results")
            .RequireRateLimiting("search");
        return endpoints;
    }

    private static async Task<IResult> Preview(
        HttpContext context,
        [FromBody] BulkTranscriptExportPreviewRequest request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        try
        {
            var selection = await BulkTranscriptSelectionResolver.ResolveAsync(
                    db,
                    context,
                    request.Selector,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new
            {
                normalizedSelector = JsonSerializer.Deserialize<JsonElement>(
                    selection.NormalizedSelectorJson),
                selection.StudentCount,
                resultCount = selection.Candidates.Count,
                selection.SourceFingerprint,
                limits = new
                {
                    students = BulkTranscriptSelectionResolver.MaximumStudents,
                    results = BulkTranscriptSelectionResolver.MaximumResults,
                    archiveBytes = BulkTranscriptExportJobWorker.MaximumArchiveBytes,
                },
            });
        }
        catch (BulkTranscriptSelectionException exception)
        {
            return SelectionProblem(context, exception);
        }
    }

    private static async Task<IResult> Create(
        ClaimsPrincipal principal,
        HttpContext context,
        [FromBody] BulkTranscriptExportCreateRequest request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IWriteCoordinator writeCoordinator,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        if (!IsSha256(request.SourceFingerprint))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "BULK_EXPORT_SOURCE_FINGERPRINT_INVALID",
                "一括出力を開始できません",
                "最新の件数確認をやり直してください。");
        }

        var actorId = ApiHelpers.StaffId(principal);
        var idempotencyKey = context.Request.Headers["Idempotency-Key"]
            .SingleOrDefault()?.Trim();
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status400BadRequest,
                "IDEMPOTENCY_KEY_REQUIRED",
                "一括出力を開始できません",
                "この操作にはIdempotency-Keyが必要です。");
        }

        var requestFingerprint = ComputeRequestFingerprint(request);
        var replay = await writeCoordinator.ExecuteAsync<IResult?>(async token =>
        {
            var existing = await db.BulkTranscriptExports
                .Include(item => item.BackgroundJob)
                .SingleOrDefaultAsync(item =>
                    item.CreatedByStaffUserId == actorId
                    && item.RequestIdempotencyKey == idempotencyKey,
                    token)
                .ConfigureAwait(false);
            if (existing is null)
            {
                var activeSiteCount = await db.BulkTranscriptExports.CountAsync(
                        item => item.State == "queued"
                            || item.State == "rendering",
                        token)
                    .ConfigureAwait(false);
                var activeActorCount = await db.BulkTranscriptExports.CountAsync(
                        item => item.CreatedByStaffUserId == actorId
                            && (item.State == "queued"
                                || item.State == "rendering"),
                        token)
                    .ConfigureAwait(false);
                return activeActorCount >= MaximumActiveExportsPerActor
                    || activeSiteCount >= MaximumActiveExportsPerSite
                    ? ActiveLimitProblem(
                        context,
                        activeActorCount,
                        activeSiteCount)
                    : null;
            }

            await EnsureVerifiedSnapshotCurrentAsync(
                    db,
                    context,
                    principal,
                    existing,
                    timeProvider,
                    "create_replay",
                    token)
                .ConfigureAwait(false);
            return DomainReplay(
                context,
                existing,
                requestFingerprint);
        }, cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        BulkTranscriptSelection selection;
        try
        {
            selection = await BulkTranscriptSelectionResolver.ResolveAsync(
                    db,
                    context,
                    request.Selector,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BulkTranscriptSelectionException exception)
        {
            return SelectionProblem(context, exception);
        }

        if (!string.Equals(
                selection.SourceFingerprint,
                request.SourceFingerprint,
                StringComparison.Ordinal))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "BULK_EXPORT_SOURCE_SNAPSHOT_STALE",
                "対象の確定結果が更新されています",
                "件数と対象をもう一度確認してから一括出力してください。");
        }

        var now = timeProvider.GetUtcNow();
        var exportId = UlidId.New(now);
        var frozenSources = new List<FrozenBulkResultSource>(
            selection.Candidates.Count);
        for (var index = 0; index < selection.Candidates.Count; index++)
        {
            var candidate = selection.Candidates[index];
            ResultReportSource source;
            try
            {
                source = await ResultReportSourceLoader.LoadAsync(
                        db,
                        candidate.SubmissionId,
                        BuildChildReportId(exportId, index),
                        now,
                        includeTeacherComments: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ResultReportSourceException exception)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    exception.ErrorCode.ToUpperInvariant(),
                    "一括出力を開始できません",
                    exception.SafeDetail);
            }

            if (!Matches(candidate, source))
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status412PreconditionFailed,
                    "BULK_EXPORT_SOURCE_SNAPSHOT_STALE",
                    "対象の確定結果が更新されています",
                    "件数と対象をもう一度確認してから一括出力してください。");
            }

            frozenSources.Add(new FrozenBulkResultSource(
                index + 1,
                candidate.SubmissionId,
                candidate.SubmissionRevision,
                candidate.StudentId,
                candidate.StudentRevision,
                candidate.TestSessionId,
                candidate.TestSessionRevision,
                candidate.GradingRunId,
                candidate.ResultSourceRevision,
                candidate.TemplateVersionId,
                candidate.TemplateVersionNumber,
                candidate.TemplateVersionRevision,
                candidate.TestTemplateId,
                candidate.TestTemplateRevision,
                source.SourceHash));
        }

        // Source loading is intentionally outside the render worker so the
        // queued job owns an exact immutable lineage. Re-resolve once after
        // that bounded read to close the window where filter membership,
        // identity, or result revisions could drift during request creation.
        BulkTranscriptSelection confirmedSelection;
        try
        {
            confirmedSelection = await BulkTranscriptSelectionResolver.ResolveAsync(
                    db,
                    context,
                    request.Selector,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BulkTranscriptSelectionException exception)
        {
            return SelectionProblem(context, exception);
        }

        if (!string.Equals(
                confirmedSelection.SourceFingerprint,
                selection.SourceFingerprint,
                StringComparison.Ordinal)
            || !confirmedSelection.Candidates
                .Select(item => item.SubmissionId)
                .SequenceEqual(
                    selection.Candidates.Select(item => item.SubmissionId),
                    StringComparer.Ordinal))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "BULK_EXPORT_SOURCE_SNAPSHOT_STALE",
                "対象の確定結果が更新されています",
                "件数と対象をもう一度確認してから一括出力してください。");
        }

        return await writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);

            // Recheck inside the same write transaction that creates the
            // export. This is the crash-safe domain replay path when the
            // generic HTTP replay record was never persisted.
            var existing = await db.BulkTranscriptExports
                .Include(item => item.BackgroundJob)
                .SingleOrDefaultAsync(item =>
                    item.CreatedByStaffUserId == actorId
                    && item.RequestIdempotencyKey == idempotencyKey,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return DomainReplay(context, existing, requestFingerprint);
            }

            var activeSiteCount = await db.BulkTranscriptExports.CountAsync(
                    item => item.State == "queued"
                        || item.State == "rendering",
                    token)
                .ConfigureAwait(false);
            var activeActorCount = await db.BulkTranscriptExports.CountAsync(
                    item => item.CreatedByStaffUserId == actorId
                        && (item.State == "queued"
                            || item.State == "rendering"),
                    token)
                .ConfigureAwait(false);
            if (activeActorCount >= MaximumActiveExportsPerActor
                || activeSiteCount >= MaximumActiveExportsPerSite)
            {
                return ActiveLimitProblem(
                    context,
                    activeActorCount,
                    activeSiteCount);
            }

            var jobId = UlidId.New(now.AddMilliseconds(1));
            var record = new BulkTranscriptExportEntity
            {
                Id = exportId,
                BackgroundJobId = jobId,
                RequestIdempotencyKey = idempotencyKey,
                RequestFingerprint = requestFingerprint,
                SelectorJson = selection.NormalizedSelectorJson,
                SelectorHash = selection.SelectorHash,
                SourceSnapshotJson = JsonSerializer.Serialize(
                    frozenSources,
                    SnapshotJsonOptions),
                SourceFingerprint = selection.SourceFingerprint,
                RendererVersion = ResultPdfRenderer.CurrentRendererVersion,
                PackageFormatVersion =
                    BulkTranscriptExportJobWorker.PackageFormatVersion,
                State = "queued",
                StudentCount = selection.StudentCount,
                ResultCount = frozenSources.Count,
                ProcessedResultCount = 0,
                CreatedByStaffUserId = actorId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BulkTranscriptExports.Add(record);
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = BulkTranscriptExportJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey =
                    $"bulk-transcript:{record.Id}:{record.SourceFingerprint}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    exportId = record.Id,
                }),
                State = "queued",
                MaxAttempts = 5,
                NextAttemptAt = now,
                CorrelationId = context.TraceIdentifier,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now.AddMilliseconds(2)),
                OccurredAt = now,
                ActorStaffUserId = actorId,
                EventType = "bulk_transcript_export.requested",
                ObjectType = "bulk_transcript_export",
                ObjectId = record.Id,
                Outcome = "succeeded",
                ReasonCode = "teacher_requested",
                CorrelationId = context.TraceIdentifier,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    record.StudentCount,
                    record.ResultCount,
                    record.SelectorHash,
                    record.SourceFingerprint,
                    record.PackageFormatVersion,
                }),
            });
            AddStatusOutbox(
                db,
                now,
                context.TraceIdentifier,
                record.Id,
                record.State);

            try
            {
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                db.ChangeTracker.Clear();
                var raced = await db.BulkTranscriptExports
                    .AsNoTracking()
                    .Include(item => item.BackgroundJob)
                    .SingleOrDefaultAsync(item =>
                        item.CreatedByStaffUserId == actorId
                        && item.RequestIdempotencyKey == idempotencyKey,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                return DomainReplay(context, raced, requestFingerprint);
            }

            return Results.Accepted(
                $"/api/v1/transcript-exports/{record.Id}",
                ToStatus(record, progressBasisPoints: 0));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> GetStatus(
        string exportId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IWriteCoordinator writeCoordinator,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        return await writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            var record = await db.BulkTranscriptExports
                .Include(item => item.BackgroundJob)
                .SingleOrDefaultAsync(item => item.Id == exportId, token)
                .ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }

            await EnsureVerifiedSnapshotCurrentAsync(
                    db,
                    context,
                    principal,
                    record,
                    timeProvider,
                    "status",
                    token)
                .ConfigureAwait(false);
            return Results.Ok(ToStatus(
                record,
                record.BackgroundJob.ProgressBasisPoints));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> Download(
        string exportId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        IContentStore contentStore,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IWriteCoordinator writeCoordinator,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        var file = await writeCoordinator.ExecuteAsync(async token =>
        {
            var record = await db.BulkTranscriptExports
                .Include(item => item.FileReference)
                    .ThenInclude(item => item!.FileObject)
                .SingleOrDefaultAsync(item => item.Id == exportId, token)
                .ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            await EnsureVerifiedSnapshotCurrentAsync(
                    db,
                    context,
                    principal,
                    record,
                    timeProvider,
                    "download",
                    token)
                .ConfigureAwait(false);
            return new BulkTranscriptDownloadState(
                record,
                record.FileReference,
                record.FileReference?.FileObject);
        }, cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            return Results.NotFound();
        }

        if (file.Record.State == "superseded")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "BULK_EXPORT_SUPERSEDED",
                "一括PDFの対象結果が更新されています",
                "件数確認からやり直し、現在の確定結果で一括PDFを再作成してください。");
        }

        if (file.Record.State != "verified"
            || file.Record.FileReferenceId is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "BULK_EXPORT_NOT_READY",
                "一括PDFはまだダウンロードできません",
                file.Record.State is "failed" or "superseded"
                    ? "一括出力に失敗したか、対象結果が更新されました。件数確認からやり直してください。"
                    : "作成が完了するまでお待ちください。");
        }

        var fileObject = file.FileObject;
        if (fileObject is null
            || file.FileReference?.Id != file.Record.FileReferenceId
            || fileObject.State is "deleted" or "missing")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "BULK_EXPORT_FILE_GONE",
                "一括PDFの保存期間が終了しました",
                "必要な場合は帳票画面からもう一度作成してください。");
        }

        if (fileObject.State != "available"
            || fileObject.StorageClass
                != ContentStorageClass.ResultReport.ToString()
            || fileObject.Sha256 != file.Record.Sha256
            || fileObject.Bytes != file.Record.Bytes
            || fileObject.Extension != "zip")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "BULK_EXPORT_FILE_UNAVAILABLE",
                "一括PDFを確認できません",
                "管理者に保存領域の確認を依頼してください。");
        }

        var locator = new ContentObjectLocator(
            ContentStorageClass.ResultReport,
            fileObject.Sha256,
            fileObject.Bytes,
            fileObject.Extension);
        Stream stream;
        try
        {
            stream = await contentStore.OpenReadAsync(locator, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "BULK_EXPORT_FILE_GONE",
                "一括PDFを保存領域で確認できません",
                "必要な場合は帳票画面からもう一度作成してください。");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            "application/zip",
            $"{file.Record.CreatedAt:yyyyMMdd-HHmmss}_生徒別結果PDF.zip",
            file.Record.CompletedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static object ToStatus(
        BulkTranscriptExportEntity record,
        int progressBasisPoints) => new
    {
        id = record.Id,
        record.State,
        progressBasisPoints = Math.Clamp(progressBasisPoints, 0, 10_000),
        record.ProcessedResultCount,
        record.StudentCount,
        record.ResultCount,
        record.SourceFingerprint,
        record.SelectorHash,
        normalizedSelector = JsonSerializer.Deserialize<JsonElement>(
            record.SelectorJson),
        record.RendererVersion,
        record.PackageFormatVersion,
        record.Sha256,
        record.Bytes,
        record.ErrorCode,
        record.SafeErrorDetail,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt,
        superseded = record.SupersededAt is not null
            || record.State == "superseded",
        record.SupersededAt,
        record.SupersededReason,
        fileUrl = record.State == "verified"
            ? $"/api/v1/transcript-exports/{record.Id}/file"
            : null,
    };

    private static IResult SelectionProblem(
        HttpContext context,
        BulkTranscriptSelectionException exception)
    {
        IReadOnlyList<object>? errors = exception.InvalidSubmissionIds is not null
            ? exception.InvalidSubmissionIds
                .Select(id => (object)new { submissionId = id })
                .ToArray()
            : exception.NonExportableResultCount is int count
                ? [new { code = "non_exportable_result_count", count }]
                : null;
        return ApiHelpers.Problem(
            context,
            exception.StatusCode,
            exception.ErrorCode.ToUpperInvariant(),
            "一括出力の対象を確認できません",
            exception.SafeDetail,
            errors);
    }

    private static bool Matches(
        BulkTranscriptCandidate candidate,
        ResultReportSource source) =>
        candidate.SubmissionId == source.SubmissionId
        && candidate.SubmissionRevision == source.SubmissionRevision
        && candidate.GradingRunId == source.GradingRunId
        && candidate.ResultSourceRevision == source.ResultSourceRevision
        && candidate.TemplateVersionId == source.TemplateVersionId
        && candidate.TemplateVersionNumber == source.TemplateVersionNumber;

    private static async Task EnsureVerifiedSnapshotCurrentAsync(
        OokiGraderDbContext db,
        HttpContext context,
        ClaimsPrincipal principal,
        BulkTranscriptExportEntity record,
        TimeProvider timeProvider,
        string detectedBy,
        CancellationToken cancellationToken)
    {
        if (record.State != "verified"
            || await SnapshotIsCurrentAsync(
                    db,
                    context,
                    record,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        // Callers hold the process-wide write coordinator. The state guard
        // makes the audit and outbox transition exactly once on this host;
        // the entity revision remains a cross-process concurrency backstop.
        if (record.State != "verified")
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        record.State = "superseded";
        record.ErrorCode = "bulk_export_source_changed";
        record.SafeErrorDetail =
            "対象の確定結果が更新されたため、現在の一括PDFを配布できません。";
        record.SupersededAt = now;
        record.SupersededReason = "source_changed_after_completion";
        record.UpdatedAt = now;
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "bulk_transcript_export.superseded",
            ObjectType = "bulk_transcript_export",
            ObjectId = record.Id,
            Outcome = "succeeded",
            ReasonCode = "source_changed_after_completion",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                record.SelectorHash,
                record.SourceFingerprint,
                detectedBy,
            }),
        });
        AddStatusOutbox(
            db,
            now,
            context.TraceIdentifier,
            record.Id,
            record.State);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SnapshotIsCurrentAsync(
        OokiGraderDbContext db,
        HttpContext context,
        BulkTranscriptExportEntity record,
        CancellationToken cancellationToken)
    {
        BulkTranscriptExportSelector? selector;
        try
        {
            selector = JsonSerializer.Deserialize<BulkTranscriptExportSelector>(
                record.SelectorJson,
                SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (selector is null)
        {
            return false;
        }

        BulkTranscriptSelection current;
        try
        {
            current = await BulkTranscriptSelectionResolver.ResolveAsync(
                    db,
                    context,
                    selector,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (BulkTranscriptSelectionException)
        {
            return false;
        }

        return current.Candidates.Count == record.ResultCount
            && SecureEquals(current.SelectorHash, record.SelectorHash)
            && SecureEquals(
                current.SourceFingerprint,
                record.SourceFingerprint);
    }

    private static IResult DomainReplay(
        HttpContext context,
        BulkTranscriptExportEntity record,
        string requestFingerprint)
    {
        if (!SecureEquals(record.RequestFingerprint, requestFingerprint))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_KEY_REUSED",
                "一括出力を開始できません",
                "同じIdempotency-Keyが異なるリクエストに使用されました。");
        }

        context.Response.Headers["Idempotency-Replayed"] = "true";
        return Results.Accepted(
            $"/api/v1/transcript-exports/{record.Id}",
            ToStatus(record, record.BackgroundJob.ProgressBasisPoints));
    }

    private static IResult ActiveLimitProblem(
        HttpContext context,
        int activeActorCount,
        int activeSiteCount)
    {
        context.Response.Headers.RetryAfter =
            ActiveLimitRetryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "https://ooki-grader.local/problems/bulk-export-active-limit-reached",
            title: "一括出力の待機件数が上限に達しています",
            detail: "進行中の一括出力が完了してから再試行してください。",
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "BULK_EXPORT_ACTIVE_LIMIT_REACHED",
                ["correlationId"] = context.TraceIdentifier,
                ["retryable"] = true,
                ["retryAfterSeconds"] = ActiveLimitRetryAfterSeconds,
                ["activeActorCount"] = activeActorCount,
                ["activeSiteCount"] = activeSiteCount,
                ["actorLimit"] = MaximumActiveExportsPerActor,
                ["siteLimit"] = MaximumActiveExportsPerSite,
            });
    }

    private static string ComputeRequestFingerprint(
        BulkTranscriptExportCreateRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                request.SourceFingerprint,
                request.Selector,
            },
            SnapshotJsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static bool SecureEquals(string? left, string? right)
    {
        if (left is null
            || right is null
            || left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }

    internal static string BuildChildReportId(string exportId, int zeroBasedIndex) =>
        $"{exportId}-{zeroBasedIndex + 1:D4}";

    internal static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string exportId,
        string state)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "bulk_transcript_export",
            AggregateId = exportId,
            EventType = "bulk_transcript_export.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new { exportId, state }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool ReportsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("Features:Reports.Pdf");

    private sealed record BulkTranscriptDownloadState(
        BulkTranscriptExportEntity Record,
        FileReferenceEntity? FileReference,
        FileObjectEntity? FileObject);
}
