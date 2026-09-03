using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Api;

public static class OrderedScanRoutingEndpoints
{
    public const string ClassificationRateLimitPolicy =
        "ordered-scan-routing-classify";
    public const int MaximumItems = 1_000;
    public const long MaximumItemBytes = 250_000_000;
    private const int MinimumVisualScoreBasisPoints = 6_500;
    private const int MinimumVisualMarginBasisPoints = 250;
    private const long MaximumTemplateReferenceSourceBytes =
        PreprocessingOptions.DefaultMaxInputBytes;
    private const long MaximumTemplateReferenceArtifactBytes =
        PreprocessingOptions.DefaultMaxNormalizedArtifactBytes;
    private const long MaximumTemplateReferencePixels =
        PreprocessingOptions.DefaultMaxTotalPixels;

    public static IEndpointRouteBuilder MapOrderedScanRoutingEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/ordered-scan-routing:classify-page",
                ClassifyPage)
            .WithTags("Ordered scan routing")
            .Accepts<byte[]>("application/pdf")
            .Produces<OrderedScanRoutingResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithMetadata(new RequestSizeLimitAttribute(MaximumItemBytes))
            .RequireAuthorization("upload")
            .RequireRateLimiting(ClassificationRateLimitPolicy);
        return endpoints;
    }

    private static async Task<IResult> ClassifyPage(
        HttpContext context,
        [FromQuery] string? clientItemId,
        [FromQuery] string? fileName,
        [FromQuery] long sizeBytes,
        [FromQuery] int inputOrdinal,
        OokiGraderDbContext db,
        IContentStore contentStore,
        IPreprocessingService preprocessingService,
        CancellationToken cancellationToken)
    {
        var item = new OrderedScanRoutingItem(
            clientItemId ?? string.Empty,
            fileName ?? string.Empty,
            sizeBytes,
            inputOrdinal);
        if (!IsValidItem(item)
            || inputOrdinal > MaximumItems
            || !IsPdfContentType(context.Request.ContentType)
            || context.Request.ContentLength is <= 0 or > MaximumItemBytes
            || context.Request.ContentLength != sizeBytes)
        {
            return InvalidRoutingRequest(context);
        }

        PreprocessedPage candidate;
        try
        {
            var processed = await preprocessingService.ProcessAsync(
                    context.Request.Body,
                    new PreprocessingInput(
                        "application/pdf",
                        item.FileName,
                        MaximumPages: 1),
                    cancellationToken)
                .ConfigureAwait(false);
            if (processed.Pages.Count != 1)
            {
                ClearPages(processed.Pages);
                return InvalidSinglePagePdf(context);
            }

            candidate = processed.Pages[0];
        }
        catch (PreprocessingException)
        {
            return InvalidSinglePagePdf(context);
        }

        try
        {
            var sessionSeeds = await LoadOpenSessionsAsync(db, cancellationToken)
                .ConfigureAwait(false);
            var publicSessions = sessionSeeds
                .Select(item => item.Session)
                .ToArray();
            var routable = sessionSeeds
                .Where(item => item.Session.ExpectedPageCount is > 0)
                .ToArray();
            if (routable.Length == 0)
            {
                return RoutingResponse(
                    context,
                    publicSessions,
                    Decision(
                        item,
                        "needsReview",
                        null,
                        null,
                        null,
                        [],
                        "NO_ROUTABLE_OPEN_SESSION"));
            }

            var visualMatches = new Dictionary<string, TemplateVisualMatch?>(
                StringComparer.Ordinal);
            foreach (var templateGroup in routable.GroupBy(
                         session => session.TemplateVersionId,
                         StringComparer.Ordinal))
            {
                var first = templateGroup.First();
                visualMatches[first.TemplateVersionId] =
                    await TryScoreTemplateAsync(
                            db,
                            contentStore,
                            preprocessingService,
                            first,
                            candidate,
                            cancellationToken)
                        .ConfigureAwait(false);
            }

            var decision = ClassifyByContent(
                item,
                routable,
                visualMatches);
            return RoutingResponse(context, publicSessions, decision);
        }
        finally
        {
            ClearPage(candidate);
        }
    }

    private static async Task<RoutingSessionSeed[]> LoadOpenSessionsAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken) =>
        await db.TestSessions
            .AsNoTracking()
            .Where(item => item.State == "open")
            .OrderBy(item => item.TestDate)
            .ThenBy(item => item.Id)
            .Select(item => new RoutingSessionSeed(
                new OrderedScanRoutingSession(
                    item.Id,
                    item.TitleOverride
                        ?? item.TemplateTitleSnapshot
                        ?? item.TemplateVersion.TestTemplate.Title,
                    item.TestDate,
                    item.ClassLabel,
                    item.TemplateGradeLabelSnapshot
                        ?? item.TemplateVersion.TestTemplate.GradeLabel,
                    item.TemplateSubjectSnapshot
                        ?? item.TemplateVersion.TestTemplate.Subject,
                    item.Course
                        ?? item.TemplateCourseSnapshot
                        ?? item.TemplateVersion.TestTemplate.Course,
                    item.TemplateVersion.ExpectedSubmissionPageCount),
                item.TemplateVersionId,
                item.TemplateVersion.OriginatingUnitId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    private static async Task<TemplateVisualMatch?> TryScoreTemplateAsync(
        OokiGraderDbContext db,
        IContentStore contentStore,
        IPreprocessingService preprocessingService,
        RoutingSessionSeed session,
        PreprocessedPage candidate,
        CancellationToken cancellationToken)
    {
        var sources = await db.TemplateSources
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == session.TemplateVersionId
                && (item.SourceRole == "blank_test"
                    || item.SourceRole == "contains_model_answers"
                    || item.SourceRole == "contains_non_model_answers"))
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .Select(item => new TemplateReferenceSeed(
                item.Id,
                item.SourceRole,
                item.DisplayName,
                item.Ordinal,
                item.UploadSessionId,
                item.FileReferenceId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedRole = sources.Any(item => item.SourceRole == "blank_test")
            ? "blank_test"
            : sources.Any(item => item.SourceRole == "contains_model_answers")
                ? "contains_model_answers"
                : "contains_non_model_answers";
        var selected = sources
            .Where(item => item.SourceRole == selectedRole)
            .ToArray();
        if (selected.Length == 0
            || selected.Any(item => item.FileReferenceId is null))
        {
            return null;
        }

        var referenceIds = selected
            .Select(item => item.FileReferenceId!)
            .ToArray();
        var references = await db.FileReferences
            .AsNoTracking()
            .Include(item => item.FileObject)
            .Where(item => referenceIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken)
            .ConfigureAwait(false);
        var pages = new List<PreprocessedPage>();
        long sourceBytes = 0;
        long artifactBytes = 0;
        long pixels = 0;
        try
        {
            foreach (var source in selected)
            {
                if (!references.TryGetValue(
                        source.FileReferenceId!,
                        out var reference)
                    || reference.FileObject.State != "available"
                    || reference.FileObject.VerifiedMime is not (
                        "application/pdf"
                        or "image/png"
                        or "image/jpeg"
                        or "image/tiff"
                        or "image/webp")
                    || reference.FileObject.Bytes <= 0
                    || reference.FileObject.Sha256.Length != 64
                    || !TryParseTemplateStorageClass(
                        reference.FileObject.StorageClass,
                        out var storageClass)
                    || !HasValidReferenceProvenance(
                        source,
                        reference,
                        session.OriginatingUnitId,
                        storageClass))
                {
                    return null;
                }

                sourceBytes = checked(sourceBytes + reference.FileObject.Bytes);
                var remainingArtifactBytes =
                    MaximumTemplateReferenceArtifactBytes - artifactBytes;
                var remainingPixels = MaximumTemplateReferencePixels - pixels;
                if (sourceBytes > MaximumTemplateReferenceSourceBytes
                    || remainingArtifactBytes <= 0
                    || remainingPixels <= 0)
                {
                    return null;
                }

                var locator = new ContentObjectLocator(
                    storageClass,
                    reference.FileObject.Sha256,
                    reference.FileObject.Bytes,
                    reference.FileObject.Extension);
                await using var stream = await contentStore.OpenReadAsync(
                        locator,
                        cancellationToken)
                    .ConfigureAwait(false);
                var processed = await preprocessingService.ProcessAsync(
                        stream,
                        new PreprocessingInput(
                            reference.FileObject.VerifiedMime,
                            source.DisplayName,
                            MaximumPages:
                                OrderedScanBatchService.MaximumSubmissionPages,
                            MaximumNormalizedArtifactBytes:
                                remainingArtifactBytes,
                            MaximumTotalPixels: remainingPixels),
                        cancellationToken)
                    .ConfigureAwait(false);
                pages.AddRange(processed.Pages);
                foreach (var page in processed.Pages)
                {
                    artifactBytes = checked(
                        artifactBytes
                        + page.NormalizedPng.Bytes.LongLength
                        + page.ThumbnailPng.Bytes.LongLength);
                    pixels = checked(pixels + ((long)page.Width * page.Height));
                }
            }

            if (pages.Count != session.Session.ExpectedPageCount)
            {
                return null;
            }

            VisualPageMatch? best = null;
            VisualPageMatch? second = null;
            for (var index = 0; index < pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var alignment = preprocessingService.AlignToReference(
                    candidate,
                    pages[index],
                    cancellationToken);
                var match = new VisualPageMatch(
                    index + 1,
                    alignment.State,
                    alignment.ScoreBasisPoints ?? -1);
                if (best is null || IsBetterVisualMatch(match, best))
                {
                    second = best;
                    best = match;
                }
                else if (second is null || IsBetterVisualMatch(match, second))
                {
                    second = match;
                }

                ClearTransientAlignmentPage(candidate, alignment);
            }

            if (best is null)
            {
                return null;
            }

            var pageAmbiguous = second is not null
                && best.ScoreBasisPoints - second.ScoreBasisPoints
                    < MinimumVisualMarginBasisPoints;
            int? detectedPage = best.State == "aligned"
                && best.ScoreBasisPoints >= MinimumVisualScoreBasisPoints
                && !pageAmbiguous
                    ? best.PageNumber
                    : null;
            return new TemplateVisualMatch(
                best.ScoreBasisPoints,
                detectedPage,
                pageAmbiguous,
                best.State);
        }
        catch (Exception exception) when (
            exception is IOException or PreprocessingException)
        {
            return null;
        }
        finally
        {
            ClearPages(pages);
        }
    }

    private static OrderedScanRoutingDecision ClassifyByContent(
        OrderedScanRoutingItem item,
        IReadOnlyList<RoutingSessionSeed> sessions,
        IReadOnlyDictionary<string, TemplateVisualMatch?> visualMatches)
    {
        var publicSessions = sessions.Select(session => session.Session).ToArray();
        var filenameScores = OrderedScanFilenameRouter.ScoreCandidates(
                item,
                publicSessions)
            .ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        var scores = sessions
            .Select(session =>
            {
                visualMatches.TryGetValue(
                    session.TemplateVersionId,
                    out var visual);
                filenameScores.TryGetValue(
                    session.Session.Id,
                    out var filename);
                var evidence = new List<string>();
                if (visual is null)
                {
                    evidence.Add("template_reference_unavailable");
                }
                else
                {
                    evidence.Add(visual.AlignmentState == "aligned"
                        ? "visual_alignment"
                        : "visual_alignment_failed");
                    if (visual.DetectedTemplatePageNumber is { } pageNumber)
                    {
                        evidence.Add($"visual_page_{pageNumber}");
                    }
                    if (visual.PageAmbiguous)
                    {
                        evidence.Add("visual_page_ambiguous");
                    }
                    if (visual.ScoreBasisPoints < MinimumVisualScoreBasisPoints)
                    {
                        evidence.Add("visual_weak");
                    }
                }
                if (filename is not null)
                {
                    evidence.AddRange(filename.Evidence);
                }

                return new CombinedRouteScore(
                    session,
                    visual,
                    filename?.ConfidenceBasisPoints ?? 0,
                    evidence);
            })
            .OrderByDescending(score => score.Visual?.ScoreBasisPoints ?? -1)
            .ThenByDescending(score => score.FilenameScoreBasisPoints)
            .ThenBy(score => score.Session.Session.Id, StringComparer.Ordinal)
            .ToArray();
        var candidates = scores
            .Take(3)
            .Select(score => new OrderedScanRoutingCandidate(
                score.Session.Session.Id,
                Math.Max(
                    score.Visual?.ScoreBasisPoints ?? 0,
                    score.FilenameScoreBasisPoints),
                score.Evidence))
            .ToArray();
        var withReferences = scores
            .Where(score => score.Visual is not null)
            .ToArray();
        if (withReferences.Length != scores.Length)
        {
            return Decision(
                item,
                "needsReview",
                null,
                null,
                null,
                candidates,
                "ROUTING_TEMPLATE_REFERENCE_UNAVAILABLE");
        }

        var bestVisual = withReferences[0];
        var visualContenders = withReferences
            .Where(score => score.Visual is { } visual
                && bestVisual.Visual!.ScoreBasisPoints
                    - visual.ScoreBasisPoints < MinimumVisualMarginBasisPoints)
            .ToArray();
        var visuallyStrong = bestVisual.Visual is
            {
                AlignmentState: "aligned",
                DetectedTemplatePageNumber: not null,
                ScoreBasisPoints: >= MinimumVisualScoreBasisPoints,
            };
        if (visuallyStrong && visualContenders.Length == 1)
        {
            return Decision(
                item,
                "routed",
                bestVisual.Session.Session.Id,
                bestVisual.Session.Session.ExpectedPageCount,
                bestVisual.Visual?.DetectedTemplatePageNumber,
                candidates,
                null);
        }

        var issueCode = visuallyStrong || bestVisual.Visual?.PageAmbiguous == true
            ? "ROUTING_VISUAL_AMBIGUOUS"
            : "ROUTING_VISUAL_WEAK";
        return Decision(
            item,
            "needsReview",
            null,
            null,
            null,
            candidates,
            issueCode);
    }

    private static bool HasValidReferenceProvenance(
        TemplateReferenceSeed source,
        FileReferenceEntity reference,
        string? originatingUnitId,
        ContentStorageClass storageClass)
    {
        var uploadedSource = reference.OwnerType == "upload_session"
            && reference.OwnerId == source.UploadSessionId
            && reference.Purpose == "template_source"
            && storageClass == ContentStorageClass.TemplateSource;
        var derivedSource = originatingUnitId is { } unitId
            && reference.OwnerType == "template_generation_unit"
            && reference.OwnerId == unitId
            && reference.Purpose == "derived_source"
            && storageClass == ContentStorageClass.TemplateDerived;
        return uploadedSource || derivedSource;
    }

    private static bool TryParseTemplateStorageClass(
        string value,
        out ContentStorageClass storageClass)
    {
        if (Enum.TryParse(value, ignoreCase: false, out storageClass)
            && storageClass is ContentStorageClass.TemplateSource
                or ContentStorageClass.TemplateDerived)
        {
            return true;
        }

        storageClass = default;
        return false;
    }

    private static bool IsBetterVisualMatch(
        VisualPageMatch candidate,
        VisualPageMatch current) =>
        candidate.ScoreBasisPoints > current.ScoreBasisPoints
        || (candidate.ScoreBasisPoints == current.ScoreBasisPoints
            && candidate.PageNumber < current.PageNumber);

    private static void ClearTransientAlignmentPage(
        PreprocessedPage candidate,
        PageAlignmentResult alignment)
    {
        if (!ReferenceEquals(
                candidate.NormalizedPng.Bytes,
                alignment.Page.NormalizedPng.Bytes))
        {
            Array.Clear(alignment.Page.NormalizedPng.Bytes);
        }

        if (!ReferenceEquals(
                candidate.ThumbnailPng.Bytes,
                alignment.Page.ThumbnailPng.Bytes))
        {
            Array.Clear(alignment.Page.ThumbnailPng.Bytes);
        }
    }

    private static void ClearPages(IEnumerable<PreprocessedPage> pages)
    {
        foreach (var page in pages)
        {
            ClearPage(page);
        }
    }

    private static void ClearPage(PreprocessedPage page)
    {
        Array.Clear(page.NormalizedPng.Bytes);
        Array.Clear(page.ThumbnailPng.Bytes);
    }

    private static bool IsPdfContentType(string? contentType) =>
        string.Equals(
            contentType?.Split(';', 2)[0].Trim(),
            "application/pdf",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsValidItem(OrderedScanRoutingItem item) =>
        item.InputOrdinal > 0
        && !string.IsNullOrWhiteSpace(item.ClientItemId)
        && item.ClientItemId.Length <= 128
        && !string.IsNullOrWhiteSpace(item.FileName)
        && item.FileName.Length <= 500
        && item.SizeBytes is > 0 and <= MaximumItemBytes
        && string.Equals(
            item.FileName.Trim(),
            Path.GetFileName(item.FileName.Trim()),
            StringComparison.Ordinal)
        && string.Equals(
            Path.GetExtension(item.FileName),
            ".pdf",
            StringComparison.OrdinalIgnoreCase);

    private static IResult InvalidRoutingRequest(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "ORDERED_SCAN_ROUTING_INVALID",
            "答案を仕分けできません",
            "1ページPDFをファイル名順の連続した順番で指定してください。");

    private static IResult InvalidSinglePagePdf(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status422UnprocessableEntity,
            "ORDERED_SCAN_ROUTING_PDF_INVALID",
            "答案PDFを仕分けできません",
            "暗号化されていない1ページPDFを指定してください。");

    private static IResult RoutingResponse(
        HttpContext context,
        IReadOnlyList<OrderedScanRoutingSession> sessions,
        OrderedScanRoutingDecision decision)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(new OrderedScanRoutingResponse(
            "server_visual_alignment_v1",
            sessions,
            [decision]));
    }

    private static OrderedScanRoutingDecision Decision(
        OrderedScanRoutingItem item,
        string state,
        string? destinationSessionId,
        int? expectedPageCount,
        int? detectedTemplatePageNumber,
        IReadOnlyList<OrderedScanRoutingCandidate> candidates,
        string? issueCode) => new(
            item.ClientItemId,
            item.InputOrdinal,
            state,
            destinationSessionId,
            expectedPageCount,
            detectedTemplatePageNumber,
            candidates,
            issueCode);

    private sealed record RoutingSessionSeed(
        OrderedScanRoutingSession Session,
        string TemplateVersionId,
        string? OriginatingUnitId);

    private sealed record TemplateReferenceSeed(
        string Id,
        string SourceRole,
        string DisplayName,
        int Ordinal,
        string UploadSessionId,
        string? FileReferenceId);

    private sealed record VisualPageMatch(
        int PageNumber,
        string State,
        int ScoreBasisPoints);

    private sealed record TemplateVisualMatch(
        int ScoreBasisPoints,
        int? DetectedTemplatePageNumber,
        bool PageAmbiguous,
        string AlignmentState);

    private sealed record CombinedRouteScore(
        RoutingSessionSeed Session,
        TemplateVisualMatch? Visual,
        int FilenameScoreBasisPoints,
        IReadOnlyList<string> Evidence);
}

public sealed record OrderedScanRoutingItem(
    string ClientItemId,
    string FileName,
    long SizeBytes,
    int InputOrdinal);

public sealed record OrderedScanRoutingSession(
    string Id,
    string Title,
    DateOnly TestDate,
    string? ClassLabel,
    string? GradeLabel,
    string? Subject,
    string? Course,
    int? ExpectedPageCount);

public sealed record OrderedScanRoutingCandidate(
    string SessionId,
    int ConfidenceBasisPoints,
    IReadOnlyList<string> Evidence);

public sealed record OrderedScanRoutingDecision(
    string ClientItemId,
    int InputOrdinal,
    string State,
    string? DestinationSessionId,
    int? ExpectedPageCount,
    int? DetectedTemplatePageNumber,
    IReadOnlyList<OrderedScanRoutingCandidate> Candidates,
    string? IssueCode);

public sealed record OrderedScanRoutingResponse(
    string ClassificationMode,
    IReadOnlyList<OrderedScanRoutingSession> OpenSessions,
    IReadOnlyList<OrderedScanRoutingDecision> Items);

internal static class OrderedScanFilenameRouter
{
    public static IReadOnlyList<OrderedScanRoutingCandidate> ScoreCandidates(
        OrderedScanRoutingItem item,
        IReadOnlyList<OrderedScanRoutingSession> sessions)
    {
        var normalizedFileName = Normalize(
            Path.GetFileNameWithoutExtension(item.FileName));
        return sessions
            .Select(session => Score(normalizedFileName, session))
            .Where(candidate => candidate.ConfidenceBasisPoints > 0)
            .OrderByDescending(candidate => candidate.ConfidenceBasisPoints)
            .ThenBy(candidate => candidate.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static OrderedScanRoutingCandidate Score(
        string normalizedFileName,
        OrderedScanRoutingSession session)
    {
        var score = 0;
        var evidence = new List<string>();
        Add(session.Title, 5_000, "title");
        Add(session.ClassLabel, 2_500, "class");
        Add(session.GradeLabel, 1_500, "grade");
        Add(session.Subject, 1_000, "subject");
        Add(session.Course, 1_000, "course");
        return new OrderedScanRoutingCandidate(
            session.Id,
            Math.Clamp(score, 0, 9_900),
            evidence);

        void Add(string? value, int weight, string label)
        {
            var normalized = Normalize(value);
            if (normalized.Length < 2
                || !normalizedFileName.Contains(
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            score += weight;
            evidence.Add($"filename_{label}");
        }
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var source = value.Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
        var result = new StringBuilder(source.Length);
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                result.Append(rune.ToString());
            }
        }
        return result.ToString();
    }
}
