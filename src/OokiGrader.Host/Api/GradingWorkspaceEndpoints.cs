using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static partial class ResultsEndpoints
{
    private const int MaximumBulkConfirmationItems = 300;

    private static async Task<IResult> GetGradingWorkspace(
        string submissionId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(submissionId))
        {
            return Results.NotFound();
        }

        var submission = await db.Submissions
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.AssignedStudent)
            .Include(item => item.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.TestTemplate)
            .Include(item => item.Pages)
                .ThenInclude(page => page.NormalizedFileReference)
                    .ThenInclude(reference => reference.FileObject)
            .Include(item => item.Pages)
                .ThenInclude(page => page.ThumbnailFileReference)
                    .ThenInclude(reference => reference.FileObject)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.VoidedAt is not null)
        {
            return Results.NotFound();
        }

        if (submission.CurrentGradingRunId is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "GRADING_WORKSPACE_NOT_READY",
                "採点結果をまだ表示できません",
                "初回採点が完了してから、もう一度開いてください。");
        }

        var run = await LoadWorkspaceRunAsync(
                db,
                submission.Id,
                submission.CurrentGradingRunId,
                tracking: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "CURRENT_GRADING_RUN_MISSING",
                "現在の採点結果を確認できません",
                "管理者に連絡してください。");
        }

        var revisionError = ValidateWorkspaceRevisions(context, run);
        if (revisionError is not null)
        {
            return revisionError;
        }

        var originalPdf = await FindSubmissionPdfAsync(
                db,
                submission,
                cancellationToken)
            .ConfigureAwait(false);
        var pageByReferenceId = submission.Pages
            .ToDictionary(
                page => page.NormalizedFileReferenceId,
                page => page.PageNumber,
                StringComparer.Ordinal);
        var answerCropPageByReferenceId = await LoadAnswerCropPageNumbersAsync(
                db,
                submission.Id,
                run.QuestionResults,
                cancellationToken)
            .ConfigureAwait(false);
        var mutable = submission.TestSession.State != "archived"
            && submission.FinalizedAt is null
            && submission.VoidedAt is null
            && run.FinalizedAt is null
            && run.State != "finalized";
        var unresolved = run.QuestionResults
            .Where(IsUnresolved)
            .OrderBy(result => result.Question.OrderIndex)
            .ThenBy(result => result.Id, StringComparer.Ordinal)
            .Select(result => new
            {
                resultId = result.Id,
                sourceResultRevision = CurrentWorkspaceRevision(result)
                    .RevisionNumber,
            })
            .ToArray();
        var scanAvailable = submission.ScanPayloadState == "scan_available";
        var pages = submission.Pages
            .OrderBy(page => page.PageNumber)
            .ThenBy(page => page.Id, StringComparer.Ordinal)
            .Select(page =>
            {
                var available = scanAvailable && IsAvailableNormalizedPage(page);
                var contentUrl = available
                    ? SubmissionPageContentUrl(page.Id)
                    : null;
                var thumbnailUrl = scanAvailable && IsAvailableThumbnail(page)
                    ? SubmissionPageThumbnailUrl(submission.Id, page.Id)
                    : null;
                return new
                {
                    id = page.Id,
                    page.PageNumber,
                    page.WidthPixels,
                    page.HeightPixels,
                    page.RotationDegrees,
                    page.QualityState,
                    available,
                    contentUrl,
                    thumbnailUrl,
                };
            })
            .ToArray();
        var results = run.QuestionResults
            .OrderBy(result => result.Question.OrderIndex)
            .ThenBy(result => result.Id, StringComparer.Ordinal)
            .Select(result =>
            {
                var revision = CurrentWorkspaceRevision(result);
                return new
                {
                    resultId = result.Id,
                    result.QuestionId,
                    result.Question.OrderIndex,
                    result.Question.DisplayLabel,
                    result.Question.QuestionText,
                    result.Question.QuestionType,
                    result.Question.GradingMode,
                    pageNumbers = QuestionPageNumbers(
                        result,
                        pageByReferenceId,
                        answerCropPageByReferenceId,
                        submission.Pages.Count),
                    expectedAnswers = result.Question.AcceptedAnswers
                        .OrderBy(answer => answer.Id, StringComparer.Ordinal)
                        .Select(answer => answer.AnswerText)
                        .ToArray(),
                    transcription = revision.AnswerTextCorrection
                        ?? result.TranscribedAnswer,
                    outcome = revision.Outcome,
                    awardedPointsMilli = revision.AwardedPointsMilli,
                    maxPointsMilli = result.MaximumPointsMilli,
                    pointIncrementMilli = result.Question.PointIncrementMilli,
                    reason = revision.ReasonCode ?? result.ReasonCode,
                    result.Explanation,
                    result.ConfidenceBasisPoints,
                    kanjiRequired = !result.Question.AllowNonKanji,
                    result.Question.RequiresCompleteAnswer,
                    result.Question.AnswerOrderInsensitive,
                    result.ReviewRequired,
                    result.ReviewStatus,
                    sourceResultRevision = revision.RevisionNumber,
                    currentRevisionId = revision.Id,
                    currentRevisionSource = revision.Source,
                };
            })
            .ToArray();
        var session = submission.TestSession;
        var template = session.TemplateVersion.TestTemplate;

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            submission = new
            {
                id = submission.Id,
                submission.State,
                submission.Revision,
                fileName = submission.OriginalFileName,
                uploadedAt = submission.UploadCompletedAt,
                pageCount = submission.PageCount ?? pages.Length,
                submission.ScanPayloadState,
                submission.ScanDeletedAt,
                submission.ScanDeletionReason,
                submission.FinalizedAt,
            },
            session = new
            {
                id = session.Id,
                session.State,
                session.TestDate,
                session.ClassLabel,
            },
            test = new
            {
                templateVersionId = session.TemplateVersionId,
                templateVersionNumber = session.TemplateVersion.VersionNumber,
                title = session.TemplateTitleSnapshot ?? template.Title,
                subject = session.TemplateSubjectSnapshot ?? template.Subject,
                gradeLabel = session.TemplateGradeLabelSnapshot
                    ?? template.GradeLabel,
                category = session.TemplateCategorySnapshot ?? template.Category,
                course = session.TemplateCourseSnapshot
                    ?? session.Course
                    ?? template.Course,
            },
            student = submission.AssignedStudent is null
                ? null
                : new
                {
                    id = submission.AssignedStudent.Id,
                    submission.AssignedStudent.DisplayName,
                    submission.AssignedStudent.StudentNumber,
                    submission.AssignedStudent.SchoolClass,
                    submission.AssignedStudent.Course,
                    submission.AssignedStudent.GradeLabel,
                },
            gradingRun = WorkspaceRunSummary(run),
            originalPdf = originalPdf is null
                ? null
                : new
                {
                    available = true,
                    url = SubmissionPdfContentUrl(submission.Id),
                    contentType = "application/pdf",
                },
            pages,
            results,
            unresolvedSnapshot = unresolved,
            bulkConfirmationLimit = MaximumBulkConfirmationItems,
            canBulkConfirm = mutable && unresolved.Length is > 0
                and <= MaximumBulkConfirmationItems,
            canFinalize = mutable
                && unresolved.Length == 0
                && run.State == "ready_to_finalize",
        });
    }

    private static async Task<IResult> GetSubmissionOriginalPdf(
        string submissionId,
        HttpContext context,
        OokiGraderDbContext db,
        IContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(submissionId))
        {
            return Results.NotFound();
        }

        var submission = await db.Submissions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.VoidedAt is not null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var pdf = await FindSubmissionPdfAsync(db, submission, cancellationToken)
            .ConfigureAwait(false);
        if (pdf is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "SUBMISSION_PDF_UNAVAILABLE",
                "元の答案PDFを表示できません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        var fileObject = pdf.FileObject;
        var locator = new ContentObjectLocator(
            ContentStorageClass.ManagedScanOriginal,
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
                "SUBMISSION_PDF_GONE",
                "元の答案PDFが見つかりません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        context.Response.Headers.ContentDisposition =
            "inline; filename=\"answer.pdf\"";
        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            "application/pdf",
            lastModified: fileObject.VerifiedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> GetSubmissionPageThumbnail(
        string submissionId,
        string pageId,
        HttpContext context,
        OokiGraderDbContext db,
        IContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(submissionId) || !UlidId.IsCanonical(pageId))
        {
            return Results.NotFound();
        }

        var page = await db.SubmissionPages
            .AsNoTracking()
            .Include(item => item.Submission)
            .Include(item => item.ThumbnailFileReference)
                .ThenInclude(reference => reference.FileObject)
            .SingleOrDefaultAsync(
                item => item.Id == pageId
                    && item.SubmissionId == submissionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (page is null || page.Submission.VoidedAt is not null)
        {
            return Results.NotFound();
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (page.Submission.ScanPayloadState != "scan_available"
            || !IsAvailableThumbnail(page))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "SUBMISSION_THUMBNAIL_UNAVAILABLE",
                "答案の縮小画像を表示できません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        var fileObject = page.ThumbnailFileReference.FileObject;
        var locator = new ContentObjectLocator(
            ContentStorageClass.ManagedScanDerived,
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
                "SUBMISSION_THUMBNAIL_GONE",
                "答案の縮小画像が見つかりません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            fileObject.VerifiedMime,
            lastModified: fileObject.VerifiedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> ConfirmUnresolvedResults(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ConfirmUnresolvedResultsBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(submissionId))
        {
            return Results.NotFound();
        }

        var validationError = ValidateBulkConfirmationRequest(context, request);
        if (validationError is not null)
        {
            return validationError;
        }

        var submission = await db.Submissions
            .Include(item => item.TestSession)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
        }

        if (submission.FinalizedAt is not null
            || submission.State == "finalized")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_FINALIZED",
                "確定済みの結果は変更できません",
                "結果を再度開いてから確認してください。");
        }

        if (submission.VoidedAt is not null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "SUBMISSION_VOIDED",
                "無効な答案は変更できません",
                "別の答案を選択してください。");
        }

        if (submission.CurrentGradingRunId is null
            || submission.CurrentGradingRunId != request.GradingRunId)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "GRADING_RUN_STALE",
                "採点結果が更新されています",
                "最新の採点画面を読み込み直してください。",
                [new { currentGradingRunId = submission.CurrentGradingRunId }]);
        }

        var run = await LoadWorkspaceRunAsync(
                db,
                submission.Id,
                request.GradingRunId,
                tracking: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "GRADING_RUN_STALE",
                "採点結果が更新されています",
                "最新の採点画面を読み込み直してください。");
        }

        if (run.FinalizedAt is not null || run.State == "finalized")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_FINALIZED",
                "確定済みの結果は変更できません",
                "結果を再度開いてから確認してください。");
        }

        var revisionError = ValidateWorkspaceRevisions(context, run);
        if (revisionError is not null)
        {
            return revisionError;
        }

        var items = request.Items!;
        var requestedById = items.ToDictionary(
            item => item.ResultId,
            StringComparer.Ordinal);
        var resultsById = run.QuestionResults.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var actorId = ApiHelpers.StaffId(principal);
        if (await IsSemanticBulkReplayAsync(
                db,
                requestedById,
                resultsById,
                actorId,
                cancellationToken)
            .ConfigureAwait(false))
        {
            context.Response.Headers.CacheControl = "private, no-store";
            ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
            return Results.Ok(BulkConfirmationResponse(
                submission,
                run,
                confirmed: [],
                skipped: items.Select(item => new BulkConfirmationItemResult(
                    item.ResultId,
                    "RESULT_ALREADY_CONFIRMED",
                    CurrentWorkspaceRevision(resultsById[item.ResultId])
                        .RevisionNumber)).ToArray()));
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.SourceSubmissionRevision,
                out var expectedSubmissionRevision))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "REVISION_REQUIRED",
                "更新条件が必要です",
                "最新の採点画面を読み込み直してください。");
        }

        if (submission.Revision != expectedSubmissionRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "SUBMISSION_REVISION_STALE",
                "答案が更新されています",
                "最新の採点画面を読み込み直してください。",
                [new { currentRevision = submission.Revision }]);
        }

        if (run.ResultSourceRevision != request.SourceResultSourceRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "GRADING_RESULT_SOURCE_STALE",
                "採点結果が更新されています",
                "最新の採点画面を読み込み直してください。",
                [new { currentResultSourceRevision = run.ResultSourceRevision }]);
        }

        var itemErrors = new List<object>();
        var unresolvedIds = run.QuestionResults
            .Where(IsUnresolved)
            .Select(result => result.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (unresolvedIds.Count != items.Count
            || items.Any(item => !unresolvedIds.Contains(item.ResultId)))
        {
            itemErrors.Add(new
            {
                code = "UNRESOLVED_RESULT_SET_CHANGED",
                currentUnresolvedCount = unresolvedIds.Count,
            });
        }

        foreach (var item in items)
        {
            if (!resultsById.TryGetValue(item.ResultId, out var result))
            {
                itemErrors.Add(new
                {
                    item.ResultId,
                    code = "RESULT_NOT_IN_CURRENT_RUN",
                });
                continue;
            }

            var current = CurrentWorkspaceRevision(result);
            if (!IsUnresolved(result))
            {
                itemErrors.Add(new
                {
                    item.ResultId,
                    code = "RESULT_NOT_UNRESOLVED",
                    currentRevision = current.RevisionNumber,
                });
            }
            else if (current.RevisionNumber != item.SourceResultRevision)
            {
                itemErrors.Add(new
                {
                    item.ResultId,
                    code = "RESULT_REVISION_STALE",
                    currentRevision = current.RevisionNumber,
                });
            }
        }

        if (itemErrors.Count > 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "BULK_CONFIRMATION_SNAPSHOT_STALE",
                "確認対象が更新されています",
                "最新の採点画面を読み込み直してください。",
                itemErrors);
        }

        var exactQuestions = await LoadExactQuestionSetAsync(
            db,
            run.TemplateVersionId,
            run.QuestionResults,
            cancellationToken);
        if (exactQuestions is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "GRADING_QUESTION_SET_INVALID",
                "設問構成が一致しません",
                "この結果を確定せず、管理者に連絡してください。");
        }

        var now = timeProvider.GetUtcNow();
        var confirmed = new List<BulkConfirmationItemResult>(items.Count);
        var replacementPoints = new Dictionary<string, long>(StringComparer.Ordinal);
        var confirmationIndex = 0;
        foreach (var item in items)
        {
            var result = resultsById[item.ResultId];
            var current = CurrentWorkspaceRevision(result);
            var revision = new ResultRevisionEntity
            {
                Id = UlidId.New(now.AddTicks(++confirmationIndex)),
                QuestionResultId = result.Id,
                RevisionNumber = checked(current.RevisionNumber + 1),
                AwardedPointsMilli = current.AwardedPointsMilli,
                Outcome = current.Outcome,
                AnswerTextCorrection = current.AnswerTextCorrection
                    ?? result.TranscribedAnswer,
                ReasonCode = current.ReasonCode,
                TeacherNote = current.TeacherNote,
                // ResultRevision.Source is constrained by every deployed database.
                // The dedicated audit event below distinguishes a confirmation from
                // a score-changing teacher override without a risky table rebuild.
                Source = "teacher_override",
                ActorStaffUserId = actorId,
                CreatedAt = now,
                SupersedesRevisionId = current.Id,
                QuestionResult = result,
            };
            db.ResultRevisions.Add(revision);
            result.CurrentRevisionId = revision.Id;
            result.ReviewStatus = "resolved";
            replacementPoints[result.Id] = revision.AwardedPointsMilli;
            confirmed.Add(new BulkConfirmationItemResult(
                result.Id,
                "RESULT_CONFIRMED",
                revision.RevisionNumber));
            AddBulkConfirmationAudit(
                db,
                now.AddTicks(10_000 + confirmationIndex),
                principal,
                context,
                result.Id,
                current.RevisionNumber,
                revision.RevisionNumber);
        }

        run.EarnedPointsMilli = SumWorkspacePoints(
            run.QuestionResults,
            replacementPoints);
        run.PossiblePointsMilli = exactQuestions.Values.Aggregate(
            0L,
            static (total, maximum) => checked(total + maximum));
        run.ResultSourceRevision = checked(run.ResultSourceRevision + 1);
        var blockingReview = run.QuestionResults.Any(IsUnresolved);
        run.State = blockingReview ? "needs_grade_review" : "ready_to_finalize";
        submission.State = run.State;
        submission.UpdatedAt = now;
        AddBulkConfirmationBatchAudit(
            db,
            now.AddTicks(20_000),
            principal,
            context,
            submission.Id,
            run.Id,
            confirmed);
        AddOutbox(
            db,
            now,
            context,
            submission.Id,
            "grading.resultsConfirmed",
            new
            {
                submissionId = submission.Id,
                gradingRunId = run.Id,
                confirmedResultIds = confirmed.Select(item => item.ResultId),
                resultSourceRevision = run.ResultSourceRevision,
            });
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return ConcurrentBulkConfirmationProblem(context);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ConcurrentBulkConfirmationProblem(context);
        }

        context.Response.Headers.CacheControl = "private, no-store";
        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(BulkConfirmationResponse(
            submission,
            run,
            confirmed,
            skipped: []));
    }

    private static async Task<GradingRunEntity?> LoadWorkspaceRunAsync(
        OokiGraderDbContext db,
        string submissionId,
        string runId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<GradingRunEntity> query = db.GradingRuns;
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .AsSplitQuery()
            .Include(run => run.QuestionResults)
                .ThenInclude(result => result.Revisions)
            .Include(run => run.QuestionResults)
                .ThenInclude(result => result.Question)
                    .ThenInclude(question => question.AcceptedAnswers)
            .Include(run => run.QuestionResults)
                .ThenInclude(result => result.Question)
                    .ThenInclude(question => question.QuestionRegion)
            .Include(run => run.QuestionResults)
                .ThenInclude(result => result.Question)
                    .ThenInclude(question => question.AnswerRegion)
            .SingleOrDefaultAsync(
                run => run.Id == runId && run.SubmissionId == submissionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, int>>
        LoadAnswerCropPageNumbersAsync(
            OokiGraderDbContext db,
            string submissionId,
            IEnumerable<QuestionResultEntity> results,
            CancellationToken cancellationToken)
    {
        var referenceIds = results
            .Select(result => result.AnswerCropFileReferenceId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (referenceIds.Length == 0)
        {
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }

        var rows = await db.SubmissionArtifacts
            .AsNoTracking()
            .Where(artifact => artifact.SubmissionId == submissionId
                && artifact.ArtifactType == "answer_crop"
                && referenceIds.Contains(artifact.FileReferenceId))
            .Select(artifact => new
            {
                artifact.FileReferenceId,
                artifact.SubmissionPage.PageNumber,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .GroupBy(row => row.FileReferenceId, StringComparer.Ordinal)
            .Where(group => group.Select(row => row.PageNumber)
                .Distinct()
                .Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.First().PageNumber,
                StringComparer.Ordinal);
    }

    private static IResult? ValidateWorkspaceRevisions(
        HttpContext context,
        GradingRunEntity run)
    {
        foreach (var result in run.QuestionResults)
        {
            if (result.CurrentRevisionId is null
                || result.Revisions.Count(revision =>
                    revision.Id == result.CurrentRevisionId) != 1)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "RESULT_REVISION_MISSING",
                    "採点履歴を確認できません",
                    "管理者に連絡してください。",
                    [new { resultId = result.Id }]);
            }
        }

        return null;
    }

    private static IResult? ValidateBulkConfirmationRequest(
        HttpContext context,
        ConfirmUnresolvedResultsBody request)
    {
        if (!UlidId.IsCanonical(request.GradingRunId)
            || request.SourceSubmissionRevision <= 0
            || request.SourceResultSourceRevision <= 0
            || request.Items is not { Count: >= 1
                and <= MaximumBulkConfirmationItems }
            || request.Items.Any(item =>
                item is null
                || !UlidId.IsCanonical(item.ResultId)
                || item.SourceResultRevision <= 0)
            || request.Items.Select(item => item.ResultId)
                .Distinct(StringComparer.Ordinal)
                .Count() != request.Items.Count)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "BULK_CONFIRMATION_INVALID",
                "一括確認の内容を使用できません",
                $"1件以上{MaximumBulkConfirmationItems}件以下の、重複しない最新の採点結果を指定してください。");
        }

        return null;
    }

    private static async Task<FileReferenceEntity?> FindSubmissionPdfAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        CancellationToken cancellationToken)
    {
        if (submission.ScanPayloadState != "scan_available"
            || submission.OriginalFileObjectId is null)
        {
            return null;
        }

        var references = await db.FileReferences
            .AsNoTracking()
            .Include(reference => reference.FileObject)
            .Where(reference => reference.OwnerType == "submission"
                && reference.OwnerId == submission.Id
                && reference.Purpose == "original_scan")
            .Take(2)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (references.Length != 1)
        {
            return null;
        }

        var reference = references[0];
        var fileObject = reference.FileObject;
        return reference.FileObjectId == submission.OriginalFileObjectId
            && fileObject.Id == submission.OriginalFileObjectId
            && fileObject.State == "available"
            && fileObject.StorageClass
                == ContentStorageClass.ManagedScanOriginal.ToString()
            && fileObject.ManagedScanBytes
            && fileObject.VerifiedMime == "application/pdf"
            && fileObject.Extension == "pdf"
            && fileObject.Bytes > 0
            && fileObject.Sha256 is { Length: 64 }
            ? reference
            : null;
    }

    private static bool IsAvailableNormalizedPage(SubmissionPageEntity page)
    {
        var reference = page.NormalizedFileReference;
        var fileObject = reference.FileObject;
        return reference.OwnerType == "submission_page"
            && reference.OwnerId == page.Id
            && reference.Purpose == "normalized_page"
            && fileObject.State == "available"
            && fileObject.StorageClass
                == ContentStorageClass.ManagedScanDerived.ToString()
            && fileObject.VerifiedMime is "image/png" or "image/jpeg"
            && fileObject.Bytes > 0;
    }

    private static bool IsAvailableThumbnail(SubmissionPageEntity page)
    {
        var reference = page.ThumbnailFileReference;
        var fileObject = reference.FileObject;
        return reference.OwnerType == "submission_page"
            && reference.OwnerId == page.Id
            && reference.Purpose is "page_thumbnail" or "thumbnail"
            && fileObject.State == "available"
            && fileObject.StorageClass
                == ContentStorageClass.ManagedScanDerived.ToString()
            && fileObject.VerifiedMime is "image/png" or "image/jpeg"
            && fileObject.Bytes > 0;
    }

    private static int[] QuestionPageNumbers(
        QuestionResultEntity result,
        Dictionary<string, int> pageByReferenceId,
        Dictionary<string, int> answerCropPageByReferenceId,
        int pageCount)
    {
        var numbers = new SortedSet<int>();
        if (result.Question.QuestionRegion?.PageNumber > 0)
        {
            numbers.Add(result.Question.QuestionRegion.PageNumber);
        }

        if (result.Question.AnswerRegion?.PageNumber > 0)
        {
            numbers.Add(result.Question.AnswerRegion.PageNumber);
        }

        if (numbers.Count == 0
            && result.AnswerCropFileReferenceId is not null
            && (answerCropPageByReferenceId.TryGetValue(
                    result.AnswerCropFileReferenceId,
                    out var cropPageNumber)
                || pageByReferenceId.TryGetValue(
                    result.AnswerCropFileReferenceId,
                    out cropPageNumber)))
        {
            numbers.Add(cropPageNumber);
        }

        if (numbers.Count == 0 && pageCount == 1)
        {
            numbers.Add(1);
        }

        return numbers.ToArray();
    }

    private static IResult ConcurrentBulkConfirmationProblem(
        HttpContext context) => ApiHelpers.Problem(
            context,
            StatusCodes.Status412PreconditionFailed,
            "BULK_CONFIRMATION_CONCURRENT_UPDATE",
            "確認対象が同時に更新されました",
            "最新の採点画面を読み込み直してください。");

    private static ResultRevisionEntity CurrentWorkspaceRevision(
        QuestionResultEntity result) =>
        result.Revisions.Single(revision =>
            revision.Id == result.CurrentRevisionId);

    private static bool IsUnresolved(QuestionResultEntity result) =>
        result.ReviewRequired && result.ReviewStatus != "resolved";

    private static long SumWorkspacePoints(
        IEnumerable<QuestionResultEntity> results,
        Dictionary<string, long> replacements)
    {
        long total = 0;
        foreach (var result in results)
        {
            var points = replacements.TryGetValue(result.Id, out var replacement)
                ? replacement
                : CurrentWorkspaceRevision(result).AwardedPointsMilli;
            if (points < 0 || points > result.MaximumPointsMilli)
            {
                throw new InvalidOperationException(
                    "Persisted result points are outside the question maximum.");
            }

            total = checked(total + points);
        }

        return total;
    }

    private static async Task<bool> IsSemanticBulkReplayAsync(
        OokiGraderDbContext db,
        Dictionary<string, ConfirmUnresolvedResultItem> requested,
        Dictionary<string, QuestionResultEntity> results,
        string actorId,
        CancellationToken cancellationToken)
    {
        var revisionByResultId = new Dictionary<string, int>(
            requested.Count,
            StringComparer.Ordinal);
        foreach (var (resultId, item) in requested)
        {
            if (!results.TryGetValue(resultId, out var result)
                || result.ReviewStatus != "resolved")
            {
                return false;
            }

            var current = CurrentWorkspaceRevision(result);
            if (current.Source != "teacher_override"
                || current.ActorStaffUserId != actorId
                || current.RevisionNumber != item.SourceResultRevision + 1
                || current.SupersedesRevisionId is null)
            {
                return false;
            }

            var superseded = result.Revisions.SingleOrDefault(revision =>
                revision.Id == current.SupersedesRevisionId);
            if (superseded?.RevisionNumber != item.SourceResultRevision)
            {
                return false;
            }

            revisionByResultId[resultId] = current.RevisionNumber;
        }

        var resultIds = requested.Keys.ToArray();
        var audits = await db.AuditEvents
            .AsNoTracking()
            .Where(audit => audit.EventType == "result.confirmed"
                && audit.ObjectType == "question_result"
                && resultIds.Contains(audit.ObjectId)
                && audit.ActorStaffUserId == actorId
                && audit.ReasonCode == "bulk_teacher_confirmation")
            .Select(audit => new
            {
                audit.ObjectId,
                audit.SafeMetadataJson,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var confirmedPairs = new HashSet<(string ResultId, int Revision)>();
        foreach (var audit in audits)
        {
            if (TryReadConfirmedRevision(
                    audit.SafeMetadataJson,
                    out var confirmedRevision))
            {
                confirmedPairs.Add((audit.ObjectId, confirmedRevision));
            }
        }

        return revisionByResultId.All(pair =>
            confirmedPairs.Contains((pair.Key, pair.Value)));
    }

    private static bool TryReadConfirmedRevision(
        string? metadataJson,
        out int confirmedRevision)
    {
        confirmedRevision = 0;
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(
                    "confirmedRevision",
                    out var value)
                && value.TryGetInt32(out confirmedRevision)
                && confirmedRevision > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object WorkspaceRunSummary(GradingRunEntity run) => new
    {
        id = run.Id,
        run.State,
        run.ResultSourceRevision,
        run.EarnedPointsMilli,
        run.PossiblePointsMilli,
    };

    private static object BulkConfirmationResponse(
        SubmissionEntity submission,
        GradingRunEntity run,
        IReadOnlyCollection<BulkConfirmationItemResult> confirmed,
        IReadOnlyCollection<BulkConfirmationItemResult> skipped) => new
        {
            confirmed,
            skipped,
            gradingRun = WorkspaceRunSummary(run),
            submission = new
            {
                id = submission.Id,
                submission.State,
                submission.Revision,
            },
            canFinalize = run.State == "ready_to_finalize"
                && submission.State == "ready_to_finalize",
        };

    private static void AddBulkConfirmationAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string resultId,
        int previousRevision,
        int confirmedRevision)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "result.confirmed",
            ObjectType = "question_result",
            ObjectId = resultId,
            Outcome = "succeeded",
            ReasonCode = "bulk_teacher_confirmation",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                previousRevision,
                confirmedRevision,
            }),
        });
    }

    private static void AddBulkConfirmationBatchAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string submissionId,
        string gradingRunId,
        IReadOnlyCollection<BulkConfirmationItemResult> confirmed)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = "submission.results_confirmed",
            ObjectType = "submission",
            ObjectId = submissionId,
            Outcome = "succeeded",
            ReasonCode = "bulk_teacher_confirmation",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                gradingRunId,
                confirmedCount = confirmed.Count,
                resultIds = confirmed.Select(item => item.ResultId),
            }),
        });
    }

    private static string SubmissionPdfContentUrl(string submissionId) =>
        $"/api/v1/submissions/{Uri.EscapeDataString(submissionId)}/original-pdf";

    private static string SubmissionPageContentUrl(string pageId) =>
        $"/api/v1/review/pages/{Uri.EscapeDataString(pageId)}/content";

    private static string SubmissionPageThumbnailUrl(
        string submissionId,
        string pageId) =>
        $"/api/v1/submissions/{Uri.EscapeDataString(submissionId)}" +
        $"/pages/{Uri.EscapeDataString(pageId)}/thumbnail";

    private sealed record ConfirmUnresolvedResultsBody(
        long SourceSubmissionRevision,
        string? GradingRunId,
        long SourceResultSourceRevision,
        IReadOnlyList<ConfirmUnresolvedResultItem>? Items);

    private sealed record ConfirmUnresolvedResultItem(
        string ResultId,
        int SourceResultRevision);

    private sealed record BulkConfirmationItemResult(
        string ResultId,
        string Code,
        int SourceResultRevision);
}
