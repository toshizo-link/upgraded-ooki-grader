using Microsoft.EntityFrameworkCore;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

public static class TemplateAutomationEndpoints
{
    private const int MaximumSourceCount = 20;

    public static IEndpointRouteBuilder MapTemplateAutomationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/api/v1/templates/source-match",
                FindExactPublishedSourceMatch)
            .WithTags("Templates")
            .RequireAuthorization("teacher");
        return endpoints;
    }

    private static async Task<IResult> FindExactPublishedSourceMatch(
        HttpContext context,
        string? uploadIds,
        string? sourceRoles,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var requestedUploadIds = SplitAlignedValues(uploadIds);
        var requestedWebRoles = SplitAlignedValues(sourceRoles);
        var normalizedRoles = requestedWebRoles
            .Select(NormalizeSourceRole)
            .ToArray();
        if (requestedUploadIds.Length is 0 or > MaximumSourceCount
            || requestedUploadIds.Length != requestedWebRoles.Length
            || requestedUploadIds.Distinct(StringComparer.Ordinal).Count()
                != requestedUploadIds.Length
            || requestedUploadIds.Any(id =>
                string.IsNullOrWhiteSpace(id) || id.Length > 128)
            || normalizedRoles.Any(role => role is null))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "TEMPLATE_SOURCE_MATCH_INVALID",
                "再利用できるひな形を確認できません",
                $"完了済みアップロードと資料区分を同じ順序で1〜{MaximumSourceCount}件指定してください。");
        }

        var requestedSources = requestedUploadIds
            .Select(
                (uploadId, index) => new RequestedSource(
                    uploadId,
                    normalizedRoles[index]!))
            .ToArray();

        var completedUploads = await db.UploadSessions
            .AsNoTracking()
            .Where(upload =>
                requestedUploadIds.Contains(upload.Id)
                && upload.Purpose == "template_source"
                && upload.State == "completed"
                && upload.DestinationType == "template_source")
            .Select(upload => upload.Id)
            .ToArrayAsync(cancellationToken);
        if (completedUploads.Length != requestedUploadIds.Length)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_SOURCE_UPLOAD_INCOMPLETE",
                "ファイルの受信が完了していません",
                "すべての問題用紙のアップロード完了後に再利用判定を実行してください。");
        }

        var uploadedFiles = await db.FileReferences
            .AsNoTracking()
            .Where(reference =>
                reference.OwnerType == "upload_session"
                && requestedUploadIds.Contains(reference.OwnerId)
                && reference.Purpose == "template_source")
            .Select(reference => new
            {
                UploadId = reference.OwnerId,
                reference.FileObjectId,
            })
            .ToArrayAsync(cancellationToken);
        if (uploadedFiles.Length != requestedUploadIds.Length
            || uploadedFiles
                .GroupBy(file => file.UploadId, StringComparer.Ordinal)
                .Any(group => group.Count() != 1))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "TEMPLATE_SOURCE_FILE_MISSING",
                "保存済みファイルを確認できません",
                "アップロードをやり直してください。");
        }

        var uploadedFileByUploadId = uploadedFiles.ToDictionary(
            file => file.UploadId,
            StringComparer.Ordinal);
        var requestedFiles = requestedSources
            .Select(source => new RequestedFile(
                source.UploadId,
                uploadedFileByUploadId[source.UploadId].FileObjectId,
                source.SourceRole))
            .ToArray();
        var uploadedObjectIds = requestedFiles
            .Select(file => file.FileObjectId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requestedIdentity = requestedFiles
            .Select(file => (file.FileObjectId, file.SourceRole))
            .OrderBy(item => item.FileObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.SourceRole, StringComparer.Ordinal)
            .ToArray();
        var candidateVersionIds = await (
                from source in db.TemplateSources.AsNoTracking()
                join reference in db.FileReferences.AsNoTracking()
                    on source.FileReferenceId equals reference.Id
                join version in db.TemplateVersions.AsNoTracking()
                    on source.TemplateVersionId equals version.Id
                where version.State == "published"
                    && uploadedObjectIds.Contains(reference.FileObjectId)
                select version.Id)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        if (candidateVersionIds.Length == 0)
        {
            return Results.Ok(new { exactMatch = (object?)null });
        }

        var candidateSources = await (
                from source in db.TemplateSources.AsNoTracking()
                join reference in db.FileReferences.AsNoTracking()
                    on source.FileReferenceId equals reference.Id
                where candidateVersionIds.Contains(source.TemplateVersionId)
                select new
                {
                    source.TemplateVersionId,
                    source.SourceRole,
                    reference.FileObjectId,
                })
            .ToArrayAsync(cancellationToken);
        var candidateSourceCounts = await db.TemplateSources
            .AsNoTracking()
            .Where(source => candidateVersionIds.Contains(
                source.TemplateVersionId))
            .GroupBy(source => source.TemplateVersionId)
            .Select(group => new { VersionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(
                item => item.VersionId,
                item => item.Count,
                StringComparer.Ordinal,
                cancellationToken);
        var exactVersionIds = candidateSources
            .GroupBy(source => source.TemplateVersionId, StringComparer.Ordinal)
            .Where(group =>
                candidateSourceCounts[group.Key] == requestedIdentity.Length
                && group
                    .Select(source => (source.FileObjectId, source.SourceRole))
                    .OrderBy(item => item.FileObjectId, StringComparer.Ordinal)
                    .ThenBy(item => item.SourceRole, StringComparer.Ordinal)
                    .SequenceEqual(requestedIdentity))
            .Select(group => group.Key)
            .ToArray();
        if (exactVersionIds.Length == 0)
        {
            return Results.Ok(new { exactMatch = (object?)null });
        }

        var match = await db.TemplateVersions
            .AsNoTracking()
            .Include(version => version.TestTemplate)
            .Where(version => exactVersionIds.Contains(version.Id))
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => version.VersionNumber)
            .Select(version => new
            {
                TemplateId = version.TestTemplateId,
                TemplateTitle = version.TestTemplate.Title,
                VersionId = version.Id,
                version.VersionNumber,
                version.ContentHash,
                version.PublishedAt,
            })
            .FirstAsync(cancellationToken);
        // Echo the caller-confirmed roles. A prior template must never rewrite
        // the authority classification selected for the current upload.
        var matchedRoles = requestedFiles
            .Select(file => new
            {
                file.UploadId,
                SourceRole = ToWebSourceRole(file.SourceRole),
            })
            .ToArray();

        return Results.Ok(new
        {
            exactMatch = new
            {
                match.TemplateId,
                match.TemplateTitle,
                match.VersionId,
                match.VersionNumber,
                match.ContentHash,
                match.PublishedAt,
                sources = matchedRoles,
            },
        });
    }

    private static string ToWebSourceRole(string value) =>
        value switch
        {
            "blank_test" => "blankTest",
            "contains_model_answers" => "containsModelAnswers",
            "contains_non_model_answers" => "containsNonModelAnswers",
            "separate_answer_key" => "separateAnswerKey",
            _ => value,
        };

    private static string[] SplitAlignedValues(string? value) =>
        (value ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries);

    private static string? NormalizeSourceRole(string value) =>
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

    private sealed record RequestedSource(string UploadId, string SourceRole);

    private sealed record RequestedFile(
        string UploadId,
        string FileObjectId,
        string SourceRole);
}
