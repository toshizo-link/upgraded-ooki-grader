namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class BackupPolicyEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Default metadata backup";
    public bool Enabled { get; set; }
    public string? DestinationRootPath { get; set; }
    public bool DestinationEncryptionConfirmed { get; set; }
    public bool IncludeManagedScans { get; set; }
    public bool IncludeReports { get; set; } = true;
    public int ScheduleLocalHour { get; set; } = 2;
    public int ScheduleLocalMinute { get; set; }
    public int DailyRetentionDays { get; set; } = 14;
    public int WeeklyRetentionWeeks { get; set; } = 8;
    public int MonthlyRetentionMonths { get; set; } = 12;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<BackupRecordEntity> Records { get; } =
        new List<BackupRecordEntity>();
}

public sealed class BackupRecordEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string BackupPolicyId { get; set; } = "default";
    public string BackgroundJobId { get; set; } = string.Empty;
    public string Trigger { get; set; } = "manual";
    public string State { get; set; } = "queued";
    public string? CreatedByStaffUserId { get; set; }
    public string? DestinationRelativePath { get; set; }
    public string? ManifestSha256 { get; set; }
    public string? DatabaseSha256 { get; set; }
    public long DatabaseBytes { get; set; }
    public int ObjectCount { get; set; }
    public long ObjectBytes { get; set; }
    public int SecretEnvelopeCount { get; set; }
    public long SecretEnvelopeBytes { get; set; }
    public string? DatabaseMigrationId { get; set; }
    public long DatabaseDataVersion { get; set; }
    public string? ApplicationVersion { get; set; }
    public string? IntegrityResult { get; set; }
    public DateTimeOffset? LastVerificationAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public BackupPolicyEntity BackupPolicy { get; set; } = null!;
}
