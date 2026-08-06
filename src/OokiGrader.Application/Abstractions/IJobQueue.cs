namespace OokiGrader.Application.Abstractions;

public enum DurableJobState
{
    Queued,
    Leased,
    RetryWaiting,
    Succeeded,
    Failed,
    Blocked,
    Cancelled
}

public enum JobFailureKind
{
    Transient,
    Permanent,
    Blocked,
    Manual
}

public sealed record EnqueueJobRequest(
    string Type,
    int SchemaVersion,
    string DeduplicationKey,
    string PayloadJson,
    int Priority = 0,
    int MaxAttempts = 8,
    DateTimeOffset? NotBefore = null,
    string? CorrelationId = null,
    string? CausationId = null);

public sealed record EnqueueJobResult(
    string JobId,
    DurableJobState State,
    bool Created);

public sealed record DurableJobLease(
    string JobId,
    string Type,
    int SchemaVersion,
    string PayloadJson,
    int Priority,
    int AttemptCount,
    int MaxAttempts,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    long Revision,
    string? CorrelationId,
    string? CausationId);

public sealed record DurableJobSnapshot(
    string JobId,
    string Type,
    DurableJobState State,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset? NextAttemptAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    string? ErrorCode,
    long Revision);

public sealed record JobFailure(
    JobFailureKind Kind,
    string ErrorCode,
    string? SafeDetail = null,
    DateTimeOffset? RetryAt = null);

public interface IBackgroundJobStore
{
    Task<EnqueueJobResult> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken = default);

    Task<DurableJobLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<DurableJobLease?> RenewLeaseAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<DurableJobSnapshot> FailAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        JobFailure failure,
        CancellationToken cancellationToken = default);

    Task<DurableJobSnapshot?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default);
}
