using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed partial class BackupJobWorker(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IBackupArchiveService archiveService,
    IBackupRetentionService retentionService,
    BackupOptions backupOptions,
    BackupJobCoordinator coordinator,
    IAuditSink auditSink,
    TimeProvider timeProvider,
    ILogger<BackupJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromHours(4);
    private readonly string _workerId = $"backup-{Guid.NewGuid():N}";
    private DateTimeOffset _nextScheduleCheckAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextRetentionCheckAt = DateTimeOffset.MinValue;

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
            var backupId = ParseBackupId(lease.PayloadJson);
            if (lease.Type == BackupJobTypes.Create)
            {
                await ProcessCreateAsync(lease, backupId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ProcessVerificationAsync(lease, backupId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (BackupOperationException exception)
        {
            LogBackupFailure(lease.JobId, exception.ErrorCode);
            await FailAsync(
                    lease,
                    exception.ErrorCode,
                    exception.SafeDetail,
                    exception.IsConfigurationError,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SqliteException
                or InvalidDataException
                or JsonException)
        {
            LogUnexpectedFailure(exception, lease.JobId);
            await FailAsync(
                    lease,
                    "backup_worker_error",
                    "The backup operation failed and will be retried when safe.",
                    blocked: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogUnexpectedFailure(exception, lease.JobId);
            await FailAsync(
                    lease,
                    "backup_worker_error",
                    "The backup operation failed and will be retried when safe.",
                    blocked: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            if (now >= _nextScheduleCheckAt)
            {
                try
                {
                    await coordinator.EnsureScheduledAsync(stoppingToken)
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

            if (now >= _nextRetentionCheckAt)
            {
                await TryPruneBackupsAsync(stoppingToken).ConfigureAwait(false);
                _nextRetentionCheckAt = now.AddHours(1);
            }

            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessCreateAsync(
        JobLease lease,
        string backupId,
        CancellationToken cancellationToken)
    {
        var request = await MarkRunningAndReadRequestAsync(
                lease,
                backupId,
                BackupStates.Running,
                cancellationToken)
            .ConfigureAwait(false);

        // SQLite online backup and filesystem I/O deliberately run without holding
        // the application write coordinator.
        var result = await archiveService.CreateAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);

        await CompleteCreateAsync(lease, result, cancellationToken)
            .ConfigureAwait(false);
        await TryPruneBackupsAsync(cancellationToken).ConfigureAwait(false);
        await TryAuditAsync(
                new AuditWrite(
                    "backup.created",
                    "backup",
                    backupId,
                    "success",
                    request.Trigger == "manual"
                        ? await ReadCreatedByAsync(backupId, cancellationToken)
                            .ConfigureAwait(false)
                        : null,
                    lease.CorrelationId,
                    SafeMetadataJson: JsonSerializer.Serialize(new
                    {
                        result.ManifestSha256,
                        result.DatabaseBytes,
                        result.ObjectCount,
                        managedScansIncluded =
                            archiveService.GetConfigurationStatus()
                                .IncludeManagedScans,
                    })),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessVerificationAsync(
        JobLease lease,
        string backupId,
        CancellationToken cancellationToken)
    {
        var snapshot = await MarkVerifyingAndReadAsync(
                lease,
                backupId,
                cancellationToken)
            .ConfigureAwait(false);
        var result = await archiveService.VerifyAsync(
                backupId,
                snapshot.DestinationRelativePath,
                snapshot.ManifestSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Verified)
        {
            throw new BackupOperationException(
                result.ErrorCode ?? "backup_verification_failed",
                result.SafeErrorDetail ?? "The backup verification failed.");
        }

        await CompleteVerificationAsync(lease, backupId, result, cancellationToken)
            .ConfigureAwait(false);
        await TryPruneBackupsAsync(cancellationToken).ConfigureAwait(false);
        await TryAuditAsync(
                new AuditWrite(
                    "backup.verified",
                    "backup",
                    backupId,
                    "success",
                    snapshot.CreatedByStaffUserId,
                    lease.CorrelationId,
                    SafeMetadataJson: JsonSerializer.Serialize(new
                    {
                        result.ManifestSha256,
                        result.VerifiedFileCount,
                        result.VerifiedBytes,
                    })),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryPruneBackupsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await ReadRetentionCandidatesAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            // Manifest inspection, hashing, and directory deletion deliberately
            // run without holding the serialized database write coordinator.
            var result = await retentionService.PruneAsync(
                    candidates,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExpiredBackupIds.Count > 0)
            {
                await MarkExpiredAsync(
                        result.ExpiredBackupIds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result.ExpiredBackupIds.Count > 0
                || result.Failures.Count > 0)
            {
                await TryAuditAsync(
                        new AuditWrite(
                            "backup.retention.completed",
                            "backup_policy",
                            "default",
                            result.Failures.Count == 0
                                ? "success"
                                : "partial",
                            SafeMetadataJson: JsonSerializer.Serialize(new
                            {
                                expiredCount =
                                    result.ExpiredBackupIds.Count,
                                failureCount = result.Failures.Count,
                                failureCodes = result.Failures
                                    .Select(failure => failure.ErrorCode)
                                    .Distinct(StringComparer.Ordinal)
                                    .Order(StringComparer.Ordinal)
                                    .Take(20)
                                    .ToArray(),
                            })),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogRetentionFailure(exception);
        }
    }

    private async Task<IReadOnlyList<BackupRetentionCandidate>>
        ReadRetentionCandidatesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var query = db.BackupRecords
            .AsNoTracking()
            .Where(record =>
                record.State == BackupStates.Verified
                && record.CompletedAt != null
                && record.DestinationRelativePath != null
                && record.ManifestSha256 != null);
        var recentLimit = checked(
            (backupOptions.MaximumRetentionCandidates + 1) / 2);
        var oldestLimit = checked(
            backupOptions.MaximumRetentionCandidates - recentLimit);
        var recent = await query
            .OrderByDescending(record => record.CompletedAt)
            .ThenByDescending(record => record.Id)
            .Take(recentLimit)
            .Select(record => new RetentionCandidateRow(
                record.Id,
                record.DestinationRelativePath!,
                record.ManifestSha256!,
                record.CompletedAt!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var oldest = oldestLimit == 0
            ? []
            : await query
                .OrderBy(record => record.CompletedAt)
                .ThenBy(record => record.Id)
                .Take(oldestLimit)
                .Select(record => new RetentionCandidateRow(
                    record.Id,
                    record.DestinationRelativePath!,
                    record.ManifestSha256!,
                    record.CompletedAt!.Value))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        return recent
            .Concat(oldest)
            .DistinctBy(row => row.BackupId, StringComparer.Ordinal)
            .Select(row => new BackupRetentionCandidate(
                row.BackupId,
                row.DestinationRelativePath,
                row.ManifestSha256,
                row.CompletedAt))
            .ToArray();
    }

    private Task MarkExpiredAsync(
        IReadOnlyList<string> backupIds,
        CancellationToken cancellationToken)
    {
        var ids = backupIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var records = await db.BackupRecords
                .Where(record =>
                    ids.Contains(record.Id)
                    && record.State == BackupStates.Verified)
                .ToListAsync(token)
                .ConfigureAwait(false);
            foreach (var record in records)
            {
                record.State = BackupStates.Expired;
                record.ErrorCode = null;
                record.SafeErrorDetail = null;
            }

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
                    (item.Type == BackupJobTypes.Create
                        || item.Type == BackupJobTypes.Verify)
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
                job.Type,
                job.PayloadJson,
                job.AttemptCount,
                job.MaxAttempts,
                job.Revision,
                job.CorrelationId);
        }, cancellationToken);
    }

    private Task<BackupCreationRequest> MarkRunningAndReadRequestAsync(
        JobLease lease,
        string backupId,
        string state,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == backupId, token)
                .ConfigureAwait(false);
            EnsureLease(await db.BackgroundJobs.SingleAsync(
                item => item.Id == lease.JobId,
                token), lease);
            var now = timeProvider.GetUtcNow();
            record.State = state;
            record.StartedAt ??= now;
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return new BackupCreationRequest(
                record.Id,
                record.Trigger,
                record.RequestedAt);
        }, cancellationToken);
    }

    private Task<VerificationSnapshot> MarkVerifyingAndReadAsync(
        JobLease lease,
        string backupId,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == backupId, token)
                .ConfigureAwait(false);
            EnsureLease(await db.BackgroundJobs.SingleAsync(
                item => item.Id == lease.JobId,
                token), lease);
            if (record.DestinationRelativePath is null
                || record.ManifestSha256 is null)
            {
                throw new BackupOperationException(
                    "backup_manifest_missing",
                    "The backup has no completed manifest.");
            }

            record.State = BackupStates.Verifying;
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return new VerificationSnapshot(
                record.DestinationRelativePath,
                record.ManifestSha256,
                record.CreatedByStaffUserId);
        }, cancellationToken);
    }

    private Task CompleteCreateAsync(
        JobLease lease,
        BackupCreationResult result,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleAsync(item => item.Id == lease.JobId, token)
                .ConfigureAwait(false);
            EnsureLease(job, lease);
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == result.BackupId, token)
                .ConfigureAwait(false);
            ApplyResult(record, result);
            CompleteJob(job, result.CompletedAt);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task CompleteVerificationAsync(
        JobLease lease,
        string backupId,
        BackupVerificationResult result,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleAsync(item => item.Id == lease.JobId, token)
                .ConfigureAwait(false);
            EnsureLease(job, lease);
            var record = await db.BackupRecords
                .SingleAsync(item => item.Id == backupId, token)
                .ConfigureAwait(false);
            record.State = BackupStates.Verified;
            record.ManifestSha256 = result.ManifestSha256;
            record.IntegrityResult = result.IntegrityResult;
            record.LastVerificationAt = result.CheckedAt;
            record.VerifiedAt = result.CheckedAt;
            record.ErrorCode = null;
            record.SafeErrorDetail = null;
            CompleteJob(job, result.CheckedAt);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task FailAsync(
        JobLease lease,
        string errorCode,
        string safeDetail,
        bool blocked,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == lease.JobId, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId)
            {
                return;
            }

            var backupId = ParseBackupId(job.PayloadJson);
            var record = await db.BackupRecords
                .SingleOrDefaultAsync(item => item.Id == backupId, token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            if (record is not null)
            {
                record.State = BackupStates.Failed;
                record.ErrorCode = Bound(errorCode, 200);
                record.SafeErrorDetail = Bound(safeDetail, 2_000);
                record.CompletedAt = now;
            }

            job.ErrorCode = Bound(errorCode, 200);
            job.SafeErrorDetail = Bound(safeDetail, 2_000);
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            if (blocked)
            {
                job.State = "blocked";
            }
            else if (job.AttemptCount < job.MaxAttempts)
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

    private async Task<string?> ReadCreatedByAsync(
        string backupId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        return await db.BackupRecords
            .AsNoTracking()
            .Where(record => record.Id == backupId)
            .Select(record => record.CreatedByStaffUserId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryAuditAsync(
        AuditWrite auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await auditSink.AppendAsync(auditEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogAuditFailure(exception, auditEvent.ObjectId);
        }
    }

    private static void ApplyResult(
        BackupRecordEntity record,
        BackupCreationResult result)
    {
        record.State = BackupStates.Verified;
        record.DestinationRelativePath = result.DestinationRelativePath;
        record.ManifestSha256 = result.ManifestSha256;
        record.DatabaseSha256 = result.DatabaseSha256;
        record.DatabaseBytes = result.DatabaseBytes;
        record.ObjectCount = result.ObjectCount;
        record.ObjectBytes = result.ObjectBytes;
        record.SecretEnvelopeCount = result.SecretEnvelopeCount;
        record.SecretEnvelopeBytes = result.SecretEnvelopeBytes;
        record.DatabaseMigrationId = result.DatabaseMigrationId;
        record.DatabaseDataVersion = result.DatabaseDataVersion;
        record.ApplicationVersion = typeof(Program).Assembly
            .GetName()
            .Version?
            .ToString();
        record.IntegrityResult = result.Verification.IntegrityResult;
        record.LastVerificationAt = result.Verification.CheckedAt;
        record.CompletedAt = result.CompletedAt;
        record.VerifiedAt = result.Verification.CheckedAt;
        record.ErrorCode = null;
        record.SafeErrorDetail = null;
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

    private void EnsureLease(BackgroundJobEntity job, JobLease lease)
    {
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.Revision != lease.Revision
            || job.LeaseExpiresAt <= timeProvider.GetUtcNow())
        {
            throw new BackupOperationException(
                "backup_job_lease_lost",
                "The backup worker no longer owns the durable job lease.");
        }
    }

    private static string ParseBackupId(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("backupId", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new BackupOperationException(
                "backup_job_payload_invalid",
                "The backup job payload is invalid.");
        }

        var backupId = value.GetString();
        if (backupId is null
            || !OokiGrader.Application.Identifiers.UlidId.IsCanonical(backupId))
        {
            throw new BackupOperationException(
                "backup_job_payload_invalid",
                "The backup job identifier is invalid.");
        }

        return backupId;
    }

    private static TimeSpan RetryDelay(int attempt) => attempt switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30),
    };

    private static string Bound(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [LoggerMessage(
        EventId = 1_601,
        Level = LogLevel.Warning,
        Message = "Backup job {JobId} failed with {ErrorCode}.")]
    private partial void LogBackupFailure(string jobId, string errorCode);

    [LoggerMessage(
        EventId = 1_602,
        Level = LogLevel.Error,
        Message = "Backup job {JobId} failed unexpectedly.")]
    private partial void LogUnexpectedFailure(Exception exception, string jobId);

    [LoggerMessage(
        EventId = 1_603,
        Level = LogLevel.Error,
        Message = "The scheduled backup check failed.")]
    private partial void LogScheduleFailure(Exception exception);

    [LoggerMessage(
        EventId = 1_604,
        Level = LogLevel.Error,
        Message = "The audit event for backup {BackupId} could not be appended.")]
    private partial void LogAuditFailure(Exception exception, string backupId);

    [LoggerMessage(
        EventId = 1_605,
        Level = LogLevel.Error,
        Message = "Backup retention reconciliation failed.")]
    private partial void LogRetentionFailure(Exception exception);

    private sealed record JobLease(
        string JobId,
        string Type,
        string PayloadJson,
        int AttemptCount,
        int MaxAttempts,
        long Revision,
        string? CorrelationId);

    private sealed record VerificationSnapshot(
        string DestinationRelativePath,
        string ManifestSha256,
        string? CreatedByStaffUserId);

    private sealed record RetentionCandidateRow(
        string BackupId,
        string DestinationRelativePath,
        string ManifestSha256,
        DateTimeOffset CompletedAt);
}
