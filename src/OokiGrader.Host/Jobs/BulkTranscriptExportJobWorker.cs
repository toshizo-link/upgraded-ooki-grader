using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Jobs;

public sealed partial class BulkTranscriptExportJobWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    IResultPdfRenderer renderer,
    TimeProvider timeProvider,
    ILogger<BulkTranscriptExportJobWorker> logger) : BackgroundService
{
    public const string JobType = "bulk_transcript_export.render";
    public const string PackageFormatVersion = "deterministic-result-zip-v1";
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;

    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TemporaryArchiveSweepInterval =
        TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _workerId = $"bulk-transcript-{Guid.NewGuid():N}";
    private long _nextTemporaryArchiveSweepUtcTicks;

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        TrySweepStaleTemporaryArchives();
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        string? exportId = null;
        string? temporaryPath = null;
        try
        {
            if (lease.RecoveryOnly)
            {
                await TerminalizeExhaustedLeaseAsync(lease, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

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

            await MarkRenderingAsync(lease, exportId, cancellationToken)
                .ConfigureAwait(false);
            temporaryPath = BulkTranscriptExportTemporaryFiles.CreatePath(
                lease.Id,
                lease.Revision);
            await using (var archive = new BulkTranscriptArchiveWriter(
                temporaryPath,
                MaximumArchiveBytes))
            {
                for (var index = 0; index < preparation.Sources.Count; index++)
                {
                    var frozen = preparation.Sources[index];
                    var source = await LoadAndValidateSourceAsync(
                            preparation.Record,
                            frozen,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var rendered = renderer.Render(source.Document);
                    await archive.AddAsync(
                            frozen,
                            source,
                            rendered,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await UpdateProgressAsync(
                            lease,
                            exportId,
                            index + 1,
                            preparation.Sources.Count,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                _ = await archive.CompleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            await RevalidateAllSourcesAsync(
                    preparation.Record,
                    preparation.Sources,
                    cancellationToken)
                .ConfigureAwait(false);
            await using var archiveStream = new FileStream(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await contentStore.PutAsync(
                    archiveStream,
                    ContentStorageClass.ResultReport,
                    "zip",
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored.Locator.Bytes <= 0
                || stored.Locator.Bytes > MaximumArchiveBytes)
            {
                throw Permanent(
                    "bulk_export_archive_size_invalid",
                    "The generated result archive exceeded its verified size limit.");
            }

            await CompleteWithArtifactAsync(
                    lease,
                    preparation,
                    stored,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BulkTranscriptJobException exception)
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
                    "bulk_export_source_changed",
                    exception.SafeDetail,
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            LogUnexpectedFailure(exception, lease.Id);
            await RecordFailureAsync(
                    lease,
                    exportId,
                    "bulk_export_archive_invalid",
                    "The result archive could not be verified safely.",
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
                    "bulk_export_storage_unavailable",
                    "The result archive storage is temporarily unavailable.",
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
                    "bulk_export_storage_access_denied",
                    "The result archive storage could not be accessed.",
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
                    "bulk_export_render_failed",
                    "The result archive could not be created.",
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // The host's temporary cleanup policy removes abandoned
                    // .part files; never replace the primary job outcome.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same as above.
                }
            }
        }

        return true;
    }

    private void TrySweepStaleTemporaryArchives()
    {
        var now = timeProvider.GetUtcNow();
        var currentDeadline = Volatile.Read(
            ref _nextTemporaryArchiveSweepUtcTicks);
        if (now.UtcTicks < currentDeadline)
        {
            return;
        }

        var nextDeadline = now.Add(TemporaryArchiveSweepInterval).UtcTicks;
        if (Interlocked.CompareExchange(
                ref _nextTemporaryArchiveSweepUtcTicks,
                nextDeadline,
                currentDeadline) != currentDeadline)
        {
            return;
        }

        try
        {
            var deleted = BulkTranscriptExportTemporaryFiles.SweepStale(now);
            if (deleted > 0)
            {
                LogStaleTemporaryArchivesDeleted(deleted);
            }
        }
        catch (Exception exception)
        {
            // Best-effort crash recovery must never prevent job processing.
            LogTemporaryArchiveCleanupFailure(exception);
        }
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
                    && item.State == "leased"
                    && item.LeaseExpiresAt <= now
                    && item.AttemptCount >= item.MaxAttempts)
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            var recoveryOnly = job is not null;
            job ??= await db.BackgroundJobs
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
            if (!recoveryOnly)
            {
                job.AttemptCount = checked(job.AttemptCount + 1);
            }
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
                job.CorrelationId,
                recoveryOnly);
        }, cancellationToken);
    }

    private Task TerminalizeExhaustedLeaseAsync(
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
            var now = timeProvider.GetUtcNow();
            const string errorCode = "bulk_export_retry_exhausted";
            const string safeDetail =
                "The previous result archive attempt ended before completion and the retry limit was reached.";
            job.State = "failed";
            job.CompletedAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = errorCode;
            job.SafeErrorDetail = safeDetail;
            var record = await db.BulkTranscriptExports
                .SingleOrDefaultAsync(
                    item => item.BackgroundJobId == job.Id,
                    token)
                .ConfigureAwait(false);
            if (record is not null && record.State != "verified")
            {
                record.State = "failed";
                record.ErrorCode = errorCode;
                record.SafeErrorDetail = safeDetail;
                record.UpdatedAt = now;
                BulkTranscriptExportEndpoints.AddStatusOutbox(
                    db,
                    now,
                    lease.CorrelationId,
                    record.Id,
                    record.State);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<Preparation> PrepareAsync(
        JobLease lease,
        JobPayload payload,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await db.BulkTranscriptExports
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == payload.ExportId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent(
                "bulk_export_record_missing",
                "The bulk result request no longer exists.");
        if (record.BackgroundJobId != lease.Id)
        {
            throw Permanent(
                "bulk_export_job_mismatch",
                "The bulk result request does not match its render job.");
        }

        if (record.State == "verified"
            && record.FileReferenceId is not null
            && record.Sha256 is not null
            && record.Bytes is > 0)
        {
            return new Preparation(record, [], AlreadyVerified: true);
        }

        if (record.State == "superseded"
            || record.RendererVersion != ResultPdfRenderer.CurrentRendererVersion
            || record.PackageFormatVersion != PackageFormatVersion)
        {
            throw Permanent(
                "bulk_export_version_mismatch",
                "The bulk result request is no longer renderable by this version.");
        }

        var sources = DeserializeSources(record.SourceSnapshotJson);
        if (sources.Count != record.ResultCount
            || sources.Count == 0
            || sources.Count > BulkTranscriptSelectionResolver.MaximumResults
            || sources.Select(item => item.StudentId)
                .Distinct(StringComparer.Ordinal)
                .Count() != record.StudentCount
            || sources.Select(item => item.Ordinal)
                .SequenceEqual(Enumerable.Range(1, sources.Count)) == false)
        {
            throw Permanent(
                "bulk_export_source_snapshot_invalid",
                "The bulk result source snapshot is invalid.");
        }

        await ValidateLineageAsync(db, sources, cancellationToken)
            .ConfigureAwait(false);
        return new Preparation(record, sources, AlreadyVerified: false);
    }

    private async Task<ResultReportSource> LoadAndValidateSourceAsync(
        BulkTranscriptExportEntity record,
        FrozenBulkResultSource frozen,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var source = await ResultReportSourceLoader.LoadAsync(
                db,
                frozen.SubmissionId,
                BulkTranscriptExportEndpoints.BuildChildReportId(
                    record.Id,
                    frozen.Ordinal - 1),
                record.CreatedAt,
                includeTeacherComments: false,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSourceMatches(frozen, source);
        return source;
    }

    private async Task RevalidateAllSourcesAsync(
        BulkTranscriptExportEntity record,
        IReadOnlyList<FrozenBulkResultSource> frozenSources,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        await ValidateLineageAsync(db, frozenSources, cancellationToken)
            .ConfigureAwait(false);
        foreach (var frozen in frozenSources)
        {
            var source = await ResultReportSourceLoader.LoadAsync(
                    db,
                    frozen.SubmissionId,
                    BulkTranscriptExportEndpoints.BuildChildReportId(
                        record.Id,
                        frozen.Ordinal - 1),
                    record.CreatedAt,
                    includeTeacherComments: false,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSourceMatches(frozen, source);
        }
    }

    private static async Task ValidateLineageAsync(
        OokiGraderDbContext db,
        IReadOnlyList<FrozenBulkResultSource> frozenSources,
        CancellationToken cancellationToken)
    {
        var ids = frozenSources.Select(item => item.SubmissionId).ToArray();
        var submissions = await db.Submissions
            .AsNoTracking()
            .Include(item => item.AssignedStudent)
            .Include(item => item.TestSession)
                .ThenInclude(item => item.TemplateVersion)
                    .ThenInclude(item => item.TestTemplate)
            .Include(item => item.GradingRuns)
            .Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        foreach (var frozen in frozenSources)
        {
            if (!submissions.TryGetValue(frozen.SubmissionId, out var submission)
                || submission.State != "finalized"
                || submission.FinalizedAt is null
                || submission.VoidedAt is not null
                || submission.AssignedStudentId != frozen.StudentId
                || submission.AssignedStudent is null
                || submission.Revision != frozen.SubmissionRevision
                || submission.AssignedStudent.Revision != frozen.StudentRevision
                || submission.TestSessionId != frozen.TestSessionId
                || submission.TestSession.Revision != frozen.TestSessionRevision
                || submission.CurrentGradingRunId != frozen.GradingRunId
                || submission.TestSession.TemplateVersionId
                    != frozen.TemplateVersionId
                || submission.TestSession.TemplateVersion.VersionNumber
                    != frozen.TemplateVersionNumber
                || submission.TestSession.TemplateVersion.Revision
                    != frozen.TemplateVersionRevision
                || submission.TestSession.TemplateVersion.TestTemplate.Id
                    != frozen.TestTemplateId
                || submission.TestSession.TemplateVersion.TestTemplate.Revision
                    != frozen.TestTemplateRevision)
            {
                throw Permanent(
                    "bulk_export_source_changed",
                    "A finalized result changed before the archive was completed.");
            }

            var run = submission.GradingRuns.SingleOrDefault(item =>
                item.Id == frozen.GradingRunId);
            if (run is null
                || run.State != "finalized"
                || run.TemplateVersionId != frozen.TemplateVersionId
                || run.ResultSourceRevision != frozen.ResultSourceRevision)
            {
                throw Permanent(
                    "bulk_export_source_changed",
                    "A finalized result changed before the archive was completed.");
            }
        }
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
            var record = await db.BulkTranscriptExports
                .SingleAsync(item => item.Id == exportId, token)
                .ConfigureAwait(false);
            if (record.State == "verified")
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            record.State = "rendering";
            record.ProcessedResultCount = 0;
            record.StartedAt ??= now;
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            record.UpdatedAt = now;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task UpdateProgressAsync(
        JobLease lease,
        string exportId,
        int processed,
        int total,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            var record = await db.BulkTranscriptExports
                .SingleAsync(item => item.Id == exportId, token)
                .ConfigureAwait(false);
            if (record.State == "verified")
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            record.ProcessedResultCount = processed;
            record.UpdatedAt = now;
            job.ProgressBasisPoints = Math.Clamp(
                (int)((long)processed * 9_500 / total),
                0,
                9_500);
            job.LeaseExpiresAt = now.Add(LeaseDuration);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            lease.Revision = job.Revision;
        }, cancellationToken);
    }

    private Task CompleteWithArtifactAsync(
        JobLease lease,
        Preparation preparation,
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
            var record = await db.BulkTranscriptExports
                .SingleAsync(item => item.Id == preparation.Record.Id, token)
                .ConfigureAwait(false);
            if (record.State == "verified")
            {
                CompleteJob(job, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (record.SourceFingerprint != preparation.Record.SourceFingerprint
                || record.SelectorHash != preparation.Record.SelectorHash
                || record.RendererVersion != ResultPdfRenderer.CurrentRendererVersion
                || record.PackageFormatVersion != PackageFormatVersion)
            {
                throw Permanent(
                    "bulk_export_completion_mismatch",
                    "The result archive no longer matches its request.");
            }

            await ValidateLineageAsync(db, preparation.Sources, token)
                .ConfigureAwait(false);
            foreach (var frozen in preparation.Sources)
            {
                var source = await ResultReportSourceLoader.LoadAsync(
                        db,
                        frozen.SubmissionId,
                        BulkTranscriptExportEndpoints.BuildChildReportId(
                            record.Id,
                            frozen.Ordinal - 1),
                        record.CreatedAt,
                        includeTeacherComments: false,
                        token)
                    .ConfigureAwait(false);
                EnsureSourceMatches(frozen, source);
            }

            var now = timeProvider.GetUtcNow();
            var fileObject = await db.FileObjects.SingleOrDefaultAsync(
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
                    VerifiedMime = "application/zip",
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
                    || fileObject.Extension != "zip"
                    || fileObject.RelativeObjectPath != stored.RelativePath)
                {
                    throw Permanent(
                        "bulk_export_file_object_conflict",
                        "The result archive metadata is inconsistent.");
                }

                fileObject.State = "available";
                fileObject.VerifiedAt = now;
                fileObject.DeletedAt = null;
            }

            var reference = await db.FileReferences.SingleOrDefaultAsync(
                    item => item.OwnerType == "bulk_transcript_export"
                        && item.OwnerId == record.Id
                        && item.Purpose == "bulk_result_zip",
                    token)
                .ConfigureAwait(false);
            if (reference is null)
            {
                reference = new FileReferenceEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(1)),
                    FileObjectId = fileObject.Id,
                    OwnerType = "bulk_transcript_export",
                    OwnerId = record.Id,
                    Purpose = "bulk_result_zip",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                };
                db.FileReferences.Add(reference);
                fileObject.ReferenceCountCache =
                    checked(fileObject.ReferenceCountCache + 1);
            }
            else if (reference.FileObjectId != fileObject.Id)
            {
                throw Permanent(
                    "bulk_export_file_reference_conflict",
                    "The result archive reference is inconsistent.");
            }

            record.FileReferenceId = reference.Id;
            record.Sha256 = stored.Locator.Sha256;
            record.Bytes = stored.Locator.Bytes;
            record.ProcessedResultCount = record.ResultCount;
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
                EventType = "bulk_transcript_export.completed",
                ObjectType = "bulk_transcript_export",
                ObjectId = record.Id,
                Outcome = "succeeded",
                ReasonCode = "archive_verified",
                CorrelationId = lease.CorrelationId,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    record.StudentCount,
                    record.ResultCount,
                    bytes = stored.Locator.Bytes,
                    sha256 = stored.Locator.Sha256,
                    record.SourceFingerprint,
                    record.PackageFormatVersion,
                }),
            });
            BulkTranscriptExportEndpoints.AddStatusOutbox(
                db,
                now,
                lease.CorrelationId,
                record.Id,
                record.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            lease.Revision = job.Revision;
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
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                    item => item.Id == lease.Id,
                    token)
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
                var record = await db.BulkTranscriptExports
                    .SingleOrDefaultAsync(item => item.Id == exportId, token)
                    .ConfigureAwait(false);
                if (record is not null && record.State != "verified")
                {
                    var sourceChanged = errorCode == "bulk_export_source_changed";
                    record.State = sourceChanged
                        ? "superseded"
                        : terminal ? "failed" : "queued";
                    record.ErrorCode = errorCode;
                    record.SafeErrorDetail = safeDetail;
                    record.UpdatedAt = now;
                    if (sourceChanged)
                    {
                        record.SupersededAt = now;
                        record.SupersededReason = "source_changed_before_completion";
                    }

                    BulkTranscriptExportEndpoints.AddStatusOutbox(
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
        var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                item => item.Id == lease.Id,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent(
                "bulk_export_job_missing",
                "The bulk result job no longer exists.");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= timeProvider.GetUtcNow())
        {
            throw Permanent(
                "bulk_export_job_lease_lost",
                "The bulk result job lease is no longer current.");
        }

        if (job.SchemaVersion != 1 || lease.SchemaVersion != 1)
        {
            throw Permanent(
                "bulk_export_job_schema_unsupported",
                "The bulk result job version is unsupported.");
        }

        return job;
    }

    private static List<FrozenBulkResultSource> DeserializeSources(
        string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<FrozenBulkResultSource>>(
                    json,
                    PayloadJsonOptions)
                ?? throw Permanent(
                    "bulk_export_source_snapshot_invalid",
                    "The bulk result source snapshot is invalid.");
        }
        catch (JsonException)
        {
            throw Permanent(
                "bulk_export_source_snapshot_invalid",
                "The bulk result source snapshot is invalid.");
        }
    }

    private static JobPayload DeserializePayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<JobPayload>(
                json,
                PayloadJsonOptions);
            return string.IsNullOrWhiteSpace(payload?.ExportId)
                ? throw Permanent(
                    "bulk_export_job_payload_invalid",
                    "The bulk result job payload is invalid.")
                : payload;
        }
        catch (JsonException)
        {
            throw Permanent(
                "bulk_export_job_payload_invalid",
                "The bulk result job payload is invalid.");
        }
    }

    private static void EnsureSourceMatches(
        FrozenBulkResultSource frozen,
        ResultReportSource source)
    {
        if (source.SubmissionId != frozen.SubmissionId
            || source.SubmissionRevision != frozen.SubmissionRevision
            || source.GradingRunId != frozen.GradingRunId
            || source.ResultSourceRevision != frozen.ResultSourceRevision
            || source.TemplateVersionId != frozen.TemplateVersionId
            || source.TemplateVersionNumber != frozen.TemplateVersionNumber
            || source.SourceHash != frozen.SourceHash)
        {
            throw Permanent(
                "bulk_export_source_changed",
                "A finalized result changed before the archive was completed.");
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

    private static TimeSpan RetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30),
    };

    private static BulkTranscriptJobException Permanent(
        string errorCode,
        string safeDetail) =>
        new(errorCode, safeDetail, isPermanent: true);

    [LoggerMessage(
        EventId = 7_451,
        Level = LogLevel.Warning,
        Message = "Bulk result export job {JobId} failed with {ErrorCode}.")]
    private partial void LogJobFailure(string jobId, string errorCode);

    [LoggerMessage(
        EventId = 7_452,
        Level = LogLevel.Error,
        Message = "Bulk result export job {JobId} failed unexpectedly.")]
    private partial void LogUnexpectedFailure(Exception exception, string jobId);

    [LoggerMessage(
        EventId = 7_453,
        Level = LogLevel.Information,
        Message = "Deleted {Count} stale bulk result temporary archives.")]
    private partial void LogStaleTemporaryArchivesDeleted(int count);

    [LoggerMessage(
        EventId = 7_454,
        Level = LogLevel.Warning,
        Message = "Bulk result temporary archive cleanup failed.")]
    private partial void LogTemporaryArchiveCleanupFailure(Exception exception);

    private sealed class JobLease(
        string id,
        int schemaVersion,
        string payloadJson,
        long revision,
        string? correlationId,
        bool recoveryOnly)
    {
        public string Id { get; } = id;
        public int SchemaVersion { get; } = schemaVersion;
        public string PayloadJson { get; } = payloadJson;
        public long Revision { get; set; } = revision;
        public string? CorrelationId { get; } = correlationId;
        public bool RecoveryOnly { get; } = recoveryOnly;
    }

    private sealed record JobPayload(string ExportId);

    private sealed record Preparation(
        BulkTranscriptExportEntity Record,
        IReadOnlyList<FrozenBulkResultSource> Sources,
        bool AlreadyVerified);

    private sealed class BulkTranscriptJobException(
        string errorCode,
        string safeDetail,
        bool isPermanent) : Exception(safeDetail)
    {
        public string ErrorCode { get; } = errorCode;
        public string SafeDetail { get; } = safeDetail;
        public bool IsPermanent { get; } = isPermanent;
    }
}

internal static class BulkTranscriptExportTemporaryFiles
{
    internal const int MaximumCleanupCandidates = 256;
    internal const int MaximumCleanupDeletions = 64;
    internal static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

    private const string FileNamePrefix = "bulk-transcript-";
    private const string FileNameSuffix = ".zip.part";
    private const int TokenLength = 64;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static string DefaultDirectory { get; } = Path.Combine(
        Path.GetTempPath(),
        "ooki-grader",
        "bulk-transcript-exports-v1");

    internal static string CreatePath(string jobId, long leaseRevision) =>
        CreatePath(DefaultDirectory, jobId, leaseRevision);

    internal static string CreatePath(
        string ownedDirectory,
        string jobId,
        long leaseRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var directory = Path.GetFullPath(ownedDirectory);
        var directoryInfo = Directory.CreateDirectory(directory);
        if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException(
                "The bulk result temporary directory cannot be a reparse point.");
        }

        var identity = string.Concat(
            jobId,
            "\n",
            leaseRevision.ToString(CultureInfo.InvariantCulture));
        var token = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        var fileName = string.Concat(
            FileNamePrefix,
            token,
            FileNameSuffix);
        if (!IsOwnedFileName(fileName))
        {
            throw new InvalidOperationException(
                "The bulk result temporary file name is invalid.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!string.Equals(
                Path.GetDirectoryName(path),
                directory,
                PathComparison))
        {
            throw new InvalidOperationException(
                "The bulk result temporary file escaped its owned directory.");
        }

        return path;
    }

    internal static int SweepStale(DateTimeOffset now) =>
        SweepStale(DefaultDirectory, now);

    internal static int SweepStale(
        string ownedDirectory,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedDirectory);
        var directory = Path.GetFullPath(ownedDirectory);
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var directoryInfo = new DirectoryInfo(directory);
        if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return 0;
        }

        var cutoff = now.Subtract(StaleAge).UtcDateTime;
        var examined = 0;
        var deleted = 0;
        using var entries = Directory
            .EnumerateFileSystemEntries(
                directory,
                string.Concat(FileNamePrefix, "*", FileNameSuffix),
                SearchOption.TopDirectoryOnly)
            .GetEnumerator();
        while (examined < MaximumCleanupCandidates
               && deleted < MaximumCleanupDeletions
               && entries.MoveNext())
        {
            examined++;
            var candidate = Path.GetFullPath(entries.Current);
            if (!string.Equals(
                    Path.GetDirectoryName(candidate),
                    directory,
                    PathComparison)
                || !IsOwnedFileName(Path.GetFileName(candidate)))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(candidate);
                if ((attributes & (FileAttributes.Directory
                                   | FileAttributes.ReparsePoint)) != 0
                    || File.GetLastWriteTimeUtc(candidate) > cutoff)
                {
                    continue;
                }

                File.Delete(candidate);
                deleted++;
            }
            catch (FileNotFoundException)
            {
                // Another worker or the job's finally block won the race.
            }
            catch (DirectoryNotFoundException)
            {
                // The owned directory was removed concurrently.
                break;
            }
            catch (IOException)
            {
                // A live writer can still own the file; retry on a later pass.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep cleanup bounded and best-effort.
            }
        }

        return deleted;
    }

    private static bool IsOwnedFileName(string fileName)
    {
        if (fileName.Length
                != FileNamePrefix.Length + TokenLength + FileNameSuffix.Length
            || !fileName.StartsWith(FileNamePrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(FileNameSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var token = fileName.AsSpan(FileNamePrefix.Length, TokenLength);
        foreach (var character in token)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
