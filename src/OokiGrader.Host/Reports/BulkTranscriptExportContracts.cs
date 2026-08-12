using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Host.Api;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Reports;

public sealed record BulkTranscriptExportSelector(
    IReadOnlyList<string>? SubmissionIds,
    BulkTranscriptExportFilter? Filter);

public sealed record BulkTranscriptExportFilter(
    string? Search,
    DateOnly? From,
    DateOnly? To,
    string? StudentId,
    string? TemplateId,
    string? Subject,
    string? Category,
    string? Course,
    string? Class,
    string? Sort);

public sealed record BulkTranscriptExportPreviewRequest(
    BulkTranscriptExportSelector? Selector);

public sealed record BulkTranscriptExportCreateRequest(
    string? SourceFingerprint,
    BulkTranscriptExportSelector? Selector);

internal sealed record BulkTranscriptSelection(
    string NormalizedSelectorJson,
    string SelectorHash,
    string SourceFingerprint,
    int StudentCount,
    IReadOnlyList<BulkTranscriptCandidate> Candidates);

internal sealed record BulkTranscriptCandidate(
    string SubmissionId,
    long SubmissionRevision,
    string StudentId,
    long StudentRevision,
    string TestSessionId,
    long TestSessionRevision,
    string GradingRunId,
    long ResultSourceRevision,
    string TemplateVersionId,
    int TemplateVersionNumber,
    long TemplateVersionRevision,
    string TestTemplateId,
    long TestTemplateRevision);

internal sealed record FrozenBulkResultSource(
    int Ordinal,
    string SubmissionId,
    long SubmissionRevision,
    string StudentId,
    long StudentRevision,
    string TestSessionId,
    long TestSessionRevision,
    string GradingRunId,
    long ResultSourceRevision,
    string TemplateVersionId,
    int TemplateVersionNumber,
    long TemplateVersionRevision,
    string TestTemplateId,
    long TestTemplateRevision,
    string SourceHash);

internal sealed class BulkTranscriptSelectionException(
    string errorCode,
    string safeDetail,
    int statusCode = StatusCodes.Status422UnprocessableEntity,
    IReadOnlyList<string>? invalidSubmissionIds = null,
    int? nonExportableResultCount = null) : Exception(safeDetail)
{
    public string ErrorCode { get; } = errorCode;
    public string SafeDetail { get; } = safeDetail;
    public int StatusCode { get; } = statusCode;
    public IReadOnlyList<string>? InvalidSubmissionIds { get; } =
        invalidSubmissionIds;
    public int? NonExportableResultCount { get; } =
        nonExportableResultCount;
}

internal static class BulkTranscriptSelectionResolver
{
    public const int MaximumStudents = 100;
    public const int MaximumResults = 500;
    public const string SelectionPolicyVersion = "bulk-result-selection-v1";

    private static readonly HashSet<string> AllowedSorts =
        new(StringComparer.Ordinal)
        {
            "-testDate",
            "testDate",
            "-finalizedAt",
            "finalizedAt",
            "studentName",
            "-studentName",
            "testTitle",
            "-testTitle",
            "-updatedAt",
        };

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<BulkTranscriptSelection> ResolveAsync(
        OokiGraderDbContext db,
        HttpContext context,
        BulkTranscriptExportSelector? selector,
        CancellationToken cancellationToken)
    {
        if (selector is null)
        {
            throw Invalid(
                "bulk_export_selector_required",
                "出力する確定結果を指定してください。");
        }

        var hasIds = selector.SubmissionIds is not null;
        var hasFilter = selector.Filter is not null;
        if (hasIds == hasFilter)
        {
            throw Invalid(
                "bulk_export_selector_invalid",
                "答案の選択または絞り込み条件のどちらか一方を指定してください。");
        }

        var query = db.Submissions
            .AsNoTracking()
            .Include(item => item.AssignedStudent)
            .Include(item => item.TestSession)
                .ThenInclude(item => item.TemplateVersion)
                    .ThenInclude(item => item.TestTemplate)
            .Include(item => item.GradingRuns)
            .Where(item => item.State == "finalized"
                && item.FinalizedAt != null
                && item.VoidedAt == null);

        object normalizedSelector;
        IReadOnlyList<string>? requestedIds = null;
        if (hasIds)
        {
            requestedIds = ValidateSubmissionIds(selector.SubmissionIds!);
            var ids = requestedIds.ToArray();
            query = query.Where(item => ids.Contains(item.Id));
            normalizedSelector = new
            {
                submissionIds = requestedIds,
            };
        }
        else
        {
            var filter = NormalizeFilter(
                context,
                selector.Filter!,
                out var searchTokens);
            query = ApplyFilter(query, filter, searchTokens);
            normalizedSelector = new
            {
                filter = new
                {
                    filter.Search,
                    filter.From,
                    filter.To,
                    filter.StudentId,
                    filter.TemplateId,
                    filter.Subject,
                    filter.Category,
                    filter.Course,
                    @class = filter.Class,
                    filter.Sort,
                },
            };
        }

        var rows = await query
            .Take(MaximumResults + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count > MaximumResults)
        {
            throw Invalid(
                "bulk_export_result_limit_exceeded",
                $"一度に出力できる結果は{MaximumResults}件までです。条件を絞り込んでください。");
        }

        var nonExportableIds = rows
            .Where(item => !IsExportable(item))
            .Select(item => item.Id)
            .ToArray();
        if (requestedIds is not null)
        {
            var found = rows.Select(item => item.Id)
                .ToHashSet(StringComparer.Ordinal);
            var invalid = requestedIds
                .Where(id => !found.Contains(id)
                    || nonExportableIds.Contains(id, StringComparer.Ordinal))
                .ToArray();
            if (invalid.Length > 0)
            {
                throw Invalid(
                    "bulk_export_selection_invalid",
                    "選択には未確定、無効、未割り当て、または存在しない答案が含まれています。全件を見直してください。",
                    invalid);
            }
        }
        else if (rows.Count == 0)
        {
            throw Invalid(
                "bulk_export_selection_empty",
                "条件に一致する確定結果がありません。");
        }
        else if (nonExportableIds.Length > 0)
        {
            throw new BulkTranscriptSelectionException(
                "bulk_export_filter_has_non_exportable_results",
                $"条件に一致する確定結果のうち{nonExportableIds.Length}件は、生徒未割り当てまたは確定結果の整合性不備のため出力できません。帳票一覧で対象を確認し、生徒割り当てまたは採点確定を修正してください。",
                nonExportableResultCount: nonExportableIds.Length);
        }

        rows = rows
            .OrderBy(item => item.AssignedStudent!.StudentNumberNormalized,
                StringComparer.Ordinal)
            .ThenBy(item => item.AssignedStudentId, StringComparer.Ordinal)
            .ThenBy(item => item.TestSession.TestDate)
            .ThenBy(item => item.TestSession.TitleOverride
                ?? item.TestSession.TemplateTitleSnapshot
                ?? item.TestSession.TemplateVersion.TestTemplate.Title,
                StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

        var studentCount = rows
            .Select(item => item.AssignedStudentId!)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (studentCount > MaximumStudents)
        {
            throw Invalid(
                "bulk_export_student_limit_exceeded",
                $"一度に出力できる生徒は{MaximumStudents}名までです。条件を絞り込んでください。");
        }

        var candidates = rows.Select(item =>
        {
            var run = item.GradingRuns.Single(run =>
                run.Id == item.CurrentGradingRunId);
            return new BulkTranscriptCandidate(
                item.Id,
                item.Revision,
                item.AssignedStudentId!,
                item.AssignedStudent!.Revision,
                item.TestSessionId,
                item.TestSession.Revision,
                run.Id,
                run.ResultSourceRevision,
                item.TestSession.TemplateVersionId,
                item.TestSession.TemplateVersion.VersionNumber,
                item.TestSession.TemplateVersion.Revision,
                item.TestSession.TemplateVersion.TestTemplate.Id,
                item.TestSession.TemplateVersion.TestTemplate.Revision);
        }).ToArray();

        var siteRevision = await db.SiteSettings
            .AsNoTracking()
            .Select(item => item.Revision)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectorJson = JsonSerializer.Serialize(
            normalizedSelector,
            SnapshotJsonOptions);
        var sourceFingerprint = HashJson(new
        {
            policy = SelectionPolicyVersion,
            siteRevision,
            candidates,
        });
        return new BulkTranscriptSelection(
            selectorJson,
            HashUtf8(selectorJson),
            sourceFingerprint,
            studentCount,
            candidates);
    }

    private static bool IsExportable(SubmissionEntity item)
    {
        if (item.AssignedStudentId is null
            || item.AssignedStudent is null
            || item.CurrentGradingRunId is null)
        {
            return false;
        }

        var currentRun = item.GradingRuns.SingleOrDefault(run =>
            run.Id == item.CurrentGradingRunId);
        return currentRun is not null
            && currentRun.State == "finalized"
            && currentRun.TemplateVersionId
                == item.TestSession.TemplateVersionId;
    }

    private static List<string> ValidateSubmissionIds(
        IReadOnlyList<string> submissionIds)
    {
        if (submissionIds.Count == 0)
        {
            throw Invalid(
                "bulk_export_selection_empty",
                "少なくとも1件の確定結果を選択してください。");
        }

        if (submissionIds.Count > MaximumResults)
        {
            throw Invalid(
                "bulk_export_result_limit_exceeded",
                $"一度に出力できる結果は{MaximumResults}件までです。");
        }

        var normalized = new List<string>(submissionIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in submissionIds)
        {
            var id = value?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 128)
            {
                throw Invalid(
                    "bulk_export_selection_invalid",
                    "選択した答案IDを確認してください。");
            }

            if (!seen.Add(id))
            {
                throw Invalid(
                    "bulk_export_selection_duplicate",
                    "同じ答案が複数回選択されています。");
            }

            normalized.Add(id);
        }

        normalized.Sort(StringComparer.Ordinal);
        return normalized;
    }

    private static BulkTranscriptExportFilter NormalizeFilter(
        HttpContext context,
        BulkTranscriptExportFilter filter,
        out IReadOnlyList<string> searchTokens)
    {
        if (!ListQuery.TryNormalizeSearch(
                context,
                filter.Search,
                out var normalizedSearch,
                out searchTokens,
                out _)
            || !ListQuery.TryTrimFilter(
                context,
                filter.StudentId,
                "studentId",
                out var studentId,
                out _,
                ListQuery.MaximumIdLength)
            || !ListQuery.TryTrimFilter(
                context,
                filter.TemplateId,
                "templateId",
                out var templateId,
                out _,
                ListQuery.MaximumIdLength)
            || !ListQuery.TryTrimFilter(
                context,
                filter.Subject,
                "subject",
                out var subject,
                out _)
            || !ListQuery.TryTrimFilter(
                context,
                filter.Category,
                "category",
                out var category,
                out _)
            || !ListQuery.TryTrimFilter(
                context,
                filter.Course,
                "course",
                out var course,
                out _)
            || !ListQuery.TryTrimFilter(
                context,
                filter.Class,
                "class",
                out var classLabel,
                out _))
        {
            throw Invalid(
                "bulk_export_filter_invalid",
                "絞り込み条件を確認してください。");
        }

        var normalized = new BulkTranscriptExportFilter(
            normalizedSearch,
            filter.From,
            filter.To,
            studentId,
            templateId,
            subject,
            category,
            course,
            classLabel,
            Trim(filter.Sort, 40, "並び順") ?? "-testDate");
        if (normalized.From > normalized.To)
        {
            throw Invalid(
                "bulk_export_date_range_invalid",
                "開始日は終了日以前にしてください。");
        }

        if (!AllowedSorts.Contains(normalized.Sort!))
        {
            throw Invalid(
                "bulk_export_sort_invalid",
                "並び順を確認してください。");
        }

        return normalized;
    }

    private static IQueryable<SubmissionEntity>
        ApplyFilter(
            IQueryable<SubmissionEntity> query,
            BulkTranscriptExportFilter filter,
            IReadOnlyList<string> searchTokens)
    {
        foreach (var token in searchTokens)
        {
            var pattern = ListQuery.ContainsPattern(token);
            query = query.Where(item =>
                (item.OriginalFileName != null
                    && EF.Functions.Like(
                        item.OriginalFileName,
                        pattern,
                        "\\"))
                || (item.AssignedStudent != null
                    && (EF.Functions.Like(
                            item.AssignedStudent.StudentNumberNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            item.AssignedStudent.FamilyNameNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            item.AssignedStudent.GivenNameNormalized,
                            pattern,
                            "\\")
                        || EF.Functions.Like(
                            item.AssignedStudent.FamilyNameNormalized
                                + item.AssignedStudent.GivenNameNormalized,
                            pattern,
                            "\\")
                        || item.AssignedStudent.Aliases.Any(alias =>
                            EF.Functions.Like(
                                alias.NormalizedValue,
                                pattern,
                                "\\"))))
                || (item.TestSession.TitleOverride != null
                    && EF.Functions.Like(
                        item.TestSession.TitleOverride,
                        pattern,
                        "\\"))
                || (item.TestSession.TemplateTitleSnapshot != null
                    && EF.Functions.Like(
                        item.TestSession.TemplateTitleSnapshot,
                        pattern,
                        "\\"))
                || EF.Functions.Like(
                    item.TestSession.TemplateVersion.TestTemplate.Title,
                    pattern,
                    "\\"));
        }

        if (filter.From.HasValue)
        {
            query = query.Where(item =>
                item.TestSession.TestDate >= filter.From.Value);
        }

        if (filter.To.HasValue)
        {
            query = query.Where(item =>
                item.TestSession.TestDate <= filter.To.Value);
        }

        if (filter.StudentId is not null)
        {
            query = query.Where(item =>
                item.AssignedStudentId == filter.StudentId);
        }

        if (filter.TemplateId is not null)
        {
            query = query.Where(item =>
                item.TestSession.TemplateVersion.TestTemplate.Id
                    == filter.TemplateId);
        }

        if (filter.Subject is not null)
        {
            query = query.Where(item =>
                (item.TestSession.TemplateSubjectSnapshot
                    ?? item.TestSession.TemplateVersion.TestTemplate.Subject)
                    == filter.Subject);
        }

        if (filter.Category is not null)
        {
            query = query.Where(item =>
                (item.TestSession.TemplateCategorySnapshot
                    ?? item.TestSession.TemplateVersion.TestTemplate.Category)
                    == filter.Category);
        }

        if (filter.Course is not null)
        {
            query = query.Where(item =>
                item.TestSession.Course == filter.Course);
        }

        if (filter.Class is not null)
        {
            query = query.Where(item =>
                item.TestSession.ClassLabel == filter.Class);
        }

        return query;
    }

    private static string? Trim(string? value, int maximumLength, string label)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed?.Length > maximumLength)
        {
            throw Invalid(
                "bulk_export_filter_invalid",
                $"{label}が長すぎます。");
        }

        return trimmed;
    }

    internal static string HashUtf8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string HashJson(object value) => HashUtf8(
        JsonSerializer.Serialize(value, SnapshotJsonOptions));

    private static BulkTranscriptSelectionException Invalid(
        string code,
        string detail,
        IReadOnlyList<string>? invalidIds = null) =>
        new(code, detail, invalidSubmissionIds: invalidIds);
}
