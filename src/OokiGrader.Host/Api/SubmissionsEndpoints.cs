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
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Api;

public static partial class SubmissionsEndpoints
{
    private static readonly string[] DuplicateResolutions =
        ["additionalAttempt", "replaceCanonical"];
    private static readonly HashSet<string> AllowedSubmissionListStates =
        new(StringComparer.Ordinal)
        {
            "uploading",
            "validating",
            "preprocessing",
            "duplicate_pending",
            "awaiting_name",
            "awaiting_grading",
            "awaiting_ai",
            "grading",
            "gemini_batch_running",
            "openrouter_queued",
            "budget_blocked",
            "needs_attention",
            "needs_name_review",
            "needs_grade_review",
            "ready_for_review",
            "ready_to_finalize",
            "finalized",
            "failed",
            "voided",
            "scan_deleted",
        };
    private const string SubmissionsListRoute = "GET:/api/v1/submissions";

    public static IEndpointRouteBuilder MapSubmissionsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/submissions")
            .WithTags("Submissions");
        group.MapGet("/", ListSubmissions)
            .RequireAuthorization("results")
            .RequireRateLimiting("search");
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
        string? studentId,
        string? templateId,
        string? subject,
        string? category,
        string? course,
        string? @class,
        bool? finalizedOnly,
        bool? includeFacets,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (!ListQuery.TryPageSize(
                context,
                pageSize,
                limit,
                out var take,
                out var pageSizeError))
        {
            return pageSizeError!;
        }

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
            if (!AllowedSubmissionListStates.Contains(normalizedState))
            {
                return ListQuery.Invalid(
                    context,
                    "state に認識できる答案状態を指定してください。");
            }

            if (readOnlyReviewer && normalizedState != "finalized")
            {
                query = query.Where(_ => false);
            }

            query = normalizedState switch
            {
                "awaiting_ai" => query.Where(submission =>
                    submission.State == "awaiting_name"
                    || submission.State == "awaiting_grading"
                    || submission.State == "grading"),
                "ready_for_review" => query.Where(submission =>
                    submission.State == "needs_name_review"
                    || submission.State == "needs_grade_review"
                    || submission.State == "ready_to_finalize"),
                "scan_deleted" => query.Where(submission =>
                    submission.ScanPayloadState == "scan_deleted"),
                _ => query.Where(submission =>
                    submission.State == normalizedState),
            };
        }

        var reportQuery = normalizedState == "finalized"
            || finalizedOnly == true;
        if (finalizedOnly == true
            && normalizedState is not null
            && normalizedState != "finalized")
        {
            return ListQuery.Invalid(
                context,
                "finalizedOnly=true の場合、state は finalized を指定してください。");
        }

        if (finalizedOnly == true && normalizedState is null)
        {
            query = query.Where(submission => submission.State == "finalized");
        }

        if (reportQuery)
        {
            query = query.Where(submission =>
                submission.FinalizedAt != null
                && submission.VoidedAt == null);
        }

        if (assigned.HasValue)
        {
            query = assigned.Value
                ? query.Where(submission => submission.AssignedStudentId != null)
                : query.Where(submission => submission.AssignedStudentId == null);
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return ListQuery.Invalid(
                context,
                "from は to と同じ日付またはそれ以前を指定してください。");
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

        if (!ListQuery.TryTrimFilter(
                context,
                studentId,
                "studentId",
                out var normalizedStudentId,
                out var filterError,
                ListQuery.MaximumIdLength)
            || !ListQuery.TryTrimFilter(
                context,
                templateId,
                "templateId",
                out var normalizedTemplateId,
                out filterError,
                ListQuery.MaximumIdLength)
            || !ListQuery.TryTrimFilter(
                context,
                subject,
                "subject",
                out var normalizedSubject,
                out filterError)
            || !ListQuery.TryTrimFilter(
                context,
                category,
                "category",
                out var normalizedCategory,
                out filterError)
            || !ListQuery.TryTrimFilter(
                context,
                course,
                "course",
                out var normalizedCourse,
                out filterError)
            || !ListQuery.TryTrimFilter(
                context,
                @class,
                "class",
                out var normalizedClass,
                out filterError))
        {
            return filterError!;
        }

        if (normalizedStudentId is not null)
        {
            query = query.Where(submission =>
                submission.AssignedStudentId == normalizedStudentId);
        }

        if (normalizedTemplateId is not null)
        {
            query = query.Where(submission =>
                submission.TestSession.TemplateVersion.TestTemplateId
                    == normalizedTemplateId);
        }

        if (normalizedSubject is not null)
        {
            query = query.Where(submission =>
                (submission.TestSession.TemplateSubjectSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Subject)
                    == normalizedSubject);
        }

        if (normalizedCategory is not null)
        {
            query = query.Where(submission =>
                (submission.TestSession.TemplateCategorySnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Category)
                    == normalizedCategory);
        }

        if (normalizedCourse is not null)
        {
            query = query.Where(submission =>
                submission.TestSession.Course == normalizedCourse);
        }

        if (normalizedClass is not null)
        {
            query = query.Where(submission =>
                submission.TestSession.ClassLabel == normalizedClass);
        }

        if (!ListQuery.TryNormalizeSearch(
                context,
                search,
                out var normalizedSearch,
                out var searchTokens,
                out var searchError))
        {
            return searchError!;
        }

        foreach (var token in searchTokens)
        {
            var pattern = ListQuery.ContainsPattern(token);
            query = query.Where(submission =>
                (submission.OriginalFileName != null
                    && EF.Functions.Like(
                        submission.OriginalFileName,
                        pattern,
                        "\\"))
                || (submission.AssignedStudent != null
                    && (EF.Functions.Like(
                            submission.AssignedStudent.StudentNumberNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            submission.AssignedStudent.FamilyNameNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            submission.AssignedStudent.GivenNameNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            submission.AssignedStudent.FamilyNameNormalized
                                + submission.AssignedStudent.GivenNameNormalized,
                            pattern,
                            "\\")
                        || submission.AssignedStudent.Aliases.Any(alias =>
                            EF.Functions.Like(
                                alias.NormalizedValue,
                                pattern,
                                "\\"))))
                || EF.Functions.Like(
                    submission.TestSession.TemplateVersion.TestTemplate.Title,
                    pattern,
                    "\\")
                || (submission.TestSession.TemplateTitleSnapshot != null
                    && EF.Functions.Like(
                        submission.TestSession.TemplateTitleSnapshot,
                        pattern,
                        "\\"))
                || (submission.TestSession.TitleOverride != null
                    && EF.Functions.Like(
                        submission.TestSession.TitleOverride,
                        pattern,
                        "\\")));
        }

        var requestedSort = CursorPagination.TrimToNull(sort);
        var normalizedSort = requestedSort
            ?? (reportQuery ? "-testDate" : "-uploadCompletedAt");
        var validSort = normalizedSort is "-uploadCompletedAt" or "-updatedAt"
            || (reportQuery && normalizedSort is (
                "-testDate"
                or "testDate"
                or "-finalizedAt"
                or "finalizedAt"
                or "studentName"
                or "-studentName"
                or "testTitle"
                or "-testTitle"));
        if (!validSort)
        {
            return ListQuery.Invalid(
                context,
                reportQuery
                    ? "sort は testDate、finalizedAt、studentName、testTitle、updatedAt のいずれかに、必要なら先頭の - を付けて指定してください。"
                    : "sort は -uploadCompletedAt または -updatedAt を指定してください。");
        }

        var cursorSort = normalizedSort switch
        {
            "-uploadCompletedAt" => "-uploadCompletedAt,-createdAt,id",
            "-updatedAt" => "-updatedAt,-createdAt,id",
            "-testDate" => "-testDate,id",
            "testDate" => "testDate,id",
            "-finalizedAt" => "-finalizedAt,id",
            "finalizedAt" => "finalizedAt,id",
            "studentName" => "studentName,id",
            "-studentName" => "-studentName,id",
            "testTitle" => "testTitle,id",
            _ => "-testTitle,id",
        };
        var filterBinding = CursorPagination.Bind(
            ("assigned", assigned?.ToString(CultureInfo.InvariantCulture)
                .ToLowerInvariant()),
            ("category", normalizedCategory),
            ("class", normalizedClass),
            ("course", normalizedCourse),
            ("finalizedOn", finalizedOn?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("finalizedOnly", finalizedOnly?.ToString(
                CultureInfo.InvariantCulture).ToLowerInvariant()),
            ("from", from?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("search", normalizedSearch),
            ("sessionId", requestedSessionId),
            ("sort", cursorSort),
            ("state", normalizedState),
            ("studentId", normalizedStudentId),
            ("subject", normalizedSubject),
            ("templateId", normalizedTemplateId),
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
                || position.Id.Length > ListQuery.MaximumIdLength
                || (normalizedSort == "-updatedAt" && position.Timestamp is null)
                || (normalizedSort is "-testDate" or "testDate"
                    && position.Date is null)
                || (normalizedSort is "-finalizedAt" or "finalizedAt"
                    && position.Timestamp is null)
                || (normalizedSort is (
                        "studentName"
                        or "-studentName"
                        or "testTitle"
                        or "-testTitle")
                    && (position.Text is null || position.Text.Length > 1_000))))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            if (normalizedSort == "-updatedAt")
            {
                query = query.Where(submission =>
                    submission.UpdatedAt < position.Timestamp!.Value
                    || (submission.UpdatedAt == position.Timestamp.Value
                        && (submission.CreatedAt < position.CreatedAt
                            || (submission.CreatedAt == position.CreatedAt
                                && string.Compare(
                                    submission.Id,
                                    position.Id) > 0))));
            }
            else if (normalizedSort == "-uploadCompletedAt"
                && position.Timestamp is null)
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt == null
                    && (submission.CreatedAt < position.CreatedAt
                        || (submission.CreatedAt == position.CreatedAt
                            && string.Compare(
                                submission.Id,
                                position.Id) > 0)));
            }
            else if (normalizedSort == "-uploadCompletedAt")
            {
                query = query.Where(submission =>
                    submission.UploadCompletedAt == null
                    || submission.UploadCompletedAt < position.Timestamp!.Value
                    || (submission.UploadCompletedAt == position.Timestamp.Value
                        && (submission.CreatedAt < position.CreatedAt
                            || (submission.CreatedAt == position.CreatedAt
                                && string.Compare(
                                    submission.Id,
                                    position.Id) > 0))));
            }
            else
            {
                query = normalizedSort switch
                {
                    "-testDate" => query.Where(submission =>
                        submission.TestSession.TestDate < position.Date
                        || (submission.TestSession.TestDate == position.Date
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "testDate" => query.Where(submission =>
                        submission.TestSession.TestDate > position.Date
                        || (submission.TestSession.TestDate == position.Date
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "-finalizedAt" => query.Where(submission =>
                        submission.FinalizedAt < position.Timestamp
                        || (submission.FinalizedAt == position.Timestamp
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "finalizedAt" => query.Where(submission =>
                        submission.FinalizedAt > position.Timestamp
                        || (submission.FinalizedAt == position.Timestamp
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "studentName" => query.Where(submission =>
                        string.Compare(
                            submission.AssignedStudent == null
                                ? string.Empty
                                : submission.AssignedStudent.DisplayName,
                            position.Text) > 0
                        || ((submission.AssignedStudent == null
                                    ? string.Empty
                                    : submission.AssignedStudent.DisplayName)
                                == position.Text
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "-studentName" => query.Where(submission =>
                        string.Compare(
                            submission.AssignedStudent == null
                                ? string.Empty
                                : submission.AssignedStudent.DisplayName,
                            position.Text) < 0
                        || ((submission.AssignedStudent == null
                                    ? string.Empty
                                    : submission.AssignedStudent.DisplayName)
                                == position.Text
                            && string.Compare(submission.Id, position.Id) > 0)),
                    "testTitle" => query.Where(submission =>
                        string.Compare(
                            submission.TestSession.TitleOverride
                                ?? submission.TestSession.TemplateTitleSnapshot
                                ?? submission.TestSession.TemplateVersion
                                    .TestTemplate.Title,
                            position.Text) > 0
                        || ((submission.TestSession.TitleOverride
                                    ?? submission.TestSession.TemplateTitleSnapshot
                                    ?? submission.TestSession.TemplateVersion
                                        .TestTemplate.Title)
                                == position.Text
                            && string.Compare(submission.Id, position.Id) > 0)),
                    _ => query.Where(submission =>
                        string.Compare(
                            submission.TestSession.TitleOverride
                                ?? submission.TestSession.TemplateTitleSnapshot
                                ?? submission.TestSession.TemplateVersion
                                    .TestTemplate.Title,
                            position.Text) < 0
                        || ((submission.TestSession.TitleOverride
                                    ?? submission.TestSession.TemplateTitleSnapshot
                                    ?? submission.TestSession.TemplateVersion
                                        .TestTemplate.Title)
                                == position.Text
                            && string.Compare(submission.Id, position.Id) > 0)),
                };
            }
        }

        IOrderedQueryable<SubmissionEntity> ordered = normalizedSort switch
        {
            "-uploadCompletedAt" => query
                .OrderByDescending(submission => submission.UploadCompletedAt)
                .ThenByDescending(submission => submission.CreatedAt),
            "-updatedAt" => query
                .OrderByDescending(submission => submission.UpdatedAt)
                .ThenByDescending(submission => submission.CreatedAt),
            "-testDate" => query.OrderByDescending(
                submission => submission.TestSession.TestDate),
            "testDate" => query.OrderBy(
                submission => submission.TestSession.TestDate),
            "-finalizedAt" => query.OrderByDescending(
                submission => submission.FinalizedAt),
            "finalizedAt" => query.OrderBy(submission => submission.FinalizedAt),
            "studentName" => query.OrderBy(submission =>
                submission.AssignedStudent == null
                    ? string.Empty
                    : submission.AssignedStudent.DisplayName),
            "-studentName" => query.OrderByDescending(submission =>
                submission.AssignedStudent == null
                    ? string.Empty
                    : submission.AssignedStudent.DisplayName),
            "testTitle" => query.OrderBy(submission =>
                submission.TestSession.TitleOverride
                    ?? submission.TestSession.TemplateTitleSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Title),
            _ => query.OrderByDescending(submission =>
                submission.TestSession.TitleOverride
                    ?? submission.TestSession.TemplateTitleSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Title),
        };
        var submissions = await ordered
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
                    normalizedSort switch
                    {
                        "-uploadCompletedAt" =>
                            submissions[^1].UploadCompletedAt,
                        "-updatedAt" => submissions[^1].UpdatedAt,
                        "-finalizedAt" or "finalizedAt" =>
                            submissions[^1].FinalizedAt,
                        _ => null,
                    },
                    normalizedSort is "-testDate" or "testDate"
                        ? submissions[^1].TestSession.TestDate
                        : null,
                    normalizedSort switch
                    {
                        "studentName" or "-studentName" =>
                            submissions[^1].AssignedStudent?.DisplayName
                                ?? string.Empty,
                        "testTitle" or "-testTitle" =>
                            submissions[^1].TestSession.TitleOverride
                                ?? submissions[^1].TestSession.TemplateTitleSnapshot
                                ?? submissions[^1].TestSession.TemplateVersion
                                    .TestTemplate.Title,
                        _ => null,
                    },
                    submissions[^1].CreatedAt,
                    submissions[^1].Id));

        var facets = includeFacets == true
            ? await LoadReportFacetsAsync(
                db,
                readOnlyReviewer,
                cancellationToken)
            : null;

        return Results.Ok(new
        {
            items = submissions.Select(submission => ToListItem(
                submission,
                exportStates.GetValueOrDefault(submission.Id))),
            nextCursor,
            totalApproximate = total,
            facets,
        });
    }

    private sealed record SubmissionCursorPosition(
        DateTimeOffset? Timestamp,
        DateOnly? Date,
        string? Text,
        DateTimeOffset CreatedAt,
        string Id);

    private static async Task<object> LoadReportFacetsAsync(
        OokiGraderDbContext db,
        bool readOnlyReviewer,
        CancellationToken cancellationToken)
    {
        // Both results-capable roles see the same finalized-only report corpus;
        // keeping the visibility flag explicit prevents accidental widening if
        // role-specific report visibility is added later.
        var query = db.Submissions
            .AsNoTracking()
            .Where(submission => submission.State == "finalized"
                && submission.FinalizedAt != null
                && submission.VoidedAt == null);
        if (readOnlyReviewer)
        {
            query = query.Where(submission => submission.FinalizedAt != null);
        }

        var subjectRows = await query
            .Where(submission =>
                (submission.TestSession.TemplateSubjectSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Subject) != null
                && (submission.TestSession.TemplateSubjectSnapshot
                        ?? submission.TestSession.TemplateVersion.TestTemplate.Subject)
                    != string.Empty
                && (submission.TestSession.TemplateSubjectSnapshot
                        ?? submission.TestSession.TemplateVersion.TestTemplate.Subject)!.Length
                    <= ListQuery.MaximumFilterLength)
            .GroupBy(submission =>
                submission.TestSession.TemplateSubjectSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Subject!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var subjects = subjectRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var categoryRows = await query
            .Where(submission =>
                (submission.TestSession.TemplateCategorySnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Category) != null
                && (submission.TestSession.TemplateCategorySnapshot
                        ?? submission.TestSession.TemplateVersion.TestTemplate.Category)
                    != string.Empty
                && (submission.TestSession.TemplateCategorySnapshot
                        ?? submission.TestSession.TemplateVersion.TestTemplate.Category)!.Length
                    <= ListQuery.MaximumFilterLength)
            .GroupBy(submission =>
                submission.TestSession.TemplateCategorySnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Category!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var categories = categoryRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var courseRows = await query
            .Where(submission => submission.TestSession.Course != null
                && submission.TestSession.Course != string.Empty
                && submission.TestSession.Course.Length
                    <= ListQuery.MaximumFilterLength)
            .GroupBy(submission => submission.TestSession.Course!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var courses = courseRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var classRows = await query
            .Where(submission => submission.TestSession.ClassLabel != null
                && submission.TestSession.ClassLabel != string.Empty
                && submission.TestSession.ClassLabel.Length
                    <= ListQuery.MaximumFilterLength)
            .GroupBy(submission => submission.TestSession.ClassLabel!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var classes = classRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var templateRows = await query
            .GroupBy(submission => new
            {
                Value = submission.TestSession.TemplateVersion.TestTemplateId,
                Label = submission.TestSession.TemplateTitleSnapshot
                    ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            })
            .Select(group => new
            {
                group.Key.Value,
                group.Key.Label,
                Count = group.Count(),
            })
            .OrderBy(item => item.Label)
            .ThenBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var templates = templateRows
            .Select(item => new FacetValue(
                item.Value,
                item.Label,
                item.Count))
            .ToArray();
        var studentRows = await query
            .Where(submission => submission.AssignedStudentId != null)
            .GroupBy(submission => new
            {
                Value = submission.AssignedStudentId!,
                Label = submission.AssignedStudent!.DisplayName,
            })
            .Select(group => new
            {
                group.Key.Value,
                group.Key.Label,
                Count = group.Count(),
            })
            .OrderBy(item => item.Label)
            .ThenBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var students = studentRows
            .Select(item => new FacetValue(
                item.Value,
                item.Label,
                item.Count))
            .ToArray();
        return new
        {
            subjects,
            categories,
            courses,
            classes,
            templates,
            students,
        };
    }

    private sealed record FacetValue(string Value, string Label, int Count);

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
            sessionName = submission.TestSession.TitleOverride
                ?? submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            submission.TestSession.TestDate,
            templateId = submission.TestSession.TemplateVersion.TestTemplateId,
            templateVersionId = submission.TestSession.TemplateVersionId,
            templateTitle =
                submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            testTitle = submission.TestSession.TitleOverride
                ?? submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
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
            .Include(item => item.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
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
        BackgroundJobEntity? job = null;
        string? gradingQueueReason = null;
        if (submission.CurrentGradingRunId is null)
        {
            var stagedRun = await ActivateStagedCombinedRunAsync(
                db,
                submission,
                ApiHelpers.StaffId(principal),
                now,
                cancellationToken);
            if (stagedRun is not null)
            {
                gradingQueueReason = "combined_analysis_activated";
            }
            else
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
            .Include(item => item.GradingRuns)
                .ThenInclude(run => run.QuestionResults)
            .SingleOrDefaultAsync(
                item => item.Id == submissionId,
                cancellationToken);
        if (submission is null)
        {
            return Results.NotFound();
        }

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
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
            foreach (var stagedRun in submission.GradingRuns.Where(run =>
                         run.State == "awaiting_identity"))
            {
                stagedRun.State = "discarded_non_student";
            }

            await CancelPendingCombinedAnalysisJobsAsync(
                db,
                submission,
                now,
                cancellationToken);
        }
        else
        {
            submission.AssignmentEvidenceJson =
                """{"disposition":"unidentified"}""";
            var stagedRun = await ActivateStagedCombinedRunAsync(
                db,
                submission,
                ApiHelpers.StaffId(principal),
                now,
                cancellationToken);
            GradingJobSelection? grading = null;
            if (stagedRun is null)
            {
                grading = await PrepareGradingJobAsync(
                    db,
                    submission,
                    submission.TestSession.TemplateVersion,
                    now,
                    context,
                    configuration,
                    cancellationToken);
                submission.State = "grading";
            }

            AddAudit(
                db,
                now.AddTicks(1),
                principal,
                context,
                "submission.grading_queued",
                submission.Id,
                stagedRun is not null
                    ? "combined_analysis_activated"
                    : grading!.QueueReason);
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

        if (submission.TestSession.State == "archived")
        {
            return ArchivedSessionReadOnly(context);
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
        if (!TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(version.State)
            || version.Questions.Count == 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_VERSION_INVALID",
                "採点基準を使用できません",
                "確定済みの設問を持つひな形が必要です。");
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
                    && AiTaskProfileRuntimePolicy.ReadyApprovalStates.Contains(
                        item.ApprovalState)
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
                        profile,
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
            AiTaskProfileEntity profile,
            DateTimeOffset now,
            HttpContext context,
            CancellationToken cancellationToken)
    {
        var manifestHash = submission.PreprocessingManifestHash!;
        var deduplicationKey = AiInitialGradingJobWorker
            .RootJobDeduplicationKey(
                submission.Id,
                manifestHash,
                profile.Id,
                profile.Revision,
                profile.PromptContentHash);
        var combinedPrefix =
            $"submission:{submission.Id}:gemini-analyze:{manifestHash}:";
        var existing = await db.BackgroundJobs
            .Where(job => job.Type == AiInitialGradingJobWorker.JobType
                && (job.DeduplicationKey == deduplicationKey
                    || (job.DeduplicationKey.StartsWith(combinedPrefix)
                        && (job.State == "leased"
                            || job.State == "queued"
                            || job.State == "retry_waiting"))))
            .OrderByDescending(job => job.State == "leased"
                || job.State == "queued"
                || job.State == "retry_waiting")
            .ThenByDescending(job => job.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
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
                && job.Type == "provider_free_grade"
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

    private static async Task<GradingRunEntity?> ActivateStagedCombinedRunAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        string actorStaffUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (submission.CurrentGradingRunId is not null
            || submission.VoidedAt is not null)
        {
            return null;
        }

        var staged = submission.GradingRuns
            .Where(run => run.PipelineVersion
                    == AiInitialGradingJobWorker.PipelineVersion
                && run.State == "awaiting_identity")
            .OrderByDescending(run => run.RunNumber)
            .ThenByDescending(run => run.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (staged is null)
        {
            return null;
        }

        var blockingReview = staged.QuestionResults.Any(result =>
            result.ReviewRequired && result.ReviewStatus != "resolved");
        staged.State = blockingReview
            ? "needs_grade_review"
            : "ready_to_finalize";
        staged.ActivatedAt = now;
        staged.ActivatedByStaffUserId = actorStaffUserId;
        submission.CurrentGradingRunId = staged.Id;
        submission.State = staged.State;
        submission.UpdatedAt = now;
        var resultPrefixes = staged.QuestionResults.Select(result =>
                $"question-result:{result.Id}:adjudication:")
            .ToArray();
        if (resultPrefixes.Length > 0)
        {
            var waiting = await db.BackgroundJobs
                .Where(job => job.Type == AiAdjudicationJobWorker.JobType
                    && job.State == "blocked"
                    && job.ErrorCode == "awaiting_identity")
                .ToListAsync(cancellationToken);
            foreach (var job in waiting.Where(job => resultPrefixes.Any(prefix =>
                         job.DeduplicationKey.StartsWith(
                             prefix,
                             StringComparison.Ordinal))))
            {
                job.State = "queued";
                job.NextAttemptAt = now;
                job.ErrorCode = null;
                job.SafeErrorDetail = null;
                job.CompletedAt = null;
            }
        }

        return staged;
    }

    private static async Task CancelPendingCombinedAnalysisJobsAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var prefix = $"submission:{submission.Id}:";
        var pending = await db.BackgroundJobs
            .Where(job => (job.Type == AiInitialGradingJobWorker.JobType
                    || job.Type == AiInitialGradingJobWorker.ApplyJobType)
                && job.DeduplicationKey.StartsWith(prefix)
                && (job.State == "queued" || job.State == "retry_waiting"))
            .ToListAsync(cancellationToken);
        foreach (var job in pending)
        {
            job.State = "cancelled";
            job.CompletedAt = now;
            job.ErrorCode = "submission_voided_non_student";
            job.SafeErrorDetail = null;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
        }

        var resultPrefixes = submission.GradingRuns
            .SelectMany(run => run.QuestionResults)
            .Select(result => $"question-result:{result.Id}:adjudication:")
            .ToArray();
        if (resultPrefixes.Length == 0)
        {
            return;
        }

        var adjudicationJobs = await db.BackgroundJobs
            .Where(job => job.Type == AiAdjudicationJobWorker.JobType
                && (job.State == "queued"
                    || job.State == "retry_waiting"
                    || job.State == "blocked"))
            .ToListAsync(cancellationToken);
        foreach (var job in adjudicationJobs.Where(job =>
                     resultPrefixes.Any(resultPrefix =>
                         job.DeduplicationKey.StartsWith(
                             resultPrefix,
                             StringComparison.Ordinal))))
        {
            job.State = "cancelled";
            job.CompletedAt = now;
            job.ErrorCode = "submission_voided_non_student";
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
            sessionName = submission.TestSession.TitleOverride
                ?? submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            submission.TestSession.TestDate,
            templateTitle =
                submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
            testTitle = submission.TestSession.TitleOverride
                ?? submission.TestSession.TemplateTitleSnapshot
                ?? submission.TestSession.TemplateVersion.TestTemplate.Title,
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

    private static IResult ArchivedSessionReadOnly(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "TEST_SESSION_ARCHIVED_READ_ONLY",
            "アーカイブ済みのテスト実施は変更できません",
            "過去の答案は閲覧できますが、生徒の割り当て、重複解決、採点開始はできません。");

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
