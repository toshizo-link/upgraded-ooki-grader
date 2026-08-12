using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class ReviewEndpoints
{
    private const string NameQueueRoute = "GET:/api/v1/review/name";
    private const string GradingQueueRoute = "GET:/api/v1/review/grading";
    private const string FinalizeQueueRoute = "GET:/api/v1/review/finalize";

    public static IEndpointRouteBuilder MapReviewEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/review")
            .WithTags("Review")
            .RequireAuthorization("review");
        group.MapGet("/counts", GetCounts);
        group.MapGet("/name", GetNameQueue);
        group.MapGet(
            "/artifacts/{artifactId}/content",
            GetIdentityArtifact);
        group.MapGet(
            "/pages/{pageId}/content",
            GetSubmissionPage);
        group.MapGet("/grading", GetGradingQueue);
        group.MapGet("/finalize", GetFinalizeQueue);
        return endpoints;
    }

    private static async Task<IResult> GetCounts(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var nameCount = await db.Submissions.CountAsync(
            submission => submission.State == "needs_name_review"
                && submission.VoidedAt == null
                && submission.TestSession.State != "archived",
            cancellationToken);
        var gradingCount = await db.QuestionResults.CountAsync(
            result => result.ReviewRequired
                && result.ReviewStatus != "resolved"
                && result.GradingRun.Submission.CurrentGradingRunId
                    == result.GradingRunId
                && result.GradingRun.Submission.VoidedAt == null
                && result.GradingRun.Submission.FinalizedAt == null
                && result.GradingRun.Submission.TestSession.State
                    != "archived",
            cancellationToken);
        var finalizeCount = await db.Submissions.CountAsync(
            submission => submission.State == "ready_to_finalize"
                && submission.VoidedAt == null
                && submission.FinalizedAt == null
                && submission.TestSession.State != "archived",
            cancellationToken);

        return Results.Ok(new
        {
            needsNameReview = nameCount,
            needsGradeReview = gradingCount,
            readyToFinalize = finalizeCount,
            total = checked(checked(nameCount + gradingCount) + finalizeCount),
        });
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetNameQueue(
        HttpContext context,
        string? sessionId,
        string? testSessionId,
        string? cursor,
        int? limit,
        int? pageSize,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? limit ?? 50, 1, 200);
        var query = db.Submissions
            .AsNoTracking()
            .Include(submission => submission.Pages)
            .Where(submission => submission.State == "needs_name_review"
                && submission.AssignedStudentId == null
                && submission.VoidedAt == null
                && submission.TestSession.State != "archived");
        var requestedSessionId = CursorPagination.TrimToNull(
            string.IsNullOrWhiteSpace(testSessionId)
                ? sessionId
                : testSessionId);
        if (requestedSessionId is not null)
        {
            query = query.Where(
                submission => submission.TestSessionId == requestedSessionId);
        }

        var filterBinding = CursorPagination.Bind(
            ("sessionId", requestedSessionId),
            ("sort", "uploadCompletedAt,id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                NameQueueRoute,
                filterBinding,
                out SubmissionQueueCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (!ValidSubmissionQueuePosition(position))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            if (position.UploadCompletedAt is null)
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt != null
                    || (submission.UploadCompletedAt == null
                        && string.Compare(
                            submission.Id,
                            position.Id) > 0));
            }
            else
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt > position.UploadCompletedAt.Value
                    || (submission.UploadCompletedAt
                            == position.UploadCompletedAt.Value
                        && string.Compare(
                            submission.Id,
                            position.Id) > 0));
            }
        }

        var rows = await query
            .OrderBy(submission => submission.UploadCompletedAt)
            .ThenBy(submission => submission.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(take);
        }

        var items = rows.Select(CreateNameReviewItem).ToArray();
        var nextCursor = rows.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                NameQueueRoute,
                filterBinding,
                hasMore,
                new SubmissionQueueCursorPosition(
                    rows[^1].UploadCompletedAt,
                    rows[^1].Id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private static async Task<IResult> GetIdentityArtifact(
        HttpContext context,
        string artifactId,
        OokiGraderDbContext db,
        IContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(artifactId))
        {
            return Results.NotFound();
        }

        var artifact = await db.SubmissionArtifacts
            .AsNoTracking()
            .Include(item => item.Submission)
            .Include(item => item.FileReference)
                .ThenInclude(reference => reference.FileObject)
            .SingleOrDefaultAsync(
                item => item.Id == artifactId,
                cancellationToken)
            .ConfigureAwait(false);
        if (artifact is null
            || artifact.ArtifactType is not (
                "name_crop" or "student_number_crop")
            || artifact.QuestionId is not null
            || !artifact.ProviderDisclosureAllowed
            || artifact.Submission.State != "needs_name_review"
            || artifact.Submission.AssignedStudentId is not null
            || artifact.Submission.VoidedAt is not null)
        {
            return Results.NotFound();
        }

        var fileObject = artifact.FileReference.FileObject;
        if (artifact.FileReference.OwnerType != "submission_artifact"
            || artifact.FileReference.OwnerId != artifact.Id
            || artifact.FileReference.Purpose != artifact.ArtifactType
            || fileObject.State != "available"
            || fileObject.StorageClass
                != ContentStorageClass.ManagedScanDerived.ToString()
            || fileObject.VerifiedMime is not ("image/png" or "image/jpeg")
            || fileObject.Bytes <= 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "IDENTITY_CROP_UNAVAILABLE",
                "氏名欄の画像を表示できません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        var locator = new ContentObjectLocator(
            ContentStorageClass.ManagedScanDerived,
            fileObject.Sha256,
            fileObject.Bytes,
            fileObject.Extension);
        Stream stream;
        try
        {
            stream = await contentStore
                .OpenReadAsync(locator, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "IDENTITY_CROP_GONE",
                "氏名欄の画像が見つかりません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            fileObject.VerifiedMime,
            lastModified: fileObject.VerifiedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> GetSubmissionPage(
        HttpContext context,
        string pageId,
        OokiGraderDbContext db,
        IContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (!UlidId.IsCanonical(pageId))
        {
            return Results.NotFound();
        }

        var page = await db.SubmissionPages
            .AsNoTracking()
            .Include(item => item.Submission)
            .Include(item => item.NormalizedFileReference)
                .ThenInclude(reference => reference.FileObject)
            .SingleOrDefaultAsync(item => item.Id == pageId, cancellationToken)
            .ConfigureAwait(false);
        if (page is null || page.Submission.VoidedAt is not null)
        {
            return Results.NotFound();
        }

        var reference = page.NormalizedFileReference;
        var fileObject = reference.FileObject;
        if (reference.OwnerType != "submission_page"
            || reference.OwnerId != page.Id
            || reference.Purpose != "normalized_page"
            || fileObject.State != "available"
            || fileObject.StorageClass
                != ContentStorageClass.ManagedScanDerived.ToString()
            || fileObject.VerifiedMime is not ("image/png" or "image/jpeg")
            || fileObject.Bytes <= 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "SUBMISSION_PAGE_UNAVAILABLE",
                "答案ページを表示できません",
                "保存期間または保存領域を管理者に確認してください。");
        }

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
                "SUBMISSION_PAGE_GONE",
                "答案ページが見つかりません",
                "保存期間または保存領域を管理者に確認してください。");
        }

        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ETag = $"\"sha256-{fileObject.Sha256}\"";
        return Results.File(
            stream,
            fileObject.VerifiedMime,
            lastModified: fileObject.VerifiedAt,
            entityTag: null,
            enableRangeProcessing: true);
    }

    private static object CreateNameReviewItem(
        SubmissionEntity submission)
    {
        var evidence = ParseEvidence(submission.AssignmentEvidenceJson);
        var firstPage = submission.Pages
            .OrderBy(item => item.PageNumber)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var transcription = evidence?.Transcription.VisibleName
            ?? evidence?.Transcription.VisibleStudentNumber;
        var warnings = BuildIdentityWarnings(evidence)
            .Concat(SubmissionQualityWarnings.Build(
                submission.QualitySummaryJson))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();
        var candidates = evidence?.Candidates
            .Take(5)
            .Select((candidate, index) => new
            {
                candidate.StudentId,
                candidate.DisplayName,
                candidate.StudentNumber,
                candidate.Kana,
                candidate.ClassLabel,
                rank = index + 1,
                evidence = candidate.Evidence
                    .Select(IdentityEvidenceLabel)
                    .ToArray(),
                confidenceLabel =
                    $"{candidate.RankScore / 100}.{candidate.RankScore % 100:00}",
                candidate.Expected,
                candidate.StudentNumberConflict,
                candidate.NameSimilarityBasisPoints,
            })
            .ToArray()
            ?? [];

        return new
        {
            id = submission.Id,
            submissionId = submission.Id,
            sourceRevision = submission.Revision,
            transcription,
            transcribedStudentNumber =
                evidence?.Transcription.VisibleStudentNumber,
            legibility = evidence?.Transcription.Legibility,
            providerConfidenceBasisPoints =
                evidence?.Transcription.ProviderConfidenceBasisPoints,
            nameCropUrl = SubmissionPageUrl(firstPage?.Id),
            studentNumberCropUrl = (string?)null,
            candidates,
            qualityWarnings = warnings,
        };
    }

    private static NameAssignmentEvidence? ParseEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16_000)
        {
            return null;
        }

        try
        {
            var evidence =
                JsonSerializer.Deserialize<NameAssignmentEvidence>(json);
            var supported = evidence is not null
                && ((evidence.SchemaVersion == "name_assignment_evidence_v1"
                        && evidence.PipelineVersion
                            == AiNameTranscriptionJobWorker.PipelineVersion)
                    || (evidence.SchemaVersion == "name_assignment_evidence_v2"
                        && evidence.PipelineVersion
                            == AiInitialGradingJobWorker.PipelineVersion));
            return supported
                && !evidence!.AutomaticAssignmentEnabled
                ? evidence
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] BuildIdentityWarnings(
        NameAssignmentEvidence? evidence)
    {
        if (evidence is null)
        {
            return ["AIによる氏名欄の読み取りはまだ完了していません。"];
        }

        var warnings = new List<string>(2);
        if (evidence.Transcription.UnexpectedContent)
        {
            warnings.Add("氏名欄に想定外の内容が検出されました。");
        }

        var legibilityWarning = evidence.Transcription.Legibility switch
        {
            "ambiguous" => "氏名欄の文字が曖昧です。",
            "unreadable" => "氏名欄を判読できませんでした。",
            "blank" => "氏名欄が空欄です。",
            "cropped" => "氏名欄の一部が切れている可能性があります。",
            _ => null,
        };
        if (legibilityWarning is not null)
        {
            warnings.Add(legibilityWarning);
        }

        return warnings.ToArray();
    }

    private static string IdentityEvidenceLabel(string value) => value switch
    {
        "exact_student_number" => "生徒番号が完全一致",
        "exact_full_name" => "氏名が完全一致",
        "exact_alias" => "登録済み別名が完全一致",
        "exact_stored_kana" => "登録済みカナが完全一致",
        "expected_roster" => "この試験の予定名簿",
        "student_number_conflict" => "生徒番号が不一致",
        "normalized_name_similarity" => "正規化した氏名が類似",
        _ => "ローカル名簿との比較",
    };

    private static string? IdentityArtifactUrl(string? artifactId)
    {
        return artifactId is null
            ? null
            : $"/api/v1/review/artifacts/{Uri.EscapeDataString(artifactId)}/content";
    }

    private static string? SubmissionPageUrl(string? pageId) =>
        pageId is null
            ? null
            : $"/api/v1/review/pages/{Uri.EscapeDataString(pageId)}/content";

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates these predicates to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetGradingQueue(
        HttpContext context,
        string? sessionId,
        string? studentId,
        string? cursor,
        int? limit,
        int? pageSize,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? limit ?? 50, 1, 200);
        var query = db.QuestionResults
            .AsNoTracking()
            .Include(result => result.Question)
                .ThenInclude(question => question.AcceptedAnswers)
            .Include(result => result.Revisions)
            .Include(result => result.GradingRun)
                .ThenInclude(run => run.Submission)
                    .ThenInclude(submission => submission.AssignedStudent)
            .Include(result => result.GradingRun)
                .ThenInclude(run => run.Submission)
                    .ThenInclude(submission => submission.Pages)
            .Include(result => result.GradingRun)
                .ThenInclude(run => run.Submission)
                    .ThenInclude(submission => submission.TestSession)
                        .ThenInclude(session => session.TemplateVersion)
                            .ThenInclude(version => version.TestTemplate)
            .Where(result => result.ReviewRequired
                && result.ReviewStatus != "resolved"
                && result.GradingRun.Submission.CurrentGradingRunId
                    == result.GradingRunId
                && result.GradingRun.Submission.VoidedAt == null
                && result.GradingRun.Submission.FinalizedAt == null
                && result.GradingRun.Submission.TestSession.State
                    != "archived");

        var normalizedSessionId = CursorPagination.TrimToNull(sessionId);
        if (normalizedSessionId is not null)
        {
            query = query.Where(result =>
                result.GradingRun.Submission.TestSessionId == normalizedSessionId);
        }

        var normalizedStudentId = CursorPagination.TrimToNull(studentId);
        if (normalizedStudentId is not null)
        {
            query = query.Where(result =>
                result.GradingRun.Submission.AssignedStudentId
                    == normalizedStudentId);
        }

        var filterBinding = CursorPagination.Bind(
            ("sessionId", normalizedSessionId),
            ("sort", "uploadCompletedAt,submissionId,questionOrder,id"),
            ("studentId", normalizedStudentId));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                GradingQueueRoute,
                filterBinding,
                out GradingQueueCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.SubmissionId)
                || position.SubmissionId.Length > 128
                || position.QuestionOrder < 0
                || string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            if (position.UploadCompletedAt is null)
            {
                query = query.Where(result =>
                    result.GradingRun.Submission.UploadCompletedAt != null
                    || (result.GradingRun.Submission.UploadCompletedAt == null
                        && (string.Compare(
                                result.GradingRun.SubmissionId,
                                position.SubmissionId) > 0
                            || (result.GradingRun.SubmissionId
                                    == position.SubmissionId
                                && (result.Question.OrderIndex
                                        > position.QuestionOrder
                                    || (result.Question.OrderIndex
                                            == position.QuestionOrder
                                        && string.Compare(
                                            result.Id,
                                            position.Id) > 0))))));
            }
            else
            {
                query = query.Where(result =>
                    result.GradingRun.Submission.UploadCompletedAt
                        > position.UploadCompletedAt.Value
                    || (result.GradingRun.Submission.UploadCompletedAt
                            == position.UploadCompletedAt.Value
                        && (string.Compare(
                                result.GradingRun.SubmissionId,
                                position.SubmissionId) > 0
                            || (result.GradingRun.SubmissionId
                                    == position.SubmissionId
                                && (result.Question.OrderIndex
                                        > position.QuestionOrder
                                    || (result.Question.OrderIndex
                                            == position.QuestionOrder
                                        && string.Compare(
                                            result.Id,
                                            position.Id) > 0))))));
            }
        }

        var rows = await query
            .OrderBy(result => result.GradingRun.Submission.UploadCompletedAt)
            .ThenBy(result => result.GradingRun.SubmissionId)
            .ThenBy(result => result.Question.OrderIndex)
            .ThenBy(result => result.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(take);
        }

        var items = rows.Select(result =>
        {
            var revision = result.Revisions.SingleOrDefault(
                item => item.Id == result.CurrentRevisionId);
            return new
            {
                id = result.Id,
                resultId = result.Id,
                submissionId = result.GradingRun.SubmissionId,
                gradingRunId = result.GradingRunId,
                result.QuestionId,
                questionLabel = result.Question.DisplayLabel,
                result.Question.QuestionText,
                studentDisplayName =
                    result.GradingRun.Submission.AssignedStudent?.DisplayName,
                testTitle = result.GradingRun.Submission.TestSession.TitleOverride
                    ?? result.GradingRun.Submission.TestSession.TemplateTitleSnapshot
                    ?? result.GradingRun.Submission.TestSession
                        .TemplateVersion.TestTemplate.Title,
                testDate = result.GradingRun.Submission.TestSession.TestDate,
                expectedAnswers = result.Question.AcceptedAnswers
                    .OrderBy(answer => answer.Id)
                    .Select(answer => answer.AnswerText)
                    .ToArray(),
                transcription =
                    revision?.AnswerTextCorrection ?? result.TranscribedAnswer,
                answerCropUrl = SubmissionPageUrl(
                    result.GradingRun.Submission.Pages
                        .OrderBy(page => page.PageNumber)
                        .Select(page => page.Id)
                        .FirstOrDefault()),
                proposedPointsMilli =
                    revision?.AwardedPointsMilli ?? result.ProposedPointsMilli,
                maxPointsMilli = result.MaximumPointsMilli,
                pointIncrementMilli = result.Question.PointIncrementMilli,
                proposedOutcome = revision?.Outcome ?? result.Outcome,
                reason = revision?.ReasonCode ?? result.ReasonCode,
                kanjiRequired = !result.Question.AllowNonKanji,
                warning = revision?.ReasonCode ?? result.ReasonCode,
                qualityWarnings = SubmissionQualityWarnings.Build(
                    result.GradingRun.Submission.QualitySummaryJson),
                result.ReviewStatus,
                sourceResultRevision = revision?.RevisionNumber ?? 0,
                result.GradingRun.ResultSourceRevision,
                submissionRevision = result.GradingRun.Submission.Revision,
            };
        });
        var nextCursor = rows.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                GradingQueueRoute,
                filterBinding,
                hasMore,
                new GradingQueueCursorPosition(
                    rows[^1].GradingRun.Submission.UploadCompletedAt,
                    rows[^1].GradingRun.SubmissionId,
                    rows[^1].Question.OrderIndex,
                    rows[^1].Id));

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
    private static async Task<IResult> GetFinalizeQueue(
        HttpContext context,
        string? sessionId,
        string? testSessionId,
        string? cursor,
        int? limit,
        int? pageSize,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? limit ?? 50, 1, 200);
        var query = db.Submissions
            .AsNoTracking()
            .Include(submission => submission.AssignedStudent)
            .Include(submission => submission.GradingRuns)
            .Where(submission => submission.State == "ready_to_finalize"
                && submission.FinalizedAt == null
                && submission.VoidedAt == null
                && submission.CurrentGradingRunId != null
                && submission.TestSession.State != "archived");
        var requestedSessionId = CursorPagination.TrimToNull(
            string.IsNullOrWhiteSpace(testSessionId)
                ? sessionId
                : testSessionId);
        if (requestedSessionId is not null)
        {
            query = query.Where(
                submission => submission.TestSessionId == requestedSessionId);
        }

        var filterBinding = CursorPagination.Bind(
            ("sessionId", requestedSessionId),
            ("sort", "uploadCompletedAt,id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                FinalizeQueueRoute,
                filterBinding,
                out SubmissionQueueCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (!ValidSubmissionQueuePosition(position))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            if (position.UploadCompletedAt is null)
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt != null
                    || (submission.UploadCompletedAt == null
                        && string.Compare(
                            submission.Id,
                            position.Id) > 0));
            }
            else
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt > position.UploadCompletedAt.Value
                    || (submission.UploadCompletedAt
                            == position.UploadCompletedAt.Value
                        && string.Compare(
                            submission.Id,
                            position.Id) > 0));
            }
        }

        var rows = await query
            .OrderBy(submission => submission.UploadCompletedAt)
            .ThenBy(submission => submission.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(take);
        }

        var items = rows.Select(submission =>
        {
            var run = submission.GradingRuns.Single(
                candidate => candidate.Id == submission.CurrentGradingRunId);
            return new
            {
                submission.Id,
                fileName = submission.OriginalFileName,
                studentId = submission.AssignedStudentId,
                studentDisplayName = submission.AssignedStudent?.DisplayName,
                studentNumber = submission.AssignedStudent?.StudentNumber,
                submission.State,
                totalEarnedPointsMilli = run.EarnedPointsMilli,
                totalPossiblePointsMilli = run.PossiblePointsMilli,
                uploadedAt = submission.UploadCompletedAt,
                submission.UpdatedAt,
                submission.Revision,
            };
        });
        var nextCursor = rows.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                FinalizeQueueRoute,
                filterBinding,
                hasMore,
                new SubmissionQueueCursorPosition(
                    rows[^1].UploadCompletedAt,
                    rows[^1].Id));

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private static bool ValidSubmissionQueuePosition(
        SubmissionQueueCursorPosition? position) =>
        position is null
        || (!string.IsNullOrEmpty(position.Id)
            && position.Id.Length <= 128);

    private sealed record SubmissionQueueCursorPosition(
        DateTimeOffset? UploadCompletedAt,
        string Id);

    private sealed record GradingQueueCursorPosition(
        DateTimeOffset? UploadCompletedAt,
        string SubmissionId,
        int QuestionOrder,
        string Id);
}
