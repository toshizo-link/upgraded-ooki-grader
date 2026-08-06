namespace OokiGrader.Infrastructure.Backups;

public sealed class BackupOptions
{
    public required string DatabasePath { get; init; }

    public required string ContentRootPath { get; init; }

    public string? SecretEnvelopeRootPath { get; init; }

    public string? DestinationRootPath { get; init; }

    public bool Enabled { get; init; }

    public bool DestinationEncryptionConfirmed { get; init; }

    public bool IncludeManagedScans { get; init; }

    public bool IncludeReports { get; init; } = true;

    public bool ProbeDestinationWriteAccess { get; init; } = true;

    public int ScheduleLocalHour { get; init; } = 2;

    public int ScheduleLocalMinute { get; init; }

    public int DailyRetentionDays { get; init; } = 14;

    public int WeeklyRetentionWeeks { get; init; } = 8;

    public int MonthlyRetentionMonths { get; init; } = 12;

    public int MaximumRetentionCandidates { get; init; } = 25_000;

    public int MaximumRetentionDeletesPerRun { get; init; } = 512;

    public int MaximumManifestEntries { get; init; } = 100_000;

    public int MaximumManifestBytes { get; init; } = 16 * 1024 * 1024;

    public string ApplicationVersion { get; init; } =
        typeof(BackupOptions).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
