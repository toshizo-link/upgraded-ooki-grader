using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Middleware;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static partial class ResultsEndpoints
{
    private static readonly HashSet<string> AllowedOutcomes =
        new(StringComparer.Ordinal)
        {
            "correct",
            "partial",
            "incorrect",
            "blank",
            "unreadable",
        };

    public static IEndpointRouteBuilder MapResultsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var submissions = endpoints.MapGroup("/api/v1/submissions")
            .WithTags("Results");
        submissions.MapPost(
                "/{submissionId}/results/{resultId}:override",
                OverrideResult)
            .RequireAuthorization("teacher");
        submissions.MapGet(
                "/{submissionId}/grading-workspace",
                GetGradingWorkspace)
            .RequireAuthorization("teacher");
        submissions.MapGet(
                "/{submissionId}/original-pdf",
                GetSubmissionOriginalPdf)
            .RequireAuthorization("teacher");
        submissions.MapGet(
                "/{submissionId}/pages/{pageId}/thumbnail",
                GetSubmissionPageThumbnail)
            .RequireAuthorization("teacher");
        submissions.MapPost(
                "/{submissionId}/results:confirm-unresolved",
                ConfirmUnresolvedResults)
            .RequireAuthorization("teacher")
            .RequireIdempotency();
        submissions.MapPost("/{submissionId}:finalize", FinalizeSubmission)
            .RequireAuthorization("teacher");
        submissions.MapPost("/{submissionId}:reopen", ReopenSubmission)
            .RequireAuthorization("teacher");

        endpoints.MapGet("/api/v1/results/{submissionId}", GetFinalizedResult)
            .WithTags("Results")
            .RequireAuthorization("results");
        endpoints.MapGet("/api/v1/students/{studentId}/results", GetStudentResults)
            .WithTags("Results")
            .RequireAuthorization("results");
        return endpoints;
    }

    private static async Task<IResult> OverrideResult(
        string submissionId,
        string resultId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] OverrideResultBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await LoadSubmissionWithCurrentRunAsync(
            db,
            submissionId,
            tracking: true,
            cancellationToken);
        if (submission?.CurrentGradingRunId is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
        }

        if (submission.FinalizedAt is not null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_FINALIZED",
                "確定済みの結果は変更できません",
                "理由を入力して結果を再度開いてから修正してください。");
        }

        var run = submission.GradingRuns.Single(
            item => item.Id == submission.CurrentGradingRunId);
        var result = run.QuestionResults.SingleOrDefault(item => item.Id == resultId);
        if (result is null)
        {
            return Results.NotFound();
        }

        var currentRevision = result.Revisions.SingleOrDefault(
            item => item.Id == result.CurrentRevisionId);
        if (currentRevision is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_REVISION_MISSING",
                "採点履歴を確認できません",
                "管理者に連絡してください。");
        }

        if (request.SourceResultRevision != currentRevision.RevisionNumber)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "RESULT_REVISION_STALE",
                "採点結果が更新されています",
                "最新の採点結果を確認してから修正してください。",
                [new { currentRevision = currentRevision.RevisionNumber }]);
        }

        var nextAnswerTextCorrection = request.TranscriptionCorrection is null
            ? currentRevision.AnswerTextCorrection
            : request.TranscriptionCorrection.Trim();
        var effectiveTranscription = nextAnswerTextCorrection
            ?? result.TranscribedAnswer
            ?? string.Empty;
        if (request.AwardedPointsMilli < 0
            || request.AwardedPointsMilli > result.MaximumPointsMilli
            || request.AwardedPointsMilli
                % result.Question.PointIncrementMilli != 0
            || (result.Question.RequiresCompleteAnswer
                && request.AwardedPointsMilli is > 0
                && request.AwardedPointsMilli < result.MaximumPointsMilli)
            || !AllowedOutcomes.Contains(request.Outcome)
            || !OutcomeMatchesPoints(
                request.Outcome,
                request.AwardedPointsMilli,
                result.MaximumPointsMilli)
            || (request.Outcome == "blank"
                && effectiveTranscription.Length > 0)
            || !ValidReasonCode(request.ReasonCode)
            || request.TranscriptionCorrection?.Length > 4_000
            || request.Note?.Length > 2_000)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "RESULT_OVERRIDE_INVALID",
                "採点結果を修正できません",
                "点数、判定、理由、またはメモを確認してください。");
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
        var revision = new ResultRevisionEntity
        {
            Id = UlidId.New(now),
            QuestionResultId = result.Id,
            RevisionNumber = checked(currentRevision.RevisionNumber + 1),
            AwardedPointsMilli = request.AwardedPointsMilli,
            Outcome = request.Outcome,
            AnswerTextCorrection = nextAnswerTextCorrection,
            ReasonCode = request.ReasonCode,
            TeacherNote = TrimOrNull(request.Note),
            Source = "teacher_override",
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
            SupersedesRevisionId = currentRevision.Id,
        };
        db.ResultRevisions.Add(revision);
        result.CurrentRevisionId = revision.Id;
        result.ReviewStatus = "resolved";

        run.EarnedPointsMilli = SumCurrentPoints(
            run.QuestionResults,
            result.Id,
            revision.AwardedPointsMilli);
        run.PossiblePointsMilli = exactQuestions.Values.Aggregate(
            0L,
            static (total, maximum) => checked(total + maximum));
        run.ResultSourceRevision = checked(run.ResultSourceRevision + 1);
        var blockingReview = run.QuestionResults.Any(item =>
            item.Id != result.Id
            && item.ReviewRequired
            && item.ReviewStatus != "resolved");
        run.State = blockingReview ? "needs_grade_review" : "ready_to_finalize";
        submission.State = run.State;

        AddAudit(
            db,
            now,
            principal,
            context,
            "result.overridden",
            result.Id,
            request.ReasonCode);
        AddOutbox(
            db,
            now,
            context,
            submission.Id,
            "grading.resultOverridden",
            new
            {
                submissionId = submission.Id,
                gradingRunId = run.Id,
                resultId = result.Id,
                resultSourceRevision = run.ResultSourceRevision,
            });
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            result.Id,
            result.QuestionId,
            sourceResultRevision = revision.RevisionNumber,
            revision.AwardedPointsMilli,
            revision.Outcome,
            revision.ReasonCode,
            result.ReviewStatus,
            run.EarnedPointsMilli,
            run.PossiblePointsMilli,
            run.ResultSourceRevision,
            submission.State,
            submission.Revision,
        });
    }

    private static async Task<IResult> FinalizeSubmission(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] FinalizeBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await LoadSubmissionWithCurrentRunAsync(
            db,
            submissionId,
            tracking: true,
            cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
        }

        var precondition = CheckSubmissionRevision(
            context,
            submission,
            request.SourceRevision);
        if (precondition is not null)
        {
            return precondition;
        }

        if (submission.FinalizedAt is not null)
        {
            ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
            return Results.Ok(new
            {
                submission.Id,
                submission.State,
                submission.FinalizedAt,
                submission.Revision,
            });
        }

        if ((submission.AssignedStudentId is null
                && !IsExplicitlyUnidentified(submission))
            || submission.CurrentGradingRunId is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_NOT_READY",
                "結果を確定できません",
                "生徒の割り当てと採点結果が必要です。");
        }

        if (submission.AssignedStudentId is not null)
        {
            var unresolvedDuplicate = await db.Submissions.AnyAsync(
                item => item.Id != submission.Id
                    && item.TestSessionId == submission.TestSessionId
                    && item.AssignedStudentId == submission.AssignedStudentId
                    && item.VoidedAt == null
                    && !item.CanonicalForSession
                    && item.AttemptNumber <= 1,
                cancellationToken);
            var canonicalExists = submission.CanonicalForSession
                || await db.Submissions.AnyAsync(
                    item => item.Id != submission.Id
                        && item.TestSessionId == submission.TestSessionId
                        && item.AssignedStudentId == submission.AssignedStudentId
                        && item.VoidedAt == null
                        && item.CanonicalForSession,
                    cancellationToken);
            if (unresolvedDuplicate
                || (!submission.CanonicalForSession
                    && (submission.AttemptNumber <= 1 || !canonicalExists)))
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "DUPLICATE_SUBMISSION_UNRESOLVED",
                    "重複答案の確認が必要です",
                    "代表答案または受験回番号を決めてから確定してください。");
            }
        }

        var run = submission.GradingRuns.Single(
            item => item.Id == submission.CurrentGradingRunId);
        if (run.QuestionResults.Any(result =>
                result.ReviewRequired && result.ReviewStatus != "resolved"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "BLOCKING_REVIEW_REMAINS",
                "確認が必要な設問があります",
                "すべての要確認項目を解決してから確定してください。");
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

        var earned = SumCurrentPoints(run.QuestionResults, null, null);
        var possible = exactQuestions.Values.Aggregate(
            0L,
            static (total, maximum) => checked(total + maximum));
        if (earned < 0 || earned > possible)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_TOTAL_INVALID",
                "合計点を確認できません",
                "この結果を確定せず、管理者に連絡してください。");
        }

        var now = timeProvider.GetUtcNow();
        run.EarnedPointsMilli = earned;
        run.PossiblePointsMilli = possible;
        run.State = "finalized";
        run.FinalizedAt = now;
        run.FinalizedByStaffUserId = ApiHelpers.StaffId(principal);
        submission.State = "finalized";
        submission.FinalizedAt = now;
        submission.FinalizedByStaffUserId = ApiHelpers.StaffId(principal);
        AddAudit(
            db,
            now,
            principal,
            context,
            "submission.finalized",
            submission.Id,
            "teacher_confirmed");
        AddOutbox(
            db,
            now,
            context,
            submission.Id,
            "result.finalized",
            new
            {
                submissionId = submission.Id,
                studentId = submission.AssignedStudentId,
                gradingRunId = run.Id,
                resultSourceRevision = run.ResultSourceRevision,
            });
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            submission.Id,
            submission.AssignedStudentId,
            gradingRunId = run.Id,
            run.EarnedPointsMilli,
            run.PossiblePointsMilli,
            run.ResultSourceRevision,
            submission.State,
            submission.FinalizedAt,
            submission.Revision,
        });
    }

    private static async Task<IResult> ReopenSubmission(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ReopenBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await LoadSubmissionWithCurrentRunAsync(
            db,
            submissionId,
            tracking: true,
            cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
        }

        var precondition = CheckSubmissionRevision(
            context,
            submission,
            request.SourceRevision);
        if (precondition is not null)
        {
            return precondition;
        }

        if (submission.FinalizedAt is null
            || submission.CurrentGradingRunId is null
            || !ValidReasonCode(request.ReasonCode)
            || request.Note?.Length > 2_000)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "RESULT_REOPEN_INVALID",
                "結果を再度開くことができません",
                "確定状態と理由を確認してください。");
        }

        var run = submission.GradingRuns.Single(
            item => item.Id == submission.CurrentGradingRunId);
        foreach (var result in run.QuestionResults)
        {
            result.ReviewRequired = true;
            result.ReviewStatus = "pending";
        }

        var now = timeProvider.GetUtcNow();
        run.State = "needs_grade_review";
        run.FinalizedAt = null;
        run.FinalizedByStaffUserId = null;
        submission.State = "needs_grade_review";
        submission.FinalizedAt = null;
        submission.FinalizedByStaffUserId = null;
        AddAudit(
            db,
            now,
            principal,
            context,
            "submission.reopened",
            submission.Id,
            request.ReasonCode);
        AddOutbox(
            db,
            now,
            context,
            submission.Id,
            "result.reopened",
            new
            {
                submissionId = submission.Id,
                gradingRunId = run.Id,
                reasonCode = request.ReasonCode,
            });
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            submission.Id,
            submission.State,
            submission.Revision,
        });
    }

    private static async Task<IResult> GetFinalizedResult(
        string submissionId,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var submission = await LoadSubmissionWithCurrentRunAsync(
            db,
            submissionId,
            tracking: false,
            cancellationToken);
        if (submission?.FinalizedAt is null
            || submission.VoidedAt is not null
            || submission.CurrentGradingRunId is null)
        {
            return Results.NotFound();
        }

        var run = submission.GradingRuns.Single(
            item => item.Id == submission.CurrentGradingRunId);
        return Results.Ok(ToFinalizedResult(submission, run));
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetStudentResults(
        string studentId,
        HttpContext context,
        DateOnly? from,
        DateOnly? to,
        string? cursor,
        int? limit,
        int? pageSize,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (!await db.Students.AnyAsync(
                student => student.Id == studentId,
                cancellationToken))
        {
            return Results.NotFound();
        }

        var take = Math.Clamp(pageSize ?? limit ?? 50, 1, 200);
        var query = db.Submissions
            .AsNoTracking()
            .Include(submission => submission.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.TestTemplate)
            .Include(submission => submission.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
                    .ThenInclude(result => result.Revisions)
            .Where(submission => submission.AssignedStudentId == studentId
                && submission.FinalizedAt != null
                && submission.VoidedAt == null
                && submission.CanonicalForSession
                && submission.CurrentGradingRunId != null);
        if (from.HasValue)
        {
            query = query.Where(submission =>
                submission.TestSession.TestDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(submission =>
                submission.TestSession.TestDate <= to.Value);
        }

        var route = $"GET:/api/v1/students/{studentId}/results";
        var filterBinding = CursorPagination.Bind(
            ("from", from?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("sort", "-testDate,-finalizedAt,id"),
            ("to", to?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                route,
                filterBinding,
                out StudentResultCursorPosition position,
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
            query = query.Where(submission =>
                submission.TestSession.TestDate < position.TestDate
                || (submission.TestSession.TestDate == position.TestDate
                    && (submission.FinalizedAt < position.FinalizedAt
                        || (submission.FinalizedAt == position.FinalizedAt
                            && string.Compare(
                                submission.Id,
                                position.Id) > 0))));
        }

        var submissions = await query
            .OrderByDescending(submission => submission.TestSession.TestDate)
            .ThenByDescending(submission => submission.FinalizedAt)
            .ThenBy(submission => submission.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = submissions.Count > take;
        if (hasMore)
        {
            submissions.RemoveAt(take);
        }

        var items = submissions.Select(submission =>
        {
            var run = submission.GradingRuns.Single(
                item => item.Id == submission.CurrentGradingRunId);
            var outcomes = run.QuestionResults
                .Select(CurrentOutcome)
                .ToArray();
            return new
            {
                submissionId = submission.Id,
                submission.TestSession.TestDate,
                testTitle =
                    submission.TestSession.TitleOverride
                    ?? submission.TestSession.TemplateTitleSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
                subject = submission.TestSession.TemplateSubjectSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Subject,
                category = submission.TestSession.TemplateCategorySnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Category,
                run.EarnedPointsMilli,
                run.PossiblePointsMilli,
                percentageBasisPoints = PercentageBasisPoints(
                    run.EarnedPointsMilli,
                    run.PossiblePointsMilli),
                correct = outcomes.Count(outcome => outcome == "correct"),
                partial = outcomes.Count(outcome => outcome == "partial"),
                incorrect = outcomes.Count(outcome => outcome == "incorrect"),
                blank = outcomes.Count(outcome => outcome == "blank"),
                unreadable = outcomes.Count(outcome => outcome == "unreadable"),
                resultRevision = run.ResultSourceRevision,
                submission.FinalizedAt,
            };
        });
        var nextCursor = submissions.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                route,
                filterBinding,
                hasMore,
                new StudentResultCursorPosition(
                    submissions[^1].TestSession.TestDate,
                    submissions[^1].FinalizedAt!.Value,
                    submissions[^1].Id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record StudentResultCursorPosition(
        DateOnly TestDate,
        DateTimeOffset FinalizedAt,
        string Id);

    private static object ToFinalizedResult(
        SubmissionEntity submission,
        GradingRunEntity run)
    {
        var questions = run.QuestionResults
            .OrderBy(result => result.Question.OrderIndex)
            .Select(result =>
            {
                var revision = CurrentRevision(result);
                return new
                {
                    result.Id,
                    displayLabel = result.Question.DisplayLabel,
                    result.Question.QuestionText,
                    awardedPointsMilli = revision.AwardedPointsMilli,
                    maxPointsMilli = result.MaximumPointsMilli,
                    pointIncrementMilli = result.Question.PointIncrementMilli,
                    outcome = revision.Outcome,
                    transcription =
                        revision.AnswerTextCorrection ?? result.TranscribedAnswer,
                    reason = revision.ReasonCode,
                    kanjiRuleOutcome = result.KanjiCheck,
                    cropAvailable = result.AnswerCropFileReferenceId is not null
                        && submission.ScanPayloadState == "scan_available",
                    cropUrl = (string?)null,
                    overridden = revision.Source == "teacher_override",
                    sourceResultRevision = revision.RevisionNumber,
                };
            })
            .ToArray();
        return new
        {
            submissionId = submission.Id,
            student = submission.AssignedStudent is null
                ? null
                : new
                {
                    submission.AssignedStudent.Id,
                    submission.AssignedStudent.StudentNumber,
                    submission.AssignedStudent.DisplayName,
                },
            testSessionId = submission.TestSessionId,
            submission.TestSession.TestDate,
            testTitle = submission.TestSession.TitleOverride
                ?? submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            templateVersionNumber =
                submission.TestSession.TemplateVersion.VersionNumber,
            subject = submission.TestSession.TemplateSubjectSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Subject,
            category = submission.TestSession.TemplateCategorySnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Category,
            gradingRunId = run.Id,
            run.EarnedPointsMilli,
            run.PossiblePointsMilli,
            percentageBasisPoints = PercentageBasisPoints(
                run.EarnedPointsMilli,
                run.PossiblePointsMilli),
            resultRevision = submission.Revision,
            run.ResultSourceRevision,
            status = submission.State,
            scanAvailable = submission.ScanPayloadState == "scan_available",
            submission.ScanDeletedAt,
            submission.ScanDeletionReason,
            questions,
            submission.FinalizedAt,
        };
    }

    private static async Task<SubmissionEntity?> LoadSubmissionWithCurrentRunAsync(
        OokiGraderDbContext db,
        string submissionId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<SubmissionEntity> query = db.Submissions;
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query
            .Include(submission => submission.AssignedStudent)
            .Include(submission => submission.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.TestTemplate)
            .Include(submission => submission.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
                    .ThenInclude(result => result.Question)
            .Include(submission => submission.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
                    .ThenInclude(result => result.Revisions)
            .SingleOrDefaultAsync(
                submission => submission.Id == submissionId,
                cancellationToken);
    }

    private static async Task<Dictionary<string, long>?> LoadExactQuestionSetAsync(
        OokiGraderDbContext db,
        string templateVersionId,
        ICollection<QuestionResultEntity> results,
        CancellationToken cancellationToken)
    {
        var questions = await db.Questions
            .AsNoTracking()
            .Where(question => question.TemplateVersionId == templateVersionId)
            .ToDictionaryAsync(
                question => question.Id,
                question => question.MaxPointsMilli,
                cancellationToken);
        if (questions.Count != results.Count
            || results.Any(result =>
                !questions.TryGetValue(result.QuestionId, out var maximum)
                || maximum != result.MaximumPointsMilli))
        {
            return null;
        }

        return questions;
    }

    private static long SumCurrentPoints(
        ICollection<QuestionResultEntity> results,
        string? replacementResultId,
        long? replacementPoints)
    {
        long total = 0;
        foreach (var result in results)
        {
            var points = result.Id == replacementResultId
                ? replacementPoints!.Value
                : CurrentRevision(result).AwardedPointsMilli;
            if (points < 0 || points > result.MaximumPointsMilli)
            {
                throw new InvalidOperationException(
                    "Persisted result points are outside the question maximum.");
            }

            total = checked(total + points);
        }

        return total;
    }

    private static ResultRevisionEntity CurrentRevision(QuestionResultEntity result)
    {
        return result.Revisions.SingleOrDefault(
                revision => revision.Id == result.CurrentRevisionId)
            ?? throw new InvalidOperationException(
                "A question result has no current revision.");
    }

    private static string CurrentOutcome(QuestionResultEntity result) =>
        CurrentRevision(result).Outcome;

    private static bool IsExplicitlyUnidentified(SubmissionEntity submission)
    {
        return submission.AssignedStudentId is null
            && submission.AssignmentMethod == "none"
            && submission.AssignmentEvidenceJson
                == """{"disposition":"unidentified"}""";
    }

    private static int PercentageBasisPoints(long earned, long possible)
    {
        if (possible <= 0)
        {
            return 0;
        }

        var scaled = ((BigInteger)earned * 10_000) / possible;
        return (int)BigInteger.Min(10_000, BigInteger.Max(0, scaled));
    }

    private static IResult? CheckSubmissionRevision(
        HttpContext context,
        SubmissionEntity submission,
        long sourceRevision)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                sourceRevision,
                out var expectedRevision))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status428PreconditionRequired,
                "REVISION_REQUIRED",
                "更新条件が必要です",
                "最新の提出物を再読み込みしてから操作してください。");
        }

        if (submission.Revision != expectedRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_STALE",
                "提出物が更新されています",
                "最新の状態を確認してから再実行してください。",
                [new { currentRevision = submission.Revision }]);
        }

        return null;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string eventType,
        string objectId,
        string reasonCode)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = eventType.StartsWith("result.", StringComparison.Ordinal)
                ? "question_result"
                : "submission",
            ObjectId = objectId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = context.TraceIdentifier,
        });
    }

    private static void AddOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        HttpContext context,
        string aggregateId,
        string eventType,
        object payload)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(1)),
            AggregateType = "submission",
            AggregateId = aggregateId,
            EventType = eventType,
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(payload),
            CorrelationId = context.TraceIdentifier,
            OccurredAt = now,
        });
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        HttpContext context,
        string submissionId,
        string state)
    {
        AddOutbox(
            db,
            now.AddMilliseconds(1),
            context,
            submissionId,
            "submission.status",
            new { submissionId, state });
    }

    private static bool ValidReasonCode(string? reasonCode)
    {
        return !string.IsNullOrWhiteSpace(reasonCode)
            && reasonCode.Length <= 100
            && ReasonCodePattern().IsMatch(reasonCode);
    }

    private static bool OutcomeMatchesPoints(
        string outcome,
        long awardedPointsMilli,
        long maximumPointsMilli) => outcome switch
        {
            "correct" => awardedPointsMilli == maximumPointsMilli,
            "partial" => awardedPointsMilli > 0
                && awardedPointsMilli < maximumPointsMilli,
            "incorrect" or "blank" or "unreadable" =>
                awardedPointsMilli == 0,
            _ => false,
        };

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult ArchivedSessionReadOnly(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "TEST_SESSION_ARCHIVED_READ_ONLY",
            "アーカイブ済みのテスト実施は変更できません",
            "過去の採点結果は閲覧できますが、修正、確定、再オープンはできません。");

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();

    private sealed record OverrideResultBody(
        int SourceResultRevision,
        long AwardedPointsMilli,
        string Outcome,
        string? TranscriptionCorrection,
        string ReasonCode,
        string? Note);

    private sealed record FinalizeBody(long SourceRevision);

    private sealed record ReopenBody(
        long SourceRevision,
        string ReasonCode,
        string? Note);
}
