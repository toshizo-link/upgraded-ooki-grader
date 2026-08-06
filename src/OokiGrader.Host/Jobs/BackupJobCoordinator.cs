using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public static class BackupJobTypes
{
    public const string Create = "backup.create";
    public const string Verify = "backup.verify";
}

public sealed record BackupEnqueueResult(
    string BackupId,
    string JobId,
    string State,
    bool Created);

public sealed class BackupJobCoordinator(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IBackupArchiveService archiveService,
    BackupOptions options,
    TimeProvider timeProvider)
{
    public BackupConfigurationStatus Configuration =>
        archiveService.GetConfigurationStatus();

    public Task<BackupEnqueueResult> EnqueueManualAsync(
        string? actorStaffUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var backupId = UlidId.New(now);
        return EnqueueCreateAsync(
            backupId,
            trigger: "manual",
            actorStaffUserId,
            correlationId,
            $"backup:manual:{backupId}",
            priority: 100,
            cancellationToken);
    }

    public async Task<BackupEnqueueResult?> EnsureScheduledAsync(
        CancellationToken cancellationToken = default)
    {
        var status = Configuration;
        if (!status.Enabled
            || !status.Configured
            || !status.EncryptionConfirmed)
        {
            return null;
        }

        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var timeZoneId = await db.SiteSettings
            .AsNoTracking()
            .Select(settings => settings.TimeZone)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var now = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var schedule = new TimeSpan(
            status.ScheduleLocalHour,
            status.ScheduleLocalMinute,
            0);
        var scheduledDate = DateOnly.FromDateTime(
            localNow.TimeOfDay >= schedule
                ? localNow.Date
                : localNow.Date.AddDays(-1));
        var existing = await db.BackgroundJobs
            .AsNoTracking()
            .Where(job =>
                job.Type == BackupJobTypes.Create
                && job.DeduplicationKey
                    == $"backup:scheduled:{scheduledDate:yyyyMMdd}")
            .Select(job => new
            {
                job.Id,
                job.State,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            var backupId = await db.BackupRecords
                .AsNoTracking()
                .Where(record => record.BackgroundJobId == existing.Id)
                .Select(record => record.Id)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false);
            return new BackupEnqueueResult(
                backupId,
                existing.Id,
                existing.State,
                Created: false);
        }

        return await EnqueueCreateAsync(
                UlidId.New(now),
                trigger: "scheduled",
                actorStaffUserId: null,
                correlationId: null,
                $"backup:scheduled:{scheduledDate:yyyyMMdd}",
                priority: 10,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BackupEnqueueResult> EnqueueVerificationAsync(
        string backupId,
        string? actorStaffUserId,
        string? correlationId,
        CancellationToken cancellationToken = default)
    {
        if (!UlidId.IsCanonical(backupId))
        {
            throw new ArgumentException(
                "A canonical ULID backup identifier is required.",
                nameof(backupId));
        }

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var record = await db.BackupRecords
                .SingleOrDefaultAsync(item => item.Id == backupId, token)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"Backup '{backupId}' does not exist.");
            if (record.State == BackupStates.Expired)
            {
                throw new InvalidOperationException(
                    "The backup set has expired under the retention policy.");
            }

            if (record.DestinationRelativePath is null
                || record.ManifestSha256 is null)
            {
                throw new InvalidOperationException(
                    "The backup has not produced a verifiable manifest.");
            }

            var now = timeProvider.GetUtcNow();
            var deduplicationKey =
                $"backup:verify:{backupId}:{record.ManifestSha256}:{now:yyyyMMddHHmm}";
            var existing = await db.BackgroundJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    job => job.DeduplicationKey == deduplicationKey,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return new BackupEnqueueResult(
                    backupId,
                    existing.Id,
                    existing.State,
                    Created: false);
            }

            var jobId = UlidId.New(now.AddMilliseconds(1));
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = BackupJobTypes.Verify,
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = 100,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    backupId,
                    requestedAt = now,
                }),
                State = "queued",
                MaxAttempts = 3,
                NextAttemptAt = now,
                CorrelationId = correlationId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return new BackupEnqueueResult(
                backupId,
                jobId,
                "queued",
                Created: true);
        }, cancellationToken);
    }

    private Task<BackupEnqueueResult> EnqueueCreateAsync(
        string backupId,
        string trigger,
        string? actorStaffUserId,
        string? correlationId,
        string deduplicationKey,
        int priority,
        CancellationToken cancellationToken)
    {
        var status = Configuration;
        if (!status.Enabled
            || !status.Configured
            || !status.EncryptionConfirmed
            || !status.DestinationAccessible)
        {
            throw new BackupOperationException(
                status.ErrorCode ?? "backup_unavailable",
                "The encrypted backup destination is not ready.",
                isConfigurationError: true);
        }

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);

            var existing = await db.BackgroundJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    job => job.DeduplicationKey == deduplicationKey,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var existingBackupId = await db.BackupRecords
                    .AsNoTracking()
                    .Where(record => record.BackgroundJobId == existing.Id)
                    .Select(record => record.Id)
                    .SingleAsync(token)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new BackupEnqueueResult(
                    existingBackupId,
                    existing.Id,
                    existing.State,
                    Created: false);
            }

            var now = timeProvider.GetUtcNow();
            var policy = await db.BackupPolicies
                .SingleOrDefaultAsync(item => item.Id == "default", token)
                .ConfigureAwait(false);
            if (policy is null)
            {
                policy = new BackupPolicyEntity
                {
                    Id = "default",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.BackupPolicies.Add(policy);
            }

            ApplyOptions(policy, options);
            var siteSettings = await db.SiteSettings
                .SingleAsync(token)
                .ConfigureAwait(false);
            siteSettings.BackupPolicyId = policy.Id;

            var jobId = UlidId.New(now.AddMilliseconds(1));
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = BackupJobTypes.Create,
                SchemaVersion = 1,
                DeduplicationKey = deduplicationKey,
                Priority = priority,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    backupId,
                    trigger,
                    requestedAt = now,
                }),
                State = "queued",
                MaxAttempts = 5,
                NextAttemptAt = now,
                CorrelationId = correlationId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.BackupRecords.Add(new BackupRecordEntity
            {
                Id = backupId,
                BackupPolicyId = policy.Id,
                BackgroundJobId = jobId,
                Trigger = trigger,
                State = BackupStates.Queued,
                CreatedByStaffUserId = actorStaffUserId,
                RequestedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new BackupEnqueueResult(
                backupId,
                jobId,
                "queued",
                Created: true);
        }, cancellationToken);
    }

    private static void ApplyOptions(
        BackupPolicyEntity policy,
        BackupOptions backupOptions)
    {
        policy.Enabled = backupOptions.Enabled;
        policy.DestinationRootPath = backupOptions.DestinationRootPath;
        policy.DestinationEncryptionConfirmed =
            backupOptions.DestinationEncryptionConfirmed;
        policy.IncludeManagedScans = backupOptions.IncludeManagedScans;
        policy.IncludeReports = backupOptions.IncludeReports;
        policy.ScheduleLocalHour = backupOptions.ScheduleLocalHour;
        policy.ScheduleLocalMinute = backupOptions.ScheduleLocalMinute;
        policy.DailyRetentionDays = backupOptions.DailyRetentionDays;
        policy.WeeklyRetentionWeeks = backupOptions.WeeklyRetentionWeeks;
        policy.MonthlyRetentionMonths = backupOptions.MonthlyRetentionMonths;
    }
}
