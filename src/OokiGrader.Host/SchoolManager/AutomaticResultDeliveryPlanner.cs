using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.SchoolManager;

public sealed class AutomaticResultDeliveryPlanner(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    TimeProvider timeProvider)
{
    public Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default) =>
        writeCoordinator.ExecuteAsync(
            token => ProcessUnderWriteLockAsync(token),
            cancellationToken);

    private async Task<bool> ProcessUnderWriteLockAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var activationStartedAt = await db.SchoolManagerSettings
            .AsNoTracking()
            .Where(settings => settings.Enabled
                && settings.ActivationStartedAt != null)
            .Select(settings => settings.ActivationStartedAt)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (activationStartedAt is null)
        {
            return false;
        }

        var obsolete = await db.GuardianDeliveries
            .Where(delivery => delivery.Kind == "result_pdf"
                && delivery.State != "sent"
                && delivery.State != "canceled"
                && delivery.State != "sending"
                && delivery.Submission != null
                && (delivery.Submission.State != "finalized"
                    || delivery.Submission.FinalizedAt == null))
            .OrderBy(delivery => delivery.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (obsolete is not null)
        {
            obsolete.State = "canceled";
            obsolete.LastErrorCode = "result_reopened";
            obsolete.SafeErrorDetail =
                "The result was reopened before guardian delivery.";
            obsolete.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var candidates = await db.Submissions
            .AsNoTracking()
            .Where(submission => submission.State == "finalized"
                && submission.FinalizedAt != null
                && submission.FinalizedAt >= activationStartedAt
                && submission.ScanPayloadState == "scan_available"
                && submission.OriginalFileObjectId != null
                && db.FileObjects.Any(file =>
                    file.Id == submission.OriginalFileObjectId
                    && file.State == "available"
                    && file.VerifiedMime == "application/pdf"
                    && file.Extension == "pdf"
                    && file.StorageClass
                        == ContentStorageClass.ManagedScanOriginal.ToString())
                && submission.AssignedStudentId != null
                && submission.CurrentGradingRunId != null
                && submission.FinalizedByStaffUserId != null)
            .Select(submission => new
            {
                submission.Id,
                StudentId = submission.AssignedStudentId!,
                ActorId = submission.FinalizedByStaffUserId!,
                GradingRunId = submission.CurrentGradingRunId!,
                ResultSourceRevision = submission.GradingRuns
                    .Where(run => run.Id == submission.CurrentGradingRunId)
                    .Select(run => run.ResultSourceRevision)
                    .Single(),
                submission.FinalizedAt,
            })
            .OrderBy(item => item.FinalizedAt)
            .ThenBy(item => item.Id)
            .Take(50)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            var sourceKey = $"{candidate.Id}:{candidate.GradingRunId}:" +
                candidate.ResultSourceRevision;
            if (await db.GuardianDeliveries.AnyAsync(
                    delivery => delivery.Kind == "result_pdf"
                        && delivery.StudentId == candidate.StudentId
                        && delivery.SourceKey == sourceKey,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                continue;
            }

            var exportId = UlidId.New(now);
            var source = await ResultReportSourceLoader.LoadAsync(
                    db,
                    candidate.Id,
                    exportId,
                    now,
                    includeTeacherComments: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (source.OriginalPdf is null)
            {
                continue;
            }

            var export = await db.ExportRecords
                .Where(item => item.SubmissionId == candidate.Id
                    && item.SourceHash == source.SourceHash
                    && item.RendererVersion == ResultPdfRenderer.CurrentRendererVersion
                    && item.State != "failed")
                .OrderByDescending(item => item.ExportRevision)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (export is null)
            {
                export = await QueueExportAsync(
                        db,
                        source,
                        exportId,
                        candidate.ActorId,
                        now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var deliveryId = UlidId.New(now.AddMilliseconds(4));
            db.GuardianDeliveries.Add(new GuardianDeliveryEntity
            {
                Id = deliveryId,
                Kind = "result_pdf",
                StudentId = candidate.StudentId,
                SubmissionId = candidate.Id,
                ExportRecordId = export.Id,
                SourceKey = sourceKey,
                AttachmentName = BuildAttachmentName(source.Document.TestDate),
                State = export.State == "verified" ? "ready" : "pending",
                NotBeforeAt = now,
                MaxAttempts = 5,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now.AddMilliseconds(5)),
                OccurredAt = now,
                ActorStaffUserId = candidate.ActorId,
                EventType = "guardian_delivery.queued",
                ObjectType = "guardian_delivery",
                ObjectId = deliveryId,
                Outcome = "succeeded",
                ReasonCode = "teacher_finalized_result",
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    kind = "result_pdf",
                    submissionId = candidate.Id,
                    exportId = export.Id,
                }),
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static async Task<ExportRecordEntity> QueueExportAsync(
        OokiGraderDbContext db,
        ResultReportSource source,
        string exportId,
        string actorId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(
            (await db.ExportRecords
                .Where(item => item.SubmissionId == source.SubmissionId)
                .Select(item => (int?)item.ExportRevision)
                .MaxAsync(cancellationToken)
                .ConfigureAwait(false) ?? 0) + 1);
        var jobId = UlidId.New(now.AddMilliseconds(1));
        var record = new ExportRecordEntity
        {
            Id = exportId,
            SubmissionId = source.SubmissionId,
            GradingRunId = source.GradingRunId,
            ResultSourceRevision = source.ResultSourceRevision,
            SubmissionRevisionAtCreate = source.SubmissionRevision,
            TemplateVersionId = source.TemplateVersionId,
            TemplateVersionNumber = source.TemplateVersionNumber,
            ExportRevision = nextRevision,
            Type = "result_pdf",
            RendererVersion = ResultPdfRenderer.CurrentRendererVersion,
            SourceHash = source.SourceHash,
            BackgroundJobId = jobId,
            State = "queued",
            CreatedByStaffUserId = actorId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.ExportRecords.Add(record);
        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = jobId,
            Type = ResultPdfJobWorker.JobType,
            SchemaVersion = 1,
            DeduplicationKey = $"export:{record.Id}:sourceHash:{record.SourceHash}:" +
                record.RendererVersion,
            Priority = 1,
            PayloadJson = JsonSerializer.Serialize(new { exportId = record.Id }),
            State = "queued",
            MaxAttempts = 5,
            NextAttemptAt = now,
            CorrelationId = $"guardian-delivery:{source.SubmissionId}",
            CreatedAt = now,
            UpdatedAt = now,
        });
        return record;
    }

    private static string BuildAttachmentName(DateOnly testDate) =>
        $"採点結果_{testDate:yyyyMMdd}.pdf";
}
