using OokiGrader.Domain.Grading;

namespace OokiGrader.Infrastructure.Persistence.Entities;

/// <summary>
/// Durable owner for one ordered upload of single-page scans for a selected
/// test session. ExpectedPageCount is snapshotted from the published template.
/// </summary>
public sealed class OrderedScanBatchEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string TestSessionId { get; set; } = string.Empty;
    public int ExpectedPageCount { get; set; }
    public OrderedScanBatchStatus Status { get; set; } =
        OrderedScanBatchStatus.Draft;
    public string AssemblyPolicyVersion { get; set; } = string.Empty;
    public string? PlanHash { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorJson { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;

    public TestSessionEntity TestSession { get; set; } = null!;
    public ICollection<OrderedScanItemEntity> Items { get; } =
        new List<OrderedScanItemEntity>();
}

/// <summary>
/// One uploaded one-page PDF at its immutable scanner/client order position.
/// </summary>
public sealed class OrderedScanItemEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public int InputOrdinal { get; set; }
    public string ClientItemId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string? UploadSessionId { get; set; }
    public string? SourceFileReferenceId { get; set; }
    public string? SourceSha256 { get; set; }
    public long? SourceBytes { get; set; }
    public DateTimeOffset? UploadCompletedAt { get; set; }
    public int? DetectedTemplatePageNumber { get; set; }
    public int? ClassificationConfidenceBasisPoints { get; set; }
    public OrderedScanItemStatus Status { get; set; } =
        OrderedScanItemStatus.Pending;
    public int? GroupOrdinal { get; set; }
    public string? SubmissionId { get; set; }
    public int? SubmissionPageNumber { get; set; }
    public string? IssueCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public OrderedScanBatchEntity Batch { get; set; } = null!;
    public UploadSessionEntity? UploadSession { get; set; }
    public FileReferenceEntity? SourceFileReference { get; set; }
    public SubmissionEntity? Submission { get; set; }
}

/// <summary>
/// Append-only lineage from a logical submission page to the original
/// one-page upload and its submission-owned file reference.
/// </summary>
public sealed class SubmissionSourcePageEntity : IRetentionMutableLineageEntity
{
    public string Id { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string OrderedScanItemId { get; set; } = string.Empty;
    public string UploadSessionId { get; set; } = string.Empty;
    public string? FileReferenceId { get; set; }
    public int SourcePageNumber { get; set; } = 1;
    public string SourceSha256 { get; set; } = string.Empty;
    public string AssemblyPolicyVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public SubmissionEntity Submission { get; set; } = null!;
    public OrderedScanItemEntity OrderedScanItem { get; set; } = null!;
    public UploadSessionEntity UploadSession { get; set; } = null!;
    public FileReferenceEntity? FileReference { get; set; }
}
