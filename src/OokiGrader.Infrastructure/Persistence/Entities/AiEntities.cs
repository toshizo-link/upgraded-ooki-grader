namespace OokiGrader.Infrastructure.Persistence.Entities;

public sealed class AiConnectionEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = "geminiDirect";
    public string EndpointProfile { get; set; } = "googleGenerativeLanguage";
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public string SecretReference { get; set; } = string.Empty;
    public string KeyFingerprint { get; set; } = string.Empty;
    public int CredentialRevision { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 75;
    public int ConcurrencyLimit { get; set; } = 2;
    public string State { get; set; } = "pending_probe";
    public string? LastCapabilityProbeState { get; set; }
    public string? LastCapabilityProbeErrorCode { get; set; }
    public DateTimeOffset? LastCapabilityProbeAt { get; set; }
    public string? LastBatchCapabilityProbeState { get; set; }
    public string? LastBatchCapabilityProbeErrorCode { get; set; }
    public DateTimeOffset? LastBatchCapabilityProbeAt { get; set; }
    public int? LastBatchCapabilityProbeCredentialRevision { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public ICollection<AiTaskProfileEntity> TaskProfiles { get; } =
        new List<AiTaskProfileEntity>();
    public ICollection<AiCapabilityProbeEntity> CapabilityProbes { get; } =
        new List<AiCapabilityProbeEntity>();
    public ICollection<AiBatchEntity> Batches { get; } =
        new List<AiBatchEntity>();
}

public sealed class AiCapabilityProbeEntity
{
    public string Id { get; set; } = string.Empty;
    public string AiConnectionId { get; set; } = string.Empty;
    public int ConnectionRevision { get; set; }
    public string State { get; set; } = "running";
    public bool Authentication { get; set; }
    public bool ModelAvailable { get; set; }
    public bool ImageInput { get; set; }
    public bool StructuredOutput { get; set; }
    public bool UsageMetadata { get; set; }
    public string BatchState { get; set; } = "not_run";
    public bool BatchAvailable { get; set; }
    public bool BatchCleanupSucceeded { get; set; }
    public string? BatchSafeErrorCode { get; set; }
    public long? BatchLatencyMilliseconds { get; set; }
    public string? SafeErrorCode { get; set; }
    public long? LatencyMilliseconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public AiConnectionEntity AiConnection { get; set; } = null!;
}

public sealed class AiTaskProfileEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public string AiConnectionId { get; set; } = string.Empty;
    public long ConnectionRevision { get; set; }
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public string ProcessingStrategy { get; set; } = "expedite_standard";
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string PromptContentHash { get; set; } = string.Empty;
    public string ThinkingLevel { get; set; } = "minimal";
    public string MediaResolution { get; set; } = "high";
    public int MaxOutputTokens { get; set; } = 8_192;
    public int ConcurrencyLimit { get; set; } = 2;
    public string ApprovalState { get; set; } = "draft";
    public string? AccuracyEvaluationId { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public string? ActivatedByStaffUserId { get; set; }
    public string CreatedByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;

    public AiConnectionEntity AiConnection { get; set; } = null!;
    public ICollection<AiRequestEntity> Requests { get; } = new List<AiRequestEntity>();
    public ICollection<AiBatchEntity> Batches { get; } = new List<AiBatchEntity>();
    public ICollection<AiEvaluationRecordEntity> EvaluationRecords { get; } =
        new List<AiEvaluationRecordEntity>();
}

public sealed class AiEvaluationRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string AiTaskProfileId { get; set; } = string.Empty;
    public long TaskProfileRevision { get; set; }
    public string Provider { get; set; } = "geminiDirect";
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public long ConnectionRevision { get; set; }
    public string TaskType { get; set; } = string.Empty;
    public string ProcessingStrategy { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string PromptContentHash { get; set; } = string.Empty;
    public string DatasetVersion { get; set; } = string.Empty;
    public string DatasetSha256 { get; set; } = string.Empty;
    public string EvidenceSha256 { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public int AgreementBasisPoints { get; set; }
    public int LowerConfidenceBoundBasisPoints { get; set; }
    public int CriticalFailureCount { get; set; }
    public bool TeacherReviewOnly { get; set; }
    public string SignedOffByStaffUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public AiTaskProfileEntity AiTaskProfile { get; set; } = null!;
}

public sealed class AiRequestEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string RequestKey { get; set; } = string.Empty;
    public string AiTaskProfileId { get; set; } = string.Empty;
    public long TaskProfileRevision { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public long EntityRevision { get; set; }
    public string InputManifestHash { get; set; } = string.Empty;
    public int AttemptNumber { get; set; } = 1;
    public string? RetryOfAiRequestId { get; set; }
    public string State { get; set; } = "prepared";
    public int DispatchAttempt { get; set; }
    public bool PossibleDuplicate { get; set; }
    public string? ProviderResponseId { get; set; }
    public string? ActualModel { get; set; }
    public string? FinishReason { get; set; }
    public string? AcceptedResponseHash { get; set; }
    public string? ValidatedResponseJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;

    public AiTaskProfileEntity AiTaskProfile { get; set; } = null!;
    public AiUsageEntity? Usage { get; set; }
    public AiBatchRequestEntity? BatchRequest { get; set; }
}

public sealed class AiBatchEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = "geminiDirect";
    public string ModelId { get; set; } = "gemini-3.5-flash-lite";
    public string AiConnectionId { get; set; } = string.Empty;
    public long ConnectionRevision { get; set; }
    public string AiTaskProfileId { get; set; } = string.Empty;
    public long TaskProfileRevision { get; set; }
    public string CompatibilityKey { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = "{}";
    public string ManifestHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = "prepared";
    public int SubmissionEpoch { get; set; } = 1;
    public int CreateAttemptCount { get; set; }
    public DateTimeOffset? CreateAttemptStartedAt { get; set; }
    public DateTimeOffset? CreateAttemptCompletedAt { get; set; }
    public string? ProviderBatchName { get; set; }
    public string? ProviderInputFileName { get; set; }
    public string? ProviderOutputFileName { get; set; }
    public DateTimeOffset? ProviderInputFileExpiresAt { get; set; }
    public string? InputJsonLinesSha256 { get; set; }
    public long InputJsonLinesBytes { get; set; }
    public int RequestCount { get; set; }
    public long SuccessfulRequestCount { get; set; }
    public long FailedRequestCount { get; set; }
    public long PendingRequestCount { get; set; }
    public bool PossibleDuplicate { get; set; }
    public int ReconciliationAttemptCount { get; set; }
    public DateTimeOffset? ReconciliationDeadlineAt { get; set; }
    public DateTimeOffset? LastPolledAt { get; set; }
    public DateTimeOffset? NextActionAt { get; set; }
    public DateTimeOffset? RemoteCreatedAt { get; set; }
    public DateTimeOffset? RemoteUpdatedAt { get; set; }
    public DateTimeOffset? RemoteEndedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? SafeErrorDetail { get; set; }
    public string CleanupState { get; set; } = "not_started";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;

    public AiConnectionEntity AiConnection { get; set; } = null!;
    public AiTaskProfileEntity AiTaskProfile { get; set; } = null!;
    public ICollection<AiBatchRequestEntity> Requests { get; } =
        new List<AiBatchRequestEntity>();
}

public sealed class AiBatchRequestEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = string.Empty;
    public string? AiBatchId { get; set; }
    public string AiRequestId { get; set; } = string.Empty;
    public string RequestKey { get; set; } = string.Empty;
    public string CompatibilityKey { get; set; } = string.Empty;
    public string? ProviderRequestJson { get; set; }
    public string ProviderRequestHash { get; set; } = string.Empty;
    public long ProviderRequestBytes { get; set; }
    public int? Ordinal { get; set; }
    public string State { get; set; } = "ready";
    public string? ProviderResponseId { get; set; }
    public string? ResponseJson { get; set; }
    public string? ResponseHash { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long Revision { get; set; } = 1;

    public AiBatchEntity? AiBatch { get; set; }
    public AiRequestEntity AiRequest { get; set; } = null!;
}

public sealed class AiUsageEntity
{
    public string Id { get; set; } = string.Empty;
    public string AiRequestId { get; set; } = string.Empty;
    public string RequestedProvider { get; set; } = string.Empty;
    public string RequestedModel { get; set; } = string.Empty;
    public string? ActualProvider { get; set; }
    public string? ActualModel { get; set; }
    public int? InputTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ThinkingTokens { get; set; }
    public int? TotalTokens { get; set; }
    public string? PricingSnapshotId { get; set; }
    public long EstimatedUsdMicros { get; set; }
    public long EstimatedJpyMicros { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTimeOffset MeasuredAt { get; set; }

    public AiRequestEntity AiRequest { get; set; } = null!;
    public PricingSnapshotEntity? PricingSnapshot { get; set; }
}

public sealed class PricingSnapshotEntity
{
    public string Id { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public long InputUsdMicrosPerMillionTokens { get; set; }
    public long OutputUsdMicrosPerMillionTokens { get; set; }
    public long ThinkingUsdMicrosPerMillionTokens { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset EffectiveAt { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

public sealed class AiBudgetPolicyEntity : IRevisionedEntity, IUpdatedEntity
{
    public string Id { get; set; } = "default";
    public long DailyWarningUsdMicros { get; set; }
    public long DailyHardUsdMicros { get; set; }
    public long MonthlyWarningUsdMicros { get; set; }
    public long MonthlyHardUsdMicros { get; set; }
    public long UsdToJpyMicros { get; set; } = 150_000_000;
    public bool Active { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
}

public sealed class AiBudgetReservationEntity
{
    public string Id { get; set; } = string.Empty;
    public string AiRequestId { get; set; } = string.Empty;
    public DateOnly UsageDay { get; set; }
    public string UsageMonth { get; set; } = string.Empty;
    public long ReservedUsdMicros { get; set; }
    public long ActualUsdMicros { get; set; }
    public string State { get; set; } = "reserved";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }

    public AiRequestEntity AiRequest { get; set; } = null!;
}
