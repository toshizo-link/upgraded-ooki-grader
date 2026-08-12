using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Jobs;

/// <summary>
/// Classifies one-page uploads against the selected published template, then
/// materializes deterministic multi-page submissions without trusting upload
/// completion order. InputOrdinal is the sole scanner-order authority.
/// </summary>
public sealed class OrderedScanAssemblyWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    IPreprocessingService preprocessingService,
    IOrderedScanAssemblyPlanner planner,
    TimeProvider timeProvider) : BackgroundService
{
    private const int JobSchemaVersion = OrderedScanBatchService.JobSchemaVersion;
    private const int MinimumRoleScoreBasisPoints = 6_500;
    private const int MinimumRoleMarginBasisPoints = 250;
    private const string RoleClassificationPolicyVersion =
        "ordered-scan-template-role-v1";
    private const long MaximumTemplateReferenceSourceBytes =
        PreprocessingOptions.DefaultMaxInputBytes;
    private const long MaximumTemplateReferenceArtifactBytes =
        PreprocessingOptions.DefaultMaxNormalizedArtifactBytes;
    private const long MaximumTemplateReferencePixels =
        PreprocessingOptions.DefaultMaxTotalPixels;
    private const int CompositeSpoolBufferBytes = 128 * 1024;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LeaseHeartbeatInterval =
        TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _workerId = $"ordered-scan-{Guid.NewGuid():N}";

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        using var workCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        var heartbeatFailure = new HeartbeatFailureState();
        var heartbeatTask = RunLeaseHeartbeatAsync(
            lease,
            heartbeatFailure,
            workCancellation,
            heartbeatCancellation.Token);
        try
        {
            var claim = await PrepareClaimAsync(lease, workCancellation.Token)
                .ConfigureAwait(false);
            if (claim is null)
            {
                return true;
            }

            var prepared = await ClassifyAndAssembleAsync(
                    lease,
                    claim,
                    workCancellation.Token)
                .ConfigureAwait(false);
            heartbeatFailure.ThrowIfFailed();
            await RenewLeaseAsync(lease, 9_500, workCancellation.Token)
                .ConfigureAwait(false);
            await PersistAsync(lease, claim, prepared, workCancellation.Token)
                .ConfigureAwait(false);
            heartbeatFailure.ThrowIfFailed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (heartbeatFailure.HasFailure)
        {
            await RecordHeartbeatFailureAsync(
                    lease,
                    heartbeatFailure,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OrderedScanWorkerException exception)
        {
            await RecordFailureAsync(
                    lease,
                    exception.Code,
                    exception.IsPermanent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PreprocessingException exception)
        {
            await RecordFailureAsync(
                    lease,
                    $"ordered_scan_{exception.Code}",
                    isPermanent: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await RecordFailureAsync(
                    lease,
                    "ordered_scan_worker_error",
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (heartbeatCancellation.IsCancellationRequested)
            {
                // Normal heartbeat shutdown after the leased work finishes.
            }
        }

        return true;
    }

    private async Task RunLeaseHeartbeatAsync(
        JobLease lease,
        HeartbeatFailureState failureState,
        CancellationTokenSource workCancellation,
        CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            LeaseHeartbeatInterval,
            timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)
                       .ConfigureAwait(false))
            {
                await RenewLeaseAsync(lease, 500, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The owning ProcessNextAsync invocation has finished or was stopped.
        }
        catch (Exception exception)
        {
            failureState.Record(exception);
            workCancellation.Cancel();
        }
    }

    private Task RecordHeartbeatFailureAsync(
        JobLease lease,
        HeartbeatFailureState failureState,
        CancellationToken cancellationToken)
    {
        var exception = failureState.SourceException;
        return exception switch
        {
            OrderedScanWorkerException ordered => RecordFailureAsync(
                lease,
                ordered.Code,
                ordered.IsPermanent,
                cancellationToken),
            PreprocessingException preprocessing => RecordFailureAsync(
                lease,
                $"ordered_scan_{preprocessing.Code}",
                isPermanent: true,
                cancellationToken),
            _ => RecordFailureAsync(
                lease,
                "ordered_scan_worker_error",
                isPermanent: false,
                cancellationToken),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item => item.Type == OrderedScanBatchService.JobType
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
            job.LeaseExpiresAt = now.Add(LeaseDuration);
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
                job.Revision,
                job.CorrelationId);
        }, cancellationToken);
    }

    private async Task<BatchClaim?> PrepareClaimAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.SchemaVersion != JobSchemaVersion)
        {
            throw Permanent("ordered_scan_job_schema_unsupported");
        }

        OrderedScanPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<OrderedScanPayload>(
                    lease.PayloadJson,
                    JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw Permanent("ordered_scan_job_payload_invalid");
        }

        if (string.IsNullOrWhiteSpace(payload.BatchId))
        {
            throw Permanent("ordered_scan_job_payload_invalid");
        }

        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await db.OrderedScanBatches
            .AsNoTracking()
            .Include(item => item.TestSession)
                .ThenInclude(item => item.TemplateVersion)
            .Include(item => item.Items)
                .ThenInclude(item => item.SourceFileReference)
                    .ThenInclude(item => item!.FileObject)
            .SingleOrDefaultAsync(
                item => item.Id == payload.BatchId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("ordered_scan_batch_missing");
        if (batch.Status == OrderedScanBatchStatus.Completed
            || batch.Status == OrderedScanBatchStatus.NeedsReview)
        {
            await CompleteJobAsync(lease, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (batch.Status != OrderedScanBatchStatus.Processing
            || batch.ExpectedPageCount is <= 0
                or > OrderedScanBatchService.MaximumSubmissionPages
            || batch.AssemblyPolicyVersion
                != OrderedScanAssemblyPlanner.CurrentPolicyVersion
            || batch.Items.Count == 0)
        {
            throw Permanent("ordered_scan_batch_state_invalid");
        }

        var sourceItems = new List<SourceItemClaim>(batch.Items.Count);
        foreach (var item in batch.Items.OrderBy(item => item.InputOrdinal))
        {
            var reference = item.SourceFileReference;
            var fileObject = reference?.FileObject;
            if (item.Status != OrderedScanItemStatus.Uploaded
                || item.UploadSessionId is null
                || reference is null
                || fileObject is null
                || reference.OwnerType != "ordered_scan_batch"
                || reference.OwnerId != batch.Id
                || reference.Purpose != "ordered_scan_page"
                || fileObject.State != "available"
                || fileObject.StorageClass
                    != ContentStorageClass.ManagedScanOriginal.ToString()
                || fileObject.VerifiedMime != "application/pdf"
                || item.SourceSha256 != fileObject.Sha256
                || item.SourceBytes != fileObject.Bytes)
            {
                throw Permanent("ordered_scan_source_page_invalid");
            }

            sourceItems.Add(new SourceItemClaim(
                item.Id,
                item.InputOrdinal,
                item.ClientItemId,
                item.OriginalFileName,
                item.UploadSessionId,
                reference.Id,
                new ContentObjectLocator(
                    ContentStorageClass.ManagedScanOriginal,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.Extension)));
        }

        var referenceSources = await LoadReferenceSourcesAsync(
                db,
                batch.TestSession.TemplateVersionId,
                cancellationToken)
            .ConfigureAwait(false);
        return new BatchClaim(
            batch.Id,
            batch.Revision,
            batch.TestSessionId,
            batch.ExpectedPageCount,
            batch.AssemblyPolicyVersion,
            batch.PlanHash ?? string.Empty,
            batch.CreatedByStaffUserId,
            sourceItems,
            referenceSources);
    }

    private static async Task<IReadOnlyList<ReferenceSourceClaim>>
        LoadReferenceSourcesAsync(
            OokiGraderDbContext db,
            string templateVersionId,
            CancellationToken cancellationToken)
    {
        var candidates = await db.TemplateSources
            .AsNoTracking()
            .Where(item => item.TemplateVersionId == templateVersionId
                && (item.SourceRole == "blank_test"
                    || item.SourceRole == "contains_model_answers"
                    || item.SourceRole == "contains_non_model_answers"))
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id)
            .Select(item => new
            {
                item.Id,
                item.SourceRole,
                item.DisplayName,
                item.Ordinal,
                item.UploadSessionId,
                item.FileReferenceId,
                item.TemplateVersion.OriginatingUnitId,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
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
            throw Permanent("ordered_scan_template_reference_missing");
        }

        var referenceIds = selected.Select(item => item.FileReferenceId!).ToArray();
        var references = await db.FileReferences
            .AsNoTracking()
            .Include(item => item.FileObject)
            .Where(item => referenceIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken)
            .ConfigureAwait(false);
        var result = new List<ReferenceSourceClaim>(selected.Length);
        long totalSourceBytes = 0;
        foreach (var source in selected)
        {
            if (!references.TryGetValue(source.FileReferenceId!, out var reference)
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
                    out var storageClass))
            {
                throw Permanent("ordered_scan_template_reference_invalid");
            }

            var uploadedSource = reference.OwnerType == "upload_session"
                && reference.OwnerId == source.UploadSessionId
                && reference.Purpose == "template_source"
                && storageClass == ContentStorageClass.TemplateSource;
            var derivedSource = source.OriginatingUnitId is { } unitId
                && reference.OwnerType == "template_generation_unit"
                && reference.OwnerId == unitId
                && reference.Purpose == "derived_source"
                && storageClass == ContentStorageClass.TemplateDerived;
            if (!uploadedSource && !derivedSource)
            {
                throw Permanent("ordered_scan_template_reference_invalid");
            }

            totalSourceBytes = checked(
                totalSourceBytes + reference.FileObject.Bytes);
            if (totalSourceBytes > MaximumTemplateReferenceSourceBytes)
            {
                throw Permanent("ordered_scan_template_reference_byte_limit");
            }

            result.Add(new ReferenceSourceClaim(
                source.Id,
                source.Ordinal,
                source.DisplayName,
                reference.FileObject.VerifiedMime,
                new ContentObjectLocator(
                    storageClass,
                    reference.FileObject.Sha256,
                    reference.FileObject.Bytes,
                    reference.FileObject.Extension)));
        }

        return result;
    }

    private async Task<PreparedBatch> ClassifyAndAssembleAsync(
        JobLease lease,
        BatchClaim claim,
        CancellationToken cancellationToken)
    {
        var referencePages = new List<PreprocessedPage>();
        long referenceArtifactBytes = 0;
        long referencePixels = 0;
        var referenceSourceNumber = 0;
        foreach (var source in claim.ReferenceSources
                     .OrderBy(item => item.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            referenceSourceNumber++;
            await RenewLeaseAsync(
                    lease,
                    Math.Min(900, checked(500 + (referenceSourceNumber * 100))),
                    cancellationToken)
                .ConfigureAwait(false);
            var remainingArtifactBytes =
                MaximumTemplateReferenceArtifactBytes - referenceArtifactBytes;
            var remainingPixels = MaximumTemplateReferencePixels - referencePixels;
            if (remainingArtifactBytes <= 0 || remainingPixels <= 0)
            {
                throw Permanent("ordered_scan_template_reference_limit");
            }

            await using var stream = await contentStore.OpenReadAsync(
                    source.Locator,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await preprocessingService.ProcessAsync(
                    stream,
                    new PreprocessingInput(
                        source.VerifiedMime,
                        source.DisplayName,
                        MaximumPages: OrderedScanBatchService.MaximumSubmissionPages,
                        MaximumNormalizedArtifactBytes: remainingArtifactBytes,
                        MaximumTotalPixels: remainingPixels),
                    cancellationToken)
                .ConfigureAwait(false);
            referencePages.AddRange(result.Pages);
            foreach (var page in result.Pages)
            {
                referenceArtifactBytes = checked(
                    referenceArtifactBytes
                    + page.NormalizedPng.Bytes.LongLength
                    + page.ThumbnailPng.Bytes.LongLength);
                referencePixels = checked(
                    referencePixels + ((long)page.Width * page.Height));
            }

            if (referencePages.Count
                > OrderedScanBatchService.MaximumSubmissionPages)
            {
                throw Permanent("ordered_scan_template_page_count_unsupported");
            }

            if (referenceArtifactBytes
                    > MaximumTemplateReferenceArtifactBytes
                || referencePixels > MaximumTemplateReferencePixels)
            {
                throw Permanent("ordered_scan_template_reference_limit");
            }
        }

        if (referencePages.Count != claim.ExpectedPageCount)
        {
            throw Permanent("ordered_scan_template_page_count_mismatch");
        }

        await RenewLeaseAsync(lease, 1_000, cancellationToken)
            .ConfigureAwait(false);

        var classifications = new List<ClassifiedItem>(claim.Items.Count);
        var directIssues = new List<OrderedScanAssemblyIssue>();
        var duplicateSourceOrdinals = claim.Items
            .GroupBy(item => item.Locator.Sha256, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.InputOrdinal))
            .ToHashSet();
        foreach (var item in claim.Items.OrderBy(item => item.InputOrdinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = await contentStore.OpenReadAsync(
                    item.Locator,
                    cancellationToken)
                .ConfigureAwait(false);
            var result = await preprocessingService.ProcessAsync(
                    stream,
                    new PreprocessingInput(
                        "application/pdf",
                        item.FileName,
                        MaximumPages: 1),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Pages.Count != 1)
            {
                throw Permanent("ordered_scan_source_not_single_page");
            }

            var candidate = result.Pages[0];
            RoleMatch? best = null;
            RoleMatch? second = null;
            for (var referenceIndex = 0;
                 referenceIndex < referencePages.Count;
                 referenceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var alignment = preprocessingService.AlignToReference(
                    candidate,
                    referencePages[referenceIndex],
                    cancellationToken);
                var match = new RoleMatch(
                    referenceIndex + 1,
                    alignment.State,
                    alignment.ScoreBasisPoints ?? -1);
                if (best is null || IsBetterRoleMatch(match, best))
                {
                    second = best;
                    best = match;
                }
                else if (second is null || IsBetterRoleMatch(match, second))
                {
                    second = match;
                }

                ClearTransientAlignmentPage(candidate, alignment);
            }

            if (best is null)
            {
                throw Permanent("ordered_scan_template_reference_missing");
            }

            var expectedPageNumber = ((item.InputOrdinal - 1)
                % claim.ExpectedPageCount) + 1;
            int? detectedPageNumber = null;
            var roleAmbiguous = second is not null
                && best.Score - second.Score < MinimumRoleMarginBasisPoints;
            if (best.State == "aligned"
                && best.Score >= MinimumRoleScoreBasisPoints
                && !roleAmbiguous)
            {
                detectedPageNumber = best.PageNumber;
            }

            ClearPageArtifacts(candidate);

            string? issueCode = null;
            if (duplicateSourceOrdinals.Contains(item.InputOrdinal))
            {
                issueCode = "ORDERED_SCAN_EXACT_DUPLICATE_PAGE";
                directIssues.Add(new OrderedScanAssemblyIssue(
                    issueCode,
                    item.InputOrdinal,
                    ((item.InputOrdinal - 1) / claim.ExpectedPageCount) + 1,
                    expectedPageNumber,
                    detectedPageNumber,
                    $"Input ordinal {item.InputOrdinal} has the same PDF content " +
                    "as another page in this batch."));
            }

            if (roleAmbiguous)
            {
                issueCode ??= "ORDERED_SCAN_PAGE_ROLE_AMBIGUOUS";
                directIssues.Add(new OrderedScanAssemblyIssue(
                    "ORDERED_SCAN_PAGE_ROLE_AMBIGUOUS",
                    item.InputOrdinal,
                    ((item.InputOrdinal - 1) / claim.ExpectedPageCount) + 1,
                    expectedPageNumber,
                    null,
                    $"Input ordinal {item.InputOrdinal} did not have the " +
                    $"required {MinimumRoleMarginBasisPoints}-point template-page " +
                    $"margin under {RoleClassificationPolicyVersion}."));
            }
            else if (detectedPageNumber is null)
            {
                issueCode ??= "ORDERED_SCAN_PAGE_ALIGNMENT_FAILED";
                directIssues.Add(new OrderedScanAssemblyIssue(
                    "ORDERED_SCAN_PAGE_ALIGNMENT_FAILED",
                    item.InputOrdinal,
                    ((item.InputOrdinal - 1) / claim.ExpectedPageCount) + 1,
                    expectedPageNumber,
                    null,
                    $"Input ordinal {item.InputOrdinal} could not be aligned to " +
                    $"template page {expectedPageNumber}."));
            }
            else if (detectedPageNumber != expectedPageNumber)
            {
                issueCode ??= "ORDERED_SCAN_PAGE_ORDER_MISMATCH";
                directIssues.Add(new OrderedScanAssemblyIssue(
                    "ORDERED_SCAN_PAGE_ORDER_MISMATCH",
                    item.InputOrdinal,
                    ((item.InputOrdinal - 1) / claim.ExpectedPageCount) + 1,
                    expectedPageNumber,
                    detectedPageNumber,
                    $"Input ordinal {item.InputOrdinal} matched template page " +
                    $"{detectedPageNumber} instead of {expectedPageNumber}."));
            }

            classifications.Add(new ClassifiedItem(
                item,
                detectedPageNumber,
                detectedPageNumber is null ? null : best.Score,
                issueCode));
            await RenewLeaseAsync(
                    lease,
                    1_000 + (int)Math.Round(
                        6_000d * classifications.Count / claim.Items.Count,
                        MidpointRounding.AwayFromZero),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = planner.Plan(
            claim.ExpectedPageCount,
            classifications
                .OrderBy(item => item.Source.InputOrdinal)
                .Select(item => new OrderedScanPageObservation(
                    item.Source.InputOrdinal,
                    item.DetectedTemplatePageNumber))
                .ToArray());
        var allIssues = directIssues
            .Concat(plan.Issues)
            .DistinctBy(item => new
            {
                item.Code,
                item.InputOrdinal,
                item.GroupOrdinal,
                item.ExpectedTemplatePageNumber,
                item.ActualTemplatePageNumber,
            })
            .ToArray();
        if (!plan.CanFinalizeAutomatically || directIssues.Count > 0)
        {
            return new PreparedBatch(classifications, plan, allIssues, []);
        }

        var compositeGroups = new List<PreparedGroup>(plan.Groups.Count);
        var assembledPageCount = 0;
        foreach (var group in plan.Groups.OrderBy(item => item.GroupOrdinal))
        {
            await RenewLeaseAsync(
                    lease,
                    7_000 + (int)Math.Round(
                        2_000d * group.GroupOrdinal / plan.Groups.Count,
                        MidpointRounding.AwayFromZero),
                    cancellationToken)
                .ConfigureAwait(false);
            await using var pdfSpool = CreateCompositeSpoolStream();
            using (var pdfWriter = PreprocessedDocumentEncoder.CreatePdfWriter(
                       pdfSpool,
                       PreprocessingOptions.DefaultMaxInputBytes))
            {
                foreach (var placement in group.Pages
                             .OrderBy(item => item.TemplatePageNumber))
                {
                    var classified = classifications.Single(item =>
                        item.Source.InputOrdinal == placement.InputOrdinal);
                    if (placement.TemplatePageNumber is null)
                    {
                        throw Permanent("ordered_scan_plan_output_invalid");
                    }

                    await RenewLeaseAsync(
                            lease,
                            7_000 + (int)Math.Round(
                                1_500d * assembledPageCount / claim.Items.Count,
                                MidpointRounding.AwayFromZero),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await using var pageStream = await contentStore.OpenReadAsync(
                            classified.Source.Locator,
                            cancellationToken)
                        .ConfigureAwait(false);
                    var pageResult = await preprocessingService.ProcessAsync(
                            pageStream,
                            new PreprocessingInput(
                                "application/pdf",
                                classified.Source.FileName,
                                MaximumPages: 1),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (pageResult.Pages.Count != 1)
                    {
                        throw Permanent("ordered_scan_source_not_single_page");
                    }

                    var candidate = pageResult.Pages[0];
                    PageAlignmentResult? alignment = null;
                    try
                    {
                        alignment = preprocessingService.AlignToReference(
                            candidate,
                            referencePages[
                                placement.TemplatePageNumber.Value - 1],
                            cancellationToken);
                        if (alignment.State != "aligned"
                            || alignment.ScoreBasisPoints
                                < MinimumRoleScoreBasisPoints)
                        {
                            throw Permanent("ordered_scan_alignment_changed");
                        }

                        pdfWriter.AppendPage(
                            alignment.Page with
                            {
                                PageNumber = placement.TemplatePageNumber.Value,
                            },
                            cancellationToken);
                    }
                    finally
                    {
                        if (alignment is not null)
                        {
                            ClearTransientAlignmentPage(candidate, alignment);
                        }

                        ClearPageArtifacts(candidate);
                    }

                    assembledPageCount++;
                }

                pdfWriter.Complete(cancellationToken);
            }

            await pdfSpool.FlushAsync(cancellationToken).ConfigureAwait(false);
            pdfSpool.Position = 0;
            await RenewLeaseAsync(lease, 9_000, cancellationToken)
                .ConfigureAwait(false);
            var write = await contentStore.PutAsync(
                    pdfSpool,
                    ContentStorageClass.ManagedScanOriginal,
                    "pdf",
                    cancellationToken)
                .ConfigureAwait(false);
            await RenewLeaseAsync(lease, 9_250, cancellationToken)
                .ConfigureAwait(false);
            var sourceItems = group.Pages
                .OrderBy(item => item.TemplatePageNumber)
                .Select(placement => classifications.Single(item =>
                    item.Source.InputOrdinal == placement.InputOrdinal))
                .ToArray();
            compositeGroups.Add(new PreparedGroup(
                group.GroupOrdinal,
                sourceItems,
                write,
                ComputeAssemblyManifestHash(
                    claim,
                    group.GroupOrdinal,
                    sourceItems,
                    write.Locator.Sha256)));
        }

        var compositeDuplicateIssues = compositeGroups
            .GroupBy(item => item.Write.Locator.Sha256, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item =>
                new OrderedScanAssemblyIssue(
                    "ORDERED_SCAN_EXACT_DUPLICATE_SUBMISSION",
                    null,
                    item.GroupOrdinal,
                    null,
                    null,
                    $"Group {item.GroupOrdinal} has the same assembled content " +
                    "as another group in this batch.")))
            .ToArray();
        return new PreparedBatch(
            classifications,
            plan,
            allIssues.Concat(compositeDuplicateIssues).ToArray(),
            compositeGroups);
    }

    private Task PersistAsync(
        JobLease lease,
        BatchClaim claim,
        PreparedBatch prepared,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            var batch = await db.OrderedScanBatches
                .Include(item => item.Items)
                .SingleOrDefaultAsync(item => item.Id == claim.BatchId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ordered_scan_batch_missing");
            if (batch.Status == OrderedScanBatchStatus.Completed)
            {
                CompleteJob(job, timeProvider.GetUtcNow());
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (batch.Status != OrderedScanBatchStatus.Processing
                || batch.ExpectedPageCount != claim.ExpectedPageCount
                || batch.AssemblyPolicyVersion != claim.AssemblyPolicyVersion
                || batch.PlanHash != claim.PlanHash)
            {
                throw Permanent("ordered_scan_batch_changed");
            }

            var now = timeProvider.GetUtcNow();
            var itemsById = batch.Items.ToDictionary(item => item.Id);
            foreach (var classification in prepared.Classifications)
            {
                var item = itemsById[classification.Source.Id];
                item.DetectedTemplatePageNumber =
                    classification.DetectedTemplatePageNumber;
                item.ClassificationConfidenceBasisPoints =
                    classification.ConfidenceBasisPoints;
                item.IssueCode = classification.IssueCode;
                item.Status = classification.IssueCode is null
                    ? OrderedScanItemStatus.Classified
                    : OrderedScanItemStatus.NeedsReview;
            }

            if (!prepared.Plan.CanFinalizeAutomatically
                || prepared.Issues.Count > 0)
            {
                foreach (var preparedGroup in prepared.Groups.Where(item =>
                    !item.Write.Deduplicated))
                {
                    await contentStore.DeleteAsync(
                            preparedGroup.Write.Locator,
                            token)
                        .ConfigureAwait(false);
                }

                foreach (var group in prepared.Plan.Groups)
                {
                    foreach (var placement in group.Pages)
                    {
                        var item = batch.Items.Single(candidate =>
                            candidate.InputOrdinal == placement.InputOrdinal);
                        item.GroupOrdinal = group.GroupOrdinal;
                        if (group.Status == OrderedScanGroupStatus.NeedsReview
                            || prepared.Issues.Any(issue =>
                                issue.InputOrdinal == item.InputOrdinal
                                || issue.GroupOrdinal == group.GroupOrdinal))
                        {
                            item.Status = OrderedScanItemStatus.NeedsReview;
                            item.IssueCode ??= prepared.Issues
                                .FirstOrDefault(issue =>
                                    issue.InputOrdinal == item.InputOrdinal
                                    || issue.GroupOrdinal == group.GroupOrdinal)
                                ?.Code;
                        }
                    }
                }

                batch.Status = OrderedScanBatchStatus.NeedsReview;
                batch.LastErrorCode = prepared.Issues.Count > 0
                    ? prepared.Issues[0].Code
                    : "ORDERED_SCAN_REVIEW_REQUIRED";
                batch.LastErrorJson = JsonSerializer.Serialize(
                    prepared.Issues,
                    JsonOptions);
                CompleteJob(job, now);
                await AddAuditAsync(
                    db,
                    now,
                    lease.CorrelationId,
                    batch,
                    "ordered_scan_batch.needs_review",
                    "requires_action",
                    token).ConfigureAwait(false);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var resolvedGroups = new List<ResolvedGroup>(prepared.Groups.Count);
            foreach (var preparedGroup in prepared.Groups)
            {
                var fileObject = await ResolveCompositeObjectAsync(
                        db,
                        preparedGroup.Write,
                        now,
                        token)
                    .ConfigureAwait(false);
                resolvedGroups.Add(new ResolvedGroup(preparedGroup, fileObject));
            }

            var compositeObjectIds = resolvedGroups
                .Select(item => item.FileObject.Id)
                .ToArray();
            var compositeManifestHashes = resolvedGroups
                .Select(item => item.Group.ManifestHash)
                .ToArray();
            var existingSubmissions = await db.Submissions
                .AsNoTracking()
                .Where(item => item.TestSessionId == batch.TestSessionId
                    && item.VoidedAt == null
                    && ((item.OriginalFileObjectId != null
                            && compositeObjectIds.Contains(
                                item.OriginalFileObjectId))
                        || (item.AssemblyManifestHash != null
                            && compositeManifestHashes.Contains(
                                item.AssemblyManifestHash))))
                .Select(item => new
                {
                    item.Id,
                    item.OriginalFileObjectId,
                    item.AssemblyManifestHash,
                    item.OrderedScanBatchId,
                    item.OrderedScanGroupOrdinal,
                })
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            var duplicateGroups = resolvedGroups
                .Where(group => existingSubmissions.Any(existing =>
                    existing.OrderedScanBatchId != batch.Id
                    && (existing.OriginalFileObjectId == group.FileObject.Id
                        || existing.AssemblyManifestHash
                            == group.Group.ManifestHash)))
                .ToArray();
            if (duplicateGroups.Length > 0)
            {
                var issues = duplicateGroups.Select(group =>
                    new OrderedScanAssemblyIssue(
                        "ORDERED_SCAN_EXACT_DUPLICATE_SUBMISSION",
                        null,
                        group.Group.GroupOrdinal,
                        null,
                        null,
                        $"Group {group.Group.GroupOrdinal} duplicates an existing " +
                        "submission in this test session."))
                    .ToArray();
                foreach (var duplicate in duplicateGroups)
                {
                    foreach (var classified in duplicate.Group.Items)
                    {
                        var item = itemsById[classified.Source.Id];
                        item.Status = OrderedScanItemStatus.NeedsReview;
                        item.GroupOrdinal = duplicate.Group.GroupOrdinal;
                        item.IssueCode =
                            "ORDERED_SCAN_EXACT_DUPLICATE_SUBMISSION";
                    }
                }

                foreach (var resolved in resolvedGroups.Where(item =>
                    db.Entry(item.FileObject).State == EntityState.Added))
                {
                    db.FileObjects.Remove(resolved.FileObject);
                    if (!resolved.Group.Write.Deduplicated)
                    {
                        await contentStore.DeleteAsync(
                                resolved.Group.Write.Locator,
                                token)
                            .ConfigureAwait(false);
                    }
                }

                batch.Status = OrderedScanBatchStatus.NeedsReview;
                batch.LastErrorCode =
                    "ORDERED_SCAN_EXACT_DUPLICATE_SUBMISSION";
                batch.LastErrorJson = JsonSerializer.Serialize(
                    issues,
                    JsonOptions);
                CompleteJob(job, now);
                await AddAuditAsync(
                    db,
                    now,
                    lease.CorrelationId,
                    batch,
                    "ordered_scan_batch.needs_review",
                    "requires_action",
                    token).ConfigureAwait(false);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            foreach (var resolvedGroup in resolvedGroups)
            {
                var preparedGroup = resolvedGroup.Group;
                var fileObject = resolvedGroup.FileObject;
                var submissionId = UlidId.New(now.AddTicks(
                    preparedGroup.GroupOrdinal));
                var submission = new SubmissionEntity
                {
                    Id = submissionId,
                    TestSessionId = batch.TestSessionId,
                    State = "validating",
                    ScanPayloadState = "scan_available",
                    AssignmentMethod = "none",
                    AttemptNumber = 1,
                    CanonicalForSession = false,
                    UploadedByStaffUserId = batch.CreatedByStaffUserId,
                    OriginalFileName = $"ordered-scan-{batch.Id}-" +
                        $"{preparedGroup.GroupOrdinal:D4}.pdf",
                    OriginalFileObjectId = fileObject.Id,
                    OrderedScanBatchId = batch.Id,
                    OrderedScanGroupOrdinal = preparedGroup.GroupOrdinal,
                    AssemblyManifestHash = preparedGroup.ManifestHash,
                    UploadCompletedAt = now,
                    QualitySummaryJson =
                        """{"pipeline":"safe-ingest-v1","status":"accepted","assembly":"ordered-single-page-scan-v1"}""",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Submissions.Add(submission);
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = UlidId.New(now.AddTicks(
                        10_000 + preparedGroup.GroupOrdinal)),
                    FileObjectId = fileObject.Id,
                    OwnerType = "submission",
                    OwnerId = submission.Id,
                    Purpose = "original_scan",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                fileObject.ReferenceCountCache = checked(
                    fileObject.ReferenceCountCache + 1);

                var submissionPageNumber = 0;
                foreach (var classified in preparedGroup.Items)
                {
                    submissionPageNumber = checked(submissionPageNumber + 1);
                    var item = itemsById[classified.Source.Id];
                    var sourceReference = await db.FileReferences
                        .SingleAsync(
                            reference => reference.Id
                                == classified.Source.FileReferenceId,
                            token)
                        .ConfigureAwait(false);
                    if (sourceReference.OwnerType != "ordered_scan_batch"
                        || sourceReference.OwnerId != batch.Id
                        || sourceReference.Purpose != "ordered_scan_page")
                    {
                        throw Permanent("ordered_scan_source_reference_changed");
                    }

                    sourceReference.OwnerType = "submission";
                    sourceReference.OwnerId = submission.Id;
                    sourceReference.Purpose = "original_scan_page";
                    sourceReference.RetentionAnchorAt = now;
                    item.Status = OrderedScanItemStatus.Grouped;
                    item.GroupOrdinal = preparedGroup.GroupOrdinal;
                    item.SubmissionId = submission.Id;
                    item.SubmissionPageNumber = submissionPageNumber;
                    item.IssueCode = null;
                    db.SubmissionSourcePages.Add(new SubmissionSourcePageEntity
                    {
                        Id = UlidId.New(now.AddTicks(
                            20_000
                            + (preparedGroup.GroupOrdinal * 100L)
                            + submissionPageNumber)),
                        SubmissionId = submission.Id,
                        PageNumber = submissionPageNumber,
                        OrderedScanItemId = item.Id,
                        UploadSessionId = classified.Source.UploadSessionId,
                        FileReferenceId = sourceReference.Id,
                        SourcePageNumber = 1,
                        SourceSha256 = classified.Source.Locator.Sha256,
                        AssemblyPolicyVersion = batch.AssemblyPolicyVersion,
                        CreatedAt = now,
                    });
                }

                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = UlidId.New(now.AddTicks(
                        30_000 + preparedGroup.GroupOrdinal)),
                    Type = SubmissionPreprocessingWorker.JobType,
                    SchemaVersion = 1,
                    DeduplicationKey =
                        $"submission:{submission.Id}:preprocess",
                    Priority = 0,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        submissionId = submission.Id,
                    }),
                    State = "queued",
                    MaxAttempts = 8,
                    NextAttemptAt = now,
                    CorrelationId = lease.CorrelationId,
                    CausationId = lease.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            batch.Status = OrderedScanBatchStatus.Completed;
            batch.CompletedAt = now;
            batch.LastErrorCode = null;
            batch.LastErrorJson = null;
            CompleteJob(job, now);
            await AddAuditAsync(
                db,
                now,
                lease.CorrelationId,
                batch,
                "ordered_scan_batch.completed",
                "succeeded",
                token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task<FileObjectEntity> ResolveCompositeObjectAsync(
        OokiGraderDbContext db,
        ContentWriteResult write,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var storageClass = ContentStorageClass.ManagedScanOriginal.ToString();
        var existing = db.FileObjects.Local.SingleOrDefault(
            item => item.StorageClass == storageClass
                && item.Sha256 == write.Locator.Sha256);
        existing ??= await db.FileObjects.SingleOrDefaultAsync(
            item => item.StorageClass == storageClass
                && item.Sha256 == write.Locator.Sha256,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.State != "available"
                || existing.Bytes != write.Locator.Bytes
                || existing.Extension != "pdf"
                || existing.VerifiedMime != "application/pdf"
                || !existing.ManagedScanBytes)
            {
                throw Permanent("ordered_scan_composite_object_conflict");
            }

            return existing;
        }

        var fileObject = new FileObjectEntity
        {
            Id = UlidId.New(now),
            Sha256 = write.Locator.Sha256,
            Bytes = write.Locator.Bytes,
            VerifiedMime = "application/pdf",
            Extension = write.Locator.Extension,
            RelativeObjectPath = write.RelativePath,
            StorageClass = storageClass,
            RetentionClass = "submitted_scan",
            ManagedScanBytes = true,
            State = "available",
            CreatedAt = now,
            VerifiedAt = now,
            ReferenceCountCache = 0,
        };
        db.FileObjects.Add(fileObject);
        return fileObject;
    }

    private Task CompleteJobAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease, token)
                .ConfigureAwait(false);
            CompleteJob(job, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RenewLeaseAsync(
        JobLease lease,
        int progressBasisPoints,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                item => item.Id == lease.Id,
                token).ConfigureAwait(false)
                ?? throw Permanent("ordered_scan_job_missing");
            var now = timeProvider.GetUtcNow();
            if (job.State != "leased"
                || !string.Equals(
                    job.LeaseOwner,
                    _workerId,
                    StringComparison.Ordinal)
                || job.LeaseExpiresAt is null
                || job.LeaseExpiresAt <= now)
            {
                throw Permanent("ordered_scan_job_lease_lost");
            }

            job.LeaseExpiresAt = now.Add(LeaseDuration);
            job.ProgressBasisPoints = Math.Max(
                job.ProgressBasisPoints,
                Math.Clamp(progressBasisPoints, 0, 9_500));
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordFailureAsync(
        JobLease lease,
        string errorCode,
        bool isPermanent,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                item => item.Id == lease.Id,
                token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (job is null
                || job.State != "leased"
                || !string.Equals(
                    job.LeaseOwner,
                    _workerId,
                    StringComparison.Ordinal)
                || job.LeaseExpiresAt is null
                || job.LeaseExpiresAt <= now)
            {
                return;
            }

            var terminal = isPermanent || job.AttemptCount >= job.MaxAttempts;
            job.State = terminal ? "failed" : "retry_waiting";
            job.NextAttemptAt = terminal
                ? now
                : now.AddSeconds(Math.Min(60, 1 << Math.Min(job.AttemptCount, 5)));
            job.ErrorCode = errorCode;
            job.SafeErrorDetail = errorCode;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            if (terminal)
            {
                job.CompletedAt = now;
                job.ProgressBasisPoints = 10_000;
            }

            var payload = TryDeserializePayload(job.PayloadJson);
            if (terminal && payload?.BatchId is { Length: > 0 } batchId)
            {
                var batch = await db.OrderedScanBatches.SingleOrDefaultAsync(
                    item => item.Id == batchId,
                    token).ConfigureAwait(false);
                if (batch?.Status == OrderedScanBatchStatus.Processing)
                {
                    batch.Status = OrderedScanBatchStatus.Failed;
                    batch.LastErrorCode = errorCode;
                    batch.LastErrorJson = JsonSerializer.Serialize(
                        new[]
                        {
                            new OrderedScanAssemblyIssue(
                                errorCode,
                                null,
                                null,
                                null,
                                null,
                                "Ordered scan assembly could not be completed."),
                        },
                        JsonOptions);
                    batch.CompletedAt = now;
                }
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs.SingleOrDefaultAsync(
            item => item.Id == lease.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw Permanent("ordered_scan_job_missing");
        var now = timeProvider.GetUtcNow();
        if (job.State != "leased"
            || !string.Equals(
                job.LeaseOwner,
                _workerId,
                StringComparison.Ordinal)
            || job.LeaseExpiresAt is null
            || job.LeaseExpiresAt <= now)
        {
            throw Permanent("ordered_scan_job_lease_lost");
        }

        return job;
    }

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
        job.CompletedAt = now;
    }

    private static Task AddAuditAsync(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        OrderedScanBatchEntity batch,
        string eventType,
        string outcome,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = batch.CreatedByStaffUserId,
            EventType = eventType,
            ObjectType = "ordered_scan_batch",
            ObjectId = batch.Id,
            Outcome = outcome,
            CorrelationId = correlationId,
            SafeMetadataJson = JsonSerializer.Serialize(new
            {
                batch.ExpectedPageCount,
                itemCount = batch.Items.Count,
                submissionCount = batch.Items
                    .Select(item => item.SubmissionId)
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            }),
        });
        return Task.CompletedTask;
    }

    private static string ComputeAssemblyManifestHash(
        BatchClaim claim,
        int groupOrdinal,
        IReadOnlyList<ClassifiedItem> items,
        string compositeSha256)
    {
        var canonical = new StringBuilder();
        Append(claim.AssemblyPolicyVersion);
        Append(claim.BatchId);
        Append(claim.PlanHash);
        Append(groupOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        foreach (var item in items.OrderBy(item => item.DetectedTemplatePageNumber))
        {
            Append(item.Source.InputOrdinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Append(item.Source.Locator.Sha256);
            Append(item.DetectedTemplatePageNumber?.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        }

        Append(compositeSha256);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();

        void Append(string value) => canonical
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static bool IsBetterRoleMatch(
        RoleMatch candidate,
        RoleMatch current) =>
        candidate.Score > current.Score
        || (candidate.Score == current.Score
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

    private static void ClearPageArtifacts(PreprocessedPage page)
    {
        Array.Clear(page.NormalizedPng.Bytes);
        Array.Clear(page.ThumbnailPng.Bytes);
    }

    private static FileStream CreateCompositeSpoolStream()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ooki-ordered-scan-{Guid.NewGuid():N}.pdf");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            CompositeSpoolBufferBytes,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan
            | FileOptions.DeleteOnClose);
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

    private static OrderedScanPayload? TryDeserializePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OrderedScanPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OrderedScanWorkerException Permanent(string code) =>
        new(code, isPermanent: true);

    private sealed record OrderedScanPayload(string BatchId);

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        long Revision,
        string? CorrelationId);

    private sealed record ReferenceSourceClaim(
        string Id,
        int Ordinal,
        string DisplayName,
        string VerifiedMime,
        ContentObjectLocator Locator);

    private sealed record SourceItemClaim(
        string Id,
        int InputOrdinal,
        string ClientItemId,
        string FileName,
        string UploadSessionId,
        string FileReferenceId,
        ContentObjectLocator Locator);

    private sealed record BatchClaim(
        string BatchId,
        long Revision,
        string TestSessionId,
        int ExpectedPageCount,
        string AssemblyPolicyVersion,
        string PlanHash,
        string StaffUserId,
        IReadOnlyList<SourceItemClaim> Items,
        IReadOnlyList<ReferenceSourceClaim> ReferenceSources);

    private sealed record ClassifiedItem(
        SourceItemClaim Source,
        int? DetectedTemplatePageNumber,
        int? ConfidenceBasisPoints,
        string? IssueCode);

    private sealed record RoleMatch(
        int PageNumber,
        string State,
        int Score);

    private sealed record PreparedGroup(
        int GroupOrdinal,
        IReadOnlyList<ClassifiedItem> Items,
        ContentWriteResult Write,
        string ManifestHash);

    private sealed record ResolvedGroup(
        PreparedGroup Group,
        FileObjectEntity FileObject);

    private sealed record PreparedBatch(
        IReadOnlyList<ClassifiedItem> Classifications,
        OrderedScanAssemblyPlan Plan,
        IReadOnlyList<OrderedScanAssemblyIssue> Issues,
        IReadOnlyList<PreparedGroup> Groups);

    private sealed class HeartbeatFailureState
    {
        private ExceptionDispatchInfo? _failure;

        public bool HasFailure => Volatile.Read(ref _failure) is not null;

        public Exception? SourceException =>
            Volatile.Read(ref _failure)?.SourceException;

        public void Record(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Interlocked.CompareExchange(
                ref _failure,
                ExceptionDispatchInfo.Capture(exception),
                comparand: null);
        }

        public void ThrowIfFailed() =>
            Volatile.Read(ref _failure)?.Throw();
    }

    private sealed class OrderedScanWorkerException(
        string code,
        bool isPermanent) : Exception(code)
    {
        public string Code { get; } = code;
        public bool IsPermanent { get; } = isPermanent;
    }
}
