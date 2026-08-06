using Microsoft.EntityFrameworkCore;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Infrastructure.Jobs;

public sealed class EfBackgroundJobStore(
    IDbContextFactory<OokiGraderDbContext> dbContextFactory,
    IWriteCoordinator writeCoordinator,
    IClock clock) : IBackgroundJobStore
{
    public Task<EnqueueJobResult> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateEnqueueRequest(request);

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);

            var existing = await dbContext.BackgroundJobs
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    job => job.DeduplicationKey == request.DeduplicationKey,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                EnsureCompatibleDuplicate(existing, request);
                return new EnqueueJobResult(
                    existing.Id,
                    ParseState(existing.State),
                    Created: false);
            }

            var now = clock.UtcNow;
            var entity = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = request.Type,
                SchemaVersion = request.SchemaVersion,
                DeduplicationKey = request.DeduplicationKey,
                Priority = request.Priority,
                PayloadJson = request.PayloadJson,
                State = "queued",
                AttemptCount = 0,
                MaxAttempts = request.MaxAttempts,
                NextAttemptAt = request.NotBefore ?? now,
                CorrelationId = request.CorrelationId,
                CausationId = request.CausationId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            };
            dbContext.BackgroundJobs.Add(entity);
            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            return new EnqueueJobResult(entity.Id, DurableJobState.Queued, Created: true);
        }, cancellationToken);
    }

    public Task<DurableJobLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkerAndDuration(workerId, leaseDuration);

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);

            var now = clock.UtcNow;
            var job = await dbContext.BackgroundJobs
                .Where(entity =>
                    entity.AttemptCount < entity.MaxAttempts &&
                    ((entity.State == "queued" && entity.NextAttemptAt <= now) ||
                     (entity.State == "retry_waiting" && entity.NextAttemptAt <= now) ||
                     (entity.State == "leased" && entity.LeaseExpiresAt <= now)))
                .OrderByDescending(entity => entity.Priority)
                .ThenBy(entity => entity.NextAttemptAt)
                .ThenBy(entity => entity.CreatedAt)
                .ThenBy(entity => entity.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);

            if (job is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            job.State = "leased";
            job.LeaseOwner = workerId;
            job.LeaseExpiresAt = now.Add(leaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return ToLease(job);
        }, cancellationToken);
    }

    public Task<DurableJobLease?> RenewLeaseAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkerAndDuration(workerId, leaseDuration);

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await dbContext.BackgroundJobs
                .SingleOrDefaultAsync(entity => entity.Id == jobId, token)
                .ConfigureAwait(false);

            if (job is null ||
                job.State != "leased" ||
                job.LeaseOwner != workerId ||
                job.Revision != expectedRevision ||
                job.LeaseExpiresAt <= clock.UtcNow)
            {
                return null;
            }

            job.LeaseExpiresAt = clock.UtcNow.Add(leaseDuration);
            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            return ToLease(job);
        }, cancellationToken);
    }

    public Task<bool> CompleteAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await dbContext.BackgroundJobs
                .SingleOrDefaultAsync(entity => entity.Id == jobId, token)
                .ConfigureAwait(false);

            if (job is null)
            {
                return false;
            }

            if (job.State == "succeeded")
            {
                return true;
            }

            if (!OwnsCurrentLease(job, workerId, expectedRevision, clock.UtcNow))
            {
                return false;
            }

            job.State = "succeeded";
            job.ProgressBasisPoints = 10_000;
            job.CompletedAt = clock.UtcNow;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<DurableJobSnapshot> FailAsync(
        string jobId,
        string workerId,
        long expectedRevision,
        JobFailure failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (string.IsNullOrWhiteSpace(failure.ErrorCode) || failure.ErrorCode.Length > 200)
        {
            throw new ArgumentException("A bounded error code is required.", nameof(failure));
        }

        if (failure.SafeDetail?.Length > 2_000)
        {
            throw new ArgumentException("Safe error detail is too long.", nameof(failure));
        }

        return writeCoordinator.ExecuteAsync(async token =>
        {
            await using var dbContext = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await dbContext.BackgroundJobs
                .SingleOrDefaultAsync(entity => entity.Id == jobId, token)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Job '{jobId}' does not exist.");

            var now = clock.UtcNow;
            if (!OwnsCurrentLease(job, workerId, expectedRevision, now))
            {
                throw new InvalidOperationException("The caller no longer owns the job lease.");
            }

            job.ErrorCode = failure.ErrorCode;
            job.SafeErrorDetail = failure.SafeDetail;
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;

            if (failure.Kind == JobFailureKind.Transient &&
                job.AttemptCount < job.MaxAttempts)
            {
                job.State = "retry_waiting";
                job.NextAttemptAt = failure.RetryAt ?? now.Add(DefaultRetryDelay(job.AttemptCount));
            }
            else if (failure.Kind == JobFailureKind.Blocked)
            {
                job.State = "blocked";
                job.NextAttemptAt = failure.RetryAt ?? now;
            }
            else
            {
                job.State = "failed";
                job.CompletedAt = now;
            }

            await dbContext.SaveChangesAsync(token).ConfigureAwait(false);
            return ToSnapshot(job);
        }, cancellationToken);
    }

    public async Task<DurableJobSnapshot?> GetAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var job = await dbContext.BackgroundJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == jobId, cancellationToken)
            .ConfigureAwait(false);
        return job is null ? null : ToSnapshot(job);
    }

    private static bool OwnsCurrentLease(
        BackgroundJobEntity job,
        string workerId,
        long expectedRevision,
        DateTimeOffset now)
    {
        return job.State == "leased" &&
            job.LeaseOwner == workerId &&
            job.Revision == expectedRevision &&
            job.LeaseExpiresAt > now;
    }

    private static DurableJobLease ToLease(BackgroundJobEntity job)
    {
        return new DurableJobLease(
            job.Id,
            job.Type,
            job.SchemaVersion,
            job.PayloadJson,
            job.Priority,
            job.AttemptCount,
            job.MaxAttempts,
            job.LeaseOwner!,
            job.LeaseExpiresAt!.Value,
            job.Revision,
            job.CorrelationId,
            job.CausationId);
    }

    private static DurableJobSnapshot ToSnapshot(BackgroundJobEntity job)
    {
        return new DurableJobSnapshot(
            job.Id,
            job.Type,
            ParseState(job.State),
            job.AttemptCount,
            job.MaxAttempts,
            job.NextAttemptAt,
            job.LeaseOwner,
            job.LeaseExpiresAt,
            job.ErrorCode,
            job.Revision);
    }

    private static DurableJobState ParseState(string state)
    {
        return state switch
        {
            "queued" => DurableJobState.Queued,
            "leased" => DurableJobState.Leased,
            "retry_waiting" => DurableJobState.RetryWaiting,
            "succeeded" => DurableJobState.Succeeded,
            "failed" => DurableJobState.Failed,
            "blocked" => DurableJobState.Blocked,
            "cancelled" => DurableJobState.Cancelled,
            _ => throw new InvalidOperationException($"Unknown persisted job state '{state}'.")
        };
    }

    private static TimeSpan DefaultRetryDelay(int attemptCount)
    {
        return attemptCount switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(2),
            3 => TimeSpan.FromMinutes(10),
            4 => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromHours(2)
        };
    }

    private static void ValidateWorkerAndDuration(string workerId, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 200)
        {
            throw new ArgumentException("A bounded worker ID is required.", nameof(workerId));
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static void ValidateEnqueueRequest(EnqueueJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Type) || request.Type.Length > 200)
        {
            throw new ArgumentException("A bounded job type is required.", nameof(request));
        }

        if (request.SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeduplicationKey) ||
            request.DeduplicationKey.Length > 500)
        {
            throw new ArgumentException("A bounded deduplication key is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PayloadJson))
        {
            throw new ArgumentException("A minimal JSON payload is required.", nameof(request));
        }

        if (request.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static void EnsureCompatibleDuplicate(
        BackgroundJobEntity existing,
        EnqueueJobRequest request)
    {
        if (existing.Type != request.Type ||
            existing.SchemaVersion != request.SchemaVersion ||
            existing.PayloadJson != request.PayloadJson)
        {
            throw new InvalidOperationException(
                "The job deduplication key was reused with a different durable request.");
        }
    }
}
