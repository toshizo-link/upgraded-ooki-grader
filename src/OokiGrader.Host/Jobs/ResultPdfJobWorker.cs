using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Jobs;

public sealed partial class ResultPdfJobWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    IResultPdfRenderer renderer,
    TimeProvider timeProvider,
    ILogger<ResultPdfJobWorker> logger) : BackgroundService
{
    public const string JobType = "result_pdf.render";

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _workerId = $"result-pdf-{Guid.NewGuid():N}";

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        string? exportId = null;
        try
        {
            var payload = DeserializePayload(lease.PayloadJson);
            exportId = payload.ExportId;
            var preparation = await PrepareAsync(lease, payload, cancellationToken)
                .ConfigureAwait(false);
            if (preparation.AlreadyVerified)
            {
                await CompleteJobOnlyAsync(lease, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            await MarkRenderingAsync(lease, payload.ExportId, cancellationToken)
                .ConfigureAwait(false);
            var rendered = renderer.Render(preparation.Source!.Document);
            await using var source = new MemoryStream(
                rendered.PdfBytes,
                writable: false);
            var stored = await contentStore.PutAsync(
                    source,
                    ContentStorageClass.ResultReport,
                    "pdf",
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored.Locator.Sha256 != rendered.Sha256
                || stored.Locator.Bytes != rendered.PdfBytes.LongLength)
            {
                throw Permanent(
                    "export_artifact_hash_mismatch",
                    "The generated report could not be verified.");
            }

            await CompleteWithArtifactAsync(
                    lease,
                    preparation.Source,
                    rendered,
                    stored,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ResultPdfJobException exception)
        {
            LogJobFailure(lease.Id, exception.ErrorCode);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    exception.ErrorCode,
                    exception.SafeDetail,
                    exception.IsPermanent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ResultReportSourceException exception)
        {
            LogJobFailure(lease.Id, exception.ErrorCode);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    exception.ErrorCode,
                    exception.SafeDetail,
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            LogUnexpectedFailure(exception, lease.Id);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    "export_storage_unavailable",
                    "The report storage is temporarily unavailable.",
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            LogUnexpectedFailure(exception, lease.Id);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    "export_storage_access_denied",
                    "The report storage could not be accessed.",
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, lease.Id);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    "export_render_failed",
                    "The result report could not be rendered.",
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item => item.Type == JobType
                    && item.AttemptCount < item.MaxAttempts
                    && ((item.State == "queued" && item.NextAttemptAt <= now)
                        || (item.State == "retry_waiting"
                            && item.NextAttemptAt <= now)
                        || (item.State == "leased"
                            && item.LeaseExpiresAt <= now)))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            job.State = "leased";
            job.LeaseOwner = _workerId;
            job.LeaseExpiresAt = now.Add(LeaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.SchemaVersion,
                job.PayloadJson,
                job.Revision,
                job.CorrelationId);
        }, cancellationToken);
    }

    private async Task<ReportPreparation> PrepareAsync(
        JobLease lease,
        JobPayload payload,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await db.Set<ExportRecordEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == payload.ExportId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent(
                "export_record_missing",
                "The report request no longer exists.");
        if (record.BackgroundJobId != lease.Id
            || record.Type != "result_pdf")
        {
            throw Permanent(
                "export_job_mismatch",
                "The report request does not match its render job.");
        }

        if (record.State == "verified"
            && record.FileReferenceId is not null
            && record.Sha256 is not null
            && record.Bytes is >= 0
            && record.PageCount is > 0)
        {
            return new ReportPreparation(AlreadyVerified: true, Source: null);
        }

        if (record.RendererVersion != ResultPdfRenderer.CurrentRendererVersion)
        {
            throw Permanent(
                "export_renderer_version_mismatch",
                "The report was queued for a different renderer version.");
        }

        var source = await ResultReportSourceLoader.LoadAsync(
                db,
                record.SubmissionId,
                record.Id,
                record.CreatedAt,
                includeTeacherComments: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (source.GradingRunId != record.GradingRunId
            || source.ResultSourceRevision != record.ResultSourceRevision
            || source.TemplateVersionId != record.TemplateVersionId
            || source.TemplateVersionNumber != record.TemplateVersionNumber
            || source.SourceHash != record.SourceHash)
        {
            throw Permanent(
                "export_source_changed",
                "The finalized result changed before this report was rendered.");
        }

        return new ReportPreparation(AlreadyVerified: false, source);
    }

    private Task MarkRenderingAsync(
        JobLease lease,
        string exportId,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            _ = await LoadOwnedJobAsync(db, lease, token).ConfigureAwait(false);
            var record = await db.Set<ExportRecordEntity>()
                .SingleAsync(item => item.Id == exportId, token)
                .ConfigureAwait(false);
            if (record.State == "verified")
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            record.State = "rendering";
            record.StartedAt ??= now;
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            record.UpdatedAt = now;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task CompleteWithArtifactAsync(
        JobLease lease,
        ResultReportSource source,
        ResultPdfRenderResult rendered,
        ContentWriteResult stored,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            var record = await db.Set<ExportRecordEntity>()
                .SingleAsync(item => item.Id == source.Document.ReportId, token)
                .ConfigureAwait(false);
            if (record.State == "verified")
            {
                CompleteJob(job, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (record.SourceHash != source.SourceHash
                || record.RendererVersion != rendered.RendererVersion)
            {
                throw Permanent(
                    "export_completion_mismatch",
                    "The rendered report no longer matches its request.");
            }

            var now = timeProvider.GetUtcNow();
            var fileObject = await db.FileObjects
                .SingleOrDefaultAsync(
                    item => item.StorageClass
                            == ContentStorageClass.ResultReport.ToString()
                        && item.Sha256 == stored.Locator.Sha256,
                    token)
                .ConfigureAwait(false);
            if (fileObject is null)
            {
                fileObject = new FileObjectEntity
                {
                    Id = UlidId.New(now),
                    Sha256 = stored.Locator.Sha256,
                    Bytes = stored.Locator.Bytes,
                    VerifiedMime = "application/pdf",
                    Extension = stored.Locator.Extension,
                    RelativeObjectPath = stored.RelativePath,
                    StorageClass = ContentStorageClass.ResultReport.ToString(),
                    RetentionClass = "result_report",
                    ManagedScanBytes = false,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 0,
                };
                db.FileObjects.Add(fileObject);
            }
            else
            {
                if (fileObject.Bytes != stored.Locator.Bytes
                    || fileObject.Extension != stored.Locator.Extension
                    || fileObject.RelativeObjectPath != stored.RelativePath)
                {
                    throw Permanent(
                        "export_file_object_conflict",
                        "The report artifact metadata is inconsistent.");
                }

                fileObject.State = "available";
                fileObject.VerifiedAt = now;
                fileObject.DeletedAt = null;
            }

            var fileReference = await db.FileReferences
                .SingleOrDefaultAsync(
                    item => item.OwnerType == "export_record"
                        && item.OwnerId == record.Id
                        && item.Purpose == "result_pdf",
                    token)
                .ConfigureAwait(false);
            if (fileReference is null)
            {
                fileReference = new FileReferenceEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(1)),
                    FileObjectId = fileObject.Id,
                    OwnerType = "export_record",
                    OwnerId = record.Id,
                    Purpose = "result_pdf",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                };
                db.FileReferences.Add(fileReference);
                fileObject.ReferenceCountCache =
                    checked(fileObject.ReferenceCountCache + 1);
            }
            else if (fileReference.FileObjectId != fileObject.Id)
            {
                throw Permanent(
                    "export_file_reference_conflict",
                    "The report artifact reference is inconsistent.");
            }

            record.FileReferenceId = fileReference.Id;
            record.Sha256 = rendered.Sha256;
            record.Bytes = rendered.PdfBytes.LongLength;
            record.PageCount = rendered.PageCount;
            record.State = "verified";
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            record.CompletedAt = now;
            record.UpdatedAt = now;
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now.AddMilliseconds(2)),
                OccurredAt = now,
                ActorStaffUserId = record.CreatedByStaffUserId,
                EventType = "export.completed",
                ObjectType = "export_record",
                ObjectId = record.Id,
                Outcome = "succeeded",
                ReasonCode = "render_verified",
                CorrelationId = lease.CorrelationId,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    record.ResultSourceRevision,
                    rendered.PageCount,
                    bytes = rendered.PdfBytes.LongLength,
                    rendered.Sha256,
                    rendered.RendererVersion,
                }),
            });
            AddStatusOutbox(
                db,
                now,
                lease.CorrelationId,
                record.Id,
                record.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task CompleteJobOnlyAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            CompleteJob(job, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordFailureAsync(
        JobLease lease,
        string? exportId,
        string errorCode,
        string safeDetail,
        bool isPermanent,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == lease.Id, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId
                || job.Revision != lease.Revision)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            var terminal = isPermanent || job.AttemptCount >= job.MaxAttempts;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = errorCode;
            job.SafeErrorDetail = safeDetail;
            job.State = terminal ? "failed" : "retry_waiting";
            job.NextAttemptAt = terminal
                ? now
                : now.Add(RetryDelay(job.AttemptCount));
            job.CompletedAt = terminal ? now : null;

            if (exportId is not null)
            {
                var record = await db.Set<ExportRecordEntity>()
                    .SingleOrDefaultAsync(item => item.Id == exportId, token)
                    .ConfigureAwait(false);
                if (record is not null && record.State != "verified")
                {
                    record.State = terminal ? "failed" : "queued";
                    record.ErrorCode = errorCode;
                    record.SafeErrorDetail = safeDetail;
                    record.UpdatedAt = now;
                    if (errorCode == "export_source_changed")
                    {
                        record.SupersededAt = now;
                        record.SupersededReason = "source_changed_before_render";
                    }

                    AddStatusOutbox(
                        db,
                        now,
                        lease.CorrelationId,
                        record.Id,
                        record.State);
                }
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == lease.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("export_job_missing", "The render job no longer exists.");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= timeProvider.GetUtcNow())
        {
            throw Permanent(
                "export_job_lease_lost",
                "The report render lease is no longer current.");
        }

        if (job.SchemaVersion != 1 || lease.SchemaVersion != 1)
        {
            throw Permanent(
                "export_job_schema_unsupported",
                "The report render job version is unsupported.");
        }

        return job;
    }

    private static JobPayload DeserializePayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JobPayload>(
                json,
                PayloadOptions);
            return string.IsNullOrWhiteSpace(payload?.ExportId)
                ? throw Permanent(
                    "export_job_payload_invalid",
                    "The report render job payload is invalid.")
                : payload;
        }
        catch (JsonException)
        {
            throw Permanent(
                "export_job_payload_invalid",
                "The report render job payload is invalid.");
        }
    }

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset completedAt)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.CompletedAt = completedAt;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string exportId,
        string state)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "export_record",
            AggregateId = exportId,
            EventType = "export.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new { exportId, state }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static TimeSpan RetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30),
    };

    private static ResultPdfJobException Permanent(
        string errorCode,
        string safeDetail) =>
        new(errorCode, safeDetail, isPermanent: true);

    [LoggerMessage(
        EventId = 7_401,
        Level = LogLevel.Warning,
        Message = "Result PDF job {JobId} failed with {ErrorCode}.")]
    private partial void LogJobFailure(string jobId, string errorCode);

    [LoggerMessage(
        EventId = 7_402,
        Level = LogLevel.Error,
        Message = "Result PDF job {JobId} failed unexpectedly.")]
    private partial void LogUnexpectedFailure(Exception exception, string jobId);

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        long Revision,
        string? CorrelationId);

    private sealed record JobPayload(string ExportId);

    private sealed record ReportPreparation(
        bool AlreadyVerified,
        ResultReportSource? Source);

    private sealed class ResultPdfJobException(
        string errorCode,
        string safeDetail,
        bool isPermanent) : Exception(safeDetail)
    {
        public string ErrorCode { get; } = errorCode;
        public string SafeDetail { get; } = safeDetail;
        public bool IsPermanent { get; } = isPermanent;
    }
}
