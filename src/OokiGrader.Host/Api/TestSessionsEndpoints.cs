using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class TestSessionsEndpoints
{
    private const string SessionsListRoute = "GET:/api/v1/test-sessions";

    public static IEndpointRouteBuilder MapTestSessionsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/test-sessions")
            .WithTags("Test sessions")
            .RequireAuthorization("upload");
        group.MapGet("/", ListSessions);
        group.MapPost("/", CreateSession).RequireAuthorization("teacher");
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
        string? cursor,
        int? pageSize,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(pageSize ?? 50, 1, 200);
        var query = db.TestSessions.AsNoTracking();
        var operatorOnly = IsScanOperatorOnly(principal);
        if (operatorOnly)
        {
            query = query.Where(session =>
                session.State == "open" || session.State == "closed");
        }

        var normalizedState = state is "draft" or "open" or "closed" or "archived"
            ? state
            : null;
        if (normalizedState is not null)
        {
            query = query.Where(session => session.State == normalizedState);
        }

        var normalizedSearch = CursorPagination.TrimToNull(search);
        if (normalizedSearch is not null)
        {
            if (normalizedSearch.Length > 200)
            {
                return Results.BadRequest();
            }

            query = query.Where(session =>
                (session.TitleOverride != null
                    && session.TitleOverride.Contains(normalizedSearch))
                || session.TemplateVersion.TestTemplate.Title
                    .Contains(normalizedSearch)
                || (session.ClassLabel != null
                    && session.ClassLabel.Contains(normalizedSearch)));
        }

        var filterBinding = CursorPagination.Bind(
            ("search", normalizedSearch),
            ("sort", "-testDate,-createdAt,id"),
            ("state", normalizedState),
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
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(session =>
                session.TestDate < position.TestDate
                || (session.TestDate == position.TestDate
                    && (session.CreatedAt < position.CreatedAt
                        || (session.CreatedAt == position.CreatedAt
                            && string.Compare(
                                session.Id,
                                position.Id) > 0))));
        }

        var sessions = await query
            .OrderByDescending(session => session.TestDate)
            .ThenByDescending(session => session.CreatedAt)
            .ThenBy(session => session.Id)
            .Take(limit + 1)
            .Select(session => new
            {
                session.Id,
                name = session.TitleOverride,
                sessionName = session.TitleOverride,
                templateId = session.TemplateVersion.TestTemplateId,
                session.TemplateVersionId,
                templateTitle = session.TemplateVersion.TestTemplate.Title,
                session.TemplateVersion.VersionNumber,
                session.TestDate,
                session.ClassLabel,
                session.Course,
                session.Priority,
                session.State,
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
            var lastCreatedAt = await db.TestSessions
                .AsNoTracking()
                .Where(session => session.Id == lastId)
                .Select(session => session.CreatedAt)
                .SingleAsync(cancellationToken);
            nextCursor = CursorPagination.Next(
                cursorCodec,
                SessionsListRoute,
                filterBinding,
                hasMore,
                new SessionCursorPosition(
                    sessions[^1].TestDate,
                    lastCreatedAt,
                    lastId));
        }

        if (operatorOnly)
        {
            return Results.Ok(new
            {
                items = sessions.Select(session => new
                {
                    session.Id,
                    session.name,
                    session.sessionName,
                    session.templateTitle,
                    session.VersionNumber,
                    session.TestDate,
                    session.ClassLabel,
                    session.Course,
                    session.Priority,
                    session.State,
                    session.submissionCount,
                    attentionCount = session.scanAttentionCount,
                    session.UpdatedAt,
                }),
                nextCursor,
                totalApproximate = total,
            });
        }

        return Results.Ok(new
        {
            items = sessions,
            nextCursor,
            totalApproximate = total,
        });
    }

    private static async Task<IResult> GetSession(
        string sessionId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        if (IsScanOperatorOnly(context.User))
        {
            var operatorSession = await db.TestSessions
                .AsNoTracking()
                .Where(item => item.Id == sessionId
                    && (item.State == "open" || item.State == "closed"))
                .Select(item => new
                {
                    item.Id,
                    name = item.TitleOverride,
                    sessionName = item.TitleOverride,
                    templateTitle = item.TemplateVersion.TestTemplate.Title,
                    item.TemplateVersion.VersionNumber,
                    item.TestDate,
                    item.ClassLabel,
                    item.Course,
                    item.Priority,
                    item.State,
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
                name = item.TitleOverride,
                sessionName = item.TitleOverride,
                templateId = item.TemplateVersion.TestTemplateId,
                item.TemplateVersionId,
                templateTitle = item.TemplateVersion.TestTemplate.Title,
                item.TemplateVersion.VersionNumber,
                item.TestDate,
                item.ClassLabel,
                item.Course,
                item.Priority,
                item.State,
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
        if (string.IsNullOrWhiteSpace(request.TemplateVersionId)
            || request.TestDate == default
            || request.Priority is not ("economy" or "expedite")
            || request.SessionName?.Length > 500)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "TEST_SESSION_INVALID",
                "テスト実施を作成できません",
                "公開済みのひな形、実施日、処理方法を確認してください。");
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

        if (version.State != "published")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_VERSION_NOT_PUBLISHED",
                "このひな形は使用できません",
                "採点基準を確認して公開してから、テスト実施を作成してください。");
        }

        var now = timeProvider.GetUtcNow();
        var session = new TestSessionEntity
        {
            Id = UlidId.New(now),
            TemplateVersionId = version.Id,
            TitleOverride = string.IsNullOrWhiteSpace(request.SessionName)
                ? version.TestTemplate.Title
                : request.SessionName.Trim(),
            TestDate = request.TestDate,
            Course = TrimOrNull(request.Course),
            ClassLabel = TrimOrNull(request.ClassLabel),
            Priority = request.Priority,
            State = "draft",
            CreatedByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TestSessions.Add(session);
        AddAudit(db, now, principal, context, "test_session.created", session.Id);
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, session.Revision);
        return Results.Created($"/api/v1/test-sessions/{session.Id}", new
        {
            session.Id,
            name = session.TitleOverride,
            sessionName = session.TitleOverride,
            templateId = version.TestTemplateId,
            session.TemplateVersionId,
            templateTitle = version.TestTemplate.Title,
            templateVersionNumber = version.VersionNumber,
            session.TestDate,
            session.ClassLabel,
            session.Course,
            session.Priority,
            session.State,
            expectedStudentCount = 0,
            submissionCount = 0,
            finalizedCount = 0,
            attentionCount = 0,
            session.Revision,
        });
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

    private static Task<IResult> ArchiveSession(
        string sessionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        Transition(
            sessionId,
            "archived",
            ["closed"],
            "test_session.archived",
            context,
            principal,
            db,
            timeProvider,
            cancellationToken);

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

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        string Priority);

    private sealed record UpdateSessionBody(
        string Priority,
        long? Revision);

    private sealed record ReplaceRosterBody(IReadOnlyList<string> StudentIds);

    private sealed record OperatorUploadRow(
        string Id,
        string? UploadId,
        string? OriginalFileName,
        string RawState,
        DateTimeOffset? UploadCompletedAt,
        DateTimeOffset UpdatedAt,
        int SourceRank);

    private sealed record SessionCursorPosition(
        DateOnly TestDate,
        DateTimeOffset CreatedAt,
        string Id);

    private sealed record UploadStatusCursorPosition(
        DateTimeOffset UpdatedAt,
        string Id,
        int SourceRank);
}
