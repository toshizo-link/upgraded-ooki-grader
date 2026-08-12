using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Application.Templates;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Scoring;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;
using DomainTemplateVersion = OokiGrader.Domain.Templates.TemplateVersion;

namespace OokiGrader.Host.Api;

public static class TemplatesEndpoints
{
    private const string ManualPipelineVersion = "manual-v1";
    private const int MaximumTemplatePageSize = 200;
    private const int MinimumProposalVerificationConfidenceBasisPoints = 9_500;
    private const string TemplatesListRoute = "GET:/api/v1/templates";

    private static readonly JsonSerializerOptions GenerationJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> AcknowledgementOnlyAiNotices =
    [
        "正答はAIによる提案です。先生が根拠資料と照合してください。",
        "模範解答の転記候補です。原資料との照合が必要です。",
    ];

    private static readonly HashSet<string> QuestionTypes =
    [
        "multiple_choice",
        "boolean",
        "numeric",
        "exact_short_text",
        "semantic_short_text",
        "multi_part",
        "subjective",
        "unsupported",
    ];

    private static readonly HashSet<string> GradingModes =
    [
        "deterministic",
        "transcribe_then_rules",
        "ai_rubric",
        "manual",
    ];

    private static readonly HashSet<string> AnswerProvenances =
    [
        "provided_model_answer",
        "teacher_entered",
        "ai_proposed",
        "derived_variant",
    ];

    private static readonly HashSet<string> AnswerVariantTypes =
    [
        "canonical",
        "equivalent",
        "phonetic_exception",
        "numeric",
        "regex_restricted",
        "choice",
    ];

    public static IEndpointRouteBuilder MapTemplatesEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/templates")
            .WithTags("Templates")
            .RequireAuthorization("teacher");

        group.MapGet("/", ListTemplates).RequireRateLimiting("search");
        group.MapPost("/", CreateTemplate)
            .RequireAuthorization("teacher");
        group.MapGet("/{templateId}", GetTemplate);
        group.MapDelete("/{templateId}", ArchiveTemplate)
            .RequireAuthorization("teacher");
        group.MapPost("/{templateId}:restore", RestoreTemplate)
            .RequireAuthorization("teacher");
        group.MapPost("/{templateId}/versions", CreateTemplateVersion)
            .RequireAuthorization("teacher");
        group.MapGet("/{templateId}/versions/{versionId}", GetTemplateVersion);
        group.MapPost(
                "/{templateId}/versions/{versionId}/sources",
                AttachTemplateSource)
            .RequireAuthorization("teacher");
        group.MapPost(
                "/{templateId}/versions/{versionId}:generateDraft",
                GenerateTemplateDraft)
            .RequireAuthorization("teacher");
        group.MapGet(
            "/{templateId}/versions/{versionId}/generation",
            GetTemplateGeneration);
        group.MapGet(
            "/{templateId}/versions/{versionId}/sources/{sourceId}/content",
            GetTemplateSourceContent);
        group.MapGet(
            "/{templateId}/versions/{versionId}/questions",
            ListQuestions);
        group.MapPost(
                "/{templateId}/versions/{versionId}/questions:verifyProposals",
                VerifyQuestionProposals)
            .RequireAuthorization("teacher")
            .RequireIdempotency();
        group.MapPost(
                "/{templateId}/versions/{versionId}/questions:reorder",
                ReorderQuestions)
            .RequireAuthorization("teacher");
        group.MapPost(
                "/{templateId}/versions/{versionId}/questions",
                CreateQuestion)
            .RequireAuthorization("teacher");
        group.MapPatch(
                "/{templateId}/versions/{versionId}/questions/{questionId}",
                UpdateQuestion)
            .RequireAuthorization("teacher");
        group.MapPut(
                "/{templateId}/versions/{versionId}/questions/{questionId}",
                UpdateQuestion)
            .RequireAuthorization("teacher");
        group.MapDelete(
                "/{templateId}/versions/{versionId}/questions/{questionId}",
                DeleteQuestion)
            .RequireAuthorization("teacher");
        group.MapPost(
                "/{templateId}/versions/{versionId}:validate",
                ValidateTemplateVersion)
            .RequireAuthorization("teacher");
        group.MapPost(
                "/{templateId}/versions/{versionId}:publish",
                PublishTemplateVersion)
            .RequireAuthorization("teacher")
            .RequireIdempotency();

        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListTemplates(
        HttpContext context,
        string? search,
        string? q,
        string? state,
        string? subject,
        string? category,
        string? course,
        string? grade,
        string? testType,
        string? sort,
        bool? includeFacets,
        string? cursor,
        int? pageSize,
        int? limit,
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

        var requestedSearch = CursorPagination.TrimToNull(search)
            ?? CursorPagination.TrimToNull(q);
        if (!ListQuery.TryNormalizeSearch(
                context,
                requestedSearch,
                out var normalizedSearch,
                out var searchTokens,
                out var searchError))
        {
            return searchError!;
        }

        var query = db.TestTemplates
            .AsNoTracking()
            .Include(template => template.Versions)
            .ThenInclude(version => version.Questions)
            .AsSplitQuery();

        foreach (var token in searchTokens)
        {
            var pattern = ListQuery.ContainsPattern(token);
            query = query.Where(template =>
                EF.Functions.Like(template.Title, pattern, "\\")
                || (template.Subject != null
                    && EF.Functions.Like(template.Subject, pattern, "\\"))
                || (template.Category != null
                    && EF.Functions.Like(template.Category, pattern, "\\"))
                || (template.Course != null
                    && EF.Functions.Like(template.Course, pattern, "\\"))
                || (template.GradeLabel != null
                    && EF.Functions.Like(template.GradeLabel, pattern, "\\")));
        }

        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState is not null
            && normalizedState is not ("draft" or "active" or "retired" or "archived"))
        {
            return ListQuery.Invalid(
                context,
                "state は draft、active、retired、archived のいずれかを指定してください。");
        }

        if (normalizedState is not null)
        {
            query = query.Where(template => template.State == normalizedState);
        }
        else
        {
            // Archived templates are a recoverable deletion and stay out of the
            // ordinary working set. They remain discoverable through the explicit
            // archived filter so an administrator can restore them.
            query = query.Where(template => template.State != "archived");
        }

        if (!ListQuery.TryTrimFilter(
                context,
                subject,
                "subject",
                out var normalizedSubject,
                out var filterError)
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
                grade,
                "grade",
                out var normalizedGrade,
                out filterError))
        {
            return filterError!;
        }

        if (normalizedSubject is not null)
        {
            query = query.Where(template => template.Subject == normalizedSubject);
        }

        if (normalizedCategory is not null)
        {
            query = query.Where(template => template.Category == normalizedCategory);
        }

        if (normalizedCourse is not null)
        {
            query = query.Where(template => template.Course == normalizedCourse);
        }

        if (normalizedGrade is not null)
        {
            query = query.Where(template => template.GradeLabel == normalizedGrade);
        }

        var normalizedTestTypeText = CursorPagination.TrimToNull(testType);
        TestType? normalizedTestType = normalizedTestTypeText switch
        {
            null => null,
            "hop" => TestType.Hop,
            "step" => TestType.Step,
            "classPlacement" => TestType.ClassPlacement,
            "other" => TestType.Other,
            _ => null,
        };
        if (normalizedTestTypeText is not null && normalizedTestType is null)
        {
            return ListQuery.Invalid(
                context,
                "testType は hop、step、classPlacement、other のいずれかを指定してください。");
        }

        if (normalizedTestType.HasValue)
        {
            query = FilterByPreferredTestType(
                query,
                normalizedTestType.Value);
        }

        var normalizedSort = CursorPagination.TrimToNull(sort) ?? "-updatedAt";
        if (normalizedSort is not (
            "-updatedAt"
            or "updatedAt"
            or "name"
            or "-name"
            or "subject"
            or "-subject"))
        {
            return ListQuery.Invalid(
                context,
                "sort は updatedAt、name、subject のいずれかに、必要なら先頭の - を付けて指定してください。");
        }

        var cursorSort = normalizedSort switch
        {
            "-updatedAt" => "-updatedAt,id",
            "updatedAt" => "updatedAt,id",
            "name" => "name,id",
            "-name" => "-name,id",
            "subject" => "subject,id",
            _ => "-subject,id",
        };
        var filterBinding = CursorPagination.Bind(
            ("category", normalizedCategory),
            ("course", normalizedCourse),
            ("grade", normalizedGrade),
            ("search", normalizedSearch),
            ("sort", cursorSort),
            ("state", normalizedState),
            ("subject", normalizedSubject),
            ("testType", normalizedTestTypeText));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                TemplatesListRoute,
                filterBinding,
                out TemplateCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrEmpty(position.Id)
                || position.Id.Length > ListQuery.MaximumIdLength
                || (normalizedSort is "-updatedAt" or "updatedAt"
                    ? position.Timestamp is null
                    : position.Text is null || position.Text.Length > 1_000)))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = normalizedSort switch
            {
                "-updatedAt" => query.Where(template =>
                    template.UpdatedAt < position.Timestamp
                    || (template.UpdatedAt == position.Timestamp
                        && string.Compare(template.Id, position.Id) > 0)),
                "updatedAt" => query.Where(template =>
                    template.UpdatedAt > position.Timestamp
                    || (template.UpdatedAt == position.Timestamp
                        && string.Compare(template.Id, position.Id) > 0)),
                "name" => query.Where(template =>
                    string.Compare(template.Title, position.Text) > 0
                    || (template.Title == position.Text
                        && string.Compare(template.Id, position.Id) > 0)),
                "-name" => query.Where(template =>
                    string.Compare(template.Title, position.Text) < 0
                    || (template.Title == position.Text
                        && string.Compare(template.Id, position.Id) > 0)),
                "subject" => query.Where(template =>
                    string.Compare(template.Subject ?? string.Empty, position.Text) > 0
                    || ((template.Subject ?? string.Empty) == position.Text
                        && string.Compare(template.Id, position.Id) > 0)),
                _ => query.Where(template =>
                    string.Compare(template.Subject ?? string.Empty, position.Text) < 0
                    || ((template.Subject ?? string.Empty) == position.Text
                        && string.Compare(template.Id, position.Id) > 0)),
            };
        }

        IOrderedQueryable<TestTemplateEntity> ordered = normalizedSort switch
        {
            "-updatedAt" => query.OrderByDescending(
                template => template.UpdatedAt),
            "updatedAt" => query.OrderBy(template => template.UpdatedAt),
            "name" => query.OrderBy(template => template.Title),
            "-name" => query.OrderByDescending(template => template.Title),
            "subject" => query.OrderBy(template => template.Subject ?? string.Empty),
            _ => query.OrderByDescending(
                template => template.Subject ?? string.Empty),
        };
        var templates = await ordered
            .ThenBy(template => template.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = templates.Count > take;
        if (hasMore)
        {
            templates.RemoveAt(take);
        }

        var items = templates.Select(ToTemplateSummary).ToArray();
        var nextCursor = templates.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                TemplatesListRoute,
                filterBinding,
                hasMore,
                new TemplateCursorPosition(
                    normalizedSort is "-updatedAt" or "updatedAt"
                        ? templates[^1].UpdatedAt
                        : null,
                    normalizedSort switch
                    {
                        "name" or "-name" => templates[^1].Title,
                        "subject" or "-subject" =>
                            templates[^1].Subject ?? string.Empty,
                        _ => null,
                    },
                    templates[^1].Id));
        var facets = includeFacets == true
            ? await LoadTemplateFacetsAsync(db, cancellationToken)
            : null;

        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
            facets,
        });
    }

    private static async Task<object> LoadTemplateFacetsAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var subjectRows = await db.TestTemplates
            .AsNoTracking()
            .Where(template => template.Subject != null
                && template.Subject != string.Empty
                && template.Subject.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(template => template.Subject!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var subjects = subjectRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var categoryRows = await db.TestTemplates
            .AsNoTracking()
            .Where(template => template.Category != null
                && template.Category != string.Empty
                && template.Category.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(template => template.Category!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var categories = categoryRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var gradeRows = await db.TestTemplates
            .AsNoTracking()
            .Where(template => template.GradeLabel != null
                && template.GradeLabel != string.Empty
                && template.GradeLabel.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(template => template.GradeLabel!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var grades = gradeRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var courseRows = await db.TestTemplates
            .AsNoTracking()
            .Where(template => template.Course != null
                && template.Course != string.Empty
                && template.Course.Length <= ListQuery.MaximumFilterLength)
            .GroupBy(template => template.Course!)
            .Select(group => new { Value = group.Key, Count = group.Count() })
            .OrderBy(item => item.Value)
            .Take(ListQuery.MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        var courses = courseRows
            .Select(item => new FacetValue(item.Value, item.Value, item.Count))
            .ToArray();
        var testTypes = new List<FacetValue>(4);
        foreach (var value in new[]
        {
            TestType.Hop,
            TestType.Step,
            TestType.ClassPlacement,
            TestType.Other,
        })
        {
            var count = await FilterByPreferredTestType(
                    db.TestTemplates.AsNoTracking(),
                    value)
                .CountAsync(cancellationToken);
            if (count > 0)
            {
                testTypes.Add(new FacetValue(
                    TestTypeValue(value),
                    TestTypeLabel(value),
                    count));
            }
        }

        return new { subjects, categories, grades, courses, testTypes };
    }

    private static IQueryable<TestTemplateEntity> FilterByPreferredTestType(
        IQueryable<TestTemplateEntity> query,
        TestType value) =>
        query.Where(template => template.Versions.Any(version =>
            version.TestType == value
            && (template.ActiveVersionId == version.Id
                || (template.ActiveVersionId == null
                    && !template.Versions.Any(other =>
                        other.VersionNumber > version.VersionNumber)))));

    private static string TestTypeValue(TestType value) => value switch
    {
        TestType.Hop => "hop",
        TestType.Step => "step",
        TestType.ClassPlacement => "classPlacement",
        _ => "other",
    };

    private static string TestTypeLabel(TestType value) => value switch
    {
        TestType.Hop => "HOP",
        TestType.Step => "STEP",
        TestType.ClassPlacement => "クラス分け",
        _ => "その他",
    };

    private sealed record FacetValue(string Value, string Label, int Count);

    private static async Task<IResult> CreateTemplate(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateTemplateApiRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = ValidateTemplateRequest(request);
        if (errors.Count > 0)
        {
            return ValidationProblem(
                context,
                "TEMPLATE_INVALID",
                "テストひな形を作成できません",
                errors);
        }

        var now = timeProvider.GetUtcNow();
        var template = new TestTemplateEntity
        {
            Id = UlidId.New(now),
            Title = request.Title!.Trim(),
            Subject = TrimOrNull(request.Subject),
            Category = TrimOrNull(request.Category),
            Course = TrimOrNull(request.Course),
            GradeLabel = TrimOrNull(request.GradeLabel),
            Notes = TrimOrNull(request.Notes),
            DefaultPointsMilli = request.DefaultPointsMilli!.Value,
            Source = "manual",
            State = "draft",
            CreatedByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.TestTemplates.Add(template);
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.created",
            "template",
            template.Id,
            new { defaultPointsMilli = request.DefaultPointsMilli });
        await db.SaveChangesAsync(cancellationToken);

        ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
        return Results.Created(
            $"/api/v1/templates/{template.Id}",
            ToTemplateSummary(template));
    }

    private static async Task<IResult> GetTemplate(
        string templateId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var template = await db.TestTemplates
            .AsNoTracking()
            .Include(item => item.Versions)
            .ThenInclude(version => version.Questions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
        return Results.Ok(ToTemplateSummary(template));
    }

    private static async Task<IResult> ArchiveTemplate(
        string templateId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var template = await db.TestTemplates
            .Include(item => item.Versions)
            .SingleOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        // A lost response can be retried safely without requiring the caller to
        // retain the pre-archive revision. No additional audit event is emitted.
        if (template.State == "archived")
        {
            ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
            return Results.NoContent();
        }

        if (template.Versions.Any(version => version.State == "generating"))
        {
            return Conflict(
                context,
                "TEMPLATE_EXTRACTION_IN_PROGRESS",
                "自動下書きの作成中は削除できません",
                "自動下書きの処理が完了または失敗してから、もう一度削除してください。");
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                bodyRevision: null,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        if (template.Revision != expectedRevision)
        {
            return Stale(context, template.Revision);
        }

        var now = timeProvider.GetUtcNow();
        var previousState = template.State;
        template.State = "archived";
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.archived",
            "template",
            template.Id,
            new { previousState, previousRevision = expectedRevision });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.TestTemplates
                .AsNoTracking()
                .Where(item => item.Id == template.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            return Stale(context, currentRevision ?? template.Revision);
        }

        ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreTemplate(
        string templateId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] TemplateLifecycleApiRequest? request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var template = await db.TestTemplates
            .Include(item => item.Versions)
            .ThenInclude(version => version.Questions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        // Restore is idempotent once the archived state has already been left.
        if (template.State != "archived")
        {
            ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
            return Results.Ok(ToTemplateSummary(template));
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request?.Revision,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        if (template.Revision != expectedRevision)
        {
            return Stale(context, template.Revision);
        }

        var restoredState = template.Versions.Any(
            version => version.State == "published")
                ? "active"
                : "draft";
        var now = timeProvider.GetUtcNow();
        template.State = restoredState;
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.restored",
            "template",
            template.Id,
            new { restoredState, previousRevision = expectedRevision });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.TestTemplates
                .AsNoTracking()
                .Where(item => item.Id == template.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            return Stale(context, currentRevision ?? template.Revision);
        }

        ApiHelpers.SetRevisionEtag(context.Response, template.Revision);
        return Results.Ok(ToTemplateSummary(template));
    }

    private static async Task<IResult> CreateTemplateVersion(
        string templateId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] CreateTemplateVersionApiRequest? request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var template = await db.TestTemplates
            .Include(item => item.Versions)
            .SingleOrDefaultAsync(item => item.Id == templateId, cancellationToken);
        if (template is null)
        {
            return Results.NotFound();
        }

        if (template.State == "archived")
        {
            return Conflict(
                context,
                "TEMPLATE_ARCHIVED",
                "アーカイブ済みのひな形には版を追加できません",
                "ひな形を復元してから、新しい版を作成してください。");
        }

        request ??= new CreateTemplateVersionApiRequest();
        var sourceVersionId = request.SourceVersionId ?? request.CloneFromVersionId;
        TemplateVersionEntity? source = null;
        if (!string.IsNullOrWhiteSpace(sourceVersionId))
        {
            source = await VersionGraph(db, tracking: false)
                .SingleOrDefaultAsync(
                    version => version.Id == sourceVersionId
                        && version.TestTemplateId == templateId,
                    cancellationToken);
            if (source is null)
            {
                return Results.NotFound();
            }

            if (request.SourceRevision is > 0
                && source.Revision != request.SourceRevision.Value)
            {
                return Stale(context, source.Revision);
            }
        }

        var now = timeProvider.GetUtcNow();
        var nextVersionNumber = template.Versions.Count == 0
            ? 1
            : checked(template.Versions.Max(version => version.VersionNumber) + 1);
        var version = new TemplateVersionEntity
        {
            Id = UlidId.New(now),
            TestTemplateId = templateId,
            VersionNumber = nextVersionNumber,
            State = "draft",
            BasedOnVersionId = source?.Id,
            TargetTotalPointsMilli = request.TargetTotalPointsMilli
                ?? source?.TargetTotalPointsMilli,
            DefaultPointsMilli = request.DefaultPointsMilli
                ?? source?.DefaultPointsMilli
                ?? template.DefaultPointsMilli,
            DefaultAllowNonKanji = request.DefaultAllowNonKanji
                ?? source?.DefaultAllowNonKanji
                ?? false,
            PipelineVersion = ManualPipelineVersion,
            ExpectedSubmissionPageCount = source?.ExpectedSubmissionPageCount,
            TestType = source?.TestType,
            AnswerStyle = source?.AnswerStyle,
            PromptSystem = source?.PromptSystem,
            OriginatingBatchId = source?.OriginatingBatchId,
            OriginatingUnitId = source?.OriginatingUnitId,
            GenerationProfileVersion = source?.GenerationProfileVersion,
            GenerationProfileJson = source?.GenerationProfileJson,
            GenerationProfileHash = source?.GenerationProfileHash,
            StepSetIndex = source?.StepSetIndex,
            StepVariationIndex = source?.StepVariationIndex,
            PrintedTestName = source?.PrintedTestName,
            ResolvedGrade = source?.ResolvedGrade,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.TemplateVersions.Add(version);

        if (source is not null)
        {
            CloneVersionContent(db, source, version, now, principal);
        }

        AddAudit(
            db,
            now,
            principal,
            context,
            source is null
                ? "template_version.created"
                : "template_version.cloned",
            "template_version",
            version.Id,
            new
            {
                templateId,
                basedOnVersionId = source?.Id,
                versionNumber = nextVersionNumber,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(
                context,
                "TEMPLATE_VERSION_CONFLICT",
                "新しい版を作成できませんでした",
                "別の職員が同時に版を作成しました。再読み込みしてください。");
        }

        var created = await VersionGraph(db, tracking: false)
            .SingleAsync(item => item.Id == version.Id, cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, created.Revision);
        return Results.Created(
            $"/api/v1/templates/{templateId}/versions/{created.Id}",
            ToVersionDetail(created));
    }

    private static async Task<IResult> GetTemplateVersion(
        string templateId,
        string versionId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: false,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.Ok(ToVersionDetail(version));
    }

    private static async Task<IResult> AttachTemplateSource(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] AttachTemplateSourceApiRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var requestedSourceRole = NormalizeSourceRole(request.SourceRole);
        if (string.IsNullOrWhiteSpace(request.UploadId)
            || (!string.IsNullOrWhiteSpace(request.SourceRole)
                && requestedSourceRole is null)
            || request.DisplayName?.Trim().Length > 500)
        {
            return ValidationProblem(
                context,
                "TEMPLATE_SOURCE_INVALID",
                "問題用紙を追加できません",
                [
                    FieldError(
                        "source",
                        "INVALID",
                        "完了済みのアップロード、ファイル区分、表示名を確認してください。"),
                ]);
        }

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? null
            : request.DisplayName.Trim();
        var existing = version.Sources.FirstOrDefault(
            source => source.UploadSessionId == request.UploadId);
        if (existing is not null)
        {
            var expectedDisplayName = displayName ?? existing.DisplayName;
            if ((requestedSourceRole is not null
                    && existing.SourceRole != requestedSourceRole)
                || existing.DisplayName != expectedDisplayName)
            {
                return Conflict(
                    context,
                    "TEMPLATE_SOURCE_ALREADY_ATTACHED",
                    "このファイルはすでに追加されています",
                    "同じファイルを別の区分で追加することはできません。");
            }

            return Results.Ok(ToTemplateSourceResponse(
                existing,
                templateId: templateId,
                versionId: version.Id,
                mimeType: existing.UploadSession?.DeclaredMimeType));
        }

        var upload = await db.UploadSessions
            .SingleOrDefaultAsync(
                item => item.Id == request.UploadId,
                cancellationToken);
        if (upload is null)
        {
            return Results.NotFound();
        }

        if (upload.Purpose != "template_source"
            || upload.State != "completed"
            || upload.DestinationType != "template_source")
        {
            return Conflict(
                context,
                "TEMPLATE_SOURCE_UPLOAD_INCOMPLETE",
                "ファイルの受信が完了していません",
                "アップロードを完了してから問題用紙へ追加してください。");
        }

        var fileReferenceId = await db.FileReferences
            .Where(reference =>
                reference.OwnerType == "upload_session"
                && reference.OwnerId == upload.Id
                && reference.Purpose == "template_source")
            .Select(reference => reference.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (fileReferenceId is null)
        {
            return Conflict(
                context,
                "TEMPLATE_SOURCE_FILE_MISSING",
                "保存済みファイルを確認できません",
                "アップロードをやり直してください。");
        }

        TemplateSourceRoleResolution? inference = null;
        var sourceRole = requestedSourceRole;
        if (sourceRole is null)
        {
            inference = TemplateSourceRoleInference.Infer(
                displayName ?? upload.OriginalFileName);
            sourceRole = inference.SourceRole;
        }

        var now = timeProvider.GetUtcNow();
        var source = new TemplateSourceEntity
        {
            Id = UlidId.New(now),
            TemplateVersionId = version.Id,
            UploadSessionId = upload.Id,
            FileReferenceId = fileReferenceId,
            SourceRole = sourceRole,
            DisplayName = displayName ?? upload.OriginalFileName,
            Ordinal = version.Sources.Count == 0
                ? 0
                : checked(version.Sources.Max(item => item.Ordinal) + 1),
            UploadedByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
        };
        db.TemplateSources.Add(source);
        TouchVersion(db, version, now);
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.source_attached",
            "template_version",
            version.Id,
            new
            {
                sourceId = source.Id,
                uploadId = upload.Id,
                sourceRole,
                sourceRoleInferred = inference is not null,
                sourceRoleInferenceReason = inference?.ReasonCode,
                sourceRoleConfidenceBasisPoints =
                    inference?.ConfidenceBasisPoints,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(
                context,
                "TEMPLATE_SOURCE_CONFLICT",
                "問題用紙を追加できませんでした",
                "別の職員が同時にファイルを追加しました。再読み込みしてください。");
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.Created(
            $"/api/v1/templates/{templateId}/versions/{version.Id}",
            ToTemplateSourceResponse(
                source,
                inference,
                templateId,
                version.Id,
                upload.DeclaredMimeType));
    }

    private static async Task<IResult> GetTemplateSourceContent(
        string templateId,
        string versionId,
        string sourceId,
        HttpContext context,
        OokiGraderDbContext db,
        [FromServices] IContentStore contentStore,
        CancellationToken cancellationToken)
    {
        var source = await db.TemplateSources
            .AsNoTracking()
            .Include(item => item.TemplateVersion)
            .SingleOrDefaultAsync(
                item => item.Id == sourceId
                    && item.TemplateVersionId == versionId
                    && item.TemplateVersion.TestTemplateId == templateId,
                cancellationToken)
            .ConfigureAwait(false);
        if (source is null)
        {
            return Results.NotFound();
        }

        var reference = source.FileReferenceId is null
            ? null
            : await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleOrDefaultAsync(
                    item => item.Id == source.FileReferenceId,
                    cancellationToken)
                .ConfigureAwait(false);
        var fileObject = reference?.FileObject;
        var isUploadedSource = reference is not null
            && reference.OwnerType == "upload_session"
            && reference.OwnerId == source.UploadSessionId
            && reference.Purpose == "template_source"
            && fileObject?.StorageClass
                == ContentStorageClass.TemplateSource.ToString();
        var isDeterministicDerivedSource = reference is not null
            && reference.OwnerType == "template_generation_unit"
            && reference.OwnerId == source.TemplateVersion.OriginatingUnitId
            && reference.Purpose == "derived_source"
            && fileObject?.StorageClass
                == ContentStorageClass.TemplateDerived.ToString();
        if (reference is null
            || fileObject is null
            || (!isUploadedSource && !isDeterministicDerivedSource)
            || fileObject.State != "available"
            || fileObject.VerifiedMime is not (
                "application/pdf"
                or "image/png"
                or "image/jpeg"
                or "image/webp"
                or "image/tiff")
            || fileObject.Bytes <= 0)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status410Gone,
                "TEMPLATE_SOURCE_UNAVAILABLE",
                "元のテストを表示できません",
                "ファイルの保存状態を管理者に確認してください。");
        }

        var locator = new ContentObjectLocator(
            isDeterministicDerivedSource
                ? ContentStorageClass.TemplateDerived
                : ContentStorageClass.TemplateSource,
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
                "TEMPLATE_SOURCE_GONE",
                "元のテストが見つかりません",
                "ファイルの保存状態を管理者に確認してください。");
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

    private static async Task<IResult> GenerateTemplateDraft(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] GenerateTemplateDraftApiRequest? request,
        OokiGraderDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(
                "Features:Ai.TemplateGeneration",
                false)
            || (!configuration.GetValue(
                    "Features:Ai.GeminiDirect",
                    false)
                && !configuration.GetValue(
                    "Features:Ai.OpenRouter",
                    false)))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "AI_TEMPLATE_GENERATION_DISABLED",
                "自動下書きは現在無効です",
                "管理者がAIひな形生成機能を有効にするまで、手動で作成してください。");
        }

        var replaceableMetadataFields =
            request?.ReplaceableMetadataFields?.ToArray() ?? [];
        if (request?.Priority is not (
                null or "economy" or "expedite")
            || replaceableMetadataFields.Length > 5
            || replaceableMetadataFields.Any(field =>
                !IsReplaceableMetadataField(field))
            || replaceableMetadataFields
                .Distinct(StringComparer.Ordinal)
                .Count() != replaceableMetadataFields.Length)
        {
            return ValidationProblem(
                context,
                "TEMPLATE_GENERATION_INVALID",
                "自動下書きを開始できません",
                [
                    FieldError(
                        request?.Priority is not (
                            null or "economy" or "expedite")
                            ? "priority"
                            : "replaceableMetadataFields",
                        "INVALID",
                        "処理方法と自動補完できる基本情報を確認してください。"),
                ]);
        }
        Array.Sort(replaceableMetadataFields, StringComparer.Ordinal);

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        if (version.Sources.Count == 0
            || version.Sources.Any(source =>
                source.FileReferenceId is null))
        {
            return ValidationProblem(
                context,
                "TEMPLATE_SOURCE_REQUIRED",
                "自動下書きを開始できません",
                [
                    FieldError(
                        "sources",
                        "REQUIRED",
                        "保存済みの問題用紙または模範解答を追加してください。"),
                ]);
        }

        if (version.Questions.Count > 0)
        {
            return Conflict(
                context,
                "TEMPLATE_GENERATION_REQUIRES_EMPTY_DRAFT",
                "自動下書きを開始できません",
                "手動で入力済みの問題は自動生成で置き換えません。空の版を作成してください。");
        }

        var profile = await db.AiTaskProfiles
            .AsNoTracking()
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.TaskType == "templateExtraction"
                    && item.Active,
                cancellationToken);
        if (profile is null
            || !AiTaskProfileRuntimePolicy.IsReadyApprovalState(
                profile.ApprovalState)
            || profile.ModelId != profile.AiConnection.ModelId
            || profile.ConnectionRevision
                != profile.AiConnection.CredentialRevision
            || !AiProviderCatalog.IsConnectionShapeValid(
                profile.AiConnection.Provider,
                profile.AiConnection.EndpointProfile,
                profile.AiConnection.ModelId)
            || !AiProviderCatalog.SupportsImageTasks(
                profile.AiConnection.Provider,
                profile.AiConnection.ModelId)
            || (profile.AiConnection.Provider == AiProviders.GeminiDirect
                ? !configuration.GetValue(
                    "Features:Ai.GeminiDirect",
                    false)
                : !configuration.GetValue(
                    "Features:Ai.OpenRouter",
                    false))
            || profile.AiConnection.State != "active"
            || profile.AiConnection.LastCapabilityProbeState != "passed")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "AI_NOT_CONFIGURED",
                "自動下書きを開始できません",
                "画像入力と構造化出力に合格したAIテンプレート抽出設定を有効にしてください。");
        }

        var now = timeProvider.GetUtcNow();
        var generationRevision = checked(version.Revision + 1);
        var generationJobPrefix =
            $"template-version:{version.Id}:gemini-extract:";
        var supersededJobs = await db.BackgroundJobs
            .Where(item =>
                item.Type == TemplateExtractionJobWorker.JobType
                && (item.State == "failed" || item.State == "blocked")
                && item.DeduplicationKey.StartsWith(generationJobPrefix))
            .ToListAsync(cancellationToken);
        foreach (var supersededJob in supersededJobs)
        {
            supersededJob.State = "cancelled";
            supersededJob.ErrorCode = "superseded_by_retry";
            supersededJob.SafeErrorDetail = null;
            supersededJob.LeaseOwner = null;
            supersededJob.LeaseExpiresAt = null;
            supersededJob.CompletedAt = now;
            supersededJob.UpdatedAt = now;
        }

        var job = new BackgroundJobEntity
        {
            Id = UlidId.New(now),
            Type = TemplateExtractionJobWorker.JobType,
            SchemaVersion = 1,
            DeduplicationKey =
                $"{generationJobPrefix}r{generationRevision}",
            Priority = request?.Priority == "expedite" ? 100 : 0,
            PayloadJson = JsonSerializer.Serialize(new
            {
                templateVersionId = version.Id,
                generationRevision,
                replaceableMetadataFields,
            }),
            State = "queued",
            MaxAttempts = 8,
            NextAttemptAt = now,
            CorrelationId = context.TraceIdentifier,
            CreatedAt = now,
            UpdatedAt = now,
        };
        version.State = "generating";
        version.PipelineVersion =
            TemplateExtractionJobWorker.PipelineVersion;
        version.UpdatedAt = now;
        db.BackgroundJobs.Add(job);
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "template_version",
            AggregateId = version.Id,
            EventType = "template.generation_status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                templateVersionId = version.Id,
                state = "queued",
            }),
            CorrelationId = context.TraceIdentifier,
            OccurredAt = now,
        });
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.ai_generation_queued",
            "template_version",
            version.Id,
            new
            {
                jobId = job.Id,
                priority = request?.Priority ?? "economy",
                sourceCount = version.Sources.Count,
                replaceableMetadataFields,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(
                context,
                "TEMPLATE_GENERATION_CONFLICT",
                "自動下書きを開始できません",
                "別の処理が同じ版を更新しました。再読み込みしてください。");
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.Accepted(
            $"/api/v1/templates/{templateId}/versions/{version.Id}/generation",
            new
            {
                state = "queued",
                jobId = job.Id,
                templateVersionId = version.Id,
                revision = version.Revision,
            });
    }

    private static async Task<IResult> GetTemplateGeneration(
        string templateId,
        string versionId,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: false,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        var latestRequest = await db.AiRequests
            .AsNoTracking()
            .Where(item =>
                item.EntityType == "template_version"
                && item.EntityId == version.Id
                && item.Purpose == "templateExtraction")
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var latestJob = await db.BackgroundJobs
            .AsNoTracking()
            .Where(item =>
                item.Type == TemplateExtractionJobWorker.JobType
                && item.DeduplicationKey.StartsWith(
                    $"template-version:{version.Id}:gemini-extract:"))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var state = version.AiGenerationProvenanceId is not null
            ? "completed"
            : version.State == "generating"
                ? latestRequest?.State == "dispatching"
                    || latestJob?.State == "leased"
                        ? "running"
                        : "queued"
                : latestRequest?.State is
                    "invalid_output"
                    or "safety_blocked"
                    or "failed"
                    or "budget_blocked"
                    || latestJob?.State is "failed" or "blocked"
                        ? "failed"
                        : "manual";
        var errorCode = latestRequest?.ErrorCode ?? latestJob?.ErrorCode;
        IReadOnlyList<TemplateValidationIssue> reviewIssues =
            state == "completed"
                ? BuildExtractionConsistencyIssues(version)
                : errorCode is null
                    ? []
                    : [ToGenerationFailureIssue(errorCode)];
        return Results.Ok(new
        {
            state,
            completedQuestions = version.Questions.Count,
            estimatedQuestions = version.Questions.Count,
            warnings = reviewIssues.Select(issue => issue.Message).ToArray(),
            reviewIssues,
            inferredMetadata = ReadInferredMetadata(
                latestRequest?.ValidatedResponseJson),
            detail = state switch
            {
                "manual" => "手動編集モード",
                "failed" => "自動下書きを作成できませんでした。手動編集を続けられます。",
                _ => null,
            },
            aiRequestId = version.AiGenerationProvenanceId
                ?? latestRequest?.Id,
            jobId = latestJob?.Id,
        });
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListQuestions(
        string templateId,
        string versionId,
        HttpContext context,
        string? cursor,
        int? pageSize,
        int? limit,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var versionQuery = db.TemplateVersions
            .AsNoTracking()
            .Where(version => version.TestTemplateId == templateId);
        var resolvedVersionId = versionId == "draft"
            ? await versionQuery
                .Where(version => version.State == "draft")
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => version.Id)
                .FirstOrDefaultAsync(cancellationToken)
            : await versionQuery
                .Where(version => version.Id == versionId)
                .Select(version => version.Id)
                .SingleOrDefaultAsync(cancellationToken);
        if (resolvedVersionId is null)
        {
            return Results.NotFound();
        }

        var take = Math.Clamp(
            pageSize ?? limit ?? 50,
            1,
            MaximumTemplatePageSize);
        var route =
            $"GET:/api/v1/templates/{templateId}/versions/{resolvedVersionId}/questions";
        var filterBinding = CursorPagination.Bind(
            ("sort", "orderIndex,id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                route,
                filterBinding,
                out QuestionCursorPosition position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (position.OrderIndex < 0
                || string.IsNullOrEmpty(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var query = db.Questions
            .AsNoTracking()
            .Include(question => question.AcceptedAnswers)
            .Include(question => question.QuestionRegion)
            .Include(question => question.AnswerRegion)
            .AsSplitQuery()
            .Where(question => question.TemplateVersionId == resolvedVersionId);
        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(question =>
                question.OrderIndex > position.OrderIndex
                || (question.OrderIndex == position.OrderIndex
                    && string.Compare(question.Id, position.Id) > 0));
        }

        var questions = await query
            .OrderBy(question => question.OrderIndex)
            .ThenBy(question => question.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = questions.Count > take;
        if (hasMore)
        {
            questions.RemoveAt(take);
        }

        var items = questions
            .Select(ToQuestionResponse)
            .ToArray();
        var nextCursor = questions.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                route,
                filterBinding,
                hasMore,
                new QuestionCursorPosition(
                    questions[^1].OrderIndex,
                    questions[^1].Id));
        return Results.Ok(new
        {
            items,
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record TemplateCursorPosition(
        DateTimeOffset? Timestamp,
        string? Text,
        string Id);

    private sealed record QuestionCursorPosition(
        int OrderIndex,
        string Id);

    private static async Task<IResult> VerifyQuestionProposals(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] VerifyQuestionProposalsApiRequest? request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request?.SelectionMode is not (
                null or "allNonBlocking" or "all"))
        {
            return ValidationProblem(
                context,
                "TEMPLATE_PROPOSAL_SELECTION_INVALID",
                "確認対象を選択できません",
                [
                    FieldError(
                        "selectionMode",
                        "INVALID",
                        "確認方式は all または allNonBlocking を指定してください。"),
                ]);
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request?.Revision,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.Revision != expectedRevision)
        {
            return Stale(context, version.Revision);
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        if (string.IsNullOrWhiteSpace(version.AiGenerationProvenanceId))
        {
            return Conflict(
                context,
                "TEMPLATE_PROPOSAL_REQUIRED",
                "確認できる自動生成案がありません",
                "Gemini で下書きを生成してから一括確認してください。");
        }

        var selectionMode = request?.SelectionMode ?? "allNonBlocking";
        var assessment = AssessProposalVerification(
            version,
            acknowledgeReviewableIssues: selectionMode == "all");
        if (assessment.EligibleQuestions.Count > 0)
        {
            var confirmableDraft = DomainTemplateVersion.CreateDraft(
                version.Id,
                version.TestTemplateId,
                version.VersionNumber,
                version.PipelineVersion,
                assessment.EligibleQuestions.Select(
                    question => BuildDomainQuestion(question, version)),
                aiGenerationProvenanceId: version.AiGenerationProvenanceId);
            _ = confirmableDraft.ConfirmQuestionProposals(
                assessment.EligibleQuestions.Select(question => question.Id));
        }

        var now = timeProvider.GetUtcNow();
        var verifiedQuestionCount = 0;
        var verifiedAnswerCount = 0;
        foreach (var question in assessment.EligibleQuestions)
        {
            var changed = !question.TeacherVerified;
            question.TeacherVerified = true;
            foreach (var answer in question.AcceptedAnswers)
            {
                if (!answer.TeacherVerified)
                {
                    answer.TeacherVerified = true;
                    answer.UpdatedAt = now;
                    verifiedAnswerCount++;
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            question.UpdatedAt = now;
            verifiedQuestionCount++;
        }

        if (verifiedQuestionCount > 0 || verifiedAnswerCount > 0)
        {
            TouchVersion(db, version, now);
        }

        AddAudit(
            db,
            now,
            principal,
            context,
            "template.proposals_verified",
            "template_version",
            version.Id,
            new
            {
                selectionMode,
                verifiedQuestionCount,
                verifiedAnswerCount,
                skippedQuestionCount = assessment.BlockedQuestionCount,
                issueCodes = assessment.Issues
                    .Select(issue => issue.Code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
                previousRevision = expectedRevision,
            });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.TemplateVersions
                .AsNoTracking()
                .Where(item => item.Id == version.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            return Stale(context, currentRevision ?? version.Revision);
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.Ok(
            new VerifyQuestionProposalsResponse(
                version.Revision,
                verifiedQuestionCount,
                verifiedAnswerCount,
                assessment.BlockedQuestionCount,
                assessment.Issues,
                version.Questions
                    .OrderBy(question => question.OrderIndex)
                    .ThenBy(question => question.Id)
                    .Select(ToQuestionResponse)
                    .ToArray()));
    }

    private static async Task<IResult> ReorderQuestions(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ReorderQuestionsApiRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var requestedIds = request.QuestionIds?.ToArray() ?? [];
        var requestedSet = requestedIds.ToHashSet(StringComparer.Ordinal);
        var currentSet = version.Questions
            .Select(question => question.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (requestedIds.Length != version.Questions.Count
            || requestedSet.Count != requestedIds.Length
            || !requestedSet.SetEquals(currentSet))
        {
            return ValidationProblem(
                context,
                "QUESTION_ORDER_INVALID",
                "問題の並び順を保存できません",
                [
                    FieldError(
                        "questionIds",
                        "SET_MISMATCH",
                        "現在の版に含まれる問題を重複なくすべて指定してください。"),
                ]);
        }

        var questionsById = version.Questions.ToDictionary(
            question => question.Id,
            StringComparer.Ordinal);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken);
        try
        {
            for (var index = 0; index < requestedIds.Length; index++)
            {
                questionsById[requestedIds[index]].OrderIndex = -(index + 1);
            }

            await db.SaveChangesAsync(cancellationToken);

            for (var index = 0; index < requestedIds.Length; index++)
            {
                questionsById[requestedIds[index]].OrderIndex = index + 1;
            }

            TouchVersion(db, version, now);
            AddAudit(
                db,
                now,
                principal,
                context,
                "template.questions_reordered",
                "template_version",
                version.Id,
                new { questionIds = requestedIds });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(
                context,
                "QUESTION_ORDER_CONFLICT",
                "問題の並び順を保存できませんでした",
                "別の職員が同時に編集しました。再読み込みしてください。");
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        var items = requestedIds
            .Select(id => ToQuestionResponse(questionsById[id]))
            .ToArray();
        return Results.Ok(new
        {
            items,
            nextCursor = (string?)null,
            totalApproximate = items.Length,
            revision = version.Revision,
        });
    }

    private static async Task<IResult> CreateQuestion(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] QuestionWriteRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var order = request.Order
            ?? request.SortOrder
            ?? (version.Questions.Count == 0
                ? 1
                : checked(version.Questions.Max(question => question.OrderIndex) + 1));
        var writeErrors = ValidateQuestionWrite(request, order, creating: true);
        if (writeErrors.Count > 0)
        {
            return ValidationProblem(
                context,
                "QUESTION_INVALID",
                "問題を追加できません",
                writeErrors);
        }

        var maximumPoints =
            request.MaxPointsMilli ?? version.DefaultPointsMilli;
        var pointIncrement = request.PointIncrementMilli
            ?? QuestionGradingDefaultPolicy.PointIncrementMilliFor(
                maximumPoints);
        if (pointIncrement > maximumPoints
            || maximumPoints % pointIncrement != 0)
        {
            return ValidationProblem(
                context,
                "POINT_INCREMENT_INVALID",
                "配点刻みを確認してください",
                [
                    FieldError(
                        "pointIncrementMilli",
                        "INVALID",
                        "配点は、最大点を割り切れる正の刻みで指定してください。"),
                ]);
        }

        var answerInputs = BuildAnswerInputs(request, existingAnswers: null);
        var authorityErrors = ValidateAnswerInputs(answerInputs, version);
        if (authorityErrors.Count > 0)
        {
            return ValidationProblem(
                context,
                "ANSWER_PROVENANCE_INVALID",
                "解答の出典を確認できません",
                authorityErrors);
        }

        if (version.Questions.Any(question =>
                question.OrderIndex == order
                || question.DisplayLabel == request.DisplayLabel!.Trim()))
        {
            return Conflict(
                context,
                "QUESTION_DUPLICATE",
                "問題の番号または表示順が重複しています",
                "問題番号と並び順は、この版の中で一意にしてください。");
        }

        var now = timeProvider.GetUtcNow();
        var questionType = request.QuestionType ?? "exact_short_text";
        var gradingMode = request.GradingMode
            ?? QuestionGradingDefaultPolicy.GradingModeFor(questionType);
        var canonicalAnswer = answerInputs
            .FirstOrDefault(answer => answer.VariantType == "canonical")
            ?.Text;
        var rubricText = TrimOrNull(request.Rubric);
        if (gradingMode == "ai_rubric" && rubricText is null)
        {
            rubricText = QuestionGradingDefaultPolicy.BuildDefaultRubric(
                questionType,
                canonicalAnswer);
        }

        var question = new QuestionEntity
        {
            Id = UlidId.New(now),
            TemplateVersionId = version.Id,
            LogicalQuestionId = UlidId.New(now.AddTicks(1)),
            OrderIndex = order,
            DisplayLabel = request.DisplayLabel!.Trim(),
            QuestionText = request.QuestionText?.Trim() ?? string.Empty,
            QuestionType = questionType,
            GradingMode = gradingMode,
            MaxPointsMilli = maximumPoints,
            PointIncrementMilli = pointIncrement,
            AllowNonKanji = request.AllowNonKanji ?? version.DefaultAllowNonKanji,
            RequiresCompleteAnswer = request.RequiresCompleteAnswer ?? false,
            AnswerOrderInsensitive = request.AnswerOrderInsensitive ?? false,
            KanjiPolicyNote = TrimOrNull(request.KanjiPolicyNote),
            RubricText = rubricText,
            TeacherNote = TrimOrNull(request.TeacherNote),
            RequiresReviewAlways = request.RequiresReviewAlways
                ?? QuestionGradingDefaultPolicy.RequiresReviewAlwaysFor(
                    questionType),
            TeacherVerified = request.TeacherVerified ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (request.QuestionRegion is not null)
        {
            ApplyQuestionRegion(
                db,
                question,
                request.QuestionRegion,
                "question",
                now);
        }

        if (request.AnswerRegion is not null)
        {
            ApplyQuestionRegion(
                db,
                question,
                request.AnswerRegion,
                "answer",
                now.AddTicks(1));
        }

        db.Questions.Add(question);
        ApplyAcceptedAnswers(db, question, answerInputs, version, now);
        TouchVersion(db, version, now);
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.question_created",
            "template_version",
            version.Id,
            new { questionId = question.Id });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Conflict(
                context,
                "QUESTION_CONFLICT",
                "問題を追加できませんでした",
                "表示順、ラベル、または解答が重複しています。");
        }

        var created = await db.Questions
            .AsNoTracking()
            .Include(item => item.AcceptedAnswers)
            .Include(item => item.QuestionRegion)
            .Include(item => item.AnswerRegion)
            .SingleAsync(item => item.Id == question.Id, cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, created.Revision);
        return Results.Created(
            $"/api/v1/templates/{templateId}/versions/{version.Id}/questions/{created.Id}",
            ToQuestionResponse(created));
    }

    private static async Task<IResult> UpdateQuestion(
        string templateId,
        string versionId,
        string questionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] QuestionWriteRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.Revision,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var question = version.Questions.SingleOrDefault(item => item.Id == questionId);
        if (question is null)
        {
            return Results.NotFound();
        }

        if (question.Revision != expectedRevision)
        {
            return Stale(context, question.Revision);
        }

        var order = request.Order ?? request.SortOrder ?? question.OrderIndex;
        var writeErrors = ValidateQuestionWrite(request, order, creating: false);
        if (writeErrors.Count > 0)
        {
            return ValidationProblem(
                context,
                "QUESTION_INVALID",
                "問題を更新できません",
                writeErrors);
        }

        if (version.Questions.Any(item =>
                item.Id != questionId
                && (item.OrderIndex == order
                    || item.DisplayLabel
                        == (request.DisplayLabel ?? question.DisplayLabel).Trim())))
        {
            return Conflict(
                context,
                "QUESTION_DUPLICATE",
                "問題の番号または表示順が重複しています",
                "問題番号と並び順は、この版の中で一意にしてください。");
        }

        var answerInputs = request.AcceptedAnswers is null
            && request.CanonicalAnswer is null
                ? null
                : BuildAnswerInputs(request, question.AcceptedAnswers);
        if (answerInputs is not null)
        {
            var authorityErrors = ValidateAnswerInputs(answerInputs, version);
            if (authorityErrors.Count > 0)
            {
                return ValidationProblem(
                    context,
                    "ANSWER_PROVENANCE_INVALID",
                    "解答の出典を確認できません",
                    authorityErrors);
            }
        }

        var now = timeProvider.GetUtcNow();
        var previousQuestionType = question.QuestionType;
        var nextQuestionType = request.QuestionType ?? previousQuestionType;
        var questionTypeChanged = request.QuestionType is not null
            && !string.Equals(
                request.QuestionType,
                previousQuestionType,
                StringComparison.Ordinal);
        var nextGradingMode = request.GradingMode
            ?? (questionTypeChanged
                ? QuestionGradingDefaultPolicy.GradingModeFor(nextQuestionType)
                : question.GradingMode);
        question.OrderIndex = order;
        question.DisplayLabel = (request.DisplayLabel ?? question.DisplayLabel).Trim();
        question.QuestionText = (request.QuestionText ?? question.QuestionText).Trim();
        question.QuestionType = nextQuestionType;
        question.GradingMode = nextGradingMode;
        var nextMaximum = request.MaxPointsMilli ?? question.MaxPointsMilli;
        var nextIncrement =
            request.PointIncrementMilli ?? question.PointIncrementMilli;
        if (nextIncrement <= 0
            || nextIncrement > nextMaximum
            || nextMaximum % nextIncrement != 0)
        {
            return ValidationProblem(
                context,
                "POINT_INCREMENT_INVALID",
                "配点刻みを確認してください",
                [
                    FieldError(
                        "pointIncrementMilli",
                        "INVALID",
                        "配点は、最大点を割り切れる正の刻みで指定してください。"),
                ]);
        }

        question.MaxPointsMilli = nextMaximum;
        question.PointIncrementMilli = nextIncrement;
        question.AllowNonKanji = request.AllowNonKanji ?? question.AllowNonKanji;
        question.RequiresCompleteAnswer = request.RequiresCompleteAnswer
            ?? question.RequiresCompleteAnswer;
        question.AnswerOrderInsensitive = request.AnswerOrderInsensitive
            ?? question.AnswerOrderInsensitive;
        if (request.Rubric is not null)
        {
            question.RubricText = TrimOrNull(request.Rubric);
        }
        else if (question.GradingMode == "ai_rubric"
                 && string.IsNullOrWhiteSpace(question.RubricText)
                 && (questionTypeChanged || request.GradingMode == "ai_rubric"))
        {
            var canonicalAnswer = answerInputs?
                .FirstOrDefault(answer => answer.VariantType == "canonical")
                ?.Text
                ?? question.AcceptedAnswers
                    .FirstOrDefault(answer => answer.VariantType == "canonical")
                    ?.AnswerText;
            question.RubricText =
                QuestionGradingDefaultPolicy.BuildDefaultRubric(
                    question.QuestionType,
                    canonicalAnswer);
        }

        if (request.TeacherNote is not null)
        {
            question.TeacherNote = TrimOrNull(request.TeacherNote);
        }

        if (request.KanjiPolicyNote is not null)
        {
            question.KanjiPolicyNote = TrimOrNull(request.KanjiPolicyNote);
        }

        question.RequiresReviewAlways = request.RequiresReviewAlways
            ?? (questionTypeChanged
                ? QuestionGradingDefaultPolicy.RequiresReviewAlwaysFor(
                    question.QuestionType)
                : question.RequiresReviewAlways);
        question.TeacherVerified = request.TeacherVerified ?? true;
        if (request.QuestionRegion is not null)
        {
            ApplyQuestionRegion(
                db,
                question,
                request.QuestionRegion,
                "question",
                now);
        }

        if (request.AnswerRegion is not null)
        {
            ApplyQuestionRegion(
                db,
                question,
                request.AnswerRegion,
                "answer",
                now.AddTicks(1));
        }

        if (answerInputs is not null)
        {
            ApplyAcceptedAnswers(db, question, answerInputs, version, now);
        }

        db.Entry(question).Property(item => item.Revision).IsModified = true;
        TouchVersion(db, version, now);
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.question_updated",
            "template_version",
            version.Id,
            new { questionId = question.Id, previousRevision = expectedRevision });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.Questions
                .AsNoTracking()
                .Where(item => item.Id == question.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            return Stale(context, currentRevision ?? question.Revision);
        }
        catch (DbUpdateException)
        {
            return Conflict(
                context,
                "QUESTION_CONFLICT",
                "問題を更新できませんでした",
                "表示順、ラベル、または解答が重複しています。");
        }

        ApiHelpers.SetRevisionEtag(context.Response, question.Revision);
        return Results.Ok(ToQuestionResponse(question));
    }

    private static async Task<IResult> DeleteQuestion(
        string templateId,
        string versionId,
        string questionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                bodyRevision: null,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var question = version.Questions.SingleOrDefault(item => item.Id == questionId);
        if (question is null)
        {
            return Results.NotFound();
        }

        if (question.Revision != expectedRevision)
        {
            return Stale(context, question.Revision);
        }

        var now = timeProvider.GetUtcNow();
        db.AcceptedAnswers.RemoveRange(question.AcceptedAnswers);
        if (question.QuestionRegion is not null)
        {
            db.Regions.Remove(question.QuestionRegion);
        }

        if (question.AnswerRegion is not null)
        {
            db.Regions.Remove(question.AnswerRegion);
        }

        db.Questions.Remove(question);
        TouchVersion(db, version, now);
        AddAudit(
            db,
            now,
            principal,
            context,
            "template.question_deleted",
            "template_version",
            version.Id,
            new { questionId });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var currentRevision = await db.Questions
                .AsNoTracking()
                .Where(item => item.Id == question.Id)
                .Select(item => (long?)item.Revision)
                .SingleOrDefaultAsync(cancellationToken);
            return Stale(context, currentRevision ?? question.Revision);
        }

        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateTemplateVersion(
        string templateId,
        string versionId,
        HttpContext context,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: false,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        var report = await BuildValidationReportAsync(
            version,
            db,
            cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
        return Results.Ok(report);
    }

    private static async Task<IResult> PublishTemplateVersion(
        string templateId,
        string versionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] PublishTemplateApiRequest? request,
        OokiGraderDbContext db,
        [FromServices] IContentStore contentStore,
        [FromServices] IPdfPageCountReader pdfPageCountReader,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request?.Revision,
                out var expectedRevision))
        {
            return RevisionRequired(context);
        }

        var classLabel = string.IsNullOrWhiteSpace(request?.ClassLabel)
            ? null
            : request.ClassLabel.Trim();
        if ((request?.TestDate is { } suppliedTestDate
                && suppliedTestDate == default)
            || classLabel?.Length > 500)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "TEST_SESSION_INVALID",
                "受付を開始できません",
                "実施日とクラス名を確認してください。");
        }

        var version = await FindVersionAsync(
            db,
            templateId,
            versionId,
            tracking: true,
            cancellationToken);
        if (version is null)
        {
            return Results.NotFound();
        }

        var existingSession = await db.TestSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TemplateVersionId == version.Id
                    && item.CreationSource == "template_publish",
                cancellationToken);
        if (existingSession is not null
            && TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(
                version.State))
        {
            ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
            return Results.Ok(ToVersionDetail(version) with
            {
                TestSession = ToPublishTestSessionResponse(
                    existingSession,
                    version),
            });
        }

        if (version.Revision != expectedRevision)
        {
            return Stale(context, version.Revision);
        }

        if (version.TestTemplate.State == "archived")
        {
            return Archived(context);
        }

        if (version.State != "draft")
        {
            return Immutable(context);
        }

        var report = await BuildValidationReportAsync(
            version,
            db,
            cancellationToken);
        if (!report.Valid)
        {
            return ValidationProblem(
                context,
                "TEMPLATE_PUBLISH_BLOCKED",
                "受付開始前の確認が必要です",
                report.Issues.Cast<object>().ToArray());
        }

        if (version.ExpectedSubmissionPageCount is null)
        {
            try
            {
                version.ExpectedSubmissionPageCount =
                    await ResolveExpectedSubmissionPageCountAsync(
                        version,
                        db,
                        contentStore,
                        pdfPageCountReader,
                        cancellationToken);
            }
            catch (OrderedScanBatchServiceException exception)
            {
                return ApiHelpers.Problem(
                    context,
                    exception.StatusCode,
                    exception.Code,
                    exception.Title,
                    exception.Detail);
            }
        }

        var expectedSubmissionPageCount = version.ExpectedSubmissionPageCount;

        DomainTemplateVersion domainVersion;
        try
        {
            domainVersion = BuildDomainVersion(version);
        }
        catch (DomainValidationException exception)
        {
            return ValidationProblem(
                context,
                "TEMPLATE_PUBLISH_BLOCKED",
                "受付開始前の確認が必要です",
                exception.Errors.Select(ToProblemError).ToArray());
        }

        var now = timeProvider.GetUtcNow();
        var testDate = request?.TestDate
            ?? await ResolveSiteLocalDateAsync(db, now, cancellationToken);
        var published = domainVersion.Publish(ApiHelpers.StaffId(principal), now);
        var sessionId = UlidId.New(now.AddTicks(1));
        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);
        try
        {
            // PDF inspection happens before the write transaction. Re-read the
            // two optimistic-lock owners after the transaction starts so a
            // concurrent editor cannot be published from a stale validation.
            await db.Entry(version).ReloadAsync(cancellationToken);
            await db.Entry(version.TestTemplate).ReloadAsync(cancellationToken);
            if (version.Revision != expectedRevision)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Stale(context, version.Revision);
            }

            if (version.TestTemplate.State == "archived")
            {
                await transaction.RollbackAsync(cancellationToken);
                return Archived(context);
            }

            if (version.State != "draft")
            {
                await transaction.RollbackAsync(cancellationToken);
                return Immutable(context);
            }

            version.ExpectedSubmissionPageCount = expectedSubmissionPageCount;
            var template = version.TestTemplate;
            if (!string.IsNullOrWhiteSpace(template.ActiveVersionId)
                && template.ActiveVersionId != version.Id)
            {
                var prior = await db.TemplateVersions.SingleOrDefaultAsync(
                    item => item.Id == template.ActiveVersionId,
                    cancellationToken);
                if (prior?.State == "published")
                {
                    prior.State = "superseded";
                }
            }

            version.State = "published";
            version.PublishedByStaffUserId = ApiHelpers.StaffId(principal);
            version.PublishedAt = now;
            version.ContentHash = ComputePublishedContentHash(
                version,
                published.ContentHash
                    ?? throw new InvalidOperationException(
                        "A published template must have a content hash."));
            template.ActiveVersionId = version.Id;
            template.State = "active";
            AddAudit(
                db,
                now,
                principal,
                context,
                "template_version.published",
                "template_version",
                version.Id,
                new
                {
                    templateId,
                    version.VersionNumber,
                    contentHash = version.ContentHash,
                    previousRevision = expectedRevision,
                    testSessionId = sessionId,
                    testDate,
                    classLabel,
                });

            // Persist the published state first because SQLite integrity
            // triggers require an immutable version before a session may pin it.
            // The surrounding transaction still rolls this write back if the
            // following session insert fails.
            await db.SaveChangesAsync(cancellationToken);

            var session = new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = version.Id,
                CreationSource = "template_publish",
                TitleOverride = null,
                TemplateTitleSnapshot = template.Title,
                TemplateSubjectSnapshot = template.Subject,
                TemplateGradeLabelSnapshot = template.GradeLabel,
                TemplateCategorySnapshot = template.Category,
                TemplateCourseSnapshot = template.Course,
                TestDate = testDate,
                Course = template.Course,
                ClassLabel = classLabel,
                Priority = "expedite",
                State = "open",
                CreatedByStaffUserId = ApiHelpers.StaffId(principal),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.TestSessions.Add(session);
            AddAudit(
                db,
                now.AddTicks(1),
                principal,
                context,
                "test_session.created",
                "test_session",
                session.Id,
                new
                {
                    templateId,
                    templateVersionId = version.Id,
                    source = "template_publish",
                });
            AddAudit(
                db,
                now.AddTicks(2),
                principal,
                context,
                "test_session.opened",
                "test_session",
                session.Id,
                new { source = "template_publish" });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            ApiHelpers.SetRevisionEtag(context.Response, version.Revision);
            return Results.Ok(ToVersionDetail(version) with
            {
                TestSession = ToPublishTestSessionResponse(session, version),
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var currentVersion = await FindVersionAsync(
                db,
                templateId,
                versionId,
                tracking: false,
                cancellationToken);
            var completedSession = await db.TestSessions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.TemplateVersionId == versionId
                        && item.CreationSource == "template_publish",
                    cancellationToken);
            if (currentVersion is not null
                && completedSession is not null
                && TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(
                    currentVersion.State))
            {
                ApiHelpers.SetRevisionEtag(
                    context.Response,
                    currentVersion.Revision);
                return Results.Ok(ToVersionDetail(currentVersion) with
                {
                    TestSession = ToPublishTestSessionResponse(
                        completedSession,
                        currentVersion),
                });
            }

            return Stale(
                context,
                currentVersion?.Revision ?? version.Revision);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "TEST_SESSION_START_FAILED",
                "受付を開始できませんでした",
                "ひな形を確定できませんでした。しばらく待ってから同じ操作をやり直してください。");
        }
    }

    private static async Task<int> ResolveExpectedSubmissionPageCountAsync(
        TemplateVersionEntity version,
        OokiGraderDbContext db,
        IContentStore contentStore,
        IPdfPageCountReader pdfPageCountReader,
        CancellationToken cancellationToken)
    {
        var candidates = version.Sources
            .Where(item => item.SourceRole is
                "blank_test"
                or "contains_model_answers"
                or "contains_non_model_answers")
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .ToArray();
        var selectedRole = candidates.Any(item => item.SourceRole == "blank_test")
            ? "blank_test"
            : candidates.Any(item => item.SourceRole == "contains_model_answers")
                ? "contains_model_answers"
                : "contains_non_model_answers";
        var selected = candidates
            .Where(item => item.SourceRole == selectedRole)
            .ToArray();
        if (selected.Length == 0
            || selected.Any(item => item.FileReferenceId is null))
        {
            throw PageCountUnavailable();
        }

        var referenceIds = selected.Select(item => item.FileReferenceId!).ToArray();
        var references = await db.FileReferences
            .AsNoTracking()
            .Include(item => item.FileObject)
            .Where(item => referenceIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var totalPages = 0;
        foreach (var source in selected)
        {
            if (!references.TryGetValue(source.FileReferenceId!, out var reference)
                || reference.FileObject.State != "available")
            {
                throw PageCountUnavailable();
            }

            var uploadedSource = reference.OwnerType == "upload_session"
                && reference.OwnerId == source.UploadSessionId
                && reference.Purpose == "template_source"
                && reference.FileObject.StorageClass
                    == nameof(ContentStorageClass.TemplateSource);
            var derivedSource = version.OriginatingUnitId is { } unitId
                && reference.OwnerType == "template_generation_unit"
                && reference.OwnerId == unitId
                && reference.Purpose == "derived_source"
                && reference.FileObject.StorageClass
                    == nameof(ContentStorageClass.TemplateDerived);
            if (!uploadedSource && !derivedSource)
            {
                throw PageCountUnavailable();
            }

            var fileObject = reference.FileObject;
            int sourcePages;
            if (fileObject.VerifiedMime == "application/pdf")
            {
                await using var stream = await contentStore.OpenReadAsync(
                    new ContentObjectLocator(
                        Enum.Parse<ContentStorageClass>(
                            fileObject.StorageClass,
                            ignoreCase: false),
                        fileObject.Sha256,
                        fileObject.Bytes,
                        fileObject.Extension),
                    cancellationToken);
                try
                {
                    sourcePages = await pdfPageCountReader.GetPageCountAsync(
                        stream,
                        OrderedScanBatchService.MaximumSubmissionPages + 1,
                        cancellationToken);
                }
                catch (PdfPageCountException)
                {
                    throw PageCountUnavailable();
                }
            }
            else if (fileObject.VerifiedMime is
                "image/png" or "image/jpeg" or "image/webp")
            {
                sourcePages = 1;
            }
            else
            {
                throw PageCountUnavailable();
            }

            totalPages = checked(totalPages + sourcePages);
            if (totalPages > OrderedScanBatchService.MaximumSubmissionPages)
            {
                throw new OrderedScanBatchServiceException(
                    StatusCodes.Status422UnprocessableEntity,
                    "TEMPLATE_SUBMISSION_PAGE_COUNT_UNSUPPORTED",
                    "答案のページ数が上限を超えています",
                    $"1答案は{OrderedScanBatchService.MaximumSubmissionPages}ページ以下にしてください。");
            }
        }

        var resolved = OrderedScanPageCountPolicy.Resolve(
            version.TestType ?? TestType.Other,
            totalPages);
        if (resolved != totalPages)
        {
            throw new OrderedScanBatchServiceException(
                StatusCodes.Status409Conflict,
                "TEMPLATE_SUBMISSION_PAGE_COUNT_INCONSISTENT",
                "答案のページ数がテスト種別と一致しません",
                "問題用紙とテスト種別を確認してください。");
        }

        return resolved;

        static OrderedScanBatchServiceException PageCountUnavailable() =>
            new(
                StatusCodes.Status409Conflict,
                "TEMPLATE_SUBMISSION_PAGE_COUNT_MISSING",
                "答案のページ数を確認できません",
                "暗号化されていない問題用紙PDFをひな形に追加してください。");
    }

    private static IQueryable<TemplateVersionEntity> VersionGraph(
        OokiGraderDbContext db,
        bool tracking)
    {
        var query = db.TemplateVersions
            .Include(version => version.TestTemplate)
            .Include(version => version.Sources)
            .ThenInclude(source => source.UploadSession)
            .Include(version => version.Questions)
            .ThenInclude(question => question.AcceptedAnswers)
            .Include(version => version.Questions)
            .ThenInclude(question => question.QuestionRegion)
            .Include(version => version.Questions)
            .ThenInclude(question => question.AnswerRegion)
            .AsSplitQuery();
        return tracking ? query : query.AsNoTracking();
    }

    private static async Task<TemplateVersionEntity?> FindVersionAsync(
        OokiGraderDbContext db,
        string templateId,
        string versionId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = VersionGraph(db, tracking)
            .Where(version => version.TestTemplateId == templateId);
        if (versionId == "draft")
        {
            return await query
                .Where(version => version.State == "draft")
                .OrderByDescending(version => version.VersionNumber)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return await query.SingleOrDefaultAsync(
            version => version.Id == versionId,
            cancellationToken);
    }

    private static TemplateSummaryResponse ToTemplateSummary(
        TestTemplateEntity template)
    {
        var preferredVersion = template.ActiveVersionId is null
            ? template.Versions
                .OrderByDescending(version => version.VersionNumber)
                .FirstOrDefault()
            : template.Versions.FirstOrDefault(
                version => version.Id == template.ActiveVersionId);
        var totalPoints = preferredVersion is null
            ? 0
            : SumPoints(preferredVersion.Questions);

        return new TemplateSummaryResponse(
            template.Id,
            template.Title,
            template.Subject,
            template.Category,
            template.GradeLabel,
            template.Course,
            template.State,
            preferredVersion?.Id,
            preferredVersion?.VersionNumber,
            preferredVersion?.Questions.Count ?? 0,
            totalPoints,
            template.DefaultPointsMilli,
            template.Versions.Count,
            template.UpdatedAt,
            template.Revision);
    }

    private static TemplateVersionResponse ToVersionDetail(
        TemplateVersionEntity version)
    {
        var reviewIssues = BuildExtractionConsistencyIssues(version);
        var questions = version.Questions
            .OrderBy(question => question.OrderIndex)
            .ThenBy(question => question.Id)
            .Select(ToQuestionResponse)
            .ToArray();
        var sources = version.Sources
            .OrderBy(source => source.Ordinal)
            .Select(source => ToTemplateSourceResponse(
                source,
                templateId: version.TestTemplateId,
                versionId: version.Id,
                mimeType: source.UploadSession?.DeclaredMimeType))
            .ToArray();

        return new TemplateVersionResponse(
            version.Id,
            version.TestTemplateId,
            version.VersionNumber,
            version.State,
            version.TestTemplate.Title,
            SumPoints(version.Questions),
            version.DefaultPointsMilli,
            questions,
            [],
            sources,
            reviewIssues
                .Where(issue => issue.Blocking)
                .Select(issue => issue.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            reviewIssues
                .Where(issue => !issue.Blocking)
                .Select(issue => issue.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            reviewIssues,
            version.ContentHash,
            version.UpdatedAt,
            version.PublishedAt,
            version.Revision);
    }

    private static PublishTestSessionResponse ToPublishTestSessionResponse(
        TestSessionEntity session,
        TemplateVersionEntity version)
    {
        var title = session.TitleOverride
            ?? session.TemplateTitleSnapshot
            ?? version.TestTemplate.Title;
        return new PublishTestSessionResponse(
            session.Id,
            title,
            title,
            title,
            version.TestTemplateId,
            version.Id,
            session.TemplateTitleSnapshot ?? version.TestTemplate.Title,
            version.VersionNumber,
            session.TemplateSubjectSnapshot ?? version.TestTemplate.Subject,
            session.TemplateGradeLabelSnapshot ?? version.TestTemplate.GradeLabel,
            session.TemplateCategorySnapshot ?? version.TestTemplate.Category,
            version.ExpectedSubmissionPageCount,
            session.Course
                ?? session.TemplateCourseSnapshot
                ?? version.TestTemplate.Course,
            session.TemplateCourseSnapshot ?? version.TestTemplate.Course,
            session.TestDate,
            session.ClassLabel,
            session.Priority,
            session.State,
            session.CreationSource,
            session.Revision);
    }

    private static async Task<DateOnly> ResolveSiteLocalDateAsync(
        OokiGraderDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var timeZoneId = await db.SiteSettings
            .AsNoTracking()
            .Where(item => item.Id == "site")
            .Select(item => item.TimeZone)
            .SingleOrDefaultAsync(cancellationToken);
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrWhiteSpace(timeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
        }

        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
    }

    private static TemplateSourceApiResponse ToTemplateSourceResponse(
        TemplateSourceEntity source,
        TemplateSourceRoleResolution? inference = null,
        string? templateId = null,
        string? versionId = null,
        string? mimeType = null) =>
        new(
            source.Id,
            ToWebSourceRole(source.SourceRole),
            source.DisplayName,
            source.UploadSessionId,
            templateId is null || versionId is null
                ? null
                : $"/api/v1/templates/{Uri.EscapeDataString(templateId)}" +
                    $"/versions/{Uri.EscapeDataString(versionId)}" +
                    $"/sources/{Uri.EscapeDataString(source.Id)}/content",
            mimeType,
            source.CreatedAt,
            inference is not null,
            inference?.ConfidenceBasisPoints,
            inference?.ReasonCode);

    private static TemplateQuestionResponse ToQuestionResponse(QuestionEntity question)
    {
        var answers = question.AcceptedAnswers
            .OrderBy(answer => AnswerVariantOrder(answer.VariantType))
            .ThenBy(answer => answer.Id)
            .Select(answer => new AcceptedAnswerApiResponse(
                answer.Id,
                answer.AnswerText,
                ToWebVariantType(answer.VariantType),
                answer.AnswerProvenance,
                answer.VariantType == "phonetic_exception",
                answer.TeacherVerified,
                answer.SourceFileReferenceId,
                answer.SourcePageNumber,
                answer.SourceRegionId,
                answer.Revision))
            .ToArray();
        var canonical = question.AcceptedAnswers.FirstOrDefault(
            answer => answer.VariantType == "canonical");
        var warnings = new List<string>();
        if (!question.TeacherVerified)
        {
            warnings.Add("先生による確認が必要です。");
        }

        if (question.AcceptedAnswers.Any(answer => !answer.TeacherVerified))
        {
            warnings.Add("未確認の解答候補があります。");
        }

        return new TemplateQuestionResponse(
            question.Id,
            question.DisplayLabel,
            question.OrderIndex,
            question.QuestionText,
            question.QuestionType,
            question.GradingMode,
            question.MaxPointsMilli,
            question.PointIncrementMilli,
            question.AllowNonKanji,
            question.RequiresCompleteAnswer,
            question.AnswerOrderInsensitive,
            answers,
            canonical?.AnswerText,
            question.RubricText,
            question.TeacherNote,
            question.KanjiPolicyNote,
            ToRegionResponse(question.AnswerRegion),
            ToRegionResponse(question.QuestionRegion),
            question.RequiresReviewAlways,
            canonical?.AnswerProvenance,
            canonical?.AnswerProvenance == "ai_proposed" ? "proposed" : "edited",
            warnings,
            question.TeacherVerified,
            question.Revision);
    }

    private static ProposalVerificationAssessment AssessProposalVerification(
        TemplateVersionEntity version,
        bool acknowledgeReviewableIssues)
    {
        var issues = new List<TemplateValidationIssue>();
        var eligible = new List<QuestionEntity>();
        var globalBlocker = false;
        var consistencyBlockers = BuildExtractionConsistencyIssues(version)
            .Where(issue => issue.Blocking)
            .ToArray();
        issues.AddRange(consistencyBlockers);
        var consistencyBlockedQuestionIds = consistencyBlockers
            .Where(issue => !acknowledgeReviewableIssues
                && issue.QuestionId is not null)
            .Select(issue => issue.QuestionId!)
            .ToHashSet(StringComparer.Ordinal);
        if (consistencyBlockers.Any(issue => issue.QuestionId is null))
        {
            globalBlocker = true;
        }

        if (version.Sources.Count == 0
            || version.Sources.Any(source =>
                string.IsNullOrWhiteSpace(source.FileReferenceId)))
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.source_required",
                    "保存済みの問題用紙または模範解答が必要です。",
                    null,
                    true));
            globalBlocker = true;
        }

        if (version.Questions.Count == 0)
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.no_questions",
                    "確認できる問題案がありません。",
                    null,
                    true));
            globalBlocker = true;
        }

        if (!TrySumPoints(version.Questions, out var pointTotal))
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.total_overflow",
                    "合計点が対応範囲を超えています。",
                    null,
                    true));
            globalBlocker = true;
        }
        else if (version.TargetTotalPointsMilli is not null
            && version.TargetTotalPointsMilli.Value != pointTotal)
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.target_total_mismatch",
                    "問題の配点合計が設定された満点と一致しません。",
                    null,
                    true));
            globalBlocker = true;
        }

        var hasAuthoritativeSource = version.Sources.Any(source =>
            source.SourceRole is
                "contains_model_answers"
                or "separate_answer_key");
        var requiresDocumentGlobalAuthoritativeAnswers =
            version.OriginatingUnitId is null && hasAuthoritativeSource;
        foreach (var question in version.Questions
                     .OrderBy(item => item.OrderIndex)
                     .ThenBy(item => item.Id))
        {
            var questionIssues = AssessQuestionProposal(
                question,
                version,
                requiresDocumentGlobalAuthoritativeAnswers,
                acknowledgeReviewableIssues);
            issues.AddRange(questionIssues);
            if (!globalBlocker
                && questionIssues.Count == 0
                && !consistencyBlockedQuestionIds.Contains(question.Id))
            {
                eligible.Add(question);
            }
        }

        var blockedQuestionCount = globalBlocker
            ? version.Questions.Count
            : version.Questions.Count - eligible.Count;
        return new ProposalVerificationAssessment(
            eligible,
            blockedQuestionCount,
            issues
                .DistinctBy(issue => (issue.Code, issue.QuestionId))
                .ToArray());
    }

    private static List<TemplateValidationIssue> AssessQuestionProposal(
        QuestionEntity question,
        TemplateVersionEntity version,
        bool requiresDocumentGlobalAuthoritativeAnswers,
        bool acknowledgeReviewableIssues)
    {
        if (question.TeacherVerified
            && question.AcceptedAnswers.All(answer => answer.TeacherVerified))
        {
            return [];
        }

        var issues = new List<TemplateValidationIssue>();
        void AddIssue(string code, string message) =>
            issues.Add(
                new TemplateValidationIssue(
                    code,
                    message,
                    question.Id,
                    true));

        if (string.IsNullOrWhiteSpace(question.QuestionText))
        {
            AddIssue(
                "question.text_required",
                $"{question.DisplayLabel}の問題文を確認してください。");
        }

        if (question.QuestionType == "unsupported")
        {
            AddIssue(
                "question.individual_review_required",
                $"{question.DisplayLabel}の問題形式は先生による個別確認が必要です。");
        }

        if (!acknowledgeReviewableIssues && question.RequiresReviewAlways)
        {
            AddIssue(
                "question.review_always",
                $"{question.DisplayLabel}は先生による個別確認が必要です。");
        }

        if (!acknowledgeReviewableIssues
            && (question.AiConfidenceBasisPoints is null
                || question.AiConfidenceBasisPoints.Value
                    < MinimumProposalVerificationConfidenceBasisPoints))
        {
            AddIssue(
                "question.low_confidence",
                $"{question.DisplayLabel}は認識の信頼度が低いため個別確認が必要です。");
        }

        if (!acknowledgeReviewableIssues
            && HasBlockingProposalNotice(question.TeacherNote))
        {
            AddIssue(
                "question.ai_warning",
                $"{question.DisplayLabel}にAIの警告または解答の競合があります。");
        }

        if (question.GradingMode == "ai_rubric"
            && string.IsNullOrWhiteSpace(question.RubricText))
        {
            AddIssue(
                "question.rubric_required",
                $"{question.DisplayLabel}の採点基準を入力してください。");
        }

        var answers = question.AcceptedAnswers.ToArray();
        var canonicalAnswers = answers
            .Where(answer => answer.VariantType == "canonical")
            .ToArray();
        var answerRequired = question.GradingMode is
            "deterministic"
            or "transcribe_then_rules";
        if ((answerRequired || requiresDocumentGlobalAuthoritativeAnswers)
            && canonicalAnswers.Length != 1)
        {
            AddIssue(
                "answer.canonical_required",
                $"{question.DisplayLabel}の正答を1件確認してください。");
        }
        else if (canonicalAnswers.Length > 1)
        {
            AddIssue(
                "answer.multiple_canonical",
                $"{question.DisplayLabel}には正答を1件だけ設定してください。");
        }

        if (answers.Any(answer => string.IsNullOrWhiteSpace(answer.AnswerText)))
        {
            AddIssue(
                "answer.text_required",
                $"{question.DisplayLabel}に空の解答候補があります。");
        }

        if (answers
            .GroupBy(answer => (answer.NormalizedText, answer.VariantType))
            .Any(group => group.Count() > 1))
        {
            AddIssue(
                "answer.duplicate",
                $"{question.DisplayLabel}に重複した解答候補があります。");
        }

        if (requiresDocumentGlobalAuthoritativeAnswers
            && canonicalAnswers.Length == 1
            && canonicalAnswers[0].AnswerProvenance
                != "provided_model_answer")
        {
            AddIssue(
                "answer.authoritative_source_required",
                $"{question.DisplayLabel}の正答を模範解答から確認してください。");
        }

        foreach (var answer in answers.Where(item =>
                     item.AnswerProvenance == "provided_model_answer"))
        {
            var source = version.Sources.FirstOrDefault(item =>
                item.FileReferenceId is not null
                && item.FileReferenceId == answer.SourceFileReferenceId);
            if (source is null
                || source.SourceRole is not (
                    "contains_model_answers"
                    or "separate_answer_key")
                || answer.SourcePageNumber is not > 0)
            {
                AddIssue(
                    "answer.invalid_provided_source",
                    $"{question.DisplayLabel}の模範解答の出典を確認してください。");
                break;
            }
        }

        return issues;
    }

    private static bool IsValidPageRegion(RegionEntity region) =>
        region.PageNumber > 0
        && region.XMillionths >= 0
        && region.YMillionths >= 0
        && region.WidthMillionths > 0
        && region.HeightMillionths > 0
        && (long)region.XMillionths + region.WidthMillionths <= 1_000_000
        && (long)region.YMillionths + region.HeightMillionths <= 1_000_000
        && region.RotationDegrees is 0 or 90 or 180 or 270;

    private static bool HasBlockingProposalNotice(string? teacherNote)
    {
        if (string.IsNullOrWhiteSpace(teacherNote))
        {
            return false;
        }

        return teacherNote
            .Split('\n', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Any(IsBlockingAiNotice);
    }

    private static bool IsBlockingAiNotice(string line)
    {
        var notice = line.StartsWith("[AI確認] ", StringComparison.Ordinal)
            ? line["[AI確認] ".Length..]
            : line;
        if (AcknowledgementOnlyAiNotices.Contains(notice))
        {
            return false;
        }

        if (notice.StartsWith('['))
        {
            var markerEnd = notice.IndexOf(']');
            if (markerEnd > 1)
            {
                var code = notice[1..markerEnd];
                if (code.StartsWith("template.", StringComparison.Ordinal)
                    || code.StartsWith("question.", StringComparison.Ordinal)
                    || code.StartsWith("answer.", StringComparison.Ordinal))
                {
                    return ExtractionIssueIsBlocking(code);
                }
            }
        }

        return true;
    }

    private static TemplateValidationIssue[]
        BuildExtractionConsistencyIssues(TemplateVersionEntity version)
    {
        if (version.AiGenerationProvenanceId is null)
        {
            return [];
        }

        var issues = new List<TemplateValidationIssue>();
        foreach (var duplicate in version.Questions
                     .GroupBy(
                         question => question.DisplayLabel,
                         StringComparer.Ordinal)
                     .Where(group => string.IsNullOrWhiteSpace(group.Key)
                         || group.Count() > 1))
        {
            foreach (var question in duplicate)
            {
                issues.Add(
                    new TemplateValidationIssue(
                        string.IsNullOrWhiteSpace(duplicate.Key)
                            ? "question.display_label_missing"
                            : "template.duplicate_display_label",
                        string.IsNullOrWhiteSpace(duplicate.Key)
                            ? "問題番号を確認できない設問があります。"
                            : $"問題番号「{duplicate.Key}」が重複しています。",
                        question.Id,
                        true));
            }
        }

        foreach (var question in version.Questions)
        {
            var extractionNotes = ParseExtractionReviewNotes(question).ToArray();
            // A teacher confirmation resolves question/answer-scoped extraction
            // findings. Template-scoped inventory findings remain global publish
            // gates, and duplicate labels / point totals are recomputed below
            // from the current persisted graph.
            issues.AddRange(question.TeacherVerified
                ? extractionNotes.Where(issue => issue.QuestionId is null)
                : extractionNotes);
            if (question.TeacherVerified)
            {
                continue;
            }

            if (question.AiConfidenceBasisPoints
                is < MinimumProposalVerificationConfidenceBasisPoints)
            {
                issues.Add(
                    new TemplateValidationIssue(
                        "question.ai_confidence_low",
                        $"{question.DisplayLabel}の抽出信頼度が基準を下回っています。",
                        question.Id,
                        false));
            }

            if (question.GradingMode is
                    "deterministic" or "transcribe_then_rules"
                && question.AcceptedAnswers.Count(answer =>
                    answer.VariantType == "canonical") != 1)
            {
                issues.Add(
                    new TemplateValidationIssue(
                        "answer.expected_answer_missing",
                        $"{question.DisplayLabel}の正答を確認してください。",
                        question.Id,
                        true));
            }
        }

        if (version.TargetTotalPointsMilli is > 0
            && TrySumPoints(version.Questions, out var total)
            && total != version.TargetTotalPointsMilli.Value)
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.target_total_mismatch",
                    $"提案配点合計 {total} は目標配点 " +
                    $"{version.TargetTotalPointsMilli.Value} と一致しません。",
                    null,
                    true));
        }

        return issues
            .DistinctBy(issue => (issue.Code, issue.QuestionId))
            .ToArray();
    }

    private static IEnumerable<TemplateValidationIssue>
        ParseExtractionReviewNotes(QuestionEntity question)
    {
        if (string.IsNullOrWhiteSpace(question.TeacherNote))
        {
            yield break;
        }

        foreach (var line in question.TeacherNote.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            var markerStart = line.IndexOf("] [", StringComparison.Ordinal);
            if (markerStart < 0)
            {
                continue;
            }

            markerStart += 3;
            var markerEnd = line.IndexOf(']', markerStart);
            if (markerEnd <= markerStart)
            {
                continue;
            }

            var code = line[markerStart..markerEnd];
            if (!code.StartsWith("template.", StringComparison.Ordinal)
                && !code.StartsWith("question.", StringComparison.Ordinal)
                && !code.StartsWith("answer.", StringComparison.Ordinal))
            {
                continue;
            }

            var message = line[(markerEnd + 1)..].Trim();
            yield return new TemplateValidationIssue(
                code,
                message,
                code.StartsWith("template.", StringComparison.Ordinal)
                    ? null
                    : question.Id,
                ExtractionIssueIsBlocking(code));
        }
    }

    private static bool ExtractionIssueIsBlocking(string code) =>
        code is
            "template.duplicate_source_key"
            or "template.duplicate_display_label"
            or "template.answer_slot_inventory_mismatch"
            or "template.target_total_mismatch"
            or "question.duplicate_answer_slot_ordinal"
            or "question.answer_slot_inventory_mismatch"
            or "question.fill_blank_classification_corrected"
            or "question.filled_answer_redacted"
            or "question.additional_placeholders_redacted"
            or "question.answer_slots_not_separated"
            or "question.filled_answer_removal_unconfirmed"
            or "question.fill_blank_placeholder_invalid"
            or "question.answer_region_overlap"
            or "answer.supplied_answer_missing"
            or "answer.expected_answer_missing"
            or "answer.source_conflict_or_ambiguity";

    private static bool PageRegionsOverlap(
        RegionEntity left,
        RegionEntity right) =>
        left.XMillionths < (long)right.XMillionths + right.WidthMillionths
        && right.XMillionths < (long)left.XMillionths + left.WidthMillionths
        && left.YMillionths < (long)right.YMillionths + right.HeightMillionths
        && right.YMillionths < (long)left.YMillionths + left.HeightMillionths;

    private static TemplateValidationIssue ToGenerationFailureIssue(
        string errorCode) =>
        new(
            errorCode,
            errorCode switch
            {
                "template_extract_duplicate_page" =>
                    "同じ資料ページが重複して抽出されました。",
                "template_extract_question_region_invalid" =>
                    "問題文領域がページ範囲外です。",
                "template_extract_answer_region_invalid" =>
                    "解答欄がページ範囲外です。",
                "template_extract_provided_answer_source_invalid" =>
                    "模範解答の出典を確認できません。",
                "template_extract_ai_answer_authority_conflict" =>
                    "提供済みの模範解答とAIの正答候補が競合しています。",
                "template_extract_metadata_invalid" =>
                    "テスト基本情報の抽出結果を検証できません。",
                _ => "自動下書きの検証で問題が見つかりました。",
            },
            null,
            true);

    private static InferredTemplateMetadataResponse? ReadInferredMetadata(
        string? validatedResponseJson)
    {
        if (string.IsNullOrWhiteSpace(validatedResponseJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(validatedResponseJson);
            if (!document.RootElement.TryGetProperty(
                    "metadata",
                    out var metadata)
                || metadata.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new InferredTemplateMetadataResponse(
                NullableJsonString(metadata, "title"),
                NullableJsonString(metadata, "subject"),
                NullableJsonString(metadata, "category"),
                NullableJsonString(metadata, "grade_label"),
                NullableJsonString(metadata, "course"),
                metadata.TryGetProperty("confidence", out var confidence)
                    && confidence.TryGetDouble(out var confidenceValue)
                        ? confidenceValue
                        : 0,
                metadata.TryGetProperty("warnings", out var warnings)
                    && warnings.ValueKind == JsonValueKind.Array
                        ? warnings.EnumerateArray()
                            .Where(item =>
                                item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString()!)
                            .ToArray()
                        : []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NullableJsonString(
        JsonElement owner,
        string propertyName) =>
        owner.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static async Task<TemplateValidationResponse>
        BuildValidationReportAsync(
            TemplateVersionEntity version,
            OokiGraderDbContext db,
            CancellationToken cancellationToken)
    {
        var generationIssues = await BuildGenerationPublicationIssuesAsync(
            version,
            db,
            cancellationToken);
        return BuildValidationReport(version, generationIssues);
    }

    private static TemplateValidationResponse BuildValidationReport(
        TemplateVersionEntity version,
        IEnumerable<TemplateValidationIssue> generationIssues)
    {
        var issues = new List<TemplateValidationIssue>();
        issues.AddRange(BuildExtractionConsistencyIssues(version));
        issues.AddRange(generationIssues);

        if (version.Sources.Count == 0)
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.source_required",
                    "受付開始前に問題用紙または模範解答のファイルを追加してください。",
                    null,
                    true));
        }

        if (version.State != "draft")
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.not_draft",
                    "確定済みまたは処理中の版は再確認できません。",
                    null,
                    true));
        }

        foreach (var question in version.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.QuestionText))
            {
                issues.Add(
                    new TemplateValidationIssue(
                        "question.text_required",
                        $"{question.DisplayLabel}の問題文を入力してください。",
                        question.Id,
                        true));
            }

            if (question.GradingMode == "ai_rubric"
                && string.IsNullOrWhiteSpace(question.RubricText))
            {
                issues.Add(
                    new TemplateValidationIssue(
                        "question.rubric_required",
                        $"{question.DisplayLabel}の採点基準を入力してください。",
                        question.Id,
                        true));
            }
        }

        try
        {
            var domainVersion = BuildDomainVersion(version);
            var domainValidation = domainVersion.ValidateForPublish();
            foreach (var error in domainValidation.Errors)
            {
                issues.Add(
                    new TemplateValidationIssue(
                        error.Code,
                        error.Message,
                        ResolveQuestionId(error.Path, version),
                        true));
            }
        }
        catch (DomainValidationException exception)
        {
            foreach (var error in exception.Errors)
            {
                issues.Add(
                    new TemplateValidationIssue(
                        error.Code,
                        error.Message,
                        ResolveQuestionId(error.Path, version),
                        true));
            }
        }
        catch (OverflowException)
        {
            issues.Add(
                new TemplateValidationIssue(
                    "template.total_overflow",
                    "合計点が64ビット整数の範囲を超えています。",
                    null,
                    true));
        }

        issues = issues
            .DistinctBy(issue => (issue.Code, issue.QuestionId))
            .ToList();
        var total = TrySumPoints(version.Questions, out var sum) ? sum : 0;
        var kanjiRequired = version.Questions.Count(question =>
            !question.AllowNonKanji
            && question.AcceptedAnswers.Any(answer =>
                answer.VariantType == "canonical"
                && KanjiDetector.ContainsKanji(answer.AnswerText)));

        return new TemplateValidationResponse(
            issues.All(issue => !issue.Blocking),
            version.Sources.Count,
            version.Questions.Count,
            total,
            kanjiRequired,
            version.Questions.Count(question => question.RequiresReviewAlways),
            issues);
    }

    private static async Task<IReadOnlyList<TemplateValidationIssue>>
        BuildGenerationPublicationIssuesAsync(
            TemplateVersionEntity version,
            OokiGraderDbContext db,
            CancellationToken cancellationToken)
    {
        if (version.GenerationProfileJson is null)
        {
            return [];
        }

        var issues = new List<TemplateValidationIssue>();
        if (string.IsNullOrWhiteSpace(version.OriginatingUnitId))
        {
            AddGenerationIssue(
                issues,
                "generation.origin_missing",
                "生成元の単位を確認できません。");
            return issues;
        }

        var unit = await db.TemplateGenerationUnits
            .AsNoTracking()
            .Include(item => item.Batch)
            .ThenInclude(item => item.Source)
            .SingleOrDefaultAsync(
                item => item.Id == version.OriginatingUnitId,
                cancellationToken);
        if (unit is null)
        {
            AddGenerationIssue(
                issues,
                "generation.origin_missing",
                "生成元の単位を確認できません。");
            return issues;
        }

        var derived = await db.TemplateGenerationDerivedSources
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UnitId == unit.Id,
                cancellationToken);
        FileReferenceEntity? fileReference = null;
        if (!string.IsNullOrWhiteSpace(derived?.FileReferenceId))
        {
            fileReference = await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleOrDefaultAsync(
                    item => item.Id == derived.FileReferenceId,
                    cancellationToken);
        }

        TemplateGenerationProfile? profile = null;
        try
        {
            profile = JsonSerializer.Deserialize<TemplateGenerationProfile>(
                version.GenerationProfileJson,
                GenerationJsonOptions);
        }
        catch (JsonException)
        {
        }

        if (profile is null)
        {
            AddGenerationIssue(
                issues,
                "generation.profile_invalid",
                "生成条件を読み取れません。");
        }
        else
        {
            ValidateGenerationProfile(version, unit, profile, issues);
            ValidateGenerationRoute(version, unit, profile, issues);
            ValidateGenerationRangeAndName(version, unit, profile, issues);
        }

        ValidateGenerationDraftAndWarnings(unit, issues);
        ValidateDerivedSource(
            version,
            unit,
            derived,
            fileReference,
            issues);
        return issues;
    }

    private static void ValidateGenerationProfile(
        TemplateVersionEntity version,
        TemplateGenerationUnitEntity unit,
        TemplateGenerationProfile profile,
        ICollection<TemplateValidationIssue> issues)
    {
        var expectedHash = profile.ComputeHash();
        if (version.GenerationProfileVersion
                != TemplateGenerationProfile.CurrentProfileVersion
            || profile.ProfileVersion
                != TemplateGenerationProfile.CurrentProfileVersion
            || profile.SplitPolicyVersion
                != TemplateGenerationProfile.CurrentSplitPolicyVersion
            || !TemplateGenerationProfile.IsSupportedNamingPolicyVersion(
                profile.NamingPolicyVersion)
            || profile.ExtractionPromptVersion
                != TemplateGenerationBatchService.ExtractionPromptVersion
            || profile.ExtractionSchemaVersion
                != TemplateGenerationBatchService.ExtractionSchemaVersion
            || !string.Equals(
                version.GenerationProfileHash,
                expectedHash,
                StringComparison.Ordinal)
            || !string.Equals(
                unit.GenerationProfileHash,
                expectedHash,
                StringComparison.Ordinal)
            || !string.Equals(
                unit.GenerationProfileJson,
                version.GenerationProfileJson,
                StringComparison.Ordinal)
            || version.OriginatingBatchId != unit.BatchId
            || unit.Batch.Status != TemplateGenerationBatchStatus.Completed
            || unit.Batch.CompletedAt is null
            || unit.Status != TemplateGenerationUnitStatus.Confirmed
            || unit.CreatedTemplateId != version.TestTemplateId
            || string.IsNullOrWhiteSpace(unit.CreatedTemplateVersionId)
            || profile.Subject != unit.Batch.Subject
            || profile.Subject != version.TestTemplate.Subject
            || profile.SourcePageCount != unit.Batch.SourcePageCount
            || profile.SourcePageCount <= 0
            || profile.UnitSequence != unit.Sequence
            || profile.UnitSequence <= 0)
        {
            AddGenerationIssue(
                issues,
                "generation.profile_invalid",
                "対応していない、または一致しない生成条件です。");
        }

        if (version.ResolvedGrade is < GradeLevel.Grade1 or > GradeLevel.Grade6
            || unit.ResolvedGrade != version.ResolvedGrade)
        {
            AddGenerationIssue(
                issues,
                "generation.grade_required",
                "小学1年から6年の学年を確認してください。");
        }
    }

    private static void ValidateGenerationRoute(
        TemplateVersionEntity version,
        TemplateGenerationUnitEntity unit,
        TemplateGenerationProfile profile,
        ICollection<TemplateValidationIssue> issues)
    {
        if (!Enum.IsDefined(profile.TestType)
            || version.TestType != profile.TestType
            || unit.TestType != profile.TestType
            || unit.Batch.TestType != profile.TestType)
        {
            AddGenerationIssue(
                issues,
                "generation.test_type_invalid",
                "テスト種別が生成条件と一致しません。");
            return;
        }

        TemplatePromptSystem expectedPrompt;
        try
        {
            expectedPrompt = TemplatePromptRouter.Resolve(
                profile.TestType,
                profile.AnswerStyle);
        }
        catch (DomainValidationException)
        {
            AddGenerationIssue(
                issues,
                "generation.prompt_route_invalid",
                "回答形式と生成システムの組み合わせが正しくありません。");
            return;
        }
        catch (ArgumentOutOfRangeException)
        {
            AddGenerationIssue(
                issues,
                "generation.prompt_route_invalid",
                "回答形式と生成システムの組み合わせが正しくありません。");
            return;
        }

        if (profile.PromptSystem != expectedPrompt
            || version.PromptSystem != expectedPrompt
            || unit.PromptSystem != expectedPrompt
            || unit.Batch.PromptSystem != expectedPrompt
            || version.AnswerStyle != profile.AnswerStyle
            || unit.AnswerStyle != profile.AnswerStyle
            || unit.Batch.AnswerStyle != profile.AnswerStyle)
        {
            AddGenerationIssue(
                issues,
                "generation.prompt_route_invalid",
                "回答形式と生成システムの組み合わせが正しくありません。");
        }
    }

    private static void ValidateGenerationRangeAndName(
        TemplateVersionEntity version,
        TemplateGenerationUnitEntity unit,
        TemplateGenerationProfile profile,
        ICollection<TemplateValidationIssue> issues)
    {
        var rangeMatchesProfile = profile.FirstPage == unit.FirstPage
            && profile.LastPage == unit.LastPage
            && profile.FirstPage >= 1
            && profile.LastPage >= profile.FirstPage
            && profile.LastPage <= profile.SourcePageCount;
        var expectedUnitCount = profile.TestType switch
        {
            TestType.Hop => profile.SourcePageCount,
            TestType.Step when profile.SourcePageCount % 6 == 0 =>
                profile.SourcePageCount / 2,
            TestType.ClassPlacement or TestType.Other => 1,
            _ => 0,
        };
        var canonicalRange = profile.TestType switch
        {
            TestType.Hop =>
                profile.FirstPage == profile.UnitSequence
                && profile.LastPage == profile.FirstPage,
            TestType.Step =>
                profile.FirstPage == 2L * (profile.UnitSequence - 1) + 1
                && profile.LastPage == profile.FirstPage + 1,
            TestType.ClassPlacement or TestType.Other =>
                profile.UnitSequence == 1
                && profile.FirstPage == 1
                && profile.LastPage == profile.SourcePageCount,
            _ => false,
        };
        if (!rangeMatchesProfile
            || !canonicalRange
            || unit.Batch.ExpectedUnitCount != expectedUnitCount)
        {
            AddGenerationIssue(
                issues,
                "generation.source_range_invalid",
                "生成元ページ範囲が決定的な分割計画と一致しません。");
        }

        if (profile.TestType == TestType.Hop
            && (profile.LastPage - profile.FirstPage + 1 != 1
                || profile.StepSetIndex is not null
                || profile.StepVariationIndex is not null
                || profile.DeterministicSuffix is not null
                || unit.StepSetIndex is not null
                || unit.StepVariationIndex is not null
                || unit.DeterministicSuffix is not null
                || version.StepSetIndex is not null
                || version.StepVariationIndex is not null))
        {
            AddGenerationIssue(
                issues,
                "generation.hop_range_invalid",
                "HOPは1ページ単位である必要があります。");
        }

        ValidateStepMetadata(version, unit, profile, issues);
        if (profile.NamingPolicyVersion
                == TemplateGenerationProfile.CurrentNamingPolicyVersion
            && profile.TestType != TestType.Other
            && version.ResolvedGrade is >= GradeLevel.Grade1
                and <= GradeLevel.Grade6)
        {
            string? expectedName = null;
            try
            {
                expectedName = TemplateNamePolicy.CreateKnownTestName(
                    profile.TestType,
                    profile.Subject,
                    version.ResolvedGrade.Value,
                    profile.UnitSequence,
                    profile.StepSetIndex,
                    profile.StepVariationIndex);
            }
            catch (ArgumentException)
            {
            }
            catch (DomainValidationException)
            {
            }

            if (expectedName is null
                || !string.Equals(
                    version.TestTemplate.Title,
                    expectedName,
                    StringComparison.Ordinal)
                || !string.Equals(
                    unit.FinalTemplateName,
                    expectedName,
                    StringComparison.Ordinal))
            {
                AddGenerationIssue(
                    issues,
                    "generation.final_name_invalid",
                    "テスト名が教科・学年・分割番号から作成した統一名と一致しません。");
            }
        }

        if (string.IsNullOrWhiteSpace(version.TestTemplate.Title)
            || string.IsNullOrWhiteSpace(unit.FinalTemplateName)
            || !string.Equals(
                version.TestTemplate.Title,
                unit.FinalTemplateName,
                StringComparison.Ordinal))
        {
            AddGenerationIssue(
                issues,
                "generation.final_name_required",
                "最終テスト名を確認してください。");
        }
    }

    private static void ValidateStepMetadata(
        TemplateVersionEntity version,
        TemplateGenerationUnitEntity unit,
        TemplateGenerationProfile profile,
        ICollection<TemplateValidationIssue> issues)
    {
        if (profile.TestType != TestType.Step)
        {
            if (profile.TestType is TestType.ClassPlacement or TestType.Other
                && (profile.StepSetIndex is not null
                    || profile.StepVariationIndex is not null
                    || profile.DeterministicSuffix is not null
                    || unit.StepSetIndex is not null
                    || unit.StepVariationIndex is not null
                    || unit.DeterministicSuffix is not null
                    || version.StepSetIndex is not null
                    || version.StepVariationIndex is not null))
            {
                AddGenerationIssue(
                    issues,
                    "generation.step_variation_invalid",
                    "STEP以外にSTEPの枝番を設定できません。");
            }

            return;
        }

        var expectedVariation = (profile.UnitSequence - 1) % 3 + 1;
        var expectedSet = (profile.UnitSequence - 1) / 3 + 1;
        var expectedSuffix = $"-{expectedVariation}";
        if (profile.LastPage - profile.FirstPage + 1 != 2)
        {
            AddGenerationIssue(
                issues,
                "generation.step_range_invalid",
                "STEPは2ページ単位である必要があります。");
        }

        if (profile.StepVariationIndex != expectedVariation
            || profile.StepSetIndex != expectedSet
            || unit.StepVariationIndex != expectedVariation
            || unit.StepSetIndex != expectedSet
            || version.StepVariationIndex != expectedVariation
            || version.StepSetIndex != expectedSet)
        {
            AddGenerationIssue(
                issues,
                "generation.step_variation_invalid",
                "STEPのセット番号または枝番が正しくありません。");
        }

        if (profile.DeterministicSuffix != expectedSuffix
            || unit.DeterministicSuffix != expectedSuffix
            || !version.TestTemplate.Title.EndsWith(
                expectedSuffix,
                StringComparison.Ordinal)
            || CountOccurrences(version.TestTemplate.Title, expectedSuffix) != 1)
        {
            AddGenerationIssue(
                issues,
                "generation.step_suffix_invalid",
                "STEPの固定サフィックスは末尾に1回だけ必要です。");
        }
    }

    private static void ValidateGenerationDraftAndWarnings(
        TemplateGenerationUnitEntity unit,
        ICollection<TemplateValidationIssue> issues)
    {
        var draftHashValid = !string.IsNullOrWhiteSpace(unit.ExtractionDraftJson)
            && !string.IsNullOrWhiteSpace(unit.ExtractionDraftHash)
            && unit.ExtractionDraftHash.Length == 64
            && string.Equals(
                Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(unit.ExtractionDraftJson)))
                    .ToLowerInvariant(),
                unit.ExtractionDraftHash,
                StringComparison.Ordinal);
        if (!draftHashValid)
        {
            AddGenerationIssue(
                issues,
                "generation.extraction_draft_hash_invalid",
                "抽出結果のハッシュを確認できません。");
        }

        try
        {
            var warnings = JsonSerializer.Deserialize<GenerationWarning[]>(
                unit.WarningsJson,
                GenerationJsonOptions);
            if (warnings is null)
            {
                throw new JsonException();
            }

            if (warnings.Any(item =>
                    item is null
                    || !Enum.IsDefined(item.Severity)
                    || string.IsNullOrWhiteSpace(item.Code)
                    || string.IsNullOrWhiteSpace(item.Message)))
            {
                AddGenerationIssue(
                    issues,
                    "generation.warnings_invalid",
                    "生成時の警告状態を確認できません。");
                return;
            }

            if (warnings.Any(item =>
                    item.Severity == GenerationWarningSeverity.Blocking))
            {
                AddGenerationIssue(
                    issues,
                    "generation.blocking_warning",
                    "生成時の未解決警告があります。");
            }
        }
        catch (JsonException)
        {
            AddGenerationIssue(
                issues,
                "generation.warnings_invalid",
                "生成時の警告状態を確認できません。");
        }
    }

    private static void ValidateDerivedSource(
        TemplateVersionEntity version,
        TemplateGenerationUnitEntity unit,
        TemplateGenerationDerivedSourceEntity? derived,
        FileReferenceEntity? fileReference,
        ICollection<TemplateValidationIssue> issues)
    {
        var matchingTemplateSources = version.Sources
            .Where(source =>
                source.FileReferenceId == derived?.FileReferenceId)
            .Take(2)
            .ToArray();
        var templateSource = matchingTemplateSources.Length == 1
            ? matchingTemplateSources[0]
            : null;
        var provenanceValid = derived is not null
            && fileReference is not null
            && templateSource is not null
            && derived.UnitId == unit.Id
            && derived.ParentSourceId == unit.Batch.SourceId
            && derived.ParentFirstPage == unit.FirstPage
            && derived.ParentLastPage == unit.LastPage
            && derived.OriginalContentSha256 == unit.Batch.Source.FinalSha256
            && IsSha256(derived.OriginalContentSha256)
            && ((derived.AppliedRotationsJson == "[]"
                    && derived.DerivationType == "pageRange")
                || (derived.AppliedRotationsJson != "[]"
                    && derived.DerivationType == "pageRangeAndRotation"))
            && derived.DerivationPolicyVersion
                == PdfPageRangeDerivationPolicy.CurrentVersion
            && derived.AppliedRotationsJson == unit.AppliedRotationsJson
            && derived.DerivedContentSha256 == unit.DerivedSourceSha256
            && IsSha256(derived.DerivedContentSha256)
            && derived.FileReferenceId == fileReference.Id
            && fileReference.OwnerType == "template_generation_unit"
            && fileReference.OwnerId == unit.Id
            && fileReference.Purpose == "derived_source"
            && templateSource.UploadSessionId == unit.Batch.SourceId;
        if (!provenanceValid)
        {
            AddGenerationIssue(
                issues,
                "generation.derived_source_invalid",
                "生成元PDFの派生履歴を確認できません。");
        }

        var fileObject = fileReference?.FileObject;
        if (fileObject is null
            || fileObject.State != "available"
            || fileObject.DeletedAt is not null
            || fileObject.StorageClass
                != ContentStorageClass.TemplateDerived.ToString()
            || fileObject.VerifiedMime != "application/pdf"
            || fileObject.Extension != "pdf"
            || fileObject.Bytes <= 0
            || fileObject.Sha256 != derived?.DerivedContentSha256
            || fileObject.RelativeObjectPath != unit.DerivedSourceObjectKey)
        {
            AddGenerationIssue(
                issues,
                "generation.derived_object_unavailable",
                "生成元PDFを利用できません。");
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   search,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static void AddGenerationIssue(
        ICollection<TemplateValidationIssue> issues,
        string code,
        string message) =>
        issues.Add(new TemplateValidationIssue(code, message, null, true));

    private static DomainTemplateVersion BuildDomainVersion(
        TemplateVersionEntity version)
    {
        var questions = version.Questions
            .OrderBy(question => question.OrderIndex)
            .Select(question => BuildDomainQuestion(question, version))
            .ToArray();
        return DomainTemplateVersion.CreateDraft(
            version.Id,
            version.TestTemplateId,
            version.VersionNumber,
            version.PipelineVersion,
            questions,
            version.BasedOnVersionId,
            version.TargetTotalPointsMilli is null
                ? null
                : new MilliPoints(version.TargetTotalPointsMilli.Value),
            version.DefaultAllowNonKanji,
            version.AiGenerationProvenanceId);
    }

    private static QuestionDefinition BuildDomainQuestion(
        QuestionEntity entity,
        TemplateVersionEntity version)
    {
        var acceptedAnswers = entity.AcceptedAnswers
            .Select(answer => BuildDomainAnswer(answer, version))
            .ToArray();
        var canonical = entity.AcceptedAnswers.FirstOrDefault(
            answer => answer.VariantType == "canonical");
        NumericAnswerPolicy? numericPolicy = null;
        ChoiceAnswerPolicy? choicePolicy = null;
        if (entity.QuestionType == "numeric"
            && canonical is not null
            && TryParseNumeric(canonical.AnswerText, out var expectedNumber))
        {
            numericPolicy = new NumericAnswerPolicy(expectedNumber);
        }

        if (entity.QuestionType is "multiple_choice" or "boolean"
            && canonical is not null)
        {
            var choices = entity.AcceptedAnswers
                .Select(answer => answer.AnswerText)
                .Append(canonical.AnswerText)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            choicePolicy = new ChoiceAnswerPolicy(canonical.AnswerText, choices);
        }

        RubricRule[] rubricRules = entity.GradingMode == "ai_rubric"
            && !string.IsNullOrWhiteSpace(entity.RubricText)
                ?
                [
                    new RubricRule(
                        $"rubric-{entity.Id}",
                        0,
                        RubricConditionType.ModelAssessed,
                        entity.RubricText,
                        new MilliPoints(entity.MaxPointsMilli),
                        entity.TeacherVerified),
                ]
                : [];

        return new QuestionDefinition(
            entity.Id,
            entity.LogicalQuestionId,
            entity.OrderIndex,
            entity.DisplayLabel,
            entity.QuestionText,
            ParseQuestionType(entity.QuestionType),
            ParseGradingMode(entity.GradingMode),
            new MilliPoints(entity.MaxPointsMilli),
            new MilliPoints(entity.PointIncrementMilli),
            entity.AllowNonKanji,
            entity.RequiresReviewAlways,
            entity.TeacherVerified,
            acceptedAnswers,
            rubricRules,
            numericPolicy: numericPolicy,
            choicePolicy: choicePolicy,
            kanjiPolicyNote: entity.KanjiPolicyNote,
            requiresCompleteAnswer: entity.RequiresCompleteAnswer,
            answerOrderInsensitive: entity.AnswerOrderInsensitive);
    }

    private static AcceptedAnswer BuildDomainAnswer(
        AcceptedAnswerEntity entity,
        TemplateVersionEntity version)
    {
        AnswerSourceReference? source = null;
        if (entity.AnswerProvenance == "provided_model_answer"
            && entity.SourceFileReferenceId is not null
            && entity.SourcePageNumber is > 0)
        {
            var templateSource = version.Sources.FirstOrDefault(item =>
                item.FileReferenceId == entity.SourceFileReferenceId);
            if (templateSource is not null)
            {
                source = new AnswerSourceReference(
                    templateSource.Id,
                    ParseSourceRole(templateSource.SourceRole),
                    entity.SourcePageNumber.Value,
                    entity.SourceRegionId);
            }
        }

        return new AcceptedAnswer(
            entity.Id,
            entity.AnswerText,
            ParseAnswerVariant(entity.VariantType),
            ParseAnswerProvenance(entity.AnswerProvenance),
            entity.TeacherVerified,
            source);
    }

    private static void CloneVersionContent(
        OokiGraderDbContext db,
        TemplateVersionEntity source,
        TemplateVersionEntity destination,
        DateTimeOffset now,
        ClaimsPrincipal principal)
    {
        foreach (var sourceItem in source.Sources.OrderBy(item => item.Ordinal))
        {
            db.TemplateSources.Add(new TemplateSourceEntity
            {
                Id = UlidId.New(now.AddTicks(sourceItem.Ordinal + 1L)),
                TemplateVersionId = destination.Id,
                UploadSessionId = sourceItem.UploadSessionId,
                FileReferenceId = sourceItem.FileReferenceId,
                SourceRole = sourceItem.SourceRole,
                DisplayName = sourceItem.DisplayName,
                Ordinal = sourceItem.Ordinal,
                UploadedByStaffUserId = ApiHelpers.StaffId(principal),
                CreatedAt = now,
            });
        }

        var sequence = source.Sources.Count + 10L;
        foreach (var sourceQuestion in source.Questions.OrderBy(item => item.OrderIndex))
        {
            var question = new QuestionEntity
            {
                Id = UlidId.New(now.AddTicks(sequence++)),
                TemplateVersionId = destination.Id,
                LogicalQuestionId = sourceQuestion.LogicalQuestionId,
                OrderIndex = sourceQuestion.OrderIndex,
                DisplayLabel = sourceQuestion.DisplayLabel,
                QuestionText = sourceQuestion.QuestionText,
                QuestionType = sourceQuestion.QuestionType,
                GradingMode = sourceQuestion.GradingMode,
                MaxPointsMilli = sourceQuestion.MaxPointsMilli,
                PointIncrementMilli = sourceQuestion.PointIncrementMilli,
                AllowNonKanji = sourceQuestion.AllowNonKanji,
                RequiresCompleteAnswer = sourceQuestion.RequiresCompleteAnswer,
                AnswerOrderInsensitive = sourceQuestion.AnswerOrderInsensitive,
                KanjiPolicyNote = sourceQuestion.KanjiPolicyNote,
                RubricText = sourceQuestion.RubricText,
                TeacherNote = sourceQuestion.TeacherNote,
                RequiresReviewAlways = sourceQuestion.RequiresReviewAlways,
                AiConfidenceBasisPoints = sourceQuestion.AiConfidenceBasisPoints,
                TeacherVerified = sourceQuestion.TeacherVerified,
                CreatedAt = now,
                UpdatedAt = now,
            };
            if (sourceQuestion.QuestionRegion is not null)
            {
                var region = CloneRegion(
                    sourceQuestion.QuestionRegion,
                    question.Id,
                    now.AddTicks(sequence++));
                question.QuestionRegionId = region.Id;
                question.QuestionRegion = region;
                db.Regions.Add(region);
            }

            if (sourceQuestion.AnswerRegion is not null)
            {
                var region = CloneRegion(
                    sourceQuestion.AnswerRegion,
                    question.Id,
                    now.AddTicks(sequence++));
                question.AnswerRegionId = region.Id;
                question.AnswerRegion = region;
                db.Regions.Add(region);
            }

            db.Questions.Add(question);

            foreach (var sourceAnswer in sourceQuestion.AcceptedAnswers)
            {
                db.AcceptedAnswers.Add(new AcceptedAnswerEntity
                {
                    Id = UlidId.New(now.AddTicks(sequence++)),
                    QuestionId = question.Id,
                    AnswerText = sourceAnswer.AnswerText,
                    NormalizedText = sourceAnswer.NormalizedText,
                    VariantType = sourceAnswer.VariantType,
                    CasePolicy = sourceAnswer.CasePolicy,
                    WidthPolicy = sourceAnswer.WidthPolicy,
                    PunctuationPolicy = sourceAnswer.PunctuationPolicy,
                    TeacherVerified = sourceAnswer.TeacherVerified,
                    AnswerProvenance = sourceAnswer.AnswerProvenance,
                    SourceFileReferenceId = sourceAnswer.SourceFileReferenceId,
                    SourcePageNumber = sourceAnswer.SourcePageNumber,
                    SourceRegionId = sourceAnswer.SourceRegionId,
                    Locale = sourceAnswer.Locale,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
        }
    }

    private static List<AnswerWriteModel> BuildAnswerInputs(
        QuestionWriteRequest request,
        IEnumerable<AcceptedAnswerEntity>? existingAnswers)
    {
        var existing = existingAnswers?.ToArray() ?? [];
        var requested = request.AcceptedAnswers?.ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(request.CanonicalAnswer))
        {
            var canonical = requested.FirstOrDefault(answer =>
                NormalizeVariantType(answer.VariantType, answer.IsExplicitNonKanjiException)
                == "canonical");
            if (canonical is null)
            {
                requested.Insert(
                    0,
                    new AcceptedAnswerWriteRequest
                    {
                        Text = request.CanonicalAnswer,
                        VariantType = "canonical",
                        Provenance = request.AnswerProvenance,
                    });
            }
            else
            {
                canonical.Text = request.CanonicalAnswer;
            }
        }

        var result = new List<AnswerWriteModel>(requested.Count);
        foreach (var answer in requested)
        {
            var variantType = NormalizeVariantType(
                answer.VariantType,
                answer.IsExplicitNonKanjiException);
            var normalized = JapaneseTextNormalizer.NormalizeForComparison(answer.Text);
            var matched = FindExistingAnswer(answer, variantType, normalized, existing);
            var requestedProvenance = answer.Provenance
                ?? (variantType == "canonical" ? request.AnswerProvenance : null);
            var provenance = matched?.AnswerProvenance == "provided_model_answer"
                ? "provided_model_answer"
                : requestedProvenance
                    ?? matched?.AnswerProvenance
                    ?? "teacher_entered";

            result.Add(
                new AnswerWriteModel(
                    answer.Id,
                    answer.Text?.Trim() ?? string.Empty,
                    normalized,
                    variantType,
                    provenance,
                    answer.TeacherVerified ?? true,
                    answer.SourceFileReferenceId ?? matched?.SourceFileReferenceId,
                    answer.SourcePageNumber ?? matched?.SourcePageNumber,
                    answer.SourceRegionId ?? matched?.SourceRegionId,
                    matched));
        }

        return result;
    }

    private static AcceptedAnswerEntity? FindExistingAnswer(
        AcceptedAnswerWriteRequest input,
        string variantType,
        string normalized,
        IReadOnlyCollection<AcceptedAnswerEntity> existing)
    {
        if (!string.IsNullOrWhiteSpace(input.Id))
        {
            var byId = existing.FirstOrDefault(answer => answer.Id == input.Id);
            if (byId is not null)
            {
                return byId;
            }
        }

        var sameValue = existing.FirstOrDefault(answer =>
            answer.VariantType == variantType
            && answer.NormalizedText == normalized);
        if (sameValue is not null)
        {
            return sameValue;
        }

        return variantType == "canonical"
            ? existing.FirstOrDefault(answer => answer.VariantType == "canonical")
            : null;
    }

    private static List<object> ValidateAnswerInputs(
        IReadOnlyCollection<AnswerWriteModel> answers,
        TemplateVersionEntity version)
    {
        var errors = new List<object>();
        var duplicates = answers
            .GroupBy(answer => (answer.NormalizedText, answer.VariantType))
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicates)
        {
            errors.Add(
                FieldError(
                    "acceptedAnswers",
                    "DUPLICATE",
                    $"同じ解答候補が重複しています: {duplicate.Key.NormalizedText}"));
        }

        if (answers.Count(answer => answer.VariantType == "canonical") > 1)
        {
            errors.Add(
                FieldError(
                    "acceptedAnswers",
                    "MULTIPLE_CANONICAL",
                    "正答は1件だけ指定してください。"));
        }

        if (answers
            .Where(answer => answer.Existing is not null)
            .GroupBy(answer => answer.Existing!.Id, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            errors.Add(
                FieldError(
                    "acceptedAnswers",
                    "DUPLICATE_ID",
                    "同じ解答候補IDを複数回指定できません。"));
        }

        foreach (var answer in answers)
        {
            if (string.IsNullOrWhiteSpace(answer.Text))
            {
                errors.Add(
                    FieldError(
                        "acceptedAnswers",
                        "REQUIRED",
                        "解答候補を入力してください。"));
                continue;
            }

            if (answer.Text.Length > 4000)
            {
                errors.Add(
                    FieldError(
                        "acceptedAnswers",
                        "TOO_LONG",
                        "解答候補が長すぎます。"));
            }

            if (!AnswerProvenances.Contains(answer.Provenance))
            {
                errors.Add(
                    FieldError(
                        "acceptedAnswers.provenance",
                        "INVALID",
                        "解答の出典区分が正しくありません。"));
                continue;
            }

            if (!AnswerVariantTypes.Contains(answer.VariantType))
            {
                errors.Add(
                    FieldError(
                        "acceptedAnswers.variantType",
                        "INVALID",
                        "解答候補の種類が正しくありません。"));
                continue;
            }

            if (answer.Provenance != "provided_model_answer")
            {
                continue;
            }

            var source = version.Sources.FirstOrDefault(item =>
                item.FileReferenceId is not null
                && item.FileReferenceId == answer.SourceFileReferenceId);
            if (source is null
                || source.SourceRole is not (
                    "contains_model_answers"
                    or "separate_answer_key")
                || answer.SourcePageNumber is not > 0)
            {
                errors.Add(
                    FieldError(
                        "acceptedAnswers.provenance",
                        "PROVIDED_SOURCE_REQUIRED",
                        "模範解答の出典ファイル、ページ、区分を確認してください。"));
            }
        }

        return errors;
    }

    private static void ApplyQuestionRegion(
        OokiGraderDbContext db,
        QuestionEntity question,
        PageRegionWriteRequest input,
        string regionType,
        DateTimeOffset now)
    {
        var region = regionType == "question"
            ? question.QuestionRegion
            : question.AnswerRegion;
        if (region is null)
        {
            region = new RegionEntity
            {
                Id = UlidId.New(now),
                OwnerType = "question",
                OwnerId = question.Id,
                RegionType = regionType,
                CreatedSource = "teacher",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Regions.Add(region);
            if (regionType == "question")
            {
                question.QuestionRegion = region;
                question.QuestionRegionId = region.Id;
            }
            else
            {
                question.AnswerRegion = region;
                question.AnswerRegionId = region.Id;
            }
        }

        region.PageNumber = input.PageNumber;
        region.XMillionths = input.XMillionths;
        region.YMillionths = input.YMillionths;
        region.WidthMillionths = input.WidthMillionths;
        region.HeightMillionths = input.HeightMillionths;
        region.RotationDegrees = input.RotationDegrees ?? 0;
        region.CreatedSource = "teacher";
        region.ConfidenceBasisPoints = null;
        region.UpdatedAt = now;
    }

    private static RegionEntity CloneRegion(
        RegionEntity source,
        string questionId,
        DateTimeOffset now) =>
        new()
        {
            Id = UlidId.New(now),
            OwnerType = "question",
            OwnerId = questionId,
            PageNumber = source.PageNumber,
            RegionType = source.RegionType,
            XMillionths = source.XMillionths,
            YMillionths = source.YMillionths,
            WidthMillionths = source.WidthMillionths,
            HeightMillionths = source.HeightMillionths,
            RotationDegrees = source.RotationDegrees,
            CreatedSource = "teacher",
            ConfidenceBasisPoints = source.ConfidenceBasisPoints,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static PageRegionApiResponse? ToRegionResponse(RegionEntity? region) =>
        region is null
            ? null
            : new PageRegionApiResponse(
                region.PageNumber,
                region.XMillionths,
                region.YMillionths,
                region.WidthMillionths,
                region.HeightMillionths,
                region.RotationDegrees);

    private static string ComputePublishedContentHash(
        TemplateVersionEntity version,
        string domainContentHash)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            domainContentHash,
            version.DefaultPointsMilli,
            version.ExpectedSubmissionPageCount,
            sources = version.Sources
                .OrderBy(source => source.Ordinal)
                .ThenBy(source => source.Id, StringComparer.Ordinal)
                .Select(source => new
                {
                    source.Id,
                    source.SourceRole,
                    source.FileReferenceId,
                    source.Ordinal,
                }),
            questions = version.Questions
                .OrderBy(question => question.OrderIndex)
                .ThenBy(question => question.Id, StringComparer.Ordinal)
                .Select(question => new
                {
                    question.Id,
                    question.PointIncrementMilli,
                    question.RubricText,
                    question.TeacherNote,
                    question.KanjiPolicyNote,
                    questionRegion = ToRegionResponse(question.QuestionRegion),
                    answerRegion = ToRegionResponse(question.AnswerRegion),
                }),
        });
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static void ApplyAcceptedAnswers(
        OokiGraderDbContext db,
        QuestionEntity question,
        IReadOnlyCollection<AnswerWriteModel> inputs,
        TemplateVersionEntity version,
        DateTimeOffset now)
    {
        var retained = inputs
            .Where(input => input.Existing is not null)
            .Select(input => input.Existing!.Id)
            .ToHashSet(StringComparer.Ordinal);
        var removed = question.AcceptedAnswers
            .Where(answer => !retained.Contains(answer.Id))
            .ToArray();
        db.AcceptedAnswers.RemoveRange(removed);

        foreach (var input in inputs)
        {
            var answer = input.Existing;
            if (answer is null)
            {
                answer = new AcceptedAnswerEntity
                {
                    Id = UlidId.New(now.AddTicks(question.AcceptedAnswers.Count + 1L)),
                    QuestionId = question.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.AcceptedAnswers.Add(answer);
            }

            answer.AnswerText = input.Text;
            answer.NormalizedText = input.NormalizedText;
            answer.VariantType = input.VariantType;
            answer.AnswerProvenance = input.Provenance;
            answer.TeacherVerified = input.TeacherVerified;
            answer.SourceFileReferenceId = input.SourceFileReferenceId;
            answer.SourcePageNumber = input.SourcePageNumber;
            answer.SourceRegionId = input.SourceRegionId;
            answer.Locale = "ja-JP";
            answer.WidthPolicy = "nfkc";
            answer.PunctuationPolicy = "conservative";

            if (input.Provenance != "provided_model_answer")
            {
                answer.SourceFileReferenceId = null;
                answer.SourcePageNumber = null;
                answer.SourceRegionId = null;
            }
            else
            {
                _ = version.Sources.Single(source =>
                    source.FileReferenceId == answer.SourceFileReferenceId);
            }
        }
    }

    private static List<object> ValidateTemplateRequest(
        CreateTemplateApiRequest request)
    {
        var errors = new List<object>();
        AddRequired(errors, "title", request.Title, 500);
        AddRequired(errors, "subject", request.Subject, 300);
        AddOptionalLength(errors, "category", request.Category, 300);
        AddOptionalLength(errors, "course", request.Course, 300);
        AddOptionalLength(errors, "gradeLabel", request.GradeLabel, 200);
        AddOptionalLength(errors, "notes", request.Notes, 4000);
        if (request.DefaultPointsMilli is null or <= 0)
        {
            errors.Add(
                FieldError(
                    "defaultPointsMilli",
                    "OUT_OF_RANGE",
                    "初期配点は1ミリ点以上にしてください。"));
        }

        return errors;
    }

    private static List<object> ValidateQuestionWrite(
        QuestionWriteRequest request,
        int order,
        bool creating)
    {
        var errors = new List<object>();
        if (creating || request.DisplayLabel is not null)
        {
            AddRequired(errors, "displayLabel", request.DisplayLabel, 100);
        }

        if (order < 0)
        {
            errors.Add(
                FieldError("order", "OUT_OF_RANGE", "表示順は0以上にしてください。"));
        }

        if (request.QuestionText?.Length > 20_000)
        {
            errors.Add(
                FieldError(
                    "questionText",
                    "TOO_LONG",
                    "問題文が長すぎます。"));
        }

        if (request.QuestionType is not null
            && !QuestionTypes.Contains(request.QuestionType))
        {
            errors.Add(
                FieldError(
                    "questionType",
                    "INVALID",
                    "問題形式が正しくありません。"));
        }

        if (request.GradingMode is not null
            && !GradingModes.Contains(request.GradingMode))
        {
            errors.Add(
                FieldError(
                    "gradingMode",
                    "INVALID",
                    "採点方式が正しくありません。"));
        }

        if (request.MaxPointsMilli is <= 0)
        {
            errors.Add(
                FieldError(
                    "maxPointsMilli",
                    "OUT_OF_RANGE",
                    "配点は1ミリ点以上にしてください。"));
        }
        else if (creating && request.MaxPointsMilli is null)
        {
            // Creation uses the documented one-point default.
        }

        if (request.PointIncrementMilli is <= 0)
        {
            errors.Add(
                FieldError(
                    "pointIncrementMilli",
                    "OUT_OF_RANGE",
                    "配点刻みは1ミリ点以上にしてください。"));
        }

        if (request.MaxPointsMilli is > 0
            && request.PointIncrementMilli is > 0
            && (request.PointIncrementMilli > request.MaxPointsMilli
                || request.MaxPointsMilli % request.PointIncrementMilli != 0))
        {
            errors.Add(
                FieldError(
                    "pointIncrementMilli",
                    "INVALID",
                    "配点刻みは最大点を割り切る値にしてください。"));
        }

        AddOptionalLength(errors, "rubric", request.Rubric, 20_000);
        AddOptionalLength(errors, "teacherNote", request.TeacherNote, 4_000);
        AddOptionalLength(
            errors,
            "kanjiPolicyNote",
            request.KanjiPolicyNote,
            4_000);
        ValidateRegionWrite(errors, "questionRegion", request.QuestionRegion);
        ValidateRegionWrite(errors, "answerRegion", request.AnswerRegion);

        return errors;
    }

    private static void ValidateRegionWrite(
        List<object> errors,
        string field,
        PageRegionWriteRequest? region)
    {
        if (region is null)
        {
            return;
        }

        var validRotation = region.RotationDegrees is null or 0 or 90 or 180 or 270;
        var validBounds = region.PageNumber > 0
            && region.XMillionths >= 0
            && region.YMillionths >= 0
            && region.WidthMillionths > 0
            && region.HeightMillionths > 0
            && (long)region.XMillionths + region.WidthMillionths <= 1_000_000
            && (long)region.YMillionths + region.HeightMillionths <= 1_000_000;
        if (!validRotation || !validBounds)
        {
            errors.Add(
                FieldError(
                    field,
                    "OUT_OF_BOUNDS",
                    "領域はページ内に収まる正の座標で指定してください。"));
        }
    }

    private static void TouchVersion(
        OokiGraderDbContext db,
        TemplateVersionEntity version,
        DateTimeOffset now)
    {
        version.UpdatedAt = now;
        db.Entry(version).Property(item => item.Revision).IsModified = true;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        ClaimsPrincipal principal,
        HttpContext context,
        string eventType,
        string objectType,
        string objectId,
        object? safeMetadata = null)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = objectType,
            ObjectId = objectId,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = safeMetadata is null
                ? null
                : JsonSerializer.Serialize(safeMetadata),
        });
    }

    private static void AddRequired(
        List<object> errors,
        string field,
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(FieldError(field, "REQUIRED", "必須項目です。"));
        }
        else if (value.Length > maximumLength)
        {
            errors.Add(FieldError(field, "TOO_LONG", "入力が長すぎます。"));
        }
    }

    private static void AddOptionalLength(
        List<object> errors,
        string field,
        string? value,
        int maximumLength)
    {
        if (value?.Length > maximumLength)
        {
            errors.Add(FieldError(field, "TOO_LONG", "入力が長すぎます。"));
        }
    }

    private static object FieldError(
        string field,
        string code,
        string message) =>
        new { field, code, message };

    private static object ToProblemError(DomainError error) =>
        new
        {
            field = error.Path,
            code = error.Code,
            message = error.Message,
        };

    private static IResult ValidationProblem(
        HttpContext context,
        string code,
        string title,
        IReadOnlyList<object> errors) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            code,
            title,
            "入力内容と受付開始条件を確認してください。",
            errors);

    private static IResult Conflict(
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

    private static IResult Immutable(HttpContext context) =>
        Conflict(
            context,
            "TEMPLATE_VERSION_IMMUTABLE",
            "確定済みの採点基準は変更できません",
            "新しい下書き版を複製して編集してください。");

    private static IResult Archived(HttpContext context) =>
        Conflict(
            context,
            "TEMPLATE_ARCHIVED",
            "アーカイブ済みのひな形は変更できません",
            "ひな形を復元してから編集してください。");

    private static IResult RevisionRequired(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status428PreconditionRequired,
            "REVISION_REQUIRED",
            "更新条件が必要です",
            "最新のETagまたはrevisionを指定してください。");

    private static IResult Stale(HttpContext context, long currentRevision)
    {
        ApiHelpers.SetRevisionEtag(context.Response, currentRevision);
        return ApiHelpers.Problem(
            context,
            StatusCodes.Status412PreconditionFailed,
            "REVISION_STALE",
            "別の職員が先に更新しました",
            "最新の内容を確認してから変更をやり直してください。");
    }

    private static long SumPoints(IEnumerable<QuestionEntity> questions)
    {
        checked
        {
            return questions.Sum(question => question.MaxPointsMilli);
        }
    }

    private static bool TrySumPoints(
        IEnumerable<QuestionEntity> questions,
        out long total)
    {
        try
        {
            total = SumPoints(questions);
            return true;
        }
        catch (OverflowException)
        {
            total = 0;
            return false;
        }
    }

    private static string? ResolveQuestionId(
        string? path,
        TemplateVersionEntity version)
    {
        if (path is null || !path.StartsWith("questions[", StringComparison.Ordinal))
        {
            return null;
        }

        var closing = path.IndexOf(']', "questions[".Length);
        if (closing < 0
            || !int.TryParse(
                path.AsSpan("questions[".Length, closing - "questions[".Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index))
        {
            return null;
        }

        return version.Questions
            .OrderBy(question => question.OrderIndex)
            .ElementAtOrDefault(index)
            ?.Id;
    }

    private static bool TryParseNumeric(string value, out decimal number)
    {
        var normalized = JapaneseTextNormalizer.NormalizeForComparison(value);
        if (decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign
                    | NumberStyles.AllowDecimalPoint
                    | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture,
                out number))
        {
            return true;
        }

        var pieces = normalized.Split('/');
        if (pieces.Length == 2
            && decimal.TryParse(
                pieces[0],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numerator)
            && decimal.TryParse(
                pieces[1],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var denominator)
            && denominator != 0)
        {
            try
            {
                number = numerator / denominator;
                return true;
            }
            catch (OverflowException)
            {
            }
        }

        number = 0;
        return false;
    }

    private static QuestionType ParseQuestionType(string value) =>
        value switch
        {
            "multiple_choice" => QuestionType.MultipleChoice,
            "boolean" => QuestionType.Boolean,
            "numeric" => QuestionType.Numeric,
            "exact_short_text" => QuestionType.ExactShortText,
            "semantic_short_text" => QuestionType.SemanticShortText,
            "multi_part" => QuestionType.MultiPart,
            "subjective" => QuestionType.Subjective,
            "unsupported" => QuestionType.Unsupported,
            _ => throw new DomainValidationException(
            [
                new DomainError(
                    "question.type_invalid",
                    $"Unsupported question type '{value}'."),
            ]),
        };

    private static GradingMode ParseGradingMode(string value) =>
        value switch
        {
            "deterministic" => GradingMode.Deterministic,
            "transcribe_then_rules" => GradingMode.TranscribeThenRules,
            "ai_rubric" => GradingMode.AiRubric,
            "manual" => GradingMode.Manual,
            _ => throw new DomainValidationException(
            [
                new DomainError(
                    "question.grading_mode_invalid",
                    $"Unsupported grading mode '{value}'."),
            ]),
        };

    private static AcceptedAnswerVariantType ParseAnswerVariant(string value) =>
        value switch
        {
            "canonical" => AcceptedAnswerVariantType.Canonical,
            "equivalent" => AcceptedAnswerVariantType.Equivalent,
            "phonetic_exception" => AcceptedAnswerVariantType.PhoneticException,
            "numeric" => AcceptedAnswerVariantType.Numeric,
            "regex_restricted" => AcceptedAnswerVariantType.RegexRestricted,
            "choice" => AcceptedAnswerVariantType.Choice,
            _ => throw new DomainValidationException(
            [
                new DomainError(
                    "answer.variant_invalid",
                    $"Unsupported answer variant '{value}'."),
            ]),
        };

    private static AnswerProvenance ParseAnswerProvenance(string value) =>
        value switch
        {
            "provided_model_answer" => AnswerProvenance.ProvidedModelAnswer,
            "teacher_entered" => AnswerProvenance.TeacherEntered,
            "ai_proposed" => AnswerProvenance.AiProposed,
            "derived_variant" => AnswerProvenance.DerivedVariant,
            _ => throw new DomainValidationException(
            [
                new DomainError(
                    "answer.provenance_invalid",
                    $"Unsupported answer provenance '{value}'."),
            ]),
        };

    private static TemplateSourceRole ParseSourceRole(string value) =>
        value switch
        {
            "blank_test" => TemplateSourceRole.BlankTest,
            "contains_model_answers" => TemplateSourceRole.ContainsModelAnswers,
            "contains_non_model_answers" =>
                TemplateSourceRole.ContainsNonModelAnswers,
            "separate_answer_key" => TemplateSourceRole.SeparateAnswerKey,
            _ => throw new DomainValidationException(
            [
                new DomainError(
                    "template.source_role_invalid",
                    $"Unsupported template source role '{value}'."),
            ]),
        };

    private static string NormalizeVariantType(
        string? variantType,
        bool? explicitNonKanjiException)
    {
        if (explicitNonKanjiException == true)
        {
            return "phonetic_exception";
        }

        return variantType switch
        {
            null or "" => "equivalent",
            "accepted" => "equivalent",
            "explicitException" => "phonetic_exception",
            "canonical"
                or "equivalent"
                or "phonetic_exception"
                or "numeric"
                or "regex_restricted"
                or "choice" => variantType,
            _ => "invalid",
        };
    }

    private static string ToWebVariantType(string value) =>
        value switch
        {
            "equivalent" => "accepted",
            "phonetic_exception" => "explicitException",
            _ => value,
        };

    private static int AnswerVariantOrder(string value) =>
        value switch
        {
            "canonical" => 0,
            "equivalent" => 1,
            "phonetic_exception" => 2,
            _ => 3,
        };

    private static string ToWebSourceRole(string value) =>
        value switch
        {
            "blank_test" => "blankTest",
            "contains_model_answers" => "containsModelAnswers",
            "contains_non_model_answers" => "containsNonModelAnswers",
            "separate_answer_key" => "separateAnswerKey",
            _ => value,
        };

    private static string? NormalizeSourceRole(string? value) =>
        value switch
        {
            "blankTest" or "blank_test" => "blank_test",
            "containsModelAnswers" or "contains_model_answers" =>
                "contains_model_answers",
            "containsNonModelAnswers" or "contains_non_model_answers" =>
                "contains_non_model_answers",
            "separateAnswerKey" or "separate_answer_key" =>
                "separate_answer_key",
            _ => null,
        };

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsReplaceableMetadataField(string? value) =>
        value is "title" or "subject" or "category" or "gradeLabel" or "course";

    private sealed record CreateTemplateApiRequest
    {
        public string? Title { get; init; }

        public string? Subject { get; init; }

        public string? Category { get; init; }

        public string? GradeLabel { get; init; }

        public string? Course { get; init; }

        public string? Notes { get; init; }

        public long? DefaultPointsMilli { get; init; }
    }

    private sealed record TemplateLifecycleApiRequest
    {
        public long? Revision { get; init; }
    }

    private sealed record CreateTemplateVersionApiRequest
    {
        public string? SourceVersionId { get; init; }

        public string? CloneFromVersionId { get; init; }

        public long? SourceRevision { get; init; }

        public long? TargetTotalPointsMilli { get; init; }

        public long? DefaultPointsMilli { get; init; }

        public bool? DefaultAllowNonKanji { get; init; }
    }

    private sealed record AttachTemplateSourceApiRequest
    {
        public string? UploadId { get; init; }

        public string? SourceRole { get; init; }

        public string? DisplayName { get; init; }
    }

    private sealed record GenerateTemplateDraftApiRequest
    {
        public string? Priority { get; init; }

        public IReadOnlyList<string>? ReplaceableMetadataFields { get; init; }
    }

    private sealed record VerifyQuestionProposalsApiRequest
    {
        public string? SelectionMode { get; init; }

        public long? Revision { get; init; }
    }

    private sealed record ReorderQuestionsApiRequest
    {
        public IReadOnlyList<string>? QuestionIds { get; init; }
    }

    private sealed record PublishTemplateApiRequest
    {
        public long? Revision { get; init; }

        public DateOnly? TestDate { get; init; }

        public string? ClassLabel { get; init; }
    }

    private sealed record QuestionWriteRequest
    {
        public string? DisplayLabel { get; init; }

        public int? Order { get; init; }

        public int? SortOrder { get; init; }

        public string? QuestionText { get; init; }

        public string? QuestionType { get; init; }

        public string? GradingMode { get; init; }

        public long? MaxPointsMilli { get; init; }

        public long? PointIncrementMilli { get; init; }

        public bool? AllowNonKanji { get; init; }

        public bool? RequiresCompleteAnswer { get; init; }

        public bool? AnswerOrderInsensitive { get; init; }

        public IReadOnlyList<AcceptedAnswerWriteRequest>? AcceptedAnswers { get; init; }

        public string? CanonicalAnswer { get; init; }

        public string? AnswerProvenance { get; init; }

        public string? Rubric { get; init; }

        public string? TeacherNote { get; init; }

        public string? KanjiPolicyNote { get; init; }

        public PageRegionWriteRequest? AnswerRegion { get; init; }

        public PageRegionWriteRequest? QuestionRegion { get; init; }

        public bool? RequiresReviewAlways { get; init; }

        public bool? TeacherVerified { get; init; }

        public long? Revision { get; init; }
    }

    private sealed record PageRegionWriteRequest
    {
        public int PageNumber { get; init; }

        public int XMillionths { get; init; }

        public int YMillionths { get; init; }

        public int WidthMillionths { get; init; }

        public int HeightMillionths { get; init; }

        public int? RotationDegrees { get; init; }
    }

    private sealed record AcceptedAnswerWriteRequest
    {
        public string? Id { get; init; }

        public string? Text { get; set; }

        public string? VariantType { get; init; }

        public string? Provenance { get; init; }

        public bool? IsExplicitNonKanjiException { get; init; }

        public bool? TeacherVerified { get; init; }

        public string? SourceFileReferenceId { get; init; }

        public int? SourcePageNumber { get; init; }

        public string? SourceRegionId { get; init; }
    }

    private sealed record AnswerWriteModel(
        string? RequestedId,
        string Text,
        string NormalizedText,
        string VariantType,
        string Provenance,
        bool TeacherVerified,
        string? SourceFileReferenceId,
        int? SourcePageNumber,
        string? SourceRegionId,
        AcceptedAnswerEntity? Existing);

    private sealed record TemplateSummaryResponse(
        string Id,
        string Title,
        string? Subject,
        string? Category,
        string? GradeLabel,
        string? Course,
        string LifecycleState,
        string? ActiveVersionId,
        int? ActiveVersionNumber,
        int QuestionCount,
        long TotalPointsMilli,
        long DefaultPointsMilli,
        int VersionCount,
        DateTimeOffset UpdatedAt,
        long Revision);

    private sealed record TemplateVersionResponse(
        string Id,
        string TemplateId,
        int VersionNumber,
        string State,
        string Title,
        long TotalPointsMilli,
        long DefaultPointsMilli,
        IReadOnlyList<TemplateQuestionResponse> Questions,
        IReadOnlyList<object> Pages,
        IReadOnlyList<TemplateSourceApiResponse> Sources,
        IReadOnlyList<string> BlockingWarnings,
        IReadOnlyList<string> NonBlockingWarnings,
        IReadOnlyList<TemplateValidationIssue> ReviewIssues,
        string? ContentHash,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? PublishedAt,
        long Revision,
        PublishTestSessionResponse? TestSession = null);

    private sealed record PublishTestSessionResponse(
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
        string? Course,
        string? TemplateCourse,
        DateOnly TestDate,
        string? ClassLabel,
        string Priority,
        string State,
        string CreationSource,
        long Revision);

    private sealed record TemplateSourceApiResponse(
        string Id,
        string SourceRole,
        string DisplayName,
        string? UploadId,
        string? ContentUrl,
        string? MimeType,
        DateTimeOffset CreatedAt,
        bool SourceRoleInferred,
        int? SourceRoleConfidenceBasisPoints,
        string? SourceRoleInferenceReason);

    private sealed record InferredTemplateMetadataResponse(
        string? Title,
        string? Subject,
        string? Category,
        string? GradeLabel,
        string? Course,
        double Confidence,
        IReadOnlyList<string> Warnings);

    private sealed record TemplateQuestionResponse(
        string Id,
        string DisplayLabel,
        int Order,
        string QuestionText,
        string QuestionType,
        string GradingMode,
        long MaxPointsMilli,
        long PointIncrementMilli,
        bool AllowNonKanji,
        bool RequiresCompleteAnswer,
        bool AnswerOrderInsensitive,
        IReadOnlyList<AcceptedAnswerApiResponse> AcceptedAnswers,
        string? CanonicalAnswer,
        string? Rubric,
        string? TeacherNote,
        string? KanjiPolicyNote,
        PageRegionApiResponse? AnswerRegion,
        PageRegionApiResponse? QuestionRegion,
        bool RequiresReviewAlways,
        string? AnswerProvenance,
        string ProposalState,
        IReadOnlyList<string> Warnings,
        bool TeacherVerified,
        long Revision);

    private sealed record PageRegionApiResponse(
        int PageNumber,
        int XMillionths,
        int YMillionths,
        int WidthMillionths,
        int HeightMillionths,
        int RotationDegrees);

    private sealed record AcceptedAnswerApiResponse(
        string Id,
        string Text,
        string VariantType,
        string Provenance,
        bool IsExplicitNonKanjiException,
        bool TeacherVerified,
        string? SourceFileReferenceId,
        int? SourcePageNumber,
        string? SourceRegionId,
        long Revision);

    private sealed record TemplateValidationResponse(
        bool Valid,
        int PageCount,
        int QuestionCount,
        long TotalPointsMilli,
        int KanjiRequiredCount,
        int AlwaysReviewCount,
        IReadOnlyList<TemplateValidationIssue> Issues);

    private sealed record TemplateValidationIssue(
        string Code,
        string Message,
        string? QuestionId,
        bool Blocking);

    private sealed record ProposalVerificationAssessment(
        IReadOnlyList<QuestionEntity> EligibleQuestions,
        int BlockedQuestionCount,
        IReadOnlyList<TemplateValidationIssue> Issues);

    private sealed record VerifyQuestionProposalsResponse(
        long Revision,
        int VerifiedQuestionCount,
        int VerifiedAnswerCount,
        int SkippedQuestionCount,
        IReadOnlyList<TemplateValidationIssue> Issues,
        IReadOnlyList<TemplateQuestionResponse> Questions);
}
