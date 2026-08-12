namespace OokiGrader.Infrastructure.Persistence.Entities;

/// <summary>
/// Durable request and artifact metadata for an immutable ZIP of finalized
/// per-submission result PDFs. The selector and source snapshots are server
/// generated; clients cannot supply the frozen source lineage.
/// </summary>
public sealed class BulkTranscriptExportEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string BackgroundJobId { get; set; } = string.Empty;
    public string? RequestIdempotencyKey { get; set; }
    public string? RequestFingerprint { get; set; }
    public string SelectorJson { get; set; } = "{}";
    public string SelectorHash { get; set; } = string.Empty;
    public string SourceSnapshotJson { get; set; } = "[]";
    public string SourceFingerprint { get; set; } = string.Empty;
    public string RendererVersion { get; set; } = string.Empty;
    public string PackageFormatVersion { get; set; } = string.Empty;
    public string State { get; set; } = "queued";
    public int StudentCount { get; set; }
    public int ResultCount { get; set; }
    public int ProcessedResultCount { get; set; }
    public string? FileReferenceId { get; set; }
    public string? Sha256 { get; set; }
    public long? Bytes { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public string? SupersededReason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public BackgroundJobEntity BackgroundJob { get; set; } = null!;
    public FileReferenceEntity? FileReference { get; set; }
    public StaffUserEntity CreatedByStaffUser { get; set; } = null!;
}
