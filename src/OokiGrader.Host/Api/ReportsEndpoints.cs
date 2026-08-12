using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Reports;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.Host.Api;

public static class ReportsEndpoints
{
    public static IEndpointRouteBuilder MapReportsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/results/{submissionId}/exports",
                CreateExport)
            .WithTags("Reports")
            .RequireAuthorization("teacher");
        endpoints.MapGet("/api/v1/exports/{exportId}", GetExport)
            .WithTags("Reports")
            .RequireAuthorization("results");
        endpoints.MapGet("/api/v1/exports/{exportId}/file", DownloadExport)
            .WithTags("Reports")
            .RequireAuthorization("results");
        endpoints.MapPost(
                "/api/v1/exports/{exportId}:regenerate",
                RegenerateExport)
            .WithTags("Reports")
            .RequireAuthorization("teacher");
        return endpoints;
    }

    private static async Task<IResult> CreateExport(
        string submissionId,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromBody] CreateExportBody request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        var now = timeProvider.GetUtcNow();
        var exportId = UlidId.New(now);
        ResultReportSource source;
        try
        {
            source = await ResultReportSourceLoader.LoadAsync(
                    db,
                    submissionId,
                    exportId,
                    now,
                    includeTeacherComments: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ResultReportSourceException exception)
        {
            return SourceProblem(context, exception);
        }

        if (request.ResultRevision is > 0
            && request.ResultRevision != source.SubmissionRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "RESULT_REVISION_STALE",
                "採点結果が更新されています",
                "最新の採点結果を読み込んでからPDFを作成してください。",
                [new { currentRevision = source.SubmissionRevision }]);
        }

        var existing = await db.Set<ExportRecordEntity>()
            .AsNoTracking()
            .Where(item => item.SubmissionId == submissionId
                && item.SourceHash == source.SourceHash
                && item.RendererVersion == ResultPdfRenderer.CurrentRendererVersion
                && item.State != "failed")
            .OrderByDescending(item => item.ExportRevision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return Results.Ok(ToStatus(existing));
        }

        var record = await AddExportAsync(
                db,
                source,
                exportId,
                ApiHelpers.StaffId(principal),
                now,
                context.TraceIdentifier,
                supersedesExportId: null,
                cancellationToken)
            .ConfigureAwait(false);
        return Results.Accepted(
            $"/api/v1/exports/{record.Id}",
            ToStatus(record));
    }

    private static async Task<IResult> RegenerateExport(
        string exportId,
        ClaimsPrincipal principal,
        HttpContext context,
        [FromBody] CreateExportBody request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        var previous = await db.Set<ExportRecordEntity>()
            .SingleOrDefaultAsync(
                item => item.Id == exportId,
                cancellationToken)
            .ConfigureAwait(false);
        if (previous is null)
        {
            return Results.NotFound();
        }

        var now = timeProvider.GetUtcNow();
        var newId = UlidId.New(now);
        ResultReportSource source;
        try
        {
            source = await ResultReportSourceLoader.LoadAsync(
                    db,
                    previous.SubmissionId,
                    newId,
                    now,
                    includeTeacherComments: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ResultReportSourceException exception)
        {
            return SourceProblem(context, exception);
        }

        if (request.ResultRevision is > 0
            && request.ResultRevision != source.SubmissionRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "RESULT_REVISION_STALE",
                "採点結果が更新されています",
                "最新の採点結果を読み込んでからPDFを再作成してください。",
                [new { currentRevision = source.SubmissionRevision }]);
        }

        var record = await AddExportAsync(
                db,
                source,
                newId,
                ApiHelpers.StaffId(principal),
                now,
                context.TraceIdentifier,
                previous.Id,
                cancellationToken)
            .ConfigureAwait(false);
        return Results.Accepted(
            $"/api/v1/exports/{record.Id}",
            ToStatus(record));
    }

    private static async Task<IResult> GetExport(
        string exportId,
        OokiGraderDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        var record = await db.Set<ExportRecordEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == exportId,
                cancellationToken)
            .ConfigureAwait(false);
        return record is null
            ? Results.NotFound()
            : Results.Ok(ToStatus(record));
    }

    private static async Task<IResult> DownloadExport(
        string exportId,
        HttpContext context,
        OokiGraderDbContext db,
        IContentStore contentStore,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!ReportsEnabled(configuration))
        {
            return Results.NotFound();
        }

        var file = await db.Set<ExportRecordEntity>()
            .AsNoTracking()
            .Where(item => item.Id == exportId)
            .Select(item => new
            {
                Record = item,
                FileReference = item.FileReference,
                FileObject = item.FileReference == null
                    ? null
                    : item.FileReference.FileObject,
                item.Submission.TestSession.TestDate,
                TestTitle = item.Submission.TestSession.TitleOverride
                    ?? item.Submission.TestSession.TemplateTitleSnapshot
                    ?? item.Submission.TestSession.TemplateVersion.TestTemplate.Title,
                StudentName = item.Submission.AssignedStudent == null
                    ? "生徒"
                    : item.Submission.AssignedStudent.DisplayName,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (file is null)
        {
            return Results.NotFound();
        }

        if (file.Record.State != "verified"
            || file.Record.FileReferenceId is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "EXPORT_NOT_READY",
                "PDFはまだダウンロードできません",
                file.Record.State == "failed"
                    ? "PDFの作成に失敗しました。再作成してください。"
                    : "作成が完了するまでお待ちください。");
        }

        var fileObject = file.FileObject;
        if (fileObject is null
            || file.FileReference?.Id != file.Record.FileReferenceId
            || fileObject.State is "deleted" or "missing"
            || fileObject.StorageClass != ContentStorageClass.ResultReport.ToString())
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "EXPORT_FILE_GONE",
                "PDFの保存期間が終了しました",
                "必要な場合は結果画面からPDFを再作成してください。");
        }

        if (fileObject.State != "available"
            || fileObject.Sha256 != file.Record.Sha256
            || fileObject.Bytes != file.Record.Bytes
            || fileObject.Extension != "pdf")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "EXPORT_FILE_UNAVAILABLE",
                "PDFを確認できません",
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
                "EXPORT_FILE_GONE",
                "PDFを保存領域で確認できません",
                "必要な場合は結果画面からPDFを再作成してください。");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            "application/pdf",
            BuildDownloadFilename(
                file.TestDate,
                file.TestTitle,
                file.StudentName),
            file.Record.CompletedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static async Task<ExportRecordEntity> AddExportAsync(
        OokiGraderDbContext db,
        ResultReportSource source,
        string exportId,
        string actorStaffUserId,
        DateTimeOffset now,
        string correlationId,
        string? supersedesExportId,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(
            (await db.Set<ExportRecordEntity>()
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
            CreatedByStaffUserId = actorStaffUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<ExportRecordEntity>().Add(record);
        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = jobId,
            Type = ResultPdfJobWorker.JobType,
            SchemaVersion = 1,
            DeduplicationKey =
                $"export:{record.Id}:sourceHash:{record.SourceHash}:" +
                record.RendererVersion,
            Priority = 0,
            PayloadJson = JsonSerializer.Serialize(new { exportId = record.Id }),
            State = "queued",
            MaxAttempts = 5,
            NextAttemptAt = now,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        var oldRecords = await db.Set<ExportRecordEntity>()
            .Where(item => item.SubmissionId == source.SubmissionId
                && item.Id != record.Id
                && item.SupersededAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var oldRecord in oldRecords)
        {
            oldRecord.SupersededAt = now;
            oldRecord.SupersededReason = oldRecord.Id == supersedesExportId
                ? "explicit_regeneration"
                : "newer_export_created";
            oldRecord.UpdatedAt = now;
        }

        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(2)),
            OccurredAt = now,
            ActorStaffUserId = actorStaffUserId,
            EventType = "export.requested",
            ObjectType = "export_record",
            ObjectId = record.Id,
            Outcome = "succeeded",
            ReasonCode = supersedesExportId is null
                ? "teacher_requested"
                : "teacher_regenerated",
            CorrelationId = correlationId,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                submissionId = source.SubmissionId,
                resultSourceRevision = source.ResultSourceRevision,
                exportRevision = nextRevision,
            }),
        });
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(3)),
            AggregateType = "export_record",
            AggregateId = record.Id,
            EventType = "export.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                exportId = record.Id,
                state = record.State,
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    private static object ToStatus(ExportRecordEntity record) => new
    {
        id = record.Id,
        state = record.State,
        revision = record.ExportRevision,
        resultRevision = record.ResultSourceRevision,
        templateVersionNumber = record.TemplateVersionNumber,
        rendererVersion = record.RendererVersion,
        record.SourceHash,
        record.Sha256,
        record.Bytes,
        record.PageCount,
        record.ErrorCode,
        record.SafeErrorDetail,
        record.CreatedAt,
        record.StartedAt,
        record.CompletedAt,
        superseded = record.SupersededAt is not null,
        record.SupersededAt,
        record.SupersededReason,
        fileUrl = record.State == "verified"
            ? $"/api/v1/exports/{record.Id}/file"
            : null,
    };

    private static IResult SourceProblem(
        HttpContext context,
        ResultReportSourceException exception)
    {
        var status = exception.ErrorCode == "export_submission_missing"
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status409Conflict;
        return ApiHelpers.Problem(
            context,
            status,
            exception.ErrorCode.ToUpperInvariant(),
            "結果PDFを作成できません",
            exception.SafeDetail);
    }

    private static string BuildDownloadFilename(
        DateOnly testDate,
        string testTitle,
        string studentName)
    {
        var unsafeName =
            $"{testDate:yyyy-MM-dd}_{testTitle}_{studentName}_結果.pdf";
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(['/', '\\', ':', '\r', '\n'])
            .ToHashSet();
        var safe = new string(unsafeName
            .Where(character => !invalid.Contains(character)
                && !char.IsControl(character))
            .ToArray());
        return safe.Length <= 140
            ? safe
            : $"{safe[..136]}.pdf";
    }

    private static bool ReportsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("Features:Reports.Pdf");

    private sealed record CreateExportBody(long? ResultRevision);
}
