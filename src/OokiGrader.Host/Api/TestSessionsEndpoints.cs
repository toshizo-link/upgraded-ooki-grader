using System.Security.Claims;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class TestSessionsEndpoints
{
    private const string SessionsListRoute = "GET:/api/v1/test-sessions";
    private static readonly string[] ActiveJobStates =
        ["queued", "leased", "retry_waiting"];
    private static readonly string[] GradingJobTypes =
    [
        AiInitialGradingJobWorker.JobType,
        AiInitialGradingJobWorker.ApplyJobType,
        AiAdjudicationJobWorker.JobType,
        "provider_free_grade",
    ];

    public static IEndpointRouteBuilder MapTestSessionsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/test-sessions")
            .WithTags("Test sessions")
            .RequireAuthorization("upload");
        group.MapGet("/", ListSessions).RequireRateLimiting("search");
        group.MapPost("/", CreateSession)
            .RequireAuthorization("teacher")
            .RequireIdempotency();
        group.MapGet("/{sessionId}", GetSession);
        group.MapPatch("/{sessionId}", UpdateSession)
            .RequireAuthorization("teacher");
        group.MapGet("/{sessionId}/summary", GetSummary)
            .RequireAuthorization("review");
        group.MapGet("/{sessionId}/upload-status", GetUploadStatus)
            .RequireAuthorization("upload");
        group.MapPost("/{sessionId}:open", OpenSession)
            .RequireAuthorization("teacher");
        group.MapPost("/{sessionId}:close", CloseSession)
            .RequireAuthorization("teacher");
        group.MapPost("/{sessionId}:archive", ArchiveSession)
            .RequireAuthorization("teacher");
        group.MapPut("/{sessionId}/roster", ReplaceRoster)
            .RequireAuthorization("teacher");
        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListSessions(
        HttpContext context,
        string? search,
        string? state,
        DateOnly? from,
        DateOnly? to,
        string? templateId,
        string? @class,
        string? course,
        string? sort,
        bool? includeFacets,
        string? cursor,
        int? pageSize,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (!ListQuery.TryPageSize(
                context,
                pageSize,
                limit: null,
                out var limit,
                out var pageSizeError))
        {
            return pageSizeError!;
        }

        var query = db.TestSessions.AsNoTracking();
        var operatorOnly = IsScanOperatorOnly(principal);
        if (operatorOnly)
        {
            query = query.Where(session =>
                session.State == "open" || session.State == "closed");
        }

        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState is not null
            && normalizedState is not ("draft" or "open" or "closed" or "archived"))
        {
            return ListQuery.Invalid(
                context,
                "state は draft、open、closed、archived のいずれかを指定してください。");
        }

        if (normalizedState is not null)
        {
            query = query.Where(session => session.State == normalizedState);
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            return ListQuery.Invalid(
                context,
                "from は to と同じ日付またはそれ以前を指定してください。");
        }

        if (from.HasValue)
        {
            query = query.Where(session => session.TestDate >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(session => session.TestDate <= to.Value);
        }

        if (!ListQuery.TryTrimFilter(
                context,
                templateId,
                "templateId",
                out var normalizedTemplateId,
                out var filterError,
                ListQuery.MaximumIdLength)
            || !ListQuery.TryTrimFilter(
                context,
                @class,
                "class",
                out var normalizedClass,
                out filterError)
            || !ListQuery.TryTrimFilter(
                context,
                course,
                "course",
                out var normalizedCourse,
                out filterError))
        {
            return filterError!;
        }

        if (normalizedTemplateId is not null)
        {
            query = query.Where(session =>
                session.TemplateVersion.TestTemplateId == normalizedTemplateId);
        }

        if (normalizedClass is not null)
        {
            query = query.Where(session => session.ClassLabel == normalizedClass);
        }

        if (normalizedCourse is not null)
        {
            query = query.Where(session => session.Course == normalizedCourse);
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
            query = query.Where(session =>
                (session.TitleOverride != null
                    && EF.Functions.Like(
                        session.TitleOverride,
                        pattern,
                        "\\"))
                || (session.TemplateTitleSnapshot != null
                    && EF.Functions.Like(
                        session.TemplateTitleSnapshot,
                        pattern,
                        "\\"))
                || EF.Functions.Like(
                    session.TemplateVersion.TestTemplate.Title,
                    pattern,
                    "\\")
                || (session.ClassLabel != null
                    && EF.Functions.Like(session.ClassLabel, pattern, "\\"))
                || (session.Course != null
                    && EF.Functions.Like(session.Course, pattern, "\\"))
                || (session.TemplateSubjectSnapshot != null
                    && EF.Functions.Like(
                        session.TemplateSubjectSnapshot,
                        pattern,
                        "\\"))
                || (session.TemplateVersion.TestTemplate.Subject != null
                    && EF.Functions.Like(
                        session.TemplateVersion.TestTemplate.Subject,
                        pattern,
                        "\\")));
        }

        var normalizedSort = CursorPagination.TrimToNull(sort) ?? "-testDate";
        if (normalizedSort is not (
            "-testDate"
            or "testDate"
            or "-updatedAt"
            or "updatedAt"
            or "name"
            or "-name"))
        {
            return ListQuery.Invalid(
                context,
                "sort は testDate、updatedAt、name のいずれかに、必要なら先頭の - を付けて指定してください。");
        }

        var cursorSort = normalizedSort switch
        {
            "-testDate" => "-testDate,-createdAt,id",
            "testDate" => "testDate,createdAt,id",
            "-updatedAt" => "-updatedAt,id",
            "updatedAt" => "updatedAt,id",
            "name" => "name,id",
            _ => "-name,id",
        };
        var filterBinding = CursorPagination.Bind(
            ("class", normalizedClass),
            ("course", normalizedCourse),
            ("from", from?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("search", normalizedSearch),
            ("sort", cursorSort),
            ("state", normalizedState),
            ("templateId", normalizedTemplateId),
            ("to", to?.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture)),
            ("visibility", operatorOnly ? "scan-operator" : "full"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                SessionsListRoute,
                filterBinding,
                out SessionCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > ListQuery.MaximumIdLength
                || (normalizedSort is "-testDate" or "testDate"
                    ? position.TestDate is null || position.SecondaryAt is null
                    : normalizedSort is "-updatedAt" or "updatedAt"
                        ? position.Timestamp is null
                        : string.IsNullOrEmpty(position.Text)
                            || position.Text.Length > 1_000)))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = normalizedSort switch
            {
                "-testDate" => query.Where(session =>
                    session.TestDate < position.TestDate
                    || (session.TestDate == position.TestDate
                        && (session.CreatedAt < position.SecondaryAt
                            || (session.CreatedAt == position.SecondaryAt
                                && string.Compare(
                                    session.Id,
                                    position.Id) > 0)))),
                "testDate" => query.Where(session =>
                    session.TestDate > position.TestDate
                    || (session.TestDate == position.TestDate
                        && (session.CreatedAt > position.SecondaryAt
                            || (session.CreatedAt == position.SecondaryAt
                                && string.Compare(
                                    session.Id,
                                    position.Id) > 0)))),
                "-updatedAt" => query.Where(session =>
                    session.UpdatedAt < position.Timestamp
                    || (session.UpdatedAt == position.Timestamp
                        && string.Compare(session.Id, position.Id) > 0)),
                "updatedAt" => query.Where(session =>
                    session.UpdatedAt > position.Timestamp
                    || (session.UpdatedAt == position.Timestamp
                        && string.Compare(session.Id, position.Id) > 0)),
                "name" => query.Where(session =>
                    string.Compare(
                        session.TitleOverride
                            ?? session.TemplateTitleSnapshot
                            ?? session.TemplateVersion.TestTemplate.Title,
                        position.Text) > 0
                    || ((session.TitleOverride
                                ?? session.TemplateTitleSnapshot
                                ?? session.TemplateVersion.TestTemplate.Title)
                            == position.Text
                        && string.Compare(session.Id, position.Id) > 0)),
                _ => query.Where(session =>
                    string.Compare(
                        session.TitleOverride
                            ?? session.TemplateTitleSnapshot
                            ?? session.TemplateVersion.TestTemplate.Title,
                        position.Text) < 0
                    || ((session.TitleOverride
                                ?? session.TemplateTitleSnapshot
                                ?? session.TemplateVersion.TestTemplate.Title)
                            == position.Text
                        && string.Compare(session.Id, position.Id) > 0)),
            };
        }

        IOrderedQueryable<TestSessionEntity> ordered = normalizedSort switch
        {
            "-testDate" => query
                .OrderByDescending(session => session.TestDate)
                .ThenByDescending(session => session.CreatedAt),
            "testDate" => query
                .OrderBy(session => session.TestDate)
                .ThenBy(session => session.CreatedAt),
            "-updatedAt" => query.OrderByDescending(
                session => session.UpdatedAt),
            "updatedAt" => query.OrderBy(session => session.UpdatedAt),
            "name" => query.OrderBy(session =>
                session.TitleOverride
                    ?? session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title),
            _ => query.OrderByDescending(session =>
                session.TitleOverride
                    ?? session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title),
        };
        var sessions = await ordered
            .ThenBy(session => session.Id)
            .Take(limit + 1)
            .Select(session => new
            {
                session.Id,
                name = session.TitleOverride
                    ?? session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title,
                sessionName = session.TitleOverride
                    ?? session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title,
                title = session.TitleOverride
                    ?? session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title,
                templateId = session.TemplateVersion.TestTemplateId,
                session.TemplateVersionId,
                templateTitle = session.TemplateTitleSnapshot
                    ?? session.TemplateVersion.TestTemplate.Title,
                session.TemplateVersion.VersionNumber,
                subject = session.TemplateSubjectSnapshot
                    ?? session.TemplateVersion.TestTemplate.Subject,
                gradeLabel = session.TemplateGradeLabelSnapshot
                    ?? session.TemplateVersion.TestTemplate.GradeLabel,
                category = session.TemplateCategorySnapshot
                    ?? session.TemplateVersion.TestTemplate.Category,
                expectedSubmissionPageCount =
                    session.TemplateVersion.ExpectedSubmissionPageCount,
                session.TestDate,
                session.ClassLabel,
                session.Course,
                templateCourse = session.TemplateCourseSnapshot
                    ?? session.TemplateVersion.TestTemplate.Course,
                session.Priority,
                session.State,
                session.CreationSource,
                expectedStudentCount = session.RosterMembers.Count(member => member.Expected),
                submissionCount = session.Submissions.Count,
                finalizedCount = session.Submissions.Count(
                    submission => submission.FinalizedAt != null
                        && submission.VoidedAt == null),
                attentionCount = session.Submissions.Count(submission =>
                    submission.State == "needs_attention"
                    || submission.State == "needs_name_review"
                    || submission.State == "needs_grade_review"
                    || submission.State == "failed"),
                scanAttentionCount = session.Submissions.Count(submission =>
                    submission.State == "needs_attention"
                    || submission.State == "failed"),
                session.Revision,
                session.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        var hasMore = sessions.Count > limit;
        if (hasMore)
        {
            sessions.RemoveAt(limit);
        }

        string? nextCursor = null;
        if (hasMore && sessions.Count > 0)
        {
            var lastId = sessions[^1].Id;
            var usesTestDate = normalizedSort is "-testDate" or "testDate";
            var usesUpdatedAt = normalizedSort is "-updatedAt" or "updatedAt";
            var usesName = normalizedSort is "name" or "-name";
            var cursorPosition = await db.TestSessions
                .AsNoTracking()
                .Where(session => session.Id == lastId)
                .Select(session => new SessionCursorPosition(
                    usesTestDate ? session.TestDate : null,
                    usesUpdatedAt ? session.UpdatedAt : null,
                    usesTestDate ? session.CreatedAt : null,
                    usesName
                        ? session.TitleOverride
                            ?? session.TemplateTitleSnapshot
                            ?? session.TemplateVersion.TestTemplate.Title
                        : null,
                    session.Id))
                .SingleAsync(cancellationToken);
            nextCursor = CursorPagination.Next(
                cursorCodec,
                SessionsListRoute,
                filterBinding,
                hasMore,
                cursorPosition);
        }

        if (operatorOnly)
        {
            var facets = includeFacets == true
                ? await LoadSessionFacetsAsync(
                    db,
                    operatorOnly,
                    cancellationToken)
                : null;
            return Results.Ok(new
            {
                items = sessions.Select(session => new
                {
                    session.Id,
                    session.name,
                    session.sessionName,
                    session.title,
                    session.templateTitle,
                    session.VersionNumber,
                    session.subject,
                    session.gradeLabel,
                    session.category,
                    session.TestDate,
                    session.ClassLabel,
                    session.Course,
                    session.templateCourse,
                    session.Priority,
                    session.State,
                    session.CreationSource,
                    session.submissionCount,
                    attentionCount = session.scanAttentionCount,
                    session.UpdatedAt,
                }),
                nextCursor,
                totalApproximate = total,
                facets,
            });
        }

        var fullFacets = includeFacets == true
            ? await LoadSessionFacetsAsync(
                db,
                operatorOnly,
                cancellationToken)
            : null;
        return Results.Ok(new
        {
            items = sessions,
            nextCursor,
            totalApproximate = total,
            facets = fullFacets,
        });
    }

    private static async Task<object> LoadSessionFacetsAsync(
        OokiGraderDbContext db,
        bool operatorOnly,
        CancellationToken cancellationToken)
    {
        var query = db.TestSessions.AsNoTracking();
        if (operatorOnly)
        {
            query = query.Where(session =>
                session.State == "open" || session.State == "closed");
        }

        var templateRows = await query
            .Where(session =>
                session.TemplateVersion.TestTemplate.Title.Length
                    <= ListQuery.MaximumFilterLength)
            .GroupBy(session => new
            {
                Value = session.TemplateVersion.TestTemplateId,
                Label = session.TemplateVersion.TestTemplate.Title,
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
        var classRows = await query
            .Where(session => session.ClassLabel != null
                && session.ClassLabel != string.Empty
                && session.ClassLabel.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(session => session.ClassLabel!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var classes = classRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var courseRows = await query
            .Where(session => session.Course != null
                && session.Course != string.Empty
                && session.Course.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(session => session.Course!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var courses = courseRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        return new { templates, classes, courses };
    }

    private sealed record FacetValue(string Value, string Label, int Count);

    private static async Task<IResult> GetSession(
        string sessionId,
        HttpContext context,
        OokiGraderDbContext db,
        [FromServices] OrderedScanBatchService orderedScanBatchService,
        CancellationToken cancellationToken)
    {
        var expectedSubmissionPageCount =
            await orderedScanBatchService.TryResolveExpectedPageCountForSessionAsync(
                sessionId,
                cancellationToken);
        if (IsScanOperatorOnly(context.User))
        {
            var operatorSession = await db.TestSessions
                .AsNoTracking()
                .Where(item => item.Id == sessionId
                    && (item.State == "open" || item.State == "closed"))
                .Select(item => new
                {
                    item.Id,
                    name = item.TitleOverride
                        ?? item.TemplateTitleSnapshot
                        ?? item.TemplateVersion.TestTemplate.Title,
                    sessionName = item.TitleOverride
                        ?? item.TemplateTitleSnapshot
                        ?? item.TemplateVersion.TestTemplate.Title,
                    title = item.TitleOverride
                        ?? item.TemplateTitleSnapshot
                        ?? item.TemplateVersion.TestTemplate.Title,
                    templateTitle = item.TemplateTitleSnapshot
                        ?? item.TemplateVersion.TestTemplate.Title,
                    item.TemplateVersion.VersionNumber,
                    subject = item.TemplateSubjectSnapshot
                        ?? item.TemplateVersion.TestTemplate.Subject,
                    gradeLabel = item.TemplateGradeLabelSnapshot
                        ?? item.TemplateVersion.TestTemplate.GradeLabel,
                    category = item.TemplateCategorySnapshot
                        ?? item.TemplateVersion.TestTemplate.Category,
                    expectedSubmissionPageCount,
                    item.TestDate,
                    item.ClassLabel,
                    item.Course,
                    templateCourse = item.TemplateCourseSnapshot
                        ?? item.TemplateVersion.TestTemplate.Course,
                    item.Priority,
                    item.State,
                    item.CreationSource,
                    submissionCount = item.Submissions.Count,
                    attentionCount = item.Submissions.Count(submission =>
                        submission.State == "needs_attention"
                        || submission.State == "failed"),
                    item.CreatedAt,
                    item.UpdatedAt,
                    item.ClosedAt,
                })
                .SingleOrDefaultAsync(cancellationToken);
            return operatorSession is null
                ? Results.NotFound()
                : Results.Ok(operatorSession);
        }

        var session = await db.TestSessions
            .AsNoTracking()
            .Where(item => item.Id == sessionId)
            .Select(item => new
            {
                item.Id,
                name = item.TitleOverride
                    ?? item.TemplateTitleSnapshot
                    ?? item.TemplateVersion.TestTemplate.Title,
                sessionName = item.TitleOverride
                    ?? item.TemplateTitleSnapshot
                    ?? item.TemplateVersion.TestTemplate.Title,
                title = item.TitleOverride
                    ?? item.TemplateTitleSnapshot
                    ?? item.TemplateVersion.TestTemplate.Title,
                templateId = item.TemplateVersion.TestTemplateId,
                item.TemplateVersionId,
                templateTitle = item.TemplateTitleSnapshot
                    ?? item.TemplateVersion.TestTemplate.Title,
                item.TemplateVersion.VersionNumber,
                subject = item.TemplateSubjectSnapshot
                    ?? item.TemplateVersion.TestTemplate.Subject,
                gradeLabel = item.TemplateGradeLabelSnapshot
                    ?? item.TemplateVersion.TestTemplate.GradeLabel,
                category = item.TemplateCategorySnapshot
                    ?? item.TemplateVersion.TestTemplate.Category,
                expectedSubmissionPageCount,
                item.TestDate,
                item.ClassLabel,
                item.Course,
                templateCourse = item.TemplateCourseSnapshot
                    ?? item.TemplateVersion.TestTemplate.Course,
                item.Priority,
                item.State,
                item.CreationSource,
                expectedStudentCount = item.RosterMembers.Count(member => member.Expected),
                submissionCount = item.Submissions.Count,
                finalizedCount = item.Submissions.Count(
                    submission => submission.FinalizedAt != null
                        && submission.VoidedAt == null),
                attentionCount = item.Submissions.Count(submission =>
                    submission.State == "needs_attention"
                    || submission.State == "needs_name_review"
                    || submission.State == "needs_grade_review"
                    || submission.State == "failed"),
                item.Revision,
                item.CreatedAt,
                item.UpdatedAt,
                item.ClosedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Ok(session);
    }

    private static async Task<IResult> CreateSession(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateSessionBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var priority = string.IsNullOrWhiteSpace(request.Priority)
            ? "expedite"
            : request.Priority.Trim();
        var sessionName = TrimOrNull(request.SessionName);
        var classLabel = TrimOrNull(request.ClassLabel);
        var course = TrimOrNull(request.Course);
        if (string.IsNullOrWhiteSpace(request.TemplateVersionId)
            || request.TestDate == default
            || priority is not ("economy" or "expedite")
            || sessionName?.Length > 500
            || classLabel?.Length > 500)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "TEST_SESSION_INVALID",
                "テスト実施を作成できません",
                "確定済みのひな形、実施日、処理方法を確認してください。");
        }

        var staffId = ApiHelpers.StaffId(principal);
        var requestKey = context.Request.Headers["Idempotency-Key"].Count == 1
            ? TrimOrNull(context.Request.Headers["Idempotency-Key"][0])
            : null;
        var requestFingerprint = ComputeCreateSessionFingerprint(
            request.TemplateVersionId,
            request.TestDate,
            sessionName,
            classLabel,
            course,
            priority,
            request.OpenImmediately == true);
        if (requestKey is not null)
        {
            var replay = await db.TestSessions
                .AsNoTracking()
                .Include(item => item.TemplateVersion)
                .ThenInclude(item => item.TestTemplate)
                .SingleOrDefaultAsync(
                    item => item.CreatedByStaffUserId == staffId
                        && item.RequestIdempotencyKey == requestKey,
                    cancellationToken);
            if (replay is not null)
            {
                return ReplayOrRejectCreateSession(
                    context,
                    replay,
                    requestFingerprint);
            }
        }

        var version = await db.TemplateVersions
            .AsNoTracking()
            .Include(item => item.TestTemplate)
            .SingleOrDefaultAsync(
                item => item.Id == request.TemplateVersionId,
                cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.State != "published"
            || version.TestTemplate.State == "archived")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_VERSION_NOT_PUBLISHED",
                "このひな形は使用できません",
                "採点基準を確認し、ひな形の受付を開始してからやり直してください。");
        }

        var now = timeProvider.GetUtcNow();
        var session = new TestSessionEntity
        {
            Id = UlidId.New(now),
            TemplateVersionId = version.Id,
            CreationSource = "manual",
            RequestIdempotencyKey = requestKey,
            RequestFingerprint = requestKey is null ? null : requestFingerprint,
            TitleOverride = sessionName,
            TemplateTitleSnapshot = version.TestTemplate.Title,
            TemplateSubjectSnapshot = version.TestTemplate.Subject,
            TemplateGradeLabelSnapshot = version.TestTemplate.GradeLabel,
            TemplateCategorySnapshot = version.TestTemplate.Category,
            TemplateCourseSnapshot = version.TestTemplate.Course,
            TestDate = request.TestDate,
            Course = course ?? version.TestTemplate.Course,
            ClassLabel = classLabel,
            Priority = priority,
            State = request.OpenImmediately == true ? "open" : "draft",
            CreatedByStaffUserId = staffId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TestSessions.Add(session);
        AddAudit(db, now, principal, context, "test_session.created", session.Id);
        if (request.OpenImmediately == true)
        {
            AddAudit(db, now, principal, context, "test_session.opened", session.Id);
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (requestKey is not null)
        {
            // The API response cache is persisted after this transaction. If
            // the process dies in that narrow gap, the durable actor/key fence
            // resolves the committed session on retry.
            db.ChangeTracker.Clear();
            var replay = await db.TestSessions
                .AsNoTracking()
                .Include(item => item.TemplateVersion)
                .ThenInclude(item => item.TestTemplate)
                .SingleOrDefaultAsync(
                    item => item.CreatedByStaffUserId == staffId
                        && item.RequestIdempotencyKey == requestKey,
                    cancellationToken);
            if (replay is not null)
            {
                return ReplayOrRejectCreateSession(
                    context,
                    replay,
                    requestFingerprint);
            }

            return SessionStartFailed(context);
        }

        return CreatedSession(context, session, version);
    }

    private static Task<IResult> OpenSession(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        Transition(
            sessionId,
            "open",
            ["draft", "closed"],
            "test_session.opened",
            context,
            principal,
            db,
            timeProvider,
            cancellationToken);

    private static async Task<IResult> UpdateSession(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] UpdateSessionBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.Priority is not ("economy" or "expedite"))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "TEST_SESSION_PRIORITY_INVALID",
                "処理方法を変更できません",
                "通常処理または優先処理を選択してください。");
        }

        var session = await db.TestSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId,
            cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        if (session.State == "archived")
        {
            return ArchivedReadOnly(context);
        }

        if (ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.Revision,
                out var expectedRevision)
            && session.Revision != expectedRevision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_STALE",
                "テスト実施が更新されています",
                "最新の状態を読み込み直してから変更してください。");
        }

        session.Priority = request.Priority;
        AddAudit(
            db,
            timeProvider.GetUtcNow(),
            principal,
            context,
            "test_session.priority_updated",
            session.Id);
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Ok(new
        {
            session.Id,
            session.Priority,
            session.Revision,
        });
    }

    private static Task<IResult> CloseSession(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        Transition(
            sessionId,
            "closed",
            ["open"],
            "test_session.closed",
            context,
            principal,
            db,
            timeProvider,
            cancellationToken);

    private static async Task<IResult> ArchiveSession(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = await db.TestSessions
            .Include(item => item.Submissions)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        if (session.State == "archived")
        {
            ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
            return Results.Ok(new { session.Id, session.State, session.Revision });
        }

        if (session.State != "closed")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEST_SESSION_TRANSITION_INVALID",
                "状態を変更できません",
                $"現在の状態（{session.State}）から変更できません。");
        }

        var incompleteSubmissionCount = session.Submissions.Count(submission =>
            submission.FinalizedAt is null && submission.VoidedAt is null);
        if (incompleteSubmissionCount > 0)
        {
            return ArchiveBlocked(
                context,
                "TEST_SESSION_ARCHIVE_SUBMISSIONS_INCOMPLETE",
                "未完了の答案が残っています",
                $"未確定または未取消の答案が{incompleteSubmissionCount}件あります。すべて確定または取消してからアーカイブしてください。");
        }

        var activeUploadCount = await db.UploadSessions.CountAsync(
            upload => upload.TestSessionId == session.Id
                && (upload.State == "uploading"
                    || upload.State == "finalizing"
                    || upload.State == "duplicate_pending"),
            cancellationToken);
        if (activeUploadCount > 0)
        {
            return ArchiveBlocked(
                context,
                "TEST_SESSION_ARCHIVE_UPLOADS_ACTIVE",
                "処理中のアップロードがあります",
                $"アップロードまたは重複確認が{activeUploadCount}件残っています。完了または取消してからアーカイブしてください。");
        }

        var activeOrderedBatchCount = await db.OrderedScanBatches.CountAsync(
            batch => batch.TestSessionId == session.Id
                && batch.Status != OrderedScanBatchStatus.Completed
                && batch.Status != OrderedScanBatchStatus.Failed
                && batch.Status != OrderedScanBatchStatus.Cancelled
                && batch.Status != OrderedScanBatchStatus.Expired,
            cancellationToken);
        if (activeOrderedBatchCount > 0)
        {
            return ArchiveBlocked(
                context,
                "TEST_SESSION_ARCHIVE_SCAN_BATCHES_ACTIVE",
                "答案ページの処理が残っています",
                $"処理中または確認待ちの読取バッチが{activeOrderedBatchCount}件あります。完了または取消してからアーカイブしてください。");
        }

        var submissionPrefixes = session.Submissions
            .Select(submission => $"submission:{submission.Id}:")
            .ToArray();
        if (submissionPrefixes.Length > 0)
        {
            var submissionIds = session.Submissions
                .Select(submission => submission.Id)
                .ToHashSet(StringComparer.Ordinal);
            var activeGradingJobs = await db.BackgroundJobs
                .AsNoTracking()
                .Where(job => ActiveJobStates.Contains(job.State)
                    && GradingJobTypes.Contains(job.Type))
                .Select(job => new { job.DeduplicationKey, job.PayloadJson })
                .ToArrayAsync(cancellationToken);
            var activeGradingCount = activeGradingJobs.Count(job =>
                submissionPrefixes.Any(prefix =>
                    job.DeduplicationKey.StartsWith(
                        prefix,
                        StringComparison.Ordinal))
                || TryReadSubmissionId(job.PayloadJson) is { } submissionId
                    && submissionIds.Contains(submissionId));
            if (activeGradingCount > 0)
            {
                return ArchiveBlocked(
                    context,
                    "TEST_SESSION_ARCHIVE_GRADING_ACTIVE",
                    "採点処理が完了していません",
                    $"実行中または再試行待ちの採点処理が{activeGradingCount}件あります。処理完了後にアーカイブしてください。");
            }
        }

        var now = timeProvider.GetUtcNow();
        session.State = "archived";
        AddAudit(
            db,
            now,
            principal,
            context,
            "test_session.archived",
            session.Id);
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Ok(new { session.Id, session.State, session.Revision });
    }

    private static async Task<IResult> Transition(
        string sessionId,
        string targetState,
        IReadOnlyCollection<string> allowedFrom,
        string auditEvent,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = await db.TestSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId,
            cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        if (session.State == "archived")
        {
            return ArchivedReadOnly(context);
        }

        if (session.State == targetState)
        {
            ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
            return Results.Ok(new { session.Id, session.State, session.Revision });
        }

        if (!allowedFrom.Contains(session.State))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEST_SESSION_TRANSITION_INVALID",
                "状態を変更できません",
                $"現在の状態（{session.State}）から変更できません。");
        }

        var now = timeProvider.GetUtcNow();
        session.State = targetState;
        session.ClosedAt = targetState == "closed" ? now : session.ClosedAt;
        AddAudit(db, now, principal, context, auditEvent, session.Id);
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Ok(new { session.Id, session.State, session.Revision });
    }

    private static async Task<IResult> ReplaceRoster(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ReplaceRosterBody request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = await db.TestSessions
            .Include(item => item.RosterMembers)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return Results.NotFound();
        }

        if (session.State == "archived")
        {
            return ArchivedReadOnly(context);
        }

        if (request.StudentIds.Count > 2_000
            || request.StudentIds.Count != request.StudentIds.Distinct().Count())
        {
            return Results.UnprocessableEntity();
        }

        var validCount = await db.Students.CountAsync(
            student => request.StudentIds.Contains(student.Id)
                && student.Status == "active",
            cancellationToken);
        if (validCount != request.StudentIds.Count)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "SESSION_ROSTER_INVALID",
                "名簿を更新できません",
                "無効または在籍していない生徒が含まれています。");
        }

        db.SessionRosterMembers.RemoveRange(session.RosterMembers);
        foreach (var studentId in request.StudentIds)
        {
            db.SessionRosterMembers.Add(new SessionRosterMemberEntity
            {
                TestSessionId = session.Id,
                StudentId = studentId,
                Expected = true,
            });
        }

        session.ExpectedRosterEnabled = request.StudentIds.Count > 0;
        AddAudit(
            db,
            timeProvider.GetUtcNow(),
            principal,
            context,
            "test_session.roster_replaced",
            session.Id);
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Ok(new
        {
            session.Id,
            expectedStudentCount = request.StudentIds.Count,
            session.Revision,
        });
    }

    private static async Task<IResult> GetSummary(
        string sessionId,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var summary = await db.TestSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new
            {
                session.Id,
                session.State,
                submissionCount = session.Submissions.Count,
                processing = session.Submissions.Count(submission =>
                    submission.State == "validating"
                    || submission.State == "preprocessing"
                    || submission.State == "awaiting_name"
                    || submission.State == "awaiting_grading"
                    || submission.State == "grading"),
                needsNameReview = session.Submissions.Count(
                    submission => submission.State == "needs_name_review"),
                needsGradeReview = session.Submissions.Count(
                    submission => submission.State == "needs_grade_review"),
                readyToFinalize = session.Submissions.Count(
                    submission => submission.State == "ready_to_finalize"),
                finalizedCount = session.Submissions.Count(
                    submission => submission.FinalizedAt != null
                        && submission.VoidedAt == null),
                attentionCount = session.Submissions.Count(submission =>
                    submission.State == "needs_name_review"
                    || submission.State == "needs_grade_review"
                    || submission.State == "failed"),
                failed = session.Submissions.Count(submission => submission.State == "failed"),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return summary is null ? Results.NotFound() : Results.Ok(summary);
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates these predicates to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> GetUploadStatus(
        string sessionId,
        HttpContext context,
        string? state,
        string? search,
        string? cursor,
        int? pageSize,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var sessionState = await db.TestSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => session.State)
            .SingleOrDefaultAsync(cancellationToken);
        if (sessionState is null
            || (IsScanOperatorOnly(principal)
                && sessionState is not ("open" or "closed")))
        {
            return Results.NotFound();
        }

        if (search?.Length > 200 || state?.Length > 64)
        {
            return Results.BadRequest();
        }

        var limit = Math.Clamp(pageSize ?? 100, 1, 200);
        var normalizedSearch = string.IsNullOrWhiteSpace(search)
            ? null
            : search.Trim();
        var normalizedState = NormalizeOperatorState(state);
        var route =
            $"GET:/api/v1/test-sessions/{sessionId}/upload-status";
        var filterBinding = CursorPagination.Bind(
            ("search", normalizedSearch),
            ("sort", "-updatedAt,-id,source"),
            ("state", normalizedState),
            ("visibility", IsScanOperatorOnly(principal)
                ? "scan-operator"
                : "full"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                route,
                filterBinding,
                out UploadStatusCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128
                || position.SourceRank is < 0 or > 1))
        {
            return CursorPagination.Invalid(context);
        }

        var submissionQuery = db.Submissions
            .AsNoTracking()
            .Where(submission => submission.TestSessionId == sessionId);
        var pendingUploadQuery = db.UploadSessions
            .AsNoTracking()
            .Where(upload => upload.Purpose == "completed_test"
                && upload.TestSessionId == sessionId
                && (upload.DestinationType != "submission"
                    || upload.DestinationId == null));

        if (normalizedSearch is not null)
        {
            submissionQuery = submissionQuery.Where(submission =>
                submission.OriginalFileName != null
                && submission.OriginalFileName.Contains(normalizedSearch));
            pendingUploadQuery = pendingUploadQuery.Where(upload =>
                upload.OriginalFileName.Contains(normalizedSearch));
        }

        var rawSubmissionStates = RawSubmissionStates(normalizedState);
        var rawUploadStates = RawUploadStates(normalizedState);
        if (normalizedState is not null)
        {
            submissionQuery = rawSubmissionStates.Count == 0
                ? submissionQuery.Where(_ => false)
                : submissionQuery.Where(submission =>
                    rawSubmissionStates.Contains(submission.State));
            pendingUploadQuery = rawUploadStates.Count == 0
                ? pendingUploadQuery.Where(_ => false)
                : pendingUploadQuery.Where(upload =>
                    rawUploadStates.Contains(upload.State));
        }

        var submissionTotal = await submissionQuery.CountAsync(cancellationToken);
        var pendingUploadTotal = await pendingUploadQuery.CountAsync(cancellationToken);
        if (position is not null)
        {
            submissionQuery = submissionQuery.Where(submission =>
                submission.UpdatedAt < position.UpdatedAt
                || (submission.UpdatedAt == position.UpdatedAt
                    && (string.Compare(submission.Id, position.Id) < 0
                        || (submission.Id == position.Id
                            && 0 > position.SourceRank))));
            pendingUploadQuery = pendingUploadQuery.Where(upload =>
                upload.UpdatedAt < position.UpdatedAt
                || (upload.UpdatedAt == position.UpdatedAt
                    && (string.Compare(upload.Id, position.Id) < 0
                        || (upload.Id == position.Id
                            && 1 > position.SourceRank))));
        }

        var submissions = await submissionQuery
            .OrderByDescending(submission => submission.UpdatedAt)
            .ThenByDescending(submission => submission.Id)
            .Take(limit + 1)
            .Select(submission => new OperatorUploadRow(
                submission.Id,
                null,
                submission.OriginalFileName,
                submission.State,
                submission.UploadCompletedAt,
                submission.UpdatedAt,
                0))
            .ToListAsync(cancellationToken);
        var pendingUploads = await pendingUploadQuery
            .OrderByDescending(upload => upload.UpdatedAt)
            .ThenByDescending(upload => upload.Id)
            .Take(limit + 1)
            .Select(upload => new OperatorUploadRow(
                upload.Id,
                upload.Id,
                upload.OriginalFileName,
                upload.State,
                null,
                upload.UpdatedAt,
                1))
            .ToListAsync(cancellationToken);

        var rows = submissions
            .Concat(pendingUploads)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRank)
            .Take(limit + 1)
            .ToList();
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(limit);
        }

        var items = rows
            .Select(item =>
            {
                var operatorState = ToOperatorState(item.RawState);
                return new
                {
                    item.Id,
                    item.UploadId,
                    fileName = item.OriginalFileName,
                    state = operatorState,
                    qualityWarnings = OperatorQualityWarnings(operatorState),
                    uploadedAt = item.UploadCompletedAt,
                    item.UpdatedAt,
                };
            })
            .ToArray();
        var nextCursor = rows.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                route,
                filterBinding,
                hasMore,
                new UploadStatusCursorPosition(
                    rows[^1].UpdatedAt,
                    rows[^1].Id,
                    rows[^1].SourceRank));

        var allSubmissionStates = await db.Submissions
            .AsNoTracking()
            .Where(submission => submission.TestSessionId == sessionId)
            .GroupBy(submission => submission.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.State, item => item.Count, cancellationToken);
        var allPendingUploadStates = await db.UploadSessions
            .AsNoTracking()
            .Where(upload => upload.Purpose == "completed_test"
                && upload.TestSessionId == sessionId
                && (upload.DestinationType != "submission"
                    || upload.DestinationId == null))
            .GroupBy(upload => upload.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.State, item => item.Count, cancellationToken);

        var totalCount = allSubmissionStates.Values.Sum()
            + allPendingUploadStates.Values.Sum();
        var uploadingCount = Count(allPendingUploadStates, "uploading");
        var processingCount =
            Count(allPendingUploadStates, "finalizing")
            + Count(allSubmissionStates, "uploading")
            + Count(allSubmissionStates, "validating")
            + Count(allSubmissionStates, "preprocessing")
            + Count(allSubmissionStates, "awaiting_name")
            + Count(allSubmissionStates, "awaiting_grading")
            + Count(allSubmissionStates, "grading");
        var attentionCount =
            Count(allPendingUploadStates, "failed")
            + Count(allPendingUploadStates, "cancelled")
            + Count(allPendingUploadStates, "expired")
            + Count(allSubmissionStates, "needs_attention")
            + Count(allSubmissionStates, "failed");
        var readyCount = Math.Max(
            0,
            totalCount - uploadingCount - processingCount - attentionCount);

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = checked(submissionTotal + pendingUploadTotal),
            summary = new
            {
                totalCount,
                uploadingCount,
                processingCount,
                attentionCount,
                readyCount,
            },
        });
    }

    private static bool IsScanOperatorOnly(ClaimsPrincipal principal) =>
        principal.IsInRole("scanOperator")
        && !principal.IsInRole("administrator")
        && !principal.IsInRole("teacher");

    private static string? NormalizeOperatorState(string? state) =>
        state switch
        {
            null or "" or "all" => null,
            "awaitingAi" => "awaiting_ai",
            "needsAttention" => "needs_attention",
            "readyForReview" => "ready_for_review",
            _ => state,
        };

    private static IReadOnlyCollection<string> RawSubmissionStates(string? state) =>
        state switch
        {
            "uploading" => ["uploading"],
            "validating" => ["validating"],
            "preprocessing" => ["preprocessing"],
            "awaiting_ai" => ["awaiting_name", "awaiting_grading", "grading"],
            "needs_attention" => ["needs_attention"],
            "ready_for_review" =>
                ["needs_name_review", "needs_grade_review", "ready_to_finalize"],
            "finalized" => ["finalized"],
            "failed" => ["failed", "voided"],
            _ => [],
        };

    private static IReadOnlyCollection<string> RawUploadStates(string? state) =>
        state switch
        {
            "uploading" => ["uploading"],
            "validating" => ["finalizing"],
            "failed" => ["failed", "cancelled", "expired"],
            _ => [],
        };

    private static string ToOperatorState(string state) =>
        state switch
        {
            "uploading" => "uploading",
            "finalizing" or "validating" => "validating",
            "preprocessing" => "preprocessing",
            "awaiting_name" or "awaiting_grading" or "grading" => "awaiting_ai",
            "needs_attention" => "needs_attention",
            "needs_name_review" or "needs_grade_review" or "ready_to_finalize" =>
                "ready_for_review",
            "finalized" => "finalized",
            "failed" or "cancelled" or "expired" or "voided" => "failed",
            _ => "awaiting_ai",
        };

    private static IReadOnlyCollection<string> OperatorQualityWarnings(string state) =>
        state switch
        {
            "needs_attention" => ["画像またはページ構成の確認が必要です。"],
            "failed" => ["ファイル処理に失敗しました。先生または管理者に連絡してください。"],
            _ => [],
        };

    private static int Count(
        Dictionary<string, int> counts,
        string state) =>
        counts.TryGetValue(state, out var count) ? count : 0;

    private static IResult ReplayOrRejectCreateSession(
        HttpContext context,
        TestSessionEntity session,
        string requestFingerprint)
    {
        if (!string.Equals(
                session.RequestFingerprint,
                requestFingerprint,
                StringComparison.Ordinal))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "IDEMPOTENCY_KEY_REUSED",
                "同じ操作キーが別の内容に使われました",
                "画面を再読み込みして、もう一度受付を開始してください。");
        }

        return CreatedSession(context, session, session.TemplateVersion);
    }

    private static IResult CreatedSession(
        HttpContext context,
        TestSessionEntity session,
        TemplateVersionEntity version)
    {
        var title = session.TitleOverride
            ?? session.TemplateTitleSnapshot
            ?? version.TestTemplate.Title;
        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Created(
            $"/api/v1/test-sessions/{session.Id}",
            new SessionMutationResponse(
                session.Id,
                title,
                title,
                title,
                version.TestTemplateId,
                session.TemplateVersionId,
                session.TemplateTitleSnapshot ?? version.TestTemplate.Title,
                version.VersionNumber,
                session.TemplateSubjectSnapshot ?? version.TestTemplate.Subject,
                session.TemplateGradeLabelSnapshot ?? version.TestTemplate.GradeLabel,
                session.TemplateCategorySnapshot ?? version.TestTemplate.Category,
                version.ExpectedSubmissionPageCount,
                session.TestDate,
                session.ClassLabel,
                session.Course
                    ?? session.TemplateCourseSnapshot
                    ?? version.TestTemplate.Course,
                session.TemplateCourseSnapshot ?? version.TestTemplate.Course,
                session.Priority,
                session.State,
                session.CreationSource,
                0,
                0,
                0,
                0,
                session.Revision));
    }

    private static string ComputeCreateSessionFingerprint(
        string templateVersionId,
        DateOnly testDate,
        string? sessionName,
        string? classLabel,
        string? course,
        string priority,
        bool openImmediately)
    {
        var canonical = string.Join(
            '\n',
            templateVersionId,
            testDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sessionName ?? "<null>",
            classLabel ?? "<null>",
            course ?? "<null>",
            priority,
            openImmediately ? "open" : "draft");
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static IResult SessionStartFailed(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "TEST_SESSION_START_FAILED",
            "受付を開始できませんでした",
            "しばらく待ってから同じ操作をやり直してください。");

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult ArchivedReadOnly(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "TEST_SESSION_ARCHIVED_READ_ONLY",
            "アーカイブ済みのテスト実施は変更できません",
            "過去の結果は閲覧できますが、優先度、名簿、答案、採点結果は変更できません。");

    private static IResult ArchiveBlocked(
        HttpContext context,
        string code,
        string title,
        string detail) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            code,
            title,
            detail);

    private static string? TryReadSubmissionId(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals(
                        "submissionId",
                        StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Invalid job payloads are handled by their workers. Archive
            // readiness still checks any canonical submission-key prefix.
        }

        return null;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string eventType,
        string sessionId) =>
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = "test_session",
            ObjectId = sessionId,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
        });

    private sealed record CreateSessionBody(
        string TemplateVersionId,
        DateOnly TestDate,
        string? SessionName,
        string? ClassLabel,
        string? Course,
        string? Priority,
        bool? OpenImmediately);

    private sealed record UpdateSessionBody(
        string Priority,
        long? Revision);

    private sealed record ReplaceRosterBody(IReadOnlyList<string> StudentIds);

    private sealed record SessionMutationResponse(
        string Id,
        string Name,
        string SessionName,
        string Title,
        string TemplateId,
        string TemplateVersionId,
        string TemplateTitle,
        int TemplateVersionNumber,
        string? Subject,
        string? GradeLabel,
        string? Category,
        int? ExpectedSubmissionPageCount,
        DateOnly TestDate,
        string? ClassLabel,
        string? Course,
        string? TemplateCourse,
        string Priority,
        string State,
        string CreationSource,
        int ExpectedStudentCount,
        int SubmissionCount,
        int FinalizedCount,
        int AttentionCount,
        long Revision);

    private sealed record OperatorUploadRow(
        string Id,
        string? UploadId,
        string? OriginalFileName,
        string RawState,
        DateTimeOffset? UploadCompletedAt,
        DateTimeOffset UpdatedAt,
        int SourceRank);

    private sealed record SessionCursorPosition(
        DateOnly? TestDate,
        DateTimeOffset? Timestamp,
        DateTimeOffset? SecondaryAt,
        string? Text,
        string Id);

    private sealed record UploadStatusCursorPosition(
        DateTimeOffset UpdatedAt,
        string Id,
        int SourceRank);
}
