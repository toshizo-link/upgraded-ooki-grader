using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Jobs;

public sealed record SubmissionPreprocessingWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumAlignmentSources { get; init; } = 16;
    public int MaximumAlignmentReferencePages { get; init; } = 1_000;
    public long MaximumAlignmentReferenceBytes { get; init; } =
        250L * 1024 * 1024;
    public long MaximumAlignmentReferencePixels { get; init; } =
        250_000_000;

    // Retained only so older configuration files remain loadable. Full-page AI
    // processing no longer creates coordinate-based crops.
    public int CropMarginMillionths { get; init; }

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(1)
            || LeaseDuration > TimeSpan.FromHours(2)
            || MaximumAlignmentSources is < 1 or > 64
            || MaximumAlignmentReferencePages is < 1 or > 2_000
            || MaximumAlignmentReferenceBytes is < 1 or > 1_000_000_000
            || MaximumAlignmentReferencePixels is < 1 or > 1_000_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SubmissionPreprocessingWorkerOptions),
                "One or more submission preprocessing worker options are invalid.");
        }
    }
}

public sealed partial class SubmissionPreprocessingWorker : BackgroundService
{
    public const string JobType = "submission.preprocess";

    private const string DerivedRetentionClass = "submitted_scan_derived";
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };

    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IContentStore _contentStore;
    private readonly IPreprocessingService _preprocessingService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubmissionPreprocessingWorker> _logger;
    private readonly SubmissionPreprocessingWorkerOptions _options;
    private readonly string _workerId = $"submission-preprocess-{Guid.NewGuid():N}";

    public SubmissionPreprocessingWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IPreprocessingService preprocessingService,
        TimeProvider timeProvider,
        IOptions<SubmissionPreprocessingWorkerOptions> options,
        ILogger<SubmissionPreprocessingWorker> logger)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _contentStore = contentStore;
        _preprocessingService = preprocessingService;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        _options.Validate();
    }

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        try
        {
            var claim = await ClaimSubmissionAsync(lease, cancellationToken)
                .ConfigureAwait(false);
            if (claim is null)
            {
                return true;
            }

            var alignmentSources = await LoadAlignmentSourcesAsync(
                    claim.TemplateVersionId,
                    cancellationToken)
                .ConfigureAwait(false);
            var output = await PrepareOutputAsync(
                    claim,
                    alignmentSources,
                    cancellationToken)
                .ConfigureAwait(false);
            await PersistOutputAsync(lease, claim, output, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PreprocessingException exception)
        {
            var errorCode = $"preprocessing_{exception.Code}";
            LogJobFailure(lease.Id, errorCode, exception.GetType().Name);
            await RecordFailureAsync(
                    lease,
                    errorCode,
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JobHandlingException exception)
        {
            LogJobFailure(
                lease.Id,
                exception.ErrorCode,
                exception.GetType().Name);
            await RecordFailureAsync(
                    lease,
                    exception.ErrorCode,
                    exception.IsPermanent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            const string errorCode = "preprocessing_worker_error";
            LogJobFailure(lease.Id, errorCode, exception.GetType().Name);
            await RecordFailureAsync(
                    lease,
                    errorCode,
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item =>
                    item.Type == JobType
                    && item.AttemptCount < item.MaxAttempts
                    && ((item.State == "queued" && item.NextAttemptAt <= now)
                        || (item.State == "retry_waiting"
                            && item.NextAttemptAt <= now)
                        || (item.State == "leased"
                            && item.LeaseExpiresAt <= now)))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            job.State = "leased";
            job.LeaseOwner = _workerId;
            job.LeaseExpiresAt = now.Add(_options.LeaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 500);
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.SchemaVersion,
                job.PayloadJson,
                job.CorrelationId,
                job.Revision);
        }, cancellationToken);
    }

    private Task<SubmissionClaim?> ClaimSubmissionAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            var payload = DeserializePayload(lease.PayloadJson);
            var submission = await db.Submissions
                .Include(item => item.TestSession)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("preprocessing_submission_missing");

            if (submission.PreprocessingManifestHash is not null)
            {
                var pageCount = await db.Set<SubmissionPageEntity>()
                    .AsNoTracking()
                    .CountAsync(
                        item => item.SubmissionId == submission.Id,
                        token)
                    .ConfigureAwait(false);
                if (pageCount > 0 && pageCount == submission.PageCount)
                {
                    CompleteJob(job, _timeProvider.GetUtcNow());
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    return null;
                }

                throw Permanent("preprocessing_persisted_output_inconsistent");
            }

            if (submission.State is not (
                    "validating"
                    or "preprocessing"
                    or "needs_attention"
                    or "needs_name_review")
                || submission.ScanPayloadState != "scan_available"
                || submission.OriginalFileObjectId is null
                || !QualityWasAccepted(submission.QualitySummaryJson))
            {
                throw Permanent("preprocessing_input_invalid");
            }

            var sourceObject = await db.FileObjects
                .SingleOrDefaultAsync(
                    item => item.Id == submission.OriginalFileObjectId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("preprocessing_source_missing");
            if (sourceObject.State != "available")
            {
                throw Permanent("preprocessing_source_unavailable");
            }

            var sourceStorageClass = ParseOriginalStorageClass(
                sourceObject.StorageClass);
            submission.State = "preprocessing";
            await db.SaveChangesAsync(token).ConfigureAwait(false);

            return new SubmissionClaim(
                submission.Id,
                submission.Revision,
                submission.OriginalFileObjectId,
                submission.TestSession.TemplateVersionId,
                new ContentObjectLocator(
                    sourceStorageClass,
                    sourceObject.Sha256,
                    sourceObject.Bytes,
                    sourceObject.Extension),
                sourceObject.VerifiedMime,
                submission.OriginalFileName,
                submission.UploadCompletedAt ?? submission.CreatedAt);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<AlignmentSource>>
        LoadAlignmentSourcesAsync(
            string templateVersionId,
            CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidates = await db.TemplateSources
            .AsNoTracking()
            .Where(item =>
                item.TemplateVersionId == templateVersionId
                && (item.SourceRole == "blank_test"
                    || item.SourceRole == "contains_model_answers"
                    || item.SourceRole == "contains_non_model_answers"))
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .Take(_options.MaximumAlignmentSources + 1)
            .Select(item => new
            {
                item.Id,
                item.UploadSessionId,
                item.FileReferenceId,
                item.SourceRole,
                item.DisplayName,
                item.Ordinal,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Length == 0)
        {
            return [];
        }
        if (candidates.Length > _options.MaximumAlignmentSources)
        {
            throw Permanent("preprocessing_alignment_source_limit");
        }

        var selectedRole = candidates.Any(
            item => item.SourceRole == "blank_test")
                ? "blank_test"
                : candidates.Any(
                    item => item.SourceRole == "contains_model_answers")
                    ? "contains_model_answers"
                    : "contains_non_model_answers";
        var selected = candidates
            .Where(item => item.SourceRole == selectedRole)
            .ToArray();
        if (selected.Any(item => item.FileReferenceId is null))
        {
            throw Permanent("preprocessing_alignment_reference_invalid");
        }

        var referenceIds = selected
            .Select(item => item.FileReferenceId!)
            .ToArray();
        var references = await db.FileReferences
            .AsNoTracking()
            .Include(item => item.FileObject)
            .Where(item => referenceIds.Contains(item.Id))
            .ToDictionaryAsync(
                item => item.Id,
                StringComparer.Ordinal,
                cancellationToken)
            .ConfigureAwait(false);
        var result = new List<AlignmentSource>(selected.Length);
        long totalBytes = 0;
        foreach (var source in selected)
        {
            if (!references.TryGetValue(
                    source.FileReferenceId!,
                    out var reference)
                || reference.OwnerType != "upload_session"
                || reference.OwnerId != source.UploadSessionId
                || reference.Purpose != "template_source"
                || reference.FileObject.State != "available"
                || reference.FileObject.StorageClass
                    != ContentStorageClass.TemplateSource.ToString()
                || reference.FileObject.VerifiedMime is not (
                    "application/pdf"
                    or "image/jpeg"
                    or "image/png"
                    or "image/tiff"
                    or "image/webp")
                || reference.FileObject.Bytes <= 0
                || reference.FileObject.Sha256.Length != 64)
            {
                throw Permanent("preprocessing_alignment_reference_invalid");
            }

            totalBytes = checked(
                totalBytes + reference.FileObject.Bytes);
            if (totalBytes > _options.MaximumAlignmentReferenceBytes)
            {
                throw Permanent("preprocessing_alignment_reference_limit");
            }

            result.Add(new AlignmentSource(
                source.Id,
                source.SourceRole,
                source.Ordinal,
                source.DisplayName,
                reference.FileObject.VerifiedMime,
                new ContentObjectLocator(
                    ContentStorageClass.TemplateSource,
                    reference.FileObject.Sha256,
                    reference.FileObject.Bytes,
                    reference.FileObject.Extension)));
        }

        return result;
    }

    private async Task<PreparedOutput> PrepareOutputAsync(
        SubmissionClaim claim,
        IReadOnlyList<AlignmentSource> alignmentSources,
        CancellationToken cancellationToken)
    {
        await using var source = await _contentStore
            .OpenReadAsync(claim.SourceLocator, cancellationToken)
            .ConfigureAwait(false);
        var result = await _preprocessingService
            .ProcessAsync(
                source,
                new PreprocessingInput(claim.VerifiedMime, claim.SourceName),
                cancellationToken)
            .ConfigureAwait(false);
        var referencePages = new List<PreprocessedPage>();
        long referencePixels = 0;
        foreach (var alignmentSource in alignmentSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var referenceStream = await _contentStore
                .OpenReadAsync(
                    alignmentSource.Locator,
                    cancellationToken)
                .ConfigureAwait(false);
            var referenceResult = await _preprocessingService
                .ProcessAsync(
                    referenceStream,
                    new PreprocessingInput(
                        alignmentSource.VerifiedMime,
                        alignmentSource.DisplayName),
                    cancellationToken)
                .ConfigureAwait(false);
            referencePages.AddRange(referenceResult.Pages);
            if (referencePages.Count > _options.MaximumAlignmentReferencePages)
            {
                throw Permanent("preprocessing_alignment_reference_limit");
            }

            foreach (var referencePage in referenceResult.Pages)
            {
                referencePixels = checked(
                    referencePixels
                    + ((long)referencePage.Width * referencePage.Height));
                if (referencePixels
                    > _options.MaximumAlignmentReferencePixels)
                {
                    throw Permanent(
                        "preprocessing_alignment_reference_limit");
                }
            }
        }

        var alignments = new List<PageAlignmentResult>(result.Pages.Count);
        for (var index = 0; index < result.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = result.Pages[index];
            if (alignmentSources.Count == 0)
            {
                alignments.Add(PageAlignmentResult.NotConfigured(page));
                continue;
            }

            if (index >= referencePages.Count)
            {
                alignments.Add(new PageAlignmentResult(
                    page,
                    "failed",
                    0,
                    0,
                    0,
                    0,
                    null));
                continue;
            }

            alignments.Add(_preprocessingService.AlignToReference(
                page,
                referencePages[index],
                cancellationToken));
        }

        var alignedPages = alignments
            .Select(item => item.Page)
            .ToArray();
        var repeatedPages = Fingerprinting.FindRepeatedPages(
            alignedPages,
            perceptualHammingThreshold: 4);
        var alignedResult = result with
        {
            Pages = alignedPages,
            RepeatedPages = repeatedPages,
            ManifestSha256 = ManifestHasher.Compute(
                result.PipelineVersion,
                result.InputSha256,
                result.VerifiedMimeType,
                alignedPages,
                repeatedPages,
                alignments,
                referencePages.Select(
                    item => item.NormalizedPng.Sha256)),
        };
        var pageCountMismatch = alignmentSources.Count > 0
            && referencePages.Count != alignedPages.Length;
        var preparedPages = new List<PreparedPage>(alignedPages.Length);
        foreach (var alignment in alignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = alignment.Page;
            preparedPages.Add(new PreparedPage(
                page,
                alignment,
                await PutArtifactAsync(
                        page.NormalizedPng,
                        cancellationToken)
                    .ConfigureAwait(false),
                await PutArtifactAsync(
                        page.ThumbnailPng,
                        cancellationToken)
                    .ConfigureAwait(false)));
        }

        return new PreparedOutput(
            alignedResult,
            preparedPages,
            referencePages.Count,
            pageCountMismatch);
    }

    private async Task<StoredArtifact> PutArtifactAsync(
        ImageArtifact artifact,
        CancellationToken cancellationToken)
    {
        await using var source = new MemoryStream(
            artifact.Bytes,
            writable: false);
        var write = await _contentStore
            .PutAsync(
                source,
                ContentStorageClass.ManagedScanDerived,
                artifact.Extension,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                write.Locator.Sha256,
                artifact.Sha256,
                StringComparison.Ordinal)
            || write.Locator.Bytes != artifact.Bytes.LongLength
            || !string.Equals(
                write.Locator.Extension,
                artifact.Extension,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "The content store returned inconsistent derived-object metadata.");
        }

        return new StoredArtifact(artifact, write);
    }

    private Task PersistOutputAsync(
        JobLease lease,
        SubmissionClaim claim,
        PreparedOutput output,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            var submission = await db.Submissions
                .SingleOrDefaultAsync(item => item.Id == claim.SubmissionId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("preprocessing_submission_missing");
            if (submission.Revision != claim.SubmissionRevision
                || submission.OriginalFileObjectId != claim.OriginalFileObjectId
                || submission.State != "preprocessing"
                || submission.PreprocessingManifestHash is not null)
            {
                throw Permanent("preprocessing_submission_changed");
            }

            if (await db.Set<SubmissionPageEntity>()
                    .AsNoTracking()
                    .AnyAsync(
                        item => item.SubmissionId == submission.Id,
                        token)
                    .ConfigureAwait(false)
                || await db.Set<SubmissionArtifactEntity>()
                    .AsNoTracking()
                    .AnyAsync(
                        item => item.SubmissionId == submission.Id,
                        token)
                    .ConfigureAwait(false))
            {
                throw Permanent("preprocessing_existing_output_conflict");
            }

            var allStoredArtifacts = output.Pages
                .SelectMany(item => new[]
                {
                    item.Normalized,
                    item.Thumbnail,
                })
                .ToArray();
            var fileObjects = await ResolveFileObjectsAsync(
                    db,
                    allStoredArtifacts,
                    _timeProvider.GetUtcNow(),
                    token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var repeatedByPage = output.Result.RepeatedPages
                .ToDictionary(item => item.DuplicatePageNumber);

            foreach (var preparedPage in output.Pages)
            {
                var pageId = UlidId.New(now);
                var normalizedReferenceId = AddReference(
                    db,
                    fileObjects[preparedPage.Normalized.Image.Sha256],
                    "submission_page",
                    pageId,
                    "normalized_page",
                    claim.RetentionAnchorAt,
                    now);
                var thumbnailReferenceId = AddReference(
                    db,
                    fileObjects[preparedPage.Thumbnail.Image.Sha256],
                    "submission_page",
                    pageId,
                    "page_thumbnail",
                    claim.RetentionAnchorAt,
                    now);
                repeatedByPage.TryGetValue(
                    preparedPage.Page.PageNumber,
                    out var repeated);
                db.Add(new SubmissionPageEntity
                {
                    Id = pageId,
                    SubmissionId = submission.Id,
                    PageNumber = preparedPage.Page.PageNumber,
                    NormalizedFileReferenceId = normalizedReferenceId,
                    ThumbnailFileReferenceId = thumbnailReferenceId,
                    WidthPixels = preparedPage.Page.Width,
                    HeightPixels = preparedPage.Page.Height,
                    RotationDegrees =
                        preparedPage.Alignment.RotationDegrees,
                    SourceSha256 = output.Result.InputSha256,
                    NormalizedSha256 =
                        preparedPage.Page.Fingerprint.ExactSha256,
                    DifferenceHash =
                        preparedPage.Page.Fingerprint.PerceptualHash,
                    PerceptualHash =
                        preparedPage.Page.Fingerprint.PerceptualHash,
                    QualityState = QualityState(preparedPage.Page.Quality),
                    BlurBasisPoints =
                        BlurBasisPoints(preparedPage.Page.Quality),
                    ContrastBasisPoints = ToBasisPoints(
                        preparedPage.Page.Quality.ContrastP95
                        - preparedPage.Page.Quality.ContrastP05),
                    BrightnessBasisPoints = ToBasisPoints(
                        preparedPage.Page.Quality.MeanLuminance),
                    InkCoverageBasisPoints = ToBasisPoints(
                        1 - preparedPage.Page.Quality.LightPixelFraction),
                    AlignmentState = preparedPage.Alignment.State,
                    AlignmentScoreBasisPoints =
                        preparedPage.Alignment.ScoreBasisPoints,
                    RepeatedPageNumber = repeated?.FirstPageNumber,
                    CreatedAt = now,
                });
            }

            submission.PreprocessingPipelineVersion =
                output.Result.PipelineVersion;
            submission.PreprocessingManifestHash =
                output.Result.ManifestSha256;
            submission.PreprocessingCompletedAt = now;
            submission.PageCount = output.Result.Pages.Count;
            submission.QualitySummaryJson = BuildQualitySummary(output);
            submission.State = output.HasBlockingAlignmentFailure
                ? "needs_attention"
                : "needs_name_review";
            AddAudit(
                db,
                now,
                lease.CorrelationId,
                "submission.preprocessed",
                submission.Id,
                "local_preprocessing_completed",
                JsonSerializer.Serialize(new
                {
                    pageCount = output.Result.Pages.Count,
                    artifactCount = 0,
                    warningPageCount = output.Result.Pages.Count(
                        item => item.Quality.Warnings.Count > 0),
                    repeatedPageCount = output.Result.RepeatedPages.Count,
                    alignmentWarningPageCount = output.Pages.Count(
                        item => item.Alignment.State == "warning"),
                    alignmentFailedPageCount = output.Pages.Count(
                        item => item.Alignment.State == "failed"),
                    expectedPageCount = output.AlignmentReferencePageCount,
                    pageCountMismatch = output.PageCountMismatch,
                    manifestSha256 = output.Result.ManifestSha256,
                }));
            AddStatusOutbox(
                db,
                now,
                lease.CorrelationId,
                submission.Id,
                submission.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task<Dictionary<string, FileObjectEntity>>
        ResolveFileObjectsAsync(
            OokiGraderDbContext db,
            IReadOnlyCollection<StoredArtifact> artifacts,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var unique = artifacts
            .GroupBy(item => item.Image.Sha256, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var hashes = unique
            .Select(item => item.Image.Sha256)
            .ToArray();
        var storageClass = ContentStorageClass.ManagedScanDerived.ToString();
        var existing = new List<FileObjectEntity>();
        foreach (var hashBatch in hashes.Chunk(400))
        {
            existing.AddRange(await db.FileObjects
                .Where(item =>
                    item.StorageClass == storageClass
                    && hashBatch.Contains(item.Sha256))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var byHash = existing.ToDictionary(
            item => item.Sha256,
            StringComparer.Ordinal);
        foreach (var artifact in unique)
        {
            if (byHash.TryGetValue(artifact.Image.Sha256, out var fileObject))
            {
                ValidateExistingFileObject(fileObject, artifact);
                continue;
            }

            fileObject = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = artifact.Image.Sha256,
                Bytes = artifact.Write.Locator.Bytes,
                VerifiedMime = artifact.Image.MimeType,
                Extension = artifact.Write.Locator.Extension,
                RelativeObjectPath = artifact.Write.RelativePath,
                StorageClass = storageClass,
                RetentionClass = DerivedRetentionClass,
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 0,
            };
            db.FileObjects.Add(fileObject);
            byHash.Add(fileObject.Sha256, fileObject);
        }

        return byHash;
    }

    private static void ValidateExistingFileObject(
        FileObjectEntity fileObject,
        StoredArtifact artifact)
    {
        if (fileObject.State != "available"
            || fileObject.Bytes != artifact.Write.Locator.Bytes
            || fileObject.VerifiedMime != artifact.Image.MimeType
            || fileObject.Extension != artifact.Write.Locator.Extension
            || fileObject.RelativeObjectPath != artifact.Write.RelativePath
            || !fileObject.ManagedScanBytes)
        {
            throw Permanent("preprocessing_derived_object_conflict");
        }
    }

    private static string AddReference(
        OokiGraderDbContext db,
        FileObjectEntity fileObject,
        string ownerType,
        string ownerId,
        string purpose,
        DateTimeOffset retentionAnchorAt,
        DateTimeOffset now)
    {
        var id = UlidId.New(now);
        db.FileReferences.Add(new FileReferenceEntity
        {
            Id = id,
            FileObjectId = fileObject.Id,
            OwnerType = ownerType,
            OwnerId = ownerId,
            Purpose = purpose,
            RetentionAnchorAt = retentionAnchorAt,
            CreatedAt = now,
        });
        fileObject.ReferenceCountCache = checked(
            fileObject.ReferenceCountCache + 1);
        return id;
    }

    private Task RecordFailureAsync(
        JobLease lease,
        string errorCode,
        bool isPermanent,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == lease.Id, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId
                || job.Revision != lease.Revision)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            var terminal = isPermanent || job.AttemptCount >= job.MaxAttempts;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = errorCode;
            job.SafeErrorDetail =
                "The submission could not be safely preprocessed.";
            if (terminal)
            {
                job.State = "failed";
                job.CompletedAt = now;
                var submissionId = TryGetSubmissionId(lease.PayloadJson);
                if (submissionId is not null)
                {
                    var submission = await db.Submissions
                        .SingleOrDefaultAsync(
                            item => item.Id == submissionId,
                            token)
                        .ConfigureAwait(false);
                    if (submission is not null
                        && submission.PreprocessingManifestHash is null
                        && submission.State is (
                            "validating"
                            or "preprocessing"
                            or "needs_attention"
                            or "needs_name_review"))
                    {
                        submission.State = "needs_attention";
                        AddAudit(
                            db,
                            now,
                            lease.CorrelationId,
                            "submission.preprocessing_failed",
                            submission.Id,
                            errorCode,
                            safeMetadataJson: null);
                        AddStatusOutbox(
                            db,
                            now,
                            lease.CorrelationId,
                            submission.Id,
                            submission.State);
                    }
                }
            }
            else
            {
                job.State = "retry_waiting";
                job.NextAttemptAt = now.Add(RetryDelay(job.AttemptCount));
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == lease.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("preprocessing_job_missing");
        if (job.Type != JobType
            || job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw Permanent("preprocessing_job_lease_lost");
        }

        if (job.SchemaVersion != 1 || lease.SchemaVersion != 1)
        {
            throw Permanent("preprocessing_job_schema_unsupported");
        }

        return job;
    }

    private static PreprocessPayload DeserializePayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<PreprocessPayload>(
                    json,
                    PayloadSerializerOptions)
                ?? throw Permanent("preprocessing_payload_invalid");
            if (string.IsNullOrWhiteSpace(payload.SubmissionId))
            {
                throw Permanent("preprocessing_payload_invalid");
            }

            return payload;
        }
        catch (JsonException)
        {
            throw Permanent("preprocessing_payload_invalid");
        }
    }

    private static string? TryGetSubmissionId(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<PreprocessPayload>(
                json,
                PayloadSerializerOptions);
            return string.IsNullOrWhiteSpace(payload?.SubmissionId)
                ? null
                : payload.SubmissionId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContentStorageClass ParseOriginalStorageClass(string value)
    {
        if (Enum.TryParse<ContentStorageClass>(
                value,
                ignoreCase: false,
                out var storageClass)
            && storageClass == ContentStorageClass.ManagedScanOriginal)
        {
            return storageClass;
        }

        return value == "managed_scan_original"
            ? ContentStorageClass.ManagedScanOriginal
            : throw Permanent("preprocessing_source_storage_class_invalid");
    }

    private static bool QualityWasAccepted(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("pipeline", out var pipeline)
                && pipeline.ValueEquals("safe-ingest-v1")
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueEquals("accepted");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildQualitySummary(PreparedOutput output)
    {
        var result = output.Result;
        var alignmentByPage = output.Pages.ToDictionary(
            item => item.Page.PageNumber,
            item => item.Alignment);
        return JsonSerializer.Serialize(new
        {
            pipeline = result.PipelineVersion,
            status = output.HasBlockingAlignmentFailure
                ? "needs_attention"
                : "completed",
            inputSha256 = result.InputSha256,
            manifestSha256 = result.ManifestSha256,
            pageCount = result.Pages.Count,
            expectedPageCount = output.AlignmentReferencePageCount == 0
                ? (int?)null
                : output.AlignmentReferencePageCount,
            pageCountMismatch = output.PageCountMismatch,
            warningPageCount = result.Pages.Count(
                item => item.Quality.Warnings.Count > 0),
            probablyBlankPageCount = result.Pages.Count(
                item => item.Quality.IsProbablyBlank),
            pages = result.Pages.Select(item => new
            {
                item.PageNumber,
                qualityState = QualityState(item.Quality),
                item.Quality.MeanLuminance,
                contrast = item.Quality.ContrastP95
                    - item.Quality.ContrastP05,
                item.Quality.DarkPixelFraction,
                item.Quality.LightPixelFraction,
                item.Quality.EdgeInkFraction,
                item.Quality.LaplacianVariance,
                item.Quality.IsProbablyBlank,
                alignment = new
                {
                    state = alignmentByPage[item.PageNumber].State,
                    scoreBasisPoints =
                        alignmentByPage[item.PageNumber].ScoreBasisPoints,
                    rotationDegrees =
                        alignmentByPage[item.PageNumber].RotationDegrees,
                    sourceOffsetXMillionths =
                        alignmentByPage[item.PageNumber].OffsetXMillionths,
                    sourceOffsetYMillionths =
                        alignmentByPage[item.PageNumber].OffsetYMillionths,
                    referenceSha256 =
                        alignmentByPage[item.PageNumber].ReferenceSha256,
                },
                warnings = item.Quality.Warnings
                    .OrderBy(warning => warning, StringComparer.Ordinal),
            }),
            repeatedPages = result.RepeatedPages.Select(item => new
            {
                item.FirstPageNumber,
                item.DuplicatePageNumber,
                kind = item.Kind.ToString(),
                item.HammingDistance,
            }),
        });
    }

    private static string QualityState(PageQualityMetrics quality)
    {
        if (quality.IsProbablyBlank)
        {
            return "warning";
        }

        return quality.Warnings.Count == 0 ? "accepted" : "warning";
    }

    private static int BlurBasisPoints(PageQualityMetrics quality)
    {
        const double referenceVariance = 45d;
        return ToBasisPoints(
            1d / (1d + (quality.LaplacianVariance / referenceVariance)));
    }

    private static int ToBasisPoints(double fraction)
    {
        return (int)Math.Round(
            Math.Clamp(fraction, 0, 1) * 10_000,
            MidpointRounding.AwayFromZero);
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        return attemptCount switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            3 => TimeSpan.FromMinutes(10),
            4 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2),
        };
    }

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset completedAt)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.CompletedAt = completedAt;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string eventType,
        string submissionId,
        string reasonCode,
        string? safeMetadataJson)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = eventType,
            ObjectType = "submission",
            ObjectId = submissionId,
            Outcome = eventType.EndsWith(
                "_failed",
                StringComparison.Ordinal)
                ? "failed"
                : "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            SafeMetadataJson = safeMetadataJson,
        });
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string submissionId,
        string state)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = "submission.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId,
                state,
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static JobHandlingException Permanent(string errorCode)
    {
        return new JobHandlingException(errorCode, isPermanent: true);
    }

    [LoggerMessage(
        EventId = 7151,
        Level = LogLevel.Warning,
        Message =
            "Submission preprocessing job {JobId} failed with {ErrorCode} " +
            "({ExceptionType}).")]
    private partial void LogJobFailure(
        string jobId,
        string errorCode,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        string? CorrelationId,
        long Revision);

    private sealed record PreprocessPayload(string SubmissionId);

    private sealed record SubmissionClaim(
        string SubmissionId,
        long SubmissionRevision,
        string OriginalFileObjectId,
        string TemplateVersionId,
        ContentObjectLocator SourceLocator,
        string VerifiedMime,
        string? SourceName,
        DateTimeOffset RetentionAnchorAt);

    private sealed record AlignmentSource(
        string Id,
        string SourceRole,
        int Ordinal,
        string DisplayName,
        string VerifiedMime,
        ContentObjectLocator Locator);

    private sealed record StoredArtifact(
        ImageArtifact Image,
        ContentWriteResult Write);

    private sealed record PreparedPage(
        PreprocessedPage Page,
        PageAlignmentResult Alignment,
        StoredArtifact Normalized,
        StoredArtifact Thumbnail);

    private sealed record PreparedOutput(
        PreprocessingResult Result,
        IReadOnlyList<PreparedPage> Pages,
        int AlignmentReferencePageCount,
        bool PageCountMismatch)
    {
        public bool HasBlockingAlignmentFailure =>
            PageCountMismatch
            || Pages.Any(item => item.Alignment.State == "failed");
    }

    private sealed class JobHandlingException(
        string errorCode,
        bool isPermanent) : Exception
    {
        public string ErrorCode { get; } = errorCode;
        public bool IsPermanent { get; } = isPermanent;
    }
}
