using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Retention;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed partial class RetentionJobWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IContentStore contentStore,
    TimeProvider timeProvider,
    ILogger<RetentionJobWorker> logger) : BackgroundService
{
    public const string JobType = "retention.reconcile";

    private const int MaximumManifestObjects = 1_000;
    private const long MaximumManifestBytes = 5L * 1024L * 1024L * 1024L;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private readonly string _workerId = $"retention-{Guid.NewGuid():N}";
    private DateTimeOffset _nextScheduleCheckAt = DateTimeOffset.MinValue;

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
            var manifestId = await FindUnfinishedManifestAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? await CreateManifestAsync(lease.Id, cancellationToken)
                    .ConfigureAwait(false);

            if (manifestId is null)
            {
                await CompleteJobAsync(
                        lease,
                        manifest: null,
                        queueContinuation: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            var result = await ProcessManifestAsync(
                    manifestId,
                    lease.CorrelationId,
                    cancellationToken)
                .ConfigureAwait(false);
            await CompleteJobAsync(
                    lease,
                    result,
                    queueContinuation: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RetentionOperationException exception)
        {
            LogRetentionFailure(lease.Id, exception.ErrorCode);
            await FailJobAsync(
                    lease,
                    exception.ErrorCode,
                    exception.IsPermanent,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogUnexpectedJobFailure(exception, lease.Id);
            await FailJobAsync(
                    lease,
                    "retention_worker_error",
                    isPermanent: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    public async Task<int> ReconcilePendingManifestsAsync(
        CancellationToken cancellationToken = default)
    {
        var reconciled = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var manifestId = await FindUnfinishedManifestAsync(cancellationToken)
                .ConfigureAwait(false);
            if (manifestId is null)
            {
                break;
            }

            try
            {
                await ProcessManifestAsync(
                        manifestId,
                        correlationId: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                reconciled++;
            }
            catch (RetentionOperationException exception)
            {
                LogManifestReconciliationFailure(manifestId, exception.ErrorCode);
                break;
            }
        }

        return reconciled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ReconcilePendingManifestsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            LogStartupReconciliationFailure(exception);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            if (now >= _nextScheduleCheckAt)
            {
                try
                {
                    await EnsureScheduledJobAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    LogScheduleFailure(exception);
                }

                _nextScheduleCheckAt = now.AddMinutes(5);
            }

            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    public Task EnsureScheduledJobAsync(
        CancellationToken cancellationToken = default)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var settings = await db.SiteSettings
                .AsNoTracking()
                .SingleAsync(token)
                .ConfigureAwait(false);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
            var now = timeProvider.GetUtcNow();
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var scheduledDate = DateOnly.FromDateTime(
                localNow.TimeOfDay >= TimeSpan.FromHours(3)
                    ? localNow.Date
                    : localNow.Date.AddDays(-1));
            var deduplicationKey =
                $"retention:scheduled:{scheduledDate:yyyyMMdd}";
            if (await db.BackgroundJobs
                    .AsNoTracking()
                    .AnyAsync(
                        job => job.DeduplicationKey == deduplicationKey,
                        token)
                    .ConfigureAwait(false))
            {
                return;
            }

            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = JobType,
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    reason = "scheduled",
                    scheduledDate,
                    requestedAt = now,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
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
            job.LeaseExpiresAt = now.Add(LeaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);

            return new JobLease(
                job.Id,
                job.Revision,
                job.CorrelationId);
        }, cancellationToken);
    }

    private async Task<string?> FindUnfinishedManifestAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.DeletionManifests
            .AsNoTracking()
            .Where(item => item.State == "pending" || item.State == "deleting")
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<string?> CreateManifestAsync(
        string backgroundJobId,
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

            var unfinished = await db.DeletionManifests
                .AsNoTracking()
                .Where(item => item.State == "pending" || item.State == "deleting")
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .Select(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (unfinished is not null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return unfinished;
            }

            var settings = await db.SiteSettings
                .AsNoTracking()
                .SingleAsync(token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            TimeZoneInfo siteTimeZone;
            try
            {
                siteTimeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
            }
            catch (TimeZoneNotFoundException exception)
            {
                throw Permanent("retention_timezone_invalid", exception);
            }
            catch (InvalidTimeZoneException exception)
            {
                throw Permanent("retention_timezone_invalid", exception);
            }

            DateTimeOffset cutoff;
            try
            {
                cutoff = CalendarMonthRetention.CalculateCutoffInstant(
                    now,
                    siteTimeZone,
                    settings.ScanRetentionCalendarMonths);
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                throw Permanent("retention_cutoff_invalid", exception);
            }

            var plan = await BuildManifestPlanAsync(
                    db,
                    cutoff,
                    settings.ManagedScanCleanupTargetBytes,
                    token)
                .ConfigureAwait(false);
            if (plan is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            var manifest = new DeletionManifestEntity
            {
                Id = UlidId.New(now),
                BackgroundJobId = backgroundJobId,
                Reason = plan.Reason,
                State = "pending",
                CutoffAt = plan.Reason == "age" ? cutoff : null,
                PlannedObjectCount = plan.PhysicalObjectCount,
                PlannedReferenceCount = plan.References.Count,
                PlannedBytes = plan.PhysicalBytes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.DeletionManifests.Add(manifest);

            var physicalMarkerByObject = plan.PhysicallyDeletableObjectIds
                .ToDictionary(
                    objectId => objectId,
                    objectId => plan.References
                        .Where(item => item.FileObjectId == objectId)
                        .OrderBy(item => item.FileReferenceId, StringComparer.Ordinal)
                        .First()
                        .FileReferenceId,
                    StringComparer.Ordinal);
            var itemOffset = 1;
            foreach (var item in plan.References)
            {
                db.DeletionManifestItems.Add(new DeletionManifestItemEntity
                {
                    Id = UlidId.New(now.AddMilliseconds(itemOffset++)),
                    DeletionManifestId = manifest.Id,
                    FileObjectId = item.FileObjectId,
                    FileReferenceId = item.FileReferenceId,
                    SubmissionId = item.SubmissionId,
                    Purpose = item.Purpose,
                    Sha256 = item.Sha256,
                    Bytes = item.Bytes,
                    StorageClass = item.StorageClass,
                    Extension = item.Extension,
                    RelativeObjectPath = item.RelativeObjectPath,
                    DeletePhysicalObject =
                        physicalMarkerByObject.TryGetValue(
                            item.FileObjectId,
                            out var markerReferenceId)
                        && markerReferenceId == item.FileReferenceId,
                    State = "pending",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            foreach (var submissionId in plan.References
                         .Select(item => item.SubmissionId)
                         .Distinct(StringComparer.Ordinal))
            {
                var submission = await db.Submissions
                    .SingleAsync(item => item.Id == submissionId, token)
                    .ConfigureAwait(false);
                submission.ScanPayloadState = "deletion_pending";
            }

            foreach (var objectId in plan.PhysicallyDeletableObjectIds)
            {
                var fileObject = await db.FileObjects
                    .SingleAsync(item => item.Id == objectId, token)
                    .ConfigureAwait(false);
                fileObject.State = "deletion_pending";
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return manifest.Id;
        }, cancellationToken);
    }

    private static async Task<ManifestPlan?> BuildManifestPlanAsync(
        OokiGraderDbContext db,
        DateTimeOffset cutoff,
        long cleanupTargetBytes,
        CancellationToken cancellationToken)
    {
        var submissions = await db.Submissions
            .AsNoTracking()
            .Where(item =>
                item.ScanPayloadState == "scan_available"
                && item.UploadCompletedAt != null)
            .Select(item => new SubmissionCandidate(
                item.Id,
                item.State,
                item.UploadCompletedAt!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (submissions.Count == 0)
        {
            return null;
        }

        var submissionIds = submissions
            .Select(item => item.Id)
            .ToArray();
        var referenceRows = await (
                from reference in db.FileReferences.AsNoTracking()
                join fileObject in db.FileObjects.AsNoTracking()
                    on reference.FileObjectId equals fileObject.Id
                where reference.OwnerType == "submission"
                    && submissionIds.Contains(reference.OwnerId)
                    && fileObject.ManagedScanBytes
                    && fileObject.State == "available"
                select new RetentionReference(
                    reference.Id,
                    reference.OwnerId,
                    reference.FileObjectId,
                    reference.Purpose,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.StorageClass,
                    fileObject.Extension,
                    fileObject.RelativeObjectPath))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (referenceRows.Count == 0)
        {
            return null;
        }

        var referencesBySubmission = referenceRows
            .GroupBy(item => item.SubmissionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var ageCandidates = submissions
            .Where(item =>
                item.UploadCompletedAt < cutoff
                && referencesBySubmission.ContainsKey(item.Id))
            .OrderBy(item => item.UploadCompletedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

        string reason;
        SubmissionCandidate[] orderedCandidates;
        long quotaBytesToFree = long.MaxValue;
        if (ageCandidates.Length > 0)
        {
            reason = "age";
            orderedCandidates = ageCandidates;
        }
        else
        {
            var managedBytes = await db.FileObjects
                .AsNoTracking()
                .Where(item =>
                    item.ManagedScanBytes
                    && item.State == "available")
                .SumAsync(item => (long?)item.Bytes, cancellationToken)
                .ConfigureAwait(false)
                ?? 0;
            if (managedBytes <= cleanupTargetBytes)
            {
                return null;
            }

            reason = "quota";
            quotaBytesToFree = managedBytes - cleanupTargetBytes;
            orderedCandidates = submissions
                .Where(item => referencesBySubmission.ContainsKey(item.Id))
                .OrderBy(item => QuotaPriority(item.State))
                .ThenBy(item => item.UploadCompletedAt)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
        }

        var selectedReferences = new List<RetentionReference>();
        var selectedObjectIds = new HashSet<string>(StringComparer.Ordinal);
        long selectedObjectBytes = 0;
        foreach (var candidate in orderedCandidates)
        {
            var candidateReferences = referencesBySubmission[candidate.Id];
            var newObjects = candidateReferences
                .Where(item => !selectedObjectIds.Contains(item.FileObjectId))
                .GroupBy(item => item.FileObjectId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var projectedObjectCount = selectedObjectIds.Count + newObjects.Length;
            var projectedBytes = checked(
                selectedObjectBytes + newObjects.Sum(item => item.Bytes));
            if (selectedReferences.Count > 0
                && (projectedObjectCount > MaximumManifestObjects
                    || projectedBytes > MaximumManifestBytes))
            {
                break;
            }

            selectedReferences.AddRange(candidateReferences);
            foreach (var newObject in newObjects)
            {
                selectedObjectIds.Add(newObject.FileObjectId);
                selectedObjectBytes = checked(selectedObjectBytes + newObject.Bytes);
            }

            if (reason == "quota" && selectedObjectBytes >= quotaBytesToFree)
            {
                break;
            }
        }

        if (selectedReferences.Count == 0)
        {
            return null;
        }

        var allReferencesForObjects = await db.FileReferences
            .AsNoTracking()
            .Where(item => selectedObjectIds.Contains(item.FileObjectId))
            .Select(item => new { item.Id, item.FileObjectId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedReferenceIds = selectedReferences
            .Select(item => item.FileReferenceId)
            .ToHashSet(StringComparer.Ordinal);
        var physicallyDeletableObjectIds = selectedObjectIds
            .Where(objectId => allReferencesForObjects
                .Where(item => item.FileObjectId == objectId)
                .All(item => selectedReferenceIds.Contains(item.Id)))
            .ToHashSet(StringComparer.Ordinal);
        var physicalBytes = selectedReferences
            .Where(item => physicallyDeletableObjectIds.Contains(item.FileObjectId))
            .GroupBy(item => item.FileObjectId, StringComparer.Ordinal)
            .Sum(group => group.First().Bytes);

        return new ManifestPlan(
            reason,
            selectedReferences,
            physicallyDeletableObjectIds,
            physicallyDeletableObjectIds.Count,
            physicalBytes);
    }

    private async Task<ManifestResult> ProcessManifestAsync(
        string manifestId,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var pendingItems = await PrepareManifestAsync(
                manifestId,
                cancellationToken)
            .ConfigureAwait(false);
        var outcomes = new Dictionary<string, FileDeletionOutcome>(
            StringComparer.Ordinal);

        foreach (var item in pendingItems.Where(item => item.DeletePhysicalObject))
        {
            try
            {
                outcomes[item.Id] = await DeleteVerifiedObjectAsync(
                        item,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RetentionOperationException exception)
            {
                await RecordManifestFailureAsync(
                        manifestId,
                        item.Id,
                        exception.ErrorCode,
                        outcomes,
                        correlationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or CryptographicException)
            {
                const string errorCode = "retention_file_delete_failed";
                await RecordManifestFailureAsync(
                        manifestId,
                        item.Id,
                        errorCode,
                        outcomes,
                        correlationId,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw Transient(errorCode, exception);
            }
        }

        return await FinalizeManifestAsync(
                manifestId,
                outcomes,
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<DeletionManifestItemEntity[]> PrepareManifestAsync(
        string manifestId,
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
            var manifest = await db.DeletionManifests
                .Include(item => item.Items)
                .SingleOrDefaultAsync(item => item.Id == manifestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("retention_manifest_missing");
            if (manifest.State == "completed")
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return [];
            }

            var now = timeProvider.GetUtcNow();
            manifest.State = "deleting";
            manifest.StartedAt ??= now;
            manifest.SafeErrorDetail = null;

            var submissionIds = manifest.Items
                .Select(item => item.SubmissionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var submissions = await db.Submissions
                .Where(item => submissionIds.Contains(item.Id))
                .ToListAsync(token)
                .ConfigureAwait(false);
            foreach (var submission in submissions)
            {
                if (submission.ScanPayloadState != "scan_deleted")
                {
                    submission.ScanPayloadState = "deletion_pending";
                }
            }

            var physicalObjectIds = manifest.Items
                .Where(item =>
                    item.DeletePhysicalObject
                    && item.State is not ("deleted" or "already_missing"
                        or "reference_released"))
                .Select(item => item.FileObjectId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var fileObjects = await db.FileObjects
                .Where(item => physicalObjectIds.Contains(item.Id))
                .ToListAsync(token)
                .ConfigureAwait(false);
            foreach (var fileObject in fileObjects)
            {
                if (fileObject.State is not ("deleted" or "missing"))
                {
                    fileObject.State = "deletion_pending";
                }
            }

            foreach (var item in manifest.Items.Where(item => item.State == "failed"))
            {
                item.State = "pending";
                item.ErrorCode = null;
                item.Outcome = null;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return manifest.Items
                .Where(item =>
                    item.State is not ("deleted" or "already_missing"
                        or "reference_released"))
                .Select(ToDetachedItem)
                .ToArray();
        }, cancellationToken);
    }

    private async Task<FileDeletionOutcome> DeleteVerifiedObjectAsync(
        DeletionManifestItemEntity item,
        CancellationToken cancellationToken)
    {
        var locator = CreateValidatedLocator(item);
        if (await HasUnselectedReferenceAsync(item, cancellationToken)
                .ConfigureAwait(false))
        {
            return FileDeletionOutcome.Shared;
        }

        Stream stream;
        try
        {
            stream = await contentStore.OpenReadAsync(locator, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return FileDeletionOutcome.AlreadyMissing;
        }

        await using (stream.ConfigureAwait(false))
        {
            var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)
                        .ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualHash,
                    item.Sha256,
                    StringComparison.Ordinal))
            {
                throw Permanent("retention_object_hash_mismatch");
            }
        }

        await contentStore.DeleteAsync(locator, cancellationToken)
            .ConfigureAwait(false);
        if (await contentStore.ExistsAsync(locator, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Transient("retention_object_delete_not_confirmed");
        }

        return FileDeletionOutcome.Deleted;
    }

    private async Task<bool> HasUnselectedReferenceAsync(
        DeletionManifestItemEntity item,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.FileReferences
            .AsNoTracking()
            .Where(reference => reference.FileObjectId == item.FileObjectId)
            .AnyAsync(
                reference => !db.DeletionManifestItems.Any(manifestItem =>
                    manifestItem.DeletionManifestId == item.DeletionManifestId
                    && manifestItem.FileReferenceId == reference.Id),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<ManifestResult> FinalizeManifestAsync(
        string manifestId,
        IReadOnlyDictionary<string, FileDeletionOutcome> outcomes,
        string? correlationId,
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
            var manifest = await db.DeletionManifests
                .Include(item => item.Items)
                .SingleAsync(item => item.Id == manifestId, token)
                .ConfigureAwait(false);
            if (manifest.State == "completed")
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return ToResult(manifest);
            }

            var now = timeProvider.GetUtcNow();
            foreach (var item in manifest.Items.Where(item =>
                         item.State is not ("deleted" or "already_missing"
                             or "reference_released")))
            {
                var outcome = FileDeletionOutcome.Shared;
                if (item.DeletePhysicalObject
                    && !outcomes.TryGetValue(item.Id, out outcome))
                {
                    throw Transient("retention_manifest_outcome_missing");
                }

                item.AttemptCount = checked(item.AttemptCount + 1);
                item.DeletedAt = now;
                item.ErrorCode = null;
                if (!item.DeletePhysicalObject
                    || outcome == FileDeletionOutcome.Shared)
                {
                    item.State = "reference_released";
                    item.Outcome = "shared_object_retained";
                }
                else if (outcome == FileDeletionOutcome.AlreadyMissing)
                {
                    item.State = "already_missing";
                    item.Outcome = "already_missing_reconciled";
                }
                else
                {
                    item.State = "deleted";
                    item.Outcome = "verified_deleted";
                }
            }

            var referenceIds = manifest.Items
                .Select(item => item.FileReferenceId)
                .ToArray();
            var references = await db.FileReferences
                .Where(item => referenceIds.Contains(item.Id))
                .ToListAsync(token)
                .ConfigureAwait(false);
            db.FileReferences.RemoveRange(references);

            var submissionIds = manifest.Items
                .Select(item => item.SubmissionId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var submissions = await db.Submissions
                .Where(item => submissionIds.Contains(item.Id))
                .ToListAsync(token)
                .ConfigureAwait(false);
            var itemObjectIds = manifest.Items
                .Select(item => item.FileObjectId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var submission in submissions)
            {
                if (submission.OriginalFileObjectId is not null
                    && itemObjectIds.Contains(submission.OriginalFileObjectId))
                {
                    submission.OriginalFileObjectId = null;
                }

                submission.ScanPayloadState = "scan_deleted";
                submission.ScanDeletedAt = now;
                submission.ScanDeletionReason = manifest.Reason;
            }

            var objectIds = manifest.Items
                .Select(item => item.FileObjectId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var objects = await db.FileObjects
                .Where(item => objectIds.Contains(item.Id))
                .ToListAsync(token)
                .ConfigureAwait(false);
            var removedReferenceCounts = references
                .GroupBy(item => item.FileObjectId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Count(),
                    StringComparer.Ordinal);
            foreach (var fileObject in objects)
            {
                var persistedReferenceCount = await db.FileReferences
                    .CountAsync(
                        item => item.FileObjectId == fileObject.Id,
                        token)
                    .ConfigureAwait(false);
                removedReferenceCounts.TryGetValue(
                    fileObject.Id,
                    out var removedReferenceCount);
                var remainingReferenceCount = Math.Max(
                    0,
                    persistedReferenceCount - removedReferenceCount);
                fileObject.ReferenceCountCache = remainingReferenceCount;
                if (remainingReferenceCount == 0)
                {
                    fileObject.State = "deleted";
                    fileObject.DeletedAt = now;
                }
                else if (fileObject.State == "deletion_pending")
                {
                    fileObject.State = "available";
                }
            }

            manifest.State = "completed";
            manifest.CompletedAt = now;
            manifest.DeletedObjectCount = manifest.Items
                .Count(item => item.State == "deleted");
            manifest.MissingObjectCount = manifest.Items
                .Count(item => item.State == "already_missing");
            manifest.ReleasedReferenceCount = manifest.Items.Count;
            manifest.FailureCount = manifest.Items
                .Count(item => item.State == "failed");
            manifest.DeletedBytes = manifest.Items
                .Where(item => item.State is "deleted" or "already_missing")
                .GroupBy(item => item.FileObjectId, StringComparer.Ordinal)
                .Sum(group => group.First().Bytes);
            manifest.SafeErrorDetail = null;

            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = "retention.manifest.completed",
                ObjectType = "deletion_manifest",
                ObjectId = manifest.Id,
                Outcome = "succeeded",
                ReasonCode = manifest.Reason,
                CorrelationId = correlationId,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    manifest.Reason,
                    manifest.PlannedObjectCount,
                    manifest.PlannedReferenceCount,
                    manifest.DeletedObjectCount,
                    manifest.MissingObjectCount,
                    manifest.ReleasedReferenceCount,
                    manifest.DeletedBytes,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return ToResult(manifest);
        }, cancellationToken);
    }

    private Task RecordManifestFailureAsync(
        string manifestId,
        string failedItemId,
        string errorCode,
        IReadOnlyDictionary<string, FileDeletionOutcome> outcomes,
        string? correlationId,
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
            var manifest = await db.DeletionManifests
                .Include(item => item.Items)
                .SingleAsync(item => item.Id == manifestId, token)
                .ConfigureAwait(false);
            var failedItem = manifest.Items.Single(item => item.Id == failedItemId);
            failedItem.State = "failed";
            failedItem.ErrorCode = errorCode;
            failedItem.Outcome = "filesystem_operation_failed";
            failedItem.AttemptCount = checked(failedItem.AttemptCount + 1);
            manifest.FailureCount = checked(manifest.FailureCount + 1);
            manifest.SafeErrorDetail =
                "A verified content-store operation failed; retention will reconcile safely.";

            var hasIrreversibleOutcome = outcomes.Values.Any(outcome =>
                    outcome is FileDeletionOutcome.Deleted
                        or FileDeletionOutcome.AlreadyMissing)
                || manifest.Items.Any(item =>
                    item.State is "deleted" or "already_missing");
            var now = timeProvider.GetUtcNow();
            if (!hasIrreversibleOutcome)
            {
                manifest.State = "failed";
                manifest.CompletedAt = now;

                var submissionIds = manifest.Items
                    .Select(item => item.SubmissionId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var submissions = await db.Submissions
                    .Where(item => submissionIds.Contains(item.Id))
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                foreach (var submission in submissions.Where(item =>
                             item.ScanPayloadState == "deletion_pending"))
                {
                    submission.ScanPayloadState = "scan_available";
                }

                var objectIds = manifest.Items
                    .Where(item => item.DeletePhysicalObject)
                    .Select(item => item.FileObjectId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var fileObjects = await db.FileObjects
                    .Where(item => objectIds.Contains(item.Id))
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                foreach (var fileObject in fileObjects.Where(item =>
                             item.State == "deletion_pending"))
                {
                    fileObject.State = "available";
                }
            }

            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                EventType = "retention.manifest.failed",
                ObjectType = "deletion_manifest",
                ObjectId = manifest.Id,
                Outcome = hasIrreversibleOutcome ? "partial" : "failed",
                ReasonCode = errorCode,
                CorrelationId = correlationId,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    manifest.Reason,
                    failedItemId,
                    errorCode,
                    requiresReconciliation = hasIrreversibleOutcome,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task CompleteJobAsync(
        JobLease lease,
        ManifestResult? manifest,
        bool queueContinuation,
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
            var job = await LoadOwnedJobAsync(db, lease, token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();

            if (queueContinuation && manifest is not null)
            {
                var deduplicationKey = $"retention:continuation:{manifest.Id}";
                if (!await db.BackgroundJobs.AnyAsync(
                        item => item.DeduplicationKey == deduplicationKey,
                        token)
                        .ConfigureAwait(false))
                {
                    db.BackgroundJobs.Add(new BackgroundJobEntity
                    {
                        Id = UlidId.New(now.AddMilliseconds(1)),
                        Type = JobType,
                        SchemaVersion = 1,
                        DeduplicationKey = deduplicationKey,
                        Priority = job.Priority,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            reason = "continuation",
                            previousManifestId = manifest.Id,
                        }),
                        State = "queued",
                        MaxAttempts = job.MaxAttempts,
                        NextAttemptAt = now,
                        CorrelationId = job.CorrelationId,
                        CausationId = job.Id,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
            }

            job.State = "succeeded";
            job.ProgressBasisPoints = 10_000;
            job.CompletedAt = now;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now.AddMilliseconds(2)),
                OccurredAt = now,
                EventType = "retention.run.completed",
                ObjectType = "background_job",
                ObjectId = job.Id,
                Outcome = "succeeded",
                CorrelationId = job.CorrelationId,
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    manifestId = manifest?.Id,
                    reason = manifest?.Reason,
                    deletedObjects = manifest?.DeletedObjectCount ?? 0,
                    releasedReferences = manifest?.ReleasedReferenceCount ?? 0,
                    deletedBytes = manifest?.DeletedBytes ?? 0,
                    continuationQueued = queueContinuation && manifest is not null,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task FailJobAsync(
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

            var now = timeProvider.GetUtcNow();
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = errorCode;
            job.SafeErrorDetail =
                "Retention could not safely complete this attempt.";
            if (!isPermanent && job.AttemptCount < job.MaxAttempts)
            {
                job.State = "retry_waiting";
                job.NextAttemptAt = now.Add(RetryDelay(job.AttemptCount));
            }
            else
            {
                job.State = "failed";
                job.CompletedAt = now;
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
            ?? throw Permanent("retention_job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= timeProvider.GetUtcNow())
        {
            throw Permanent("retention_job_lease_lost");
        }

        return job;
    }

    private static ContentObjectLocator CreateValidatedLocator(
        DeletionManifestItemEntity item)
    {
        if (!Enum.TryParse<ContentStorageClass>(
                item.StorageClass,
                ignoreCase: false,
                out var storageClass))
        {
            storageClass = item.StorageClass switch
            {
                "managed_scan_original" => ContentStorageClass.ManagedScanOriginal,
                "managed_scan_derived" => ContentStorageClass.ManagedScanDerived,
                _ => throw Permanent("retention_storage_class_invalid"),
            };
        }

        if (item.Sha256.Length != 64
            || item.Sha256.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw Permanent("retention_object_hash_invalid");
        }

        var expectedPath = BuildExpectedRelativePath(
            storageClass,
            item.Sha256,
            item.Extension);
        var storedPath = item.RelativeObjectPath.Replace('\\', '/');
        if (!string.Equals(expectedPath, storedPath, StringComparison.Ordinal))
        {
            throw Permanent("retention_object_path_mismatch");
        }

        return new ContentObjectLocator(
            storageClass,
            item.Sha256,
            item.Bytes,
            item.Extension);
    }

    private static string BuildExpectedRelativePath(
        ContentStorageClass storageClass,
        string sha256,
        string extension)
    {
        var classFolder = storageClass switch
        {
            ContentStorageClass.ManagedScanOriginal => "scan/original",
            ContentStorageClass.ManagedScanDerived => "scan/derived",
            _ => throw Permanent("retention_storage_class_not_managed"),
        };
        var normalizedExtension = extension.Trim().TrimStart('.').ToLowerInvariant();
        if (normalizedExtension.Length == 0
            || normalizedExtension.Length > 24
            || normalizedExtension.Any(character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_')))
        {
            throw Permanent("retention_object_extension_invalid");
        }

        return $"{classFolder}/{sha256[..2]}/{sha256[2..4]}/" +
            $"{sha256}.{normalizedExtension}";
    }

    private static DeletionManifestItemEntity ToDetachedItem(
        DeletionManifestItemEntity item) =>
        new()
        {
            Id = item.Id,
            DeletionManifestId = item.DeletionManifestId,
            FileObjectId = item.FileObjectId,
            FileReferenceId = item.FileReferenceId,
            SubmissionId = item.SubmissionId,
            Purpose = item.Purpose,
            Sha256 = item.Sha256,
            Bytes = item.Bytes,
            StorageClass = item.StorageClass,
            Extension = item.Extension,
            RelativeObjectPath = item.RelativeObjectPath,
            DeletePhysicalObject = item.DeletePhysicalObject,
            State = item.State,
            Outcome = item.Outcome,
            ErrorCode = item.ErrorCode,
            AttemptCount = item.AttemptCount,
            CreatedAt = item.CreatedAt,
            DeletedAt = item.DeletedAt,
            UpdatedAt = item.UpdatedAt,
            Revision = item.Revision,
        };

    private static int QuotaPriority(string state)
    {
        return state switch
        {
            "finalized" or "voided" => 0,
            "ready_to_finalize" or "needs_grade_review" or "needs_name_review" => 1,
            _ => 2,
        };
    }

    private static TimeSpan RetryDelay(int attemptCount)
    {
        return attemptCount switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(10),
        };
    }

    private static ManifestResult ToResult(DeletionManifestEntity manifest) =>
        new(
            manifest.Id,
            manifest.Reason,
            manifest.DeletedObjectCount,
            manifest.ReleasedReferenceCount,
            manifest.DeletedBytes);

    private static RetentionOperationException Permanent(
        string errorCode,
        Exception? innerException = null) =>
        new(errorCode, isPermanent: true, innerException);

    private static RetentionOperationException Transient(
        string errorCode,
        Exception? innerException = null) =>
        new(errorCode, isPermanent: false, innerException);

    [LoggerMessage(
        EventId = 7201,
        Level = LogLevel.Warning,
        Message = "Retention job {JobId} failed safely with {ErrorCode}.")]
    private partial void LogRetentionFailure(string jobId, string errorCode);

    [LoggerMessage(
        EventId = 7202,
        Level = LogLevel.Warning,
        Message =
            "Retention manifest {ManifestId} could not be reconciled: {ErrorCode}.")]
    private partial void LogManifestReconciliationFailure(
        string manifestId,
        string errorCode);

    [LoggerMessage(
        EventId = 7203,
        Level = LogLevel.Error,
        Message = "Unexpected failure while processing retention job {JobId}.")]
    private partial void LogUnexpectedJobFailure(
        Exception exception,
        string jobId);

    [LoggerMessage(
        EventId = 7204,
        Level = LogLevel.Error,
        Message =
            "Retention startup reconciliation failed; queued work will retry it.")]
    private partial void LogStartupReconciliationFailure(Exception exception);

    [LoggerMessage(
        EventId = 7205,
        Level = LogLevel.Error,
        Message = "Retention schedule check failed; the worker will retry it.")]
    private partial void LogScheduleFailure(Exception exception);

    private sealed record JobLease(
        string Id,
        long Revision,
        string? CorrelationId);

    private sealed record SubmissionCandidate(
        string Id,
        string State,
        DateTimeOffset UploadCompletedAt);

    private sealed record RetentionReference(
        string FileReferenceId,
        string SubmissionId,
        string FileObjectId,
        string Purpose,
        string Sha256,
        long Bytes,
        string StorageClass,
        string Extension,
        string RelativeObjectPath);

    private sealed record ManifestPlan(
        string Reason,
        IReadOnlyList<RetentionReference> References,
        IReadOnlySet<string> PhysicallyDeletableObjectIds,
        int PhysicalObjectCount,
        long PhysicalBytes);

    private sealed record ManifestResult(
        string Id,
        string Reason,
        int DeletedObjectCount,
        int ReleasedReferenceCount,
        long DeletedBytes);

    private enum FileDeletionOutcome
    {
        Deleted,
        AlreadyMissing,
        Shared,
    }

    private sealed class RetentionOperationException(
        string errorCode,
        bool isPermanent,
        Exception? innerException = null)
        : Exception(errorCode, innerException)
    {
        public string ErrorCode { get; } = errorCode;
        public bool IsPermanent { get; } = isPermanent;
    }
}
