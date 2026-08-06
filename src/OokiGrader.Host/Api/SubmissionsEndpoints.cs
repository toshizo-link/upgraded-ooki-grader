using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Api;

public static partial class SubmissionsEndpoints
{
    private static readonly string[] DuplicateResolutions =
        ["additionalAttempt", "replaceCanonical"];
    private const string SubmissionsListRoute = "GET:/api/v1/submissions";

    public static IEndpointRouteBuilder MapSubmissionsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/submissions")
            .WithTags("Submissions");
        group.MapGet("/", ListSubmissions).RequireAuthorization("results");
        group.MapGet("/{submissionId}", GetSubmission).RequireAuthorization("results");
        group.MapPost("/{submissionId}:assignStudent", AssignStudent)
            .RequireAuthorization("teacher");
        group.MapPost("/{submissionId}:markUnidentified", MarkUnidentified)
            .RequireAuthorization("teacher");
        group.MapPost("/{submissionId}:queueGrading", QueueGrading)
            .RequireAuthorization("teacher");
        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates these predicates to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListSubmissions(
        HttpContext context,
        string? sessionId,
        string? testSessionId,
        string? state,
        bool? assigned,
        string? cursor,
        int? limit,
        int? pageSize,
        string? search,
        DateOnly? from,
        DateOnly? to,
        DateOnly? finalizedOn,
        string? sort,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? limit ?? 50, 1, 200);
        var query = db.Submissions
            .AsNoTracking()
            .Include(submission => submission.AssignedStudent)
            .Include(submission => submission.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.TestTemplate)
            .Include(submission => submission.GradingRuns)
            .AsQueryable();
        var readOnlyReviewer = IsReadOnlyReviewerOnly(principal);
        if (readOnlyReviewer)
        {
            query = query.Where(submission =>
                submission.FinalizedAt != null
                && submission.VoidedAt == null);
        }

        var requestedSessionId = CursorPagination.TrimToNull(
            string.IsNullOrWhiteSpace(testSessionId)
                ? sessionId
                : testSessionId);
        if (requestedSessionId is not null)
        {
            query = query.Where(
                submission => submission.TestSessionId == requestedSessionId);
        }

        string? normalizedState = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (state.Length > 64)
            {
                return Results.BadRequest();
            }

            normalizedState = NormalizeState(state);
            if (readOnlyReviewer && normalizedState != "finalized")
            {
                query = query.Where(_ => false);
            }

            query = query.Where(submission => submission.State == normalizedState);
        }

        if (assigned.HasValue)
        {
            query = assigned.Value
                ? query.Where(submission => submission.AssignedStudentId != null)
                : query.Where(submission => submission.AssignedStudentId == null);
        }

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

        if (finalizedOn.HasValue)
        {
            var timeZoneId = await db.SiteSettings
                .AsNoTracking()
                .Select(settings => settings.TimeZone)
                .SingleAsync(cancellationToken);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var localStart = finalizedOn.Value.ToDateTime(
                TimeOnly.MinValue,
                DateTimeKind.Unspecified);
            var localEnd = localStart.AddDays(1);
            var start = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone),
                TimeSpan.Zero);
            var end = new DateTimeOffset(
                TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone),
                TimeSpan.Zero);
            query = query.Where(submission =>
                submission.FinalizedAt >= start
                && submission.FinalizedAt < end);
        }

        var normalizedSearch = CursorPagination.TrimToNull(search);
        if (normalizedSearch is not null)
        {
            if (normalizedSearch.Length > 200)
            {
                return Results.BadRequest();
            }

            query = query.Where(submission =>
                (submission.OriginalFileName != null
                    && submission.OriginalFileName.Contains(normalizedSearch))
                || (submission.AssignedStudent != null
                    && (submission.AssignedStudent.DisplayName
                            .Contains(normalizedSearch)
                        || submission.AssignedStudent.StudentNumber
                            .Contains(normalizedSearch)))
                || submission.TestSession.TemplateVersion.TestTemplate.Title
                    .Contains(normalizedSearch));
        }

        if (sort is not (null or "" or "-updatedAt"))
        {
            return Results.BadRequest();
        }

        var normalizedSort = sort == "-updatedAt"
            ? "-updatedAt,-createdAt,id"
            : "-uploadCompletedAt,-createdAt,id";
        var filterBinding = CursorPagination.Bind(
            ("assigned", assigned?.ToString(CultureInfo.InvariantCulture)
                .ToLowerInvariant()),
            ("finalizedOn", finalizedOn?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("from", from?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("search", normalizedSearch),
            ("sessionId", requestedSessionId),
            ("sort", normalizedSort),
            ("state", normalizedState),
            ("to", to?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("visibility", readOnlyReviewer ? "finalized-only" : "full"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                SubmissionsListRoute,
                filterBinding,
                out SubmissionCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128
                || (sort == "-updatedAt" && position.PrimaryAt is null)))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            if (sort == "-updatedAt")
            {
                query = query.Where(submission =>
                    submission.UpdatedAt < position.PrimaryAt!.Value
                    || (submission.UpdatedAt == position.PrimaryAt.Value
                        && (submission.CreatedAt < position.CreatedAt
                            || (submission.CreatedAt == position.CreatedAt
                                && string.Compare(
                                    submission.Id,
                                    position.Id) > 0))));
            }
            else if (position.PrimaryAt is null)
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt == null
                    && (submission.CreatedAt < position.CreatedAt
                        || (submission.CreatedAt == position.CreatedAt
                            && string.Compare(
                                submission.Id,
                                position.Id) > 0)));
            }
            else
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt == null
                    || submission.UploadCompletedAt < position.PrimaryAt.Value
                    || (submission.UploadCompletedAt == position.PrimaryAt.Value
                        && (submission.CreatedAt < position.CreatedAt
                            || (submission.CreatedAt == position.CreatedAt
                                && string.Compare(
                                    submission.Id,
                                    position.Id) > 0))));
            }
        }

        var ordered = sort == "-updatedAt"
            ? query.OrderByDescending(submission => submission.UpdatedAt)
            : query.OrderByDescending(submission => submission.UploadCompletedAt);
        var submissions = await ordered
            .ThenByDescending(submission => submission.CreatedAt)
            .ThenBy(submission => submission.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = submissions.Count > take;
        if (hasMore)
        {
            submissions.RemoveAt(take);
        }

        var submissionIds = submissions
            .Select(submission => submission.Id)
            .ToArray();
        var exportStates = await LoadLatestExportStatesAsync(
            db,
            submissionIds,
            cancellationToken);
        var nextCursor = submissions.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                SubmissionsListRoute,
                filterBinding,
                hasMore,
                new SubmissionCursorPosition(
                    sort == "-updatedAt"
                        ? submissions[^1].UpdatedAt
                        : submissions[^1].UploadCompletedAt,
                    submissions[^1].CreatedAt,
                    submissions[^1].Id));

        return Results.Ok(new
        {
            items = submissions.Select(submission => ToListItem(
                submission,
                exportStates.GetValueOrDefault(submission.Id))),
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record SubmissionCursorPosition(
        DateTimeOffset? PrimaryAt,
        DateTimeOffset CreatedAt,
        string Id);

    private static async Task<IResult> GetSubmission(
        string submissionId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .AsNoTracking()
            .Include(item => item.AssignedStudent)
            .Include(item => item.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.TestTemplate)
            .Include(item => item.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
                    .ThenInclude(result => result.Revisions)
            .SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (IsReadOnlyReviewerOnly(context.User)
            && (submission.FinalizedAt is null || submission.VoidedAt is not null))
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        var currentRun = submission.GradingRuns.SingleOrDefault(
            run => run.Id == submission.CurrentGradingRunId);
        var visualDuplicates = await db.VisualDuplicates
            .AsNoTracking()
            .Where(item =>
                item.SubmissionId == submission.Id
                || item.CandidateSubmissionId == submission.Id)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                candidateSubmissionId = item.SubmissionId == submission.Id
                    ? item.CandidateSubmissionId
                    : item.SubmissionId,
                item.HammingDistance,
                item.State,
                item.CreatedAt,
                item.ResolvedAt,
            })
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new
        {
            submission.Id,
            testSessionId = submission.TestSessionId,
            sessionName = submission.TestSession.TitleOverride,
            submission.TestSession.TestDate,
            templateId = submission.TestSession.TemplateVersion.TestTemplateId,
            templateVersionId = submission.TestSession.TemplateVersionId,
            templateTitle =
                submission.TestSession.TemplateVersion.TestTemplate.Title,
            testTitle = submission.TestSession.TemplateVersion.TestTemplate.Title,
            submission.State,
            submission.ScanPayloadState,
            submission.ScanDeletedAt,
            submission.ScanDeletionReason,
            submission.AssignmentMethod,
            submission.AssignmentConfidenceBasisPoints,
            assignedStudent = submission.AssignedStudent is null
                ? null
                : new
                {
                    submission.AssignedStudent.Id,
                    submission.AssignedStudent.StudentNumber,
                    submission.AssignedStudent.DisplayName,
                },
            studentId = submission.AssignedStudent?.Id,
            studentDisplayName = submission.AssignedStudent?.DisplayName,
            studentNumber = submission.AssignedStudent?.StudentNumber,
            submission.AttemptNumber,
            submission.CanonicalForSession,
            submission.OriginalFileName,
            fileName = submission.OriginalFileName,
            submission.UploadCompletedAt,
            uploadedAt = submission.UploadCompletedAt,
            submission.PageCount,
            submission.QualitySummaryJson,
            visualDuplicates,
            currentGradingRun = currentRun is null
                ? null
                : new
                {
                    currentRun.Id,
                    currentRun.RunNumber,
                    currentRun.State,
                    currentRun.EarnedPointsMilli,
                    currentRun.PossiblePointsMilli,
                    currentRun.ResultSourceRevision,
                    blockingReviewCount = currentRun.QuestionResults.Count(
                        result => result.ReviewRequired
                            && result.ReviewStatus != "resolved"),
                    resultCount = currentRun.QuestionResults.Count,
                    currentRun.CreatedAt,
                    currentRun.FinishedAt,
                },
            submission.FinalizedAt,
            submission.VoidedAt,
            submission.Revision,
            submission.CreatedAt,
            submission.UpdatedAt,
            totalEarnedPointsMilli = currentRun?.EarnedPointsMilli,
            totalPossiblePointsMilli = currentRun?.PossiblePointsMilli,
            finalizationChecks = new[]
            {
                new
                {
                    key = "studentAssigned",
                    label = "生徒または未特定の扱いが確認されています",
                    passed = submission.AssignedStudentId is not null
                        || IsExplicitlyUnidentified(submission),
                },
                new
                {
                    key = "gradingComplete",
                    label = "採点結果があります",
                    passed = currentRun is not null,
                },
                new
                {
                    key = "reviewsResolved",
                    label = "要確認項目が解決済みです",
                    passed = currentRun is not null
                        && currentRun.QuestionResults.All(result =>
                            !result.ReviewRequired
                            || result.ReviewStatus == "resolved"),
                },
            },
        });
    }

    private static async Task<IResult> AssignStudent(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] AssignStudentBody request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .Include(item => item.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.Questions)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.SourceRevision,
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
                "最新の状態を確認してから、もう一度割り当ててください。",
                [new { currentRevision = submission.Revision }]);
        }

        if (string.IsNullOrWhiteSpace(request.StudentId)
            || !ValidReasonCode(request.ReasonCode)
            || request.Note?.Length > 1_000)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "ASSIGNMENT_INVALID",
                "生徒を割り当てられません",
                "生徒、理由、またはメモを確認してください。");
        }

        if (submission.VoidedAt is not null
            || submission.State is not (
                "needs_name_review"
                or "awaiting_name"
                or "awaiting_grading"
                or "grading"
                or "needs_grade_review"
                or "ready_to_finalize"
                or "finalized"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "SUBMISSION_ASSIGNMENT_STATE_INVALID",
                "現在の状態では割り当てられません",
                $"現在の状態は {submission.State} です。");
        }

        var student = await db.Students
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == request.StudentId && item.Status == "active",
                cancellationToken);
        if (student is null)
        {
            return Results.NotFound();
        }

        if (submission.AssignedStudentId == student.Id
            && (submission.CanonicalForSession
                || (request.DuplicateResolution == "additionalAttempt"
                    && submission.AttemptNumber > 1)))
        {
            ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
            return Results.Ok(new
            {
                submission.Id,
                assignedStudentId = student.Id,
                assignmentMethod = submission.AssignmentMethod,
                submission.State,
                jobId = (string?)null,
                submission.AttemptNumber,
                submission.CanonicalForSession,
                submission.Revision,
            });
        }

        var duplicate = await db.Submissions.SingleOrDefaultAsync(
            item => item.Id != submission.Id
                && item.TestSessionId == submission.TestSessionId
                && item.AssignedStudentId == student.Id
                && item.CanonicalForSession
                && item.VoidedAt == null,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (duplicate is not null
            && request.DuplicateResolution is not (
                "additionalAttempt" or "replaceCanonical"))
        {
            var visualDistance = await RecordPossibleVisualDuplicateAsync(
                db,
                submission,
                duplicate,
                principal,
                context,
                now,
                cancellationToken);
            var nextAttemptNumber = await NextAttemptNumberAsync(
                db,
                submission.TestSessionId,
                student.Id,
                cancellationToken);
            return DuplicateAssignmentProblem(
                context,
                duplicate,
                nextAttemptNumber,
                visualDistance);
        }

        var previousStudentId = submission.AssignedStudentId;
        var wasExplicitlyUnidentified = IsExplicitlyUnidentified(submission);
        var duplicateResolution = request.DuplicateResolution;
        await using var replacementTransaction =
            duplicate is not null && duplicateResolution == "replaceCanonical"
                ? await db.Database.BeginTransactionAsync(cancellationToken)
                : null;
        if (duplicate is not null)
        {
            await ResolveVisualDuplicateAsync(
                db,
                submission.Id,
                duplicate.Id,
                ApiHelpers.StaffId(principal),
                now,
                cancellationToken);
            var nextAttemptNumber = await NextAttemptNumberAsync(
                db,
                submission.TestSessionId,
                student.Id,
                cancellationToken);
            if (duplicateResolution == "replaceCanonical")
            {
                duplicate.CanonicalForSession = false;
                duplicate.AttemptNumber = nextAttemptNumber;
                await db.SaveChangesAsync(cancellationToken);
                submission.CanonicalForSession = true;
                submission.AttemptNumber = 1;
            }
            else
            {
                submission.CanonicalForSession = false;
                submission.AttemptNumber = request.AttemptNumber
                    ?? nextAttemptNumber;
                if (submission.AttemptNumber < 2
                    || await db.Submissions.AnyAsync(
                        item => item.Id != submission.Id
                            && item.TestSessionId == submission.TestSessionId
                            && item.AssignedStudentId == student.Id
                            && item.AttemptNumber == submission.AttemptNumber
                            && item.VoidedAt == null,
                        cancellationToken))
                {
                    return ApiHelpers.Problem(
                        context,
                        StatusCodes.Status409Conflict,
                        "ATTEMPT_NUMBER_DUPLICATE",
                        "受験回を保存できません",
                        "次の受験回番号を使用してください。",
                        [new { nextAttemptNumber }]);
                }
            }
        }
        else
        {
            submission.CanonicalForSession = true;
            submission.AttemptNumber = 1;
        }

        submission.AssignedStudentId = student.Id;
        submission.AssignmentMethod = "teacher";
        submission.AssignmentConfidenceBasisPoints = null;
        submission.AssignmentPolicyVersion = null;
        submission.AssignmentEvidenceJson = null;
        BackgroundJobEntity? job = null;
        string? gradingQueueReason = null;
        if (submission.CurrentGradingRunId is null)
        {
            var grading = await PrepareGradingJobAsync(
                db,
                submission,
                submission.TestSession.TemplateVersion,
                now,
                context,
                configuration,
                cancellationToken);
            job = grading.Job;
            gradingQueueReason = grading.QueueReason;
            await CancelSupersededGradingJobsAsync(
                db,
                submission.Id,
                job.Id,
                now,
                cancellationToken);
            submission.State = "grading";
        }

        var assignmentEvent = previousStudentId is not null
            || wasExplicitlyUnidentified
            || submission.FinalizedAt is not null
                ? "submission.student_reassigned"
                : "submission.student_assigned";
        AddAudit(
            db,
            now,
            principal,
            context,
            assignmentEvent,
            submission.Id,
            request.ReasonCode,
            new
            {
                previousStudentId,
                assignedStudentId = student.Id,
                duplicateOfSubmissionId = duplicate?.Id,
                duplicateResolution,
                submission.AttemptNumber,
                submission.CanonicalForSession,
                submission.FinalizedAt,
            });
        if (duplicate is not null)
        {
            AddAudit(
                db,
                now.AddTicks(1),
                principal,
                context,
                "submission.duplicate_resolved",
                submission.Id,
                duplicateResolution == "replaceCanonical"
                    ? "replace_canonical"
                    : "additional_attempt",
                new
                {
                    existingSubmissionId = duplicate.Id,
                    selectedSubmissionId = submission.Id,
                    submission.AttemptNumber,
                    submission.CanonicalForSession,
                });
        }
        AddAssignmentOutbox(
            db,
            now,
            context,
            submission.Id,
            previousStudentId,
            student.Id,
            request.ReasonCode);
        if (job is not null)
        {
            AddAudit(
                db,
                now.AddTicks(1),
                principal,
                context,
                "submission.grading_queued",
                submission.Id,
                gradingQueueReason ?? "provider_free");
        }

        AddStatusOutbox(db, now, context, submission.Id, submission.State);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (replacementTransaction is not null)
            {
                await replacementTransaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "CANONICAL_SUBMISSION_DUPLICATE",
                "同じ生徒の答案が既にあります",
                "最新の答案一覧を確認してください。");
        }

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            submission.Id,
            assignedStudentId = student.Id,
            assignmentMethod = submission.AssignmentMethod,
            submission.State,
            jobId = job?.Id,
            submission.AttemptNumber,
            submission.CanonicalForSession,
            submission.Revision,
        });
    }

    private static async Task<IResult> MarkUnidentified(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] MarkUnidentifiedBody request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .Include(item => item.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.Questions)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.SourceRevision,
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

        if (submission.State != "needs_name_review"
            || submission.AssignedStudentId is not null
            || request.Status is not ("unidentified" or "nonStudentSample"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "UNIDENTIFIED_STATUS_INVALID",
                "この答案の状態を変更できません",
                "最新の生徒名確認状態と選択内容を確認してください。");
        }

        var now = timeProvider.GetUtcNow();
        submission.AssignmentMethod = "none";
        submission.AssignmentConfidenceBasisPoints = null;
        submission.AssignmentPolicyVersion = null;
        submission.AssignmentEvidenceJson = null;
        submission.CanonicalForSession = false;
        if (request.Status == "nonStudentSample")
        {
            submission.State = "voided";
            submission.VoidedAt = now;
            submission.VoidedByStaffUserId = ApiHelpers.StaffId(principal);
            submission.VoidReason = "non_student_sample";
        }
        else
        {
            submission.AssignmentEvidenceJson =
                """{"disposition":"unidentified"}""";
            var grading = await PrepareGradingJobAsync(
                db,
                submission,
                submission.TestSession.TemplateVersion,
                now,
                context,
                configuration,
                cancellationToken);
            submission.State = "grading";
            AddAudit(
                db,
                now.AddTicks(1),
                principal,
                context,
                "submission.grading_queued",
                submission.Id,
                grading.QueueReason);
        }

        var reasonCode = request.Status == "nonStudentSample"
            ? "non_student_sample"
            : "unidentified";
        AddAudit(
            db,
            now,
            principal,
            context,
            "submission.marked_unidentified",
            submission.Id,
            reasonCode);
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Ok(new
        {
            submission.Id,
            status = request.Status,
            submission.State,
            submission.Revision,
        });
    }

    private static async Task<IResult> QueueGrading(
        string submissionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] QueueGradingBody request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .Include(item => item.TestSession)
                .ThenInclude(session => session.TemplateVersion)
                    .ThenInclude(version => version.Questions)
            .SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.SourceRevision,
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

        if (submission.AssignedStudentId is null
            && !IsExplicitlyUnidentified(submission))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "STUDENT_ASSIGNMENT_REQUIRED",
                "生徒の確認が必要です",
                "生徒を割り当ててから採点してください。");
        }

        if (submission.CurrentGradingRunId is not null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "GRADING_RUN_EXISTS",
                "既に採点結果があります",
                "再採点は明示的な再採点操作から開始してください。");
        }

        if (submission.State != "awaiting_grading")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "SUBMISSION_GRADING_STATE_INVALID",
                "現在の状態では採点できません",
                $"現在の状態は {submission.State} です。");
        }

        var version = submission.TestSession.TemplateVersion;
        if (version.State != "published" || version.Questions.Count == 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_VERSION_INVALID",
                "採点基準を使用できません",
                "公開済みの設問を持つひな形が必要です。");
        }

        var now = timeProvider.GetUtcNow();
        var grading = await PrepareGradingJobAsync(
            db,
            submission,
            version,
            now,
            context,
            configuration,
            cancellationToken);
        var preparedJob = grading.Job;
        if (preparedJob.State == "succeeded")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "GRADING_JOB_ALREADY_COMPLETED",
                "採点処理は既に完了しています",
                "最新の提出物を再読み込みしてください。");
        }

        submission.State = "grading";
        AddAudit(
            db,
            now,
            principal,
            context,
            "submission.grading_queued",
            submission.Id,
            grading.QueueReason);
        AddStatusOutbox(db, now, context, submission.Id, submission.State);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, submission.Revision);
        return Results.Accepted(
            $"/api/v1/submissions/{submission.Id}",
            new
            {
                submission.Id,
                submission.State,
                jobId = preparedJob.Id,
                jobState = preparedJob.State,
                submission.Revision,
            });
    }

    private static async Task<GradingJobSelection> PrepareGradingJobAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        TemplateVersionEntity version,
        DateTimeOffset now,
        HttpContext context,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        AiTaskProfileEntity? profile = null;
        var geminiDirectEnabled = configuration.GetValue(
            "Features:Ai.GeminiDirect",
            false);
        var openRouterEnabled = configuration.GetValue(
            "Features:Ai.OpenRouter",
            false);
        if (configuration.GetValue("Features:Grading.Semantic", false)
            && (geminiDirectEnabled || openRouterEnabled))
        {
            profile = await db.AiTaskProfiles
                .AsNoTracking()
                .Include(item => item.AiConnection)
                .Where(item =>
                    item.TaskType == AiTaskTypes.InitialGrading
                    && item.Active
                    && (item.ApprovalState == "pilot_approved"
                        || item.ApprovalState == "production_approved")
                    && item.ModelId == item.AiConnection.ModelId
                    && item.ConnectionRevision
                        == item.AiConnection.CredentialRevision
                    && (item.ProcessingStrategy == "queued_standard"
                        || item.ProcessingStrategy == "expedite_standard"
                        || item.ProcessingStrategy == "gemini_batch")
                    && ((item.AiConnection.Provider
                        == AiProviders.GeminiDirect
                            && geminiDirectEnabled)
                        || (item.AiConnection.Provider
                                == AiProviders.OpenRouter
                            && openRouterEnabled))
                    && item.AiConnection.EndpointProfile
                        == (item.AiConnection.Provider
                            == AiProviders.GeminiDirect
                                ? AiProviderCatalog.GeminiEndpointProfile
                                : AiProviderCatalog.OpenRouterEndpointProfile)
                    && (item.AiConnection.Provider
                            != AiProviders.OpenRouter
                        || item.AiConnection.ModelId
                            != AiProviderCatalog.DeepSeekV4FlashModelId)
                    && item.AiConnection.State == "active"
                    && item.AiConnection.LastCapabilityProbeState == "passed"
                    && (item.ProcessingStrategy != "gemini_batch"
                        || (item.AiConnection.Provider
                                == AiProviders.GeminiDirect
                            && item.ModelId
                                == AiInitialGradingJobWorker.ModelId
                            && item.AiConnection
                                .LastBatchCapabilityProbeState == "passed"
                            && item.AiConnection
                                    .LastBatchCapabilityProbeCredentialRevision
                                == item.AiConnection.CredentialRevision)))
                .OrderByDescending(item => item.ActivatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        var questionIds = version.Questions
            .Where(question =>
                question.TeacherVerified
                && question.MaxPointsMilli > 0
                && question.PointIncrementMilli > 0)
            .Select(question => question.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (profile is not null
            && submission.PreprocessingManifestHash is { Length: 64 }
            && questionIds.Length == version.Questions.Count)
        {
            var hasPages = await db.SubmissionPages
                .AsNoTracking()
                .AnyAsync(
                    page => page.SubmissionId == submission.Id,
                    cancellationToken);
            if (hasPages)
            {
                return new GradingJobSelection(
                    await PrepareGeminiInitialGradingJobAsync(
                        db,
                        submission,
                        version,
                        now,
                        context,
                        cancellationToken),
                    AiInitialGradingJobWorker.JobType);
            }
        }

        return new GradingJobSelection(
            await PrepareProviderFreeGradingJobAsync(
                db,
                submission,
                version,
                now,
                context,
                cancellationToken),
            "provider_free");
    }

    private static async Task<BackgroundJobEntity>
        PrepareGeminiInitialGradingJobAsync(
            OokiGraderDbContext db,
            SubmissionEntity submission,
            TemplateVersionEntity version,
            DateTimeOffset now,
            HttpContext context,
            CancellationToken cancellationToken)
    {
        var manifestHash = submission.PreprocessingManifestHash!;
        var deduplicationKey =
            $"submission:{submission.Id}:gemini-grade:{manifestHash}";
        var existing = await db.BackgroundJobs.SingleOrDefaultAsync(
            job => job.DeduplicationKey == deduplicationKey,
            cancellationToken);
        if (existing is null)
        {
            existing = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = AiInitialGradingJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = submission.TestSession.Priority == "expedite" ? 100 : 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    submissionId = submission.Id,
                    templateVersionId = version.Id,
                    manifestHash,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = context.TraceIdentifier,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(existing);
            return existing;
        }

        if (existing.State is "failed" or "blocked" or "cancelled")
        {
            existing.State = "queued";
            existing.AttemptCount = 0;
            existing.NextAttemptAt = now;
            existing.ProgressBasisPoints = 0;
            existing.CompletedAt = null;
            existing.ErrorCode = null;
            existing.SafeErrorDetail = null;
            existing.LeaseOwner = null;
            existing.LeaseExpiresAt = null;
        }

        return existing;
    }

    private static async Task<BackgroundJobEntity> PrepareProviderFreeGradingJobAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        TemplateVersionEntity version,
        DateTimeOffset now,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var manifestHash = ProviderFreeJobWorker.ComputeManifestHash(
            submission,
            version,
            version.Questions);
        var deduplicationKey =
            $"submission:{submission.Id}:provider-free-grade:{manifestHash}";
        var existing = await db.BackgroundJobs.SingleOrDefaultAsync(
            job => job.DeduplicationKey == deduplicationKey,
            cancellationToken);
        if (existing is null)
        {
            var created = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = "provider_free_grade",
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = submission.TestSession.Priority == "expedite" ? 100 : 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    submissionId = submission.Id,
                    templateVersionId = version.Id,
                    manifestHash,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = context.TraceIdentifier,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(created);
            return created;
        }

        if (existing.State is "failed" or "blocked" or "cancelled")
        {
            existing.State = "queued";
            existing.AttemptCount = 0;
            existing.NextAttemptAt = now;
            existing.ProgressBasisPoints = 0;
            existing.CompletedAt = null;
            existing.ErrorCode = null;
            existing.SafeErrorDetail = null;
            existing.LeaseOwner = null;
            existing.LeaseExpiresAt = null;
        }

        return existing;
    }

    private static async Task CancelSupersededGradingJobsAsync(
        OokiGraderDbContext db,
        string submissionId,
        string currentJobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var keyPrefix = $"submission:{submissionId}:";
        var superseded = await db.BackgroundJobs
            .Where(job =>
                job.Id != currentJobId
                && (job.Type == "provider_free_grade"
                    || job.Type == AiInitialGradingJobWorker.JobType)
                && job.DeduplicationKey.StartsWith(keyPrefix)
                && (job.State == "queued"
                    || job.State == "retry_waiting"
                    || job.State == "leased"))
            .ToListAsync(cancellationToken);
        foreach (var job in superseded)
        {
            job.State = "cancelled";
            job.CompletedAt = now;
            job.ErrorCode = "identity_assignment_superseded";
            job.SafeErrorDetail = null;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
        }
    }

    private static object ToListItem(
        SubmissionEntity submission,
        string? exportState)
    {
        var currentRun = submission.GradingRuns.SingleOrDefault(
            run => run.Id == submission.CurrentGradingRunId);
        return new
        {
            submission.Id,
            testSessionId = submission.TestSessionId,
            sessionName = submission.TestSession.TitleOverride,
            submission.TestSession.TestDate,
            templateTitle =
                submission.TestSession.TemplateVersion.TestTemplate.Title,
            testTitle = submission.TestSession.TemplateVersion.TestTemplate.Title,
            submission.State,
            submission.ScanPayloadState,
            submission.ScanDeletedAt,
            submission.ScanDeletionReason,
            assignedStudent = submission.AssignedStudent is null
                ? null
                : new
                {
                    submission.AssignedStudent.Id,
                    submission.AssignedStudent.StudentNumber,
                    submission.AssignedStudent.DisplayName,
                },
            submission.AssignmentMethod,
            submission.AttemptNumber,
            submission.CanonicalForSession,
            submission.OriginalFileName,
            fileName = submission.OriginalFileName,
            studentId = submission.AssignedStudent?.Id,
            studentDisplayName = submission.AssignedStudent?.DisplayName,
            studentNumber = submission.AssignedStudent?.StudentNumber,
            submission.UploadCompletedAt,
            uploadedAt = submission.UploadCompletedAt,
            earnedPointsMilli = currentRun?.EarnedPointsMilli,
            possiblePointsMilli = currentRun?.PossiblePointsMilli,
            totalEarnedPointsMilli = currentRun?.EarnedPointsMilli,
            totalPossiblePointsMilli = currentRun?.PossiblePointsMilli,
            percentageBasisPoints = PercentageBasisPoints(
                currentRun?.EarnedPointsMilli,
                currentRun?.PossiblePointsMilli),
            qualityWarnings = SubmissionQualityWarnings.Build(
                submission.QualitySummaryJson),
            blockingReview = currentRun?.State == "needs_grade_review",
            exportState,
            submission.FinalizedAt,
            submission.UpdatedAt,
            submission.Revision,
        };
    }

    private static int? PercentageBasisPoints(long? earned, long? possible)
    {
        if (!earned.HasValue || !possible.HasValue || possible.Value <= 0)
        {
            return null;
        }

        var scaled = ((BigInteger)earned.Value * 10_000) / possible.Value;
        return (int)BigInteger.Min(10_000, BigInteger.Max(0, scaled));
    }

    internal static async Task<IReadOnlyDictionary<string, string>>
        LoadLatestExportStatesAsync(
            OokiGraderDbContext db,
            IReadOnlyCollection<string> submissionIds,
            CancellationToken cancellationToken = default)
    {
        if (submissionIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var ids = submissionIds.ToArray();
        var states = await db.ExportRecords
            .AsNoTracking()
            .Where(record =>
                ids.Contains(record.SubmissionId)
                && record.SupersededAt == null)
            .GroupBy(record => record.SubmissionId)
            .Select(group => new
            {
                SubmissionId = group.Key,
                State = group
                    .OrderByDescending(record => record.ExportRevision)
                    .ThenByDescending(record => record.Id)
                    .Select(record => record.State)
                    .First(),
            })
            .ToArrayAsync(cancellationToken);
        return states.ToDictionary(
            item => item.SubmissionId,
            item => item.State,
            StringComparer.Ordinal);
    }

    private static async Task<int> NextAttemptNumberAsync(
        OokiGraderDbContext db,
        string testSessionId,
        string studentId,
        CancellationToken cancellationToken)
    {
        var maximum = await db.Submissions
            .Where(item => item.TestSessionId == testSessionId
                && item.AssignedStudentId == studentId
                && item.VoidedAt == null)
            .MaxAsync(
                item => (int?)item.AttemptNumber,
                cancellationToken)
            ?? 0;
        return checked(Math.Max(1, maximum) + 1);
    }

    private static IResult DuplicateAssignmentProblem(
        HttpContext context,
        SubmissionEntity existing,
        int nextAttemptNumber,
        int? visualHammingDistance) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            type:
                "https://ooki-grader.local/problems/canonical-submission-duplicate",
            title: "同じ生徒の答案が既にあります",
            detail:
                "既存答案を代表として残すか、この答案を別の受験回または代表答案として登録してください。",
            instance: context.Request.Path,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "CANONICAL_SUBMISSION_DUPLICATE",
                ["correlationId"] = context.TraceIdentifier,
                ["existingSubmissionId"] = existing.Id,
                ["existingAttemptNumber"] = existing.AttemptNumber,
                ["nextAttemptNumber"] = nextAttemptNumber,
                ["allowedResolutions"] = DuplicateResolutions,
                ["possibleVisualDuplicate"] =
                    visualHammingDistance is not null,
                ["visualHammingDistance"] = visualHammingDistance,
            });

    private static async Task<int?> RecordPossibleVisualDuplicateAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        SubmissionEntity candidate,
        ClaimsPrincipal principal,
        HttpContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var distance = await ComputePaperHammingDistanceAsync(
            db,
            submission.Id,
            candidate.Id,
            cancellationToken);
        if (distance is null || distance > 4)
        {
            return null;
        }

        var firstId = string.CompareOrdinal(submission.Id, candidate.Id) < 0
            ? submission.Id
            : candidate.Id;
        var secondId = string.Equals(
            firstId,
            submission.Id,
            StringComparison.Ordinal)
            ? candidate.Id
            : submission.Id;
        if (!await db.VisualDuplicates.AnyAsync(
                item => item.SubmissionId == firstId
                    && item.CandidateSubmissionId == secondId,
                cancellationToken))
        {
            db.VisualDuplicates.Add(new VisualDuplicateEntity
            {
                Id = UlidId.New(now),
                SubmissionId = firstId,
                CandidateSubmissionId = secondId,
                HammingDistance = distance.Value,
                State = "possible",
                CreatedAt = now,
            });
            AddAudit(
                db,
                now,
                principal,
                context,
                "submission.possible_visual_duplicate",
                submission.Id,
                "page_fingerprint_match",
                new
                {
                    candidateSubmissionId = candidate.Id,
                    hammingDistance = distance.Value,
                });
            await db.SaveChangesAsync(cancellationToken);
        }

        return distance;
    }

    private static async Task ResolveVisualDuplicateAsync(
        OokiGraderDbContext db,
        string submissionId,
        string candidateSubmissionId,
        string staffId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var firstId = string.CompareOrdinal(
            submissionId,
            candidateSubmissionId) < 0
            ? submissionId
            : candidateSubmissionId;
        var secondId = string.Equals(
            firstId,
            submissionId,
            StringComparison.Ordinal)
            ? candidateSubmissionId
            : submissionId;
        var duplicate = await db.VisualDuplicates.SingleOrDefaultAsync(
            item => item.SubmissionId == firstId
                && item.CandidateSubmissionId == secondId
                && item.State == "possible",
            cancellationToken);
        if (duplicate is null)
        {
            return;
        }

        duplicate.State = "confirmed";
        duplicate.ResolvedAt = now;
        duplicate.ResolvedByStaffUserId = staffId;
    }

    private static async Task<int?> ComputePaperHammingDistanceAsync(
        OokiGraderDbContext db,
        string submissionId,
        string candidateSubmissionId,
        CancellationToken cancellationToken)
    {
        var ids = new[] { submissionId, candidateSubmissionId };
        var pages = await db.SubmissionPages
            .AsNoTracking()
            .Where(page => ids.Contains(page.SubmissionId))
            .OrderBy(page => page.SubmissionId)
            .ThenBy(page => page.PageNumber)
            .Select(page => new
            {
                page.SubmissionId,
                page.PageNumber,
                page.PerceptualHash,
            })
            .ToArrayAsync(cancellationToken);
        var first = pages
            .Where(page => page.SubmissionId == submissionId)
            .ToArray();
        var second = pages
            .Where(page => page.SubmissionId == candidateSubmissionId)
            .ToArray();
        if (first.Length == 0 || first.Length != second.Length)
        {
            return null;
        }

        var maximumDistance = 0;
        for (var index = 0; index < first.Length; index++)
        {
            if (first[index].PageNumber != second[index].PageNumber)
            {
                return null;
            }

            try
            {
                maximumDistance = Math.Max(
                    maximumDistance,
                    Fingerprinting.HammingDistance(
                        first[index].PerceptualHash,
                        second[index].PerceptualHash));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return maximumDistance;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string eventType,
        string submissionId,
        string reasonCode,
        object? safeMetadata = null)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = "submission",
            ObjectId = submissionId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = safeMetadata is null
                ? null
                : JsonSerializer.Serialize(safeMetadata),
        });
    }

    private static void AddAssignmentOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        HttpContext context,
        string submissionId,
        string? previousStudentId,
        string assignedStudentId,
        string reasonCode)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now.AddTicks(2)),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = "submission.studentAssigned",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId,
                previousStudentId,
                assignedStudentId,
                reasonCode,
            }),
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
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now.AddMilliseconds(1)),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = "submission.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new { submissionId, state }),
            CorrelationId = context.TraceIdentifier,
            OccurredAt = now,
        });
    }

    private static bool ValidReasonCode(string? reasonCode)
    {
        return !string.IsNullOrWhiteSpace(reasonCode)
            && reasonCode.Length <= 100
            && ReasonCodePattern().IsMatch(reasonCode);
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();

    private static string NormalizeState(string state)
    {
        return state switch
        {
            "needsNameReview" => "needs_name_review",
            "needsGradeReview" => "needs_grade_review",
            "readyToFinalize" => "ready_to_finalize",
            "awaitingAi" => "awaiting_ai",
            "geminiBatchRunning" => "gemini_batch_running",
            "openRouterQueued" => "openrouter_queued",
            "budgetBlocked" => "budget_blocked",
            "needsAttention" => "needs_attention",
            "readyForReview" => "ready_for_review",
            "scanDeleted" => "scan_deleted",
            _ => state,
        };
    }

    private static bool IsExplicitlyUnidentified(SubmissionEntity submission)
    {
        return submission.AssignedStudentId is null
            && submission.AssignmentMethod == "none"
            && submission.AssignmentEvidenceJson
                == """{"disposition":"unidentified"}""";
    }

    private static bool IsReadOnlyReviewerOnly(ClaimsPrincipal principal) =>
        principal.IsInRole("readOnlyReviewer")
        && !principal.IsInRole("administrator")
        && !principal.IsInRole("teacher");

    private sealed record AssignStudentBody(
        string StudentId,
        long SourceRevision,
        string ReasonCode,
        string? Note,
        string? DuplicateResolution,
        int? AttemptNumber);

    private sealed record QueueGradingBody(long SourceRevision);

    private sealed record GradingJobSelection(
        BackgroundJobEntity Job,
        string QueueReason);

    private sealed record MarkUnidentifiedBody(
        long SourceRevision,
        string Status);
}
