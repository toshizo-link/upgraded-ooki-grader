namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class BackgroundJobEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string DeduplicationKey { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string State { get; set; } = "queued";
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 8;
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int ProgressBasisPoints { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class OutboxEventEntity
{
    public string Id { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string? CorrelationId { get; set; }
    public string? CausationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public int DeliveryAttemptCount { get; set; }
}

public sealed class AuditEventEntity : IAppendOnlyEntity
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string? ActorStaffUserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public string? SourceIpPrefix { get; set; }
    public string? SafeMetadataJson { get; set; }
}

public sealed class FileObjectEntity : IRevisionedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string VerifiedMime { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string RelativeObjectPath { get; set; } = string.Empty;
    public string StorageClass { get; set; } = string.Empty;
    public string RetentionClass { get; set; } = string.Empty;
    public bool ManagedScanBytes { get; set; }
    public string State { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public int ReferenceCountCache { get; set; }
    public bool Encrypted { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<FileReferenceEntity> References { get; } =
        new List<FileReferenceEntity>();
}

public sealed class FileReferenceEntity
{
    public string Id { get; set; } = string.Empty;
    public string FileObjectId { get; set; } = string.Empty;
    public string OwnerType { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateTimeOffset RetentionAnchorAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public FileObjectEntity FileObject { get; set; } = null!;
}

public sealed class DeletionManifestEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string? BackgroundJobId { get; set; }
    public string Reason { get; set; } = "age";
    public string State { get; set; } = "pending";
    public DateTimeOffset? CutoffAt { get; set; }
    public int PlannedObjectCount { get; set; }
    public int PlannedReferenceCount { get; set; }
    public long PlannedBytes { get; set; }
    public int DeletedObjectCount { get; set; }
    public int ReleasedReferenceCount { get; set; }
    public int MissingObjectCount { get; set; }
    public int FailureCount { get; set; }
    public long DeletedBytes { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<DeletionManifestItemEntity> Items { get; } =
        new List<DeletionManifestItemEntity>();
}

public sealed class DeletionManifestItemEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string DeletionManifestId { get; set; } = string.Empty;
    public string FileObjectId { get; set; } = string.Empty;
    public string FileReferenceId { get; set; } = string.Empty;
    public string SubmissionId { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public string StorageClass { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string RelativeObjectPath { get; set; } = string.Empty;
    public bool DeletePhysicalObject { get; set; }
    public string State { get; set; } = "pending";
    public string? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public DeletionManifestEntity DeletionManifest { get; set; } = null!;
    public FileObjectEntity FileObject { get; set; } = null!;
}
