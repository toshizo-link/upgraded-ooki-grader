using Microsoft.EntityFrameworkCore;
using OokiGrader.Infrastructure.Backups;
using OokiGrader.Infrastructure.Persistence;

namespace OokiGrader.Host.Api;

public sealed record BackupHealthSnapshot(
    string State,
    string? ErrorCode,
    string? Detail,
    DateTimeOffset CheckedAt,
    DateTimeOffset? LastVerifiedAt,
    DateTimeOffset? NextScheduledAt,
    BackupConfigurationStatus Configuration);

public sealed class BackupHealthService(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IBackupArchiveService archiveService,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan WarningAge = TimeSpan.FromHours(26);
    private static readonly TimeSpan CriticalAge = TimeSpan.FromHours(72);

    public async Task<BackupHealthSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var checkedAt = timeProvider.GetUtcNow();
        var configuration = archiveService.GetConfigurationStatus();
        await using var db = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var lastVerifiedAt = await db.BackupRecords
            .AsNoTracking()
            .Where(record => record.State == BackupStates.Verified)
            .MaxAsync(
                record => (DateTimeOffset?)record.VerifiedAt,
                cancellationToken)
            .ConfigureAwait(false);
        var timeZoneId = await db.SiteSettings
            .AsNoTracking()
            .Select(settings => settings.TimeZone)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var nextScheduledAt = NextScheduledAt(
            checkedAt,
            timeZoneId,
            configuration.ScheduleLocalHour,
            configuration.ScheduleLocalMinute);

        if (!configuration.Enabled)
        {
            return Snapshot(
                "unknown",
                configuration.ErrorCode,
                "バックアップは無効です。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        if (!configuration.Configured)
        {
            return Snapshot(
                "unavailable",
                configuration.ErrorCode,
                "暗号化されたバックアップ先を構成してください。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        if (!configuration.EncryptionConfirmed)
        {
            return Snapshot(
                "unavailable",
                configuration.ErrorCode,
                "バックアップ先の暗号化を確認してください。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        if (!configuration.DestinationAccessible)
        {
            return Snapshot(
                "unavailable",
                configuration.ErrorCode,
                "バックアップ先にアクセスできません。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        if (lastVerifiedAt is null)
        {
            return Snapshot(
                "degraded",
                "backup_never_verified",
                "検証済みバックアップがまだありません。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        var age = checkedAt - lastVerifiedAt.Value;
        if (age > CriticalAge)
        {
            return Snapshot(
                "unavailable",
                "backup_older_than_72_hours",
                "検証済みバックアップが72時間以上古くなっています。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        if (age > WarningAge)
        {
            return Snapshot(
                "degraded",
                "backup_older_than_26_hours",
                "検証済みバックアップが26時間以上古くなっています。",
                checkedAt,
                lastVerifiedAt,
                nextScheduledAt,
                configuration);
        }

        return Snapshot(
            "healthy",
            errorCode: null,
            detail: null,
            checkedAt,
            lastVerifiedAt,
            nextScheduledAt,
            configuration);
    }

    private static BackupHealthSnapshot Snapshot(
        string state,
        string? errorCode,
        string? detail,
        DateTimeOffset checkedAt,
        DateTimeOffset? lastVerifiedAt,
        DateTimeOffset? nextScheduledAt,
        BackupConfigurationStatus configuration) =>
        new(
            state,
            errorCode,
            detail,
            checkedAt,
            lastVerifiedAt,
            nextScheduledAt,
            configuration);

    private static DateTimeOffset? NextScheduledAt(
        DateTimeOffset now,
        string timeZoneId,
        int hour,
        int minute)
    {
        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
            var localSchedule = new DateTime(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                hour,
                minute,
                0,
                DateTimeKind.Unspecified);
            if (localSchedule <= localNow.DateTime)
            {
                localSchedule = localSchedule.AddDays(1);
            }

            return TimeZoneInfo.ConvertTimeToUtc(localSchedule, timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
