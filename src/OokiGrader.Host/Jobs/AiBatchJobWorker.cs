using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record AiBatchJobWorkerOptions
{
    public TimeSpan WorkerPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(15);
    public int AggregationRequestCount { get; init; } = 20;
    public int MaximumRequestsPerBatch { get; init; } = 100;
    public int MaximumRequestAttempts { get; init; } = 3;
    public TimeSpan MaximumAggregationWait { get; init; } =
        TimeSpan.FromMinutes(5);
    public TimeSpan ReconciliationWindow { get; init; } =
        TimeSpan.FromMinutes(30);
    public TimeSpan InitialRemotePollInterval { get; init; } =
        TimeSpan.FromSeconds(30);
    public TimeSpan HourRemotePollInterval { get; init; } =
        TimeSpan.FromMinutes(2);
    public TimeSpan LongRemotePollInterval { get; init; } =
        TimeSpan.FromMinutes(10);

    internal void Validate()
    {
        if (WorkerPollInterval < TimeSpan.FromMilliseconds(100)
            || WorkerPollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || AggregationRequestCount is < 1 or > 100
            || MaximumRequestsPerBatch < AggregationRequestCount
            || MaximumRequestsPerBatch > 10_000
            || MaximumRequestAttempts is < 1 or > 8
            || MaximumAggregationWait < TimeSpan.Zero
            || MaximumAggregationWait > TimeSpan.FromHours(1)
            || ReconciliationWindow < TimeSpan.FromMinutes(5)
            || ReconciliationWindow > TimeSpan.FromHours(12)
            || InitialRemotePollInterval < TimeSpan.FromSeconds(1)
            || HourRemotePollInterval < InitialRemotePollInterval
            || LongRemotePollInterval < HourRemotePollInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AiBatchJobWorkerOptions),
                "One or more Gemini batch worker options are invalid.");
        }
    }
}

/// <summary>
/// Durable prepare/upload/create/poll/reconcile state machine for direct Gemini
/// Batch. It stores validated provider responses as response_ready; task-specific
/// workers must still validate/apply them and no submission is auto-finalized.
/// </summary>
public sealed partial class AiBatchJobWorker : BackgroundService
{
    public const string PrepareJobType = "gemini_batch_prepare";
    public const string SubmitJobType = "gemini_batch_submit";
    public const string PollJobType = "gemini_batch_poll";
    public const string ReconcileJobType = "gemini_batch_reconcile";
    public const int JobSchemaVersion = 1;

    private static readonly Uri GeminiBaseAddress =
        new("https://generativelanguage.googleapis.com/");
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IAiBatchProviderClient _batchProvider;
    private readonly IAiSecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AiBatchJobWorker> _logger;
    private readonly AiBatchJobWorkerOptions _options;
    private readonly string _workerId = $"gemini-batch-{Guid.NewGuid():N}";

    public AiBatchJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IAiBatchProviderClient batchProvider,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        IOptions<AiBatchJobWorkerOptions> options,
        ILogger<AiBatchJobWorker> logger)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _batchProvider = batchProvider;
        _secretStore = secretStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        _options.Validate();
    }

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        try
        {
            if (lease.SchemaVersion != JobSchemaVersion)
            {
                await FailJobAsync(
                        lease.Id,
                        "ai_batch_job_schema_unsupported",
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            switch (lease.Type)
            {
                case PrepareJobType:
                    await PrepareBatchAsync(lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case SubmitJobType:
                    await SubmitBatchAsync(lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PollJobType:
                    await PollBatchAsync(lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case ReconcileJobType:
                    await ReconcileBatchAsync(lease, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    await FailJobAsync(
                            lease.Id,
                            "ai_batch_job_type_unsupported",
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogWorkerFailure(
                lease.Id,
                lease.Type,
                exception.GetType().Name);
            await RetryJobAsync(
                    lease.Id,
                    "ai_batch_worker_error",
                    _timeProvider.GetUtcNow().AddMinutes(2),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(_options.WorkerPollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item =>
                    (item.Type == PrepareJobType
                        || item.Type == SubmitJobType
                        || item.Type == PollJobType
                        || item.Type == ReconcileJobType)
                    && item.AttemptCount < item.MaxAttempts
                    && ((item.State == "queued" && item.NextAttemptAt <= now)
                        || (item.State == "retry_waiting"
                            && item.NextAttemptAt <= now)
                        || (item.State == "leased"
                            && item.LeaseExpiresAt <= now)))
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (job is null)
            {
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            job.State = "leased";
            job.LeaseOwner = _workerId;
            job.LeaseExpiresAt = now.Add(_options.LeaseDuration);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.StartedAt ??= now;
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.Type,
                job.SchemaVersion,
                job.PayloadJson,
                job.CorrelationId);
        }, cancellationToken);
    }

    private Task PrepareBatchAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<PreparePayload>(lease.PayloadJson);
        if (payload is null || !IsSha256(payload.CompatibilityKey))
        {
            return FailJobAsync(
                lease.Id,
                "ai_batch_prepare_payload_invalid",
                cancellationToken);
        }

        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, lease.Id, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var candidates = await db.AiBatchRequests
                .Include(item => item.AiRequest)
                    .ThenInclude(item => item.AiTaskProfile)
                        .ThenInclude(item => item.AiConnection)
                .Where(item =>
                    item.State == "ready"
                    && item.AiBatchId == null
                    && item.CompatibilityKey == payload.CompatibilityKey)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .Take(_options.MaximumRequestsPerBatch)
                .ToListAsync(token)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var dueAt = candidates[0].CreatedAt.Add(
                _options.MaximumAggregationWait);
            if (!payload.SubmitNow
                && candidates.Count < _options.AggregationRequestCount
                && now < dueAt)
            {
                RetryJob(job, "ai_batch_waiting_for_aggregation", dueAt);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            ValidateCandidates(candidates);
            var firstRequest = candidates[0].AiRequest;
            var profile = firstRequest.AiTaskProfile;
            var connection = profile.AiConnection;
            var batchId = UlidId.New(now);
            var manifestItems = candidates.Select(
                    (item, ordinal) => new
                    {
                        ordinal,
                        batchRequestId = item.Id,
                        item.AiRequestId,
                        item.RequestKey,
                        item.ProviderRequestHash,
                    })
                .ToArray();
            var manifestJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                provider = AiProviders.GeminiDirect,
                modelId = GeminiBatchClient.SelectedModel,
                payload.CompatibilityKey,
                connectionId = connection.Id,
                connectionRevision = connection.CredentialRevision,
                profileId = profile.Id,
                profileRevision = profile.Revision,
                requests = manifestItems,
            });
            var manifestHash = Sha256(Encoding.UTF8.GetBytes(manifestJson));
            var batch = new AiBatchEntity
            {
                Id = batchId,
                Provider = AiProviders.GeminiDirect,
                ModelId = GeminiBatchClient.SelectedModel,
                AiConnectionId = connection.Id,
                ConnectionRevision = connection.CredentialRevision,
                AiTaskProfileId = profile.Id,
                TaskProfileRevision = profile.Revision,
                CompatibilityKey = payload.CompatibilityKey,
                ManifestJson = manifestJson,
                ManifestHash = manifestHash,
                DisplayName = $"ooki-{batchId}-{manifestHash[..12]}",
                State = "prepared",
                RequestCount = candidates.Count,
                PendingRequestCount = candidates.Count,
                ReconciliationDeadlineAt = now.Add(
                    _options.ReconciliationWindow),
                NextActionAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AiBatches.Add(batch);
            for (var index = 0; index < candidates.Count; index++)
            {
                candidates[index].AiBatchId = batch.Id;
                candidates[index].Ordinal = index;
                candidates[index].State = "prepared";
                candidates[index].UpdatedAt = now;
            }

            EnqueueBatchJob(
                db,
                SubmitJobType,
                batch,
                $"ai-batch:{batch.Id}:submit:{batch.SubmissionEpoch}",
                now,
                lease.CorrelationId);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task SubmitBatchAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<BatchPayload>(lease.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.BatchId))
        {
            await FailJobAsync(
                    lease.Id,
                    "ai_batch_submit_payload_invalid",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var claim = await LoadSubmitClaimAsync(
                lease.Id,
                payload.BatchId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return;
        }

        using var secret = await _secretStore.ReadAsync(
                new AiSecretReference(claim.SecretReference),
                cancellationToken)
            .ConfigureAwait(false);
        var inputFileName = claim.ProviderInputFileName;
        if (inputFileName is null)
        {
            await MarkUploadingAsync(
                    lease.Id,
                    claim.BatchId,
                    cancellationToken)
                .ConfigureAwait(false);
            AiBatchInputFile inputFile;
            try
            {
                inputFile = await _batchProvider.UploadJsonLinesAsync(
                        claim.Connection,
                        secret.Utf8Bytes,
                        claim.DisplayName,
                        claim.JsonLines,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AiProviderException exception)
            {
                await RecordUploadFailureAsync(
                        lease.Id,
                        claim.BatchId,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await RecordUploadedFileAsync(
                    lease.Id,
                    claim.BatchId,
                    inputFile,
                    claim.JsonLinesHash,
                    cancellationToken)
                .ConfigureAwait(false);
            inputFileName = inputFile.ProviderFileName;
        }

        var transitioned = await MarkSubmittingAsync(
                lease.Id,
                claim.BatchId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!transitioned)
        {
            return;
        }

        try
        {
            var receipt = await _batchProvider.CreateAsync(
                    claim.Connection,
                    secret.Utf8Bytes,
                    new AiBatchCreateRequest(
                        claim.DisplayName,
                        claim.ManifestHash,
                        inputFileName,
                        claim.RequestCount),
                    cancellationToken)
                .ConfigureAwait(false);
            await RecordSubmittedAsync(
                    lease.Id,
                    claim.BatchId,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiBatchCreateException exception)
        {
            await RecordCreateFailureAsync(
                    lease.Id,
                    claim.BatchId,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exception.Kind
                == AiBatchCreateFailureKind.DefiniteRemoteRejection)
            {
                using var raw = JsonDocument.Parse("{}");
                await CleanupProviderFilesAsync(
                        new RemoteClaim(
                            claim.BatchId,
                            string.Empty,
                            inputFileName,
                            claim.SecretReference,
                            claim.Connection),
                        new AiBatchStatus(
                            string.Empty,
                            claim.DisplayName,
                            AiBatchRemoteState.Failed,
                            null,
                            null,
                            _timeProvider.GetUtcNow(),
                            null,
                            null,
                            exception.SafeErrorCode,
                            raw.RootElement.Clone()),
                        secret.Utf8Bytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordCreateAmbiguityAsync(
                    lease.Id,
                    claim.BatchId,
                    "gemini_batch_create_outcome_unknown",
                    exception.GetType().Name,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PollBatchAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<BatchPayload>(lease.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.BatchId))
        {
            await FailJobAsync(
                    lease.Id,
                    "ai_batch_poll_payload_invalid",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var claim = await LoadRemoteClaimAsync(
                lease.Id,
                payload.BatchId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return;
        }

        using var secret = await _secretStore.ReadAsync(
                new AiSecretReference(claim.SecretReference),
                cancellationToken)
            .ConfigureAwait(false);
        AiBatchStatus status;
        try
        {
            status = await _batchProvider.GetAsync(
                    claim.Connection,
                    secret.Utf8Bytes,
                    claim.ProviderBatchName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiProviderException exception)
        {
            await RecordPollFailureAsync(
                    lease.Id,
                    claim.BatchId,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (status.State is AiBatchRemoteState.Pending
            or AiBatchRemoteState.Running
            or AiBatchRemoteState.Unspecified)
        {
            await RecordNonTerminalPollAsync(
                    lease.Id,
                    claim.BatchId,
                    status,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (status.State == AiBatchRemoteState.Succeeded)
        {
            IReadOnlyList<AiBatchItemResult> results;
            try
            {
                results = await _batchProvider.ReadResultsAsync(
                        claim.Connection,
                        secret.Utf8Bytes,
                        status,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AiProviderException exception)
            {
                await RecordPollFailureAsync(
                        lease.Id,
                        claim.BatchId,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await PersistBatchResultsAsync(
                    lease.Id,
                    claim.BatchId,
                    status,
                    results,
                    cancellationToken)
                .ConfigureAwait(false);
            await CleanupProviderFilesAsync(
                    claim,
                    status,
                    secret.Utf8Bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PersistTerminalFailureAsync(
                lease.Id,
                claim.BatchId,
                status,
                cancellationToken)
            .ConfigureAwait(false);
        await CleanupProviderFilesAsync(
                claim,
                status,
                secret.Utf8Bytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReconcileBatchAsync(
        JobLease lease,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<BatchPayload>(lease.PayloadJson);
        if (payload is null || string.IsNullOrWhiteSpace(payload.BatchId))
        {
            await FailJobAsync(
                    lease.Id,
                    "ai_batch_reconcile_payload_invalid",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var claim = await LoadReconcileClaimAsync(
                lease.Id,
                payload.BatchId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return;
        }

        using var secret = await _secretStore.ReadAsync(
                new AiSecretReference(claim.SecretReference),
                cancellationToken)
            .ConfigureAwait(false);
        var matches = new Dictionary<string, AiBatchStatus>(StringComparer.Ordinal);
        string? pageToken = null;
        try
        {
            for (var page = 0; page < 10; page++)
            {
                var result = await _batchProvider.ListAsync(
                        claim.Connection,
                        secret.Utf8Bytes,
                        pageToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                foreach (var batch in result.Batches)
                {
                    if (batch.DisplayName == claim.DisplayName
                        && IsWithinReconciliationWindow(claim, batch))
                    {
                        matches[batch.ProviderBatchName] = batch;
                    }
                }

                pageToken = result.NextPageToken;
                if (string.IsNullOrEmpty(pageToken))
                {
                    break;
                }
            }
        }
        catch (AiProviderException exception)
        {
            await RecordReconcileReadFailureAsync(
                    lease.Id,
                    claim.BatchId,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PersistReconciliationAsync(
                lease.Id,
                claim,
                matches.Values.ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<SubmitClaim?> LoadSubmitClaimAsync(
        string jobId,
        string batchId,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.AiConnection)
                .Include(item => item.AiTaskProfile)
                .Include(item => item.Requests)
                .SingleOrDefaultAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            if (batch is null)
            {
                FailJob(job, _timeProvider.GetUtcNow(), "ai_batch_missing");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            if (IsTerminal(batch.State))
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            if (batch.State == "submitting")
            {
                MarkReconcileRequired(
                    db,
                    batch,
                    job,
                    now,
                    "gemini_batch_submit_crash_window",
                    null);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            ValidateBatchConfiguration(batch);
            var ordered = batch.Requests
                .OrderBy(item => item.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length != batch.RequestCount
                || ordered.Any(item =>
                    item.Ordinal is null
                    || item.ProviderRequestJson is null
                    || item.State is not ("prepared" or "submitted")))
            {
                FailJob(job, now, "ai_batch_manifest_inconsistent");
                batch.State = "manual_review";
                batch.ErrorCode = "ai_batch_manifest_inconsistent";
                batch.CompletedAt = now;
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            var jsonLines = Encoding.UTF8.GetBytes(
                string.Concat(
                    ordered.Select(item => item.ProviderRequestJson! + "\n")));
            var hash = Sha256(jsonLines);
            return new SubmitClaim(
                batch.Id,
                batch.DisplayName,
                batch.ManifestHash,
                batch.RequestCount,
                batch.ProviderInputFileName,
                jsonLines,
                hash,
                ToConnection(batch.AiConnection),
                batch.AiConnection.SecretReference);
        }, cancellationToken);
    }

    private Task MarkUploadingAsync(
        string jobId,
        string batchId,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            if (batch.State is not ("prepared" or "uploading"))
            {
                FailJob(job, _timeProvider.GetUtcNow(), "ai_batch_state_changed");
            }
            else
            {
                batch.State = "uploading";
                batch.NextActionAt = null;
                batch.ErrorCode = null;
                batch.SafeErrorDetail = null;
                batch.UpdatedAt = _timeProvider.GetUtcNow();
                job.ProgressBasisPoints = Math.Max(
                    job.ProgressBasisPoints,
                    2_000);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordUploadFailureAsync(
        string jobId,
        string batchId,
        AiProviderException exception,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = _timeProvider.GetUtcNow();
            var retryAt = now.Add(
                exception.RetryAfter ?? TimeSpan.FromMinutes(2));
            batch.State = exception.IsTransient ? "prepared" : "manual_review";
            batch.ErrorCode = exception.SafeErrorCode;
            batch.NextActionAt = exception.IsTransient
                ? retryAt
                : null;
            batch.CompletedAt = exception.IsTransient ? null : now;
            if (exception.IsTransient)
            {
                RetryJob(job, exception.SafeErrorCode, retryAt);
            }
            else
            {
                FailJob(job, now, exception.SafeErrorCode);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordUploadedFileAsync(
        string jobId,
        string batchId,
        AiBatchInputFile inputFile,
        string jsonLinesHash,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            _ = await LoadOwnedJobAsync(db, jobId, token).ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            if (batch.State != "uploading"
                || batch.ProviderInputFileName is not null)
            {
                throw new InvalidOperationException(
                    "The batch upload state changed.");
            }

            batch.ProviderInputFileName = inputFile.ProviderFileName;
            batch.ProviderInputFileExpiresAt = inputFile.ExpiresAt;
            batch.InputJsonLinesSha256 = jsonLinesHash;
            batch.InputJsonLinesBytes = inputFile.Bytes;
            batch.State = "prepared";
            batch.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<bool> MarkSubmittingAsync(
        string jobId,
        string batchId,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = _timeProvider.GetUtcNow();
            if (batch.State == "submitting"
                || batch.CreateAttemptCount != 0)
            {
                MarkReconcileRequired(
                    db,
                    batch,
                    job,
                    now,
                    "gemini_batch_create_already_attempted",
                    null);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return false;
            }

            if (batch.State != "prepared"
                || batch.ProviderInputFileName is null)
            {
                FailJob(job, now, "ai_batch_not_ready_to_submit");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return false;
            }

            batch.State = "submitting";
            batch.CreateAttemptCount = 1;
            batch.CreateAttemptStartedAt = now;
            batch.NextActionAt = null;
            batch.ErrorCode = null;
            batch.SafeErrorDetail = null;
            batch.UpdatedAt = now;
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 5_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private Task RecordSubmittedAsync(
        string jobId,
        string batchId,
        AiBatchCreateReceipt receipt,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.Requests)
                .SingleAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            if (batch.State != "submitting"
                || batch.CreateAttemptCount != 1
                || receipt.DisplayName != batch.DisplayName)
            {
                throw new InvalidOperationException(
                    "The submitted batch identity changed.");
            }

            var now = _timeProvider.GetUtcNow();
            batch.ProviderBatchName = receipt.ProviderBatchName;
            batch.RemoteCreatedAt = receipt.CreatedAt;
            batch.State = "submitted";
            batch.CreateAttemptCompletedAt = now;
            batch.NextActionAt = now.Add(_options.InitialRemotePollInterval);
            batch.UpdatedAt = now;
            foreach (var item in batch.Requests)
            {
                item.State = "submitted";
                item.UpdatedAt = now;
            }

            EnqueueBatchJob(
                db,
                PollJobType,
                batch,
                PollDeduplicationKey(batch),
                batch.NextActionAt.Value,
                job.CorrelationId);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordCreateFailureAsync(
        string jobId,
        string batchId,
        AiBatchCreateException exception,
        CancellationToken cancellationToken)
    {
        if (exception.Kind == AiBatchCreateFailureKind.AmbiguousAfterSend)
        {
            return RecordCreateAmbiguityAsync(
                jobId,
                batchId,
                exception.SafeErrorCode,
                null,
                cancellationToken);
        }

        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.Requests)
                    .ThenInclude(item => item.AiRequest)
                .SingleAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (exception.Kind == AiBatchCreateFailureKind.DefinitePreSend)
            {
                batch.State = "prepared";
                batch.CreateAttemptCount = 0;
                batch.CreateAttemptStartedAt = null;
                batch.CreateAttemptCompletedAt = now;
                batch.CompletedAt = null;
                batch.NextActionAt = null;
                batch.ErrorCode = exception.SafeErrorCode;
                FailJob(job, now, exception.SafeErrorCode);
            }
            else
            {
                batch.State = "failed";
                batch.ErrorCode = exception.SafeErrorCode;
                batch.CreateAttemptCompletedAt = now;
                batch.CompletedAt = now;
                batch.NextActionAt = null;
                batch.CleanupState = "pending";
                foreach (var mapping in batch.Requests)
                {
                    mapping.State = "failed";
                    mapping.ErrorCode = exception.SafeErrorCode;
                    mapping.CompletedAt = now;
                    mapping.UpdatedAt = now;
                    mapping.AiRequest.State = "failed";
                    mapping.AiRequest.ErrorCode =
                        exception.SafeErrorCode;
                    mapping.AiRequest.CompletedAt = now;
                    mapping.AiRequest.UpdatedAt = now;
                    if (exception.IsTransient)
                    {
                        await TryScheduleRetryAsync(
                                db,
                                mapping,
                                exception.SafeErrorCode,
                                now,
                                job.CorrelationId,
                                job.Id,
                                token,
                                possibleProviderCharge: false)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        ReleaseReservation(
                            db,
                            mapping.AiRequestId,
                            now);
                    }

                    ScrubProviderPayload(mapping);
                }

                FailJob(job, now, exception.SafeErrorCode);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordCreateAmbiguityAsync(
        string jobId,
        string batchId,
        string errorCode,
        string? safeDetail,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            MarkReconcileRequired(
                db,
                batch,
                job,
                _timeProvider.GetUtcNow(),
                errorCode,
                safeDetail);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task<RemoteClaim?> LoadRemoteClaimAsync(
        string jobId,
        string batchId,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (batch is null)
            {
                FailJob(job, now, "ai_batch_missing");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            if (IsTerminal(batch.State))
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            if (batch.ProviderBatchName is null)
            {
                FailJob(job, now, "ai_batch_remote_identity_missing");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            return new RemoteClaim(
                batch.Id,
                batch.ProviderBatchName,
                batch.ProviderInputFileName,
                batch.AiConnection.SecretReference,
                ToConnection(batch.AiConnection));
        }, cancellationToken);
    }

    private Task RecordPollFailureAsync(
        string jobId,
        string batchId,
        AiProviderException exception,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = _timeProvider.GetUtcNow();
            batch.ErrorCode = exception.SafeErrorCode;
            batch.UpdatedAt = now;
            if (exception.IsTransient)
            {
                var retryAt = now.Add(
                    exception.RetryAfter ?? TimeSpan.FromMinutes(2));
                batch.NextActionAt = retryAt;
                RetryJob(job, exception.SafeErrorCode, retryAt);
            }
            else
            {
                batch.State = "manual_review";
                batch.CompletedAt = now;
                batch.NextActionAt = null;
                FailJob(job, now, exception.SafeErrorCode);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordNonTerminalPollAsync(
        string jobId,
        string batchId,
        AiBatchStatus status,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = _timeProvider.GetUtcNow();
            ApplyRemoteMetadata(batch, status, now);
            var remoteAge = now - (status.CreatedAt
                ?? batch.RemoteCreatedAt
                ?? batch.CreateAttemptStartedAt
                ?? batch.CreatedAt);
            batch.State = remoteAge >= TimeSpan.FromHours(24)
                ? "delayed"
                : status.State == AiBatchRemoteState.Running
                    ? "running"
                    : "pending";
            batch.NextActionAt = now.Add(RemotePollDelay(
                remoteAge,
                batch.Id,
                batch.Revision));
            batch.ErrorCode = status.State == AiBatchRemoteState.Unspecified
                ? "gemini_batch_state_unspecified"
                : null;
            EnqueueBatchJob(
                db,
                PollJobType,
                batch,
                PollDeduplicationKey(batch),
                batch.NextActionAt.Value,
                job.CorrelationId);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task PersistBatchResultsAsync(
        string jobId,
        string batchId,
        AiBatchStatus status,
        IReadOnlyList<AiBatchItemResult> results,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.Requests)
                    .ThenInclude(item => item.AiRequest)
                .SingleAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var byKey = results.ToDictionary(
                item => item.RequestKey,
                StringComparer.Ordinal);
            var expectedKeys = batch.Requests
                .Select(item => item.RequestKey)
                .ToHashSet(StringComparer.Ordinal);
            if (byKey.Keys.Any(key => !expectedKeys.Contains(key)))
            {
                batch.State = "manual_review";
                batch.ErrorCode = "gemini_batch_result_key_unknown";
                batch.PossibleDuplicate = true;
                batch.CompletedAt = now;
                FailJob(job, now, batch.ErrorCode);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var pricing = await db.PricingSnapshots
                .AsNoTracking()
                .Where(item =>
                    item.Provider == AiProviders.GeminiDirect
                    && item.ModelId == GeminiBatchClient.SelectedModel
                    && item.EffectiveAt <= now)
                .OrderByDescending(item => item.EffectiveAt)
                .ThenByDescending(item => item.CapturedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            var budget = await db.AiBudgetPolicies
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == "default", token)
                .ConfigureAwait(false);
            var successful = 0L;
            var failed = 0L;
            foreach (var mapping in batch.Requests)
            {
                if (!byKey.TryGetValue(mapping.RequestKey, out var result))
                {
                    mapping.State = "missing";
                    mapping.ErrorCode = "gemini_batch_result_missing";
                    mapping.CompletedAt = now;
                    mapping.UpdatedAt = now;
                    mapping.AiRequest.State = "failed";
                    mapping.AiRequest.ErrorCode = mapping.ErrorCode;
                    mapping.AiRequest.CompletedAt = now;
                    mapping.AiRequest.UpdatedAt = now;
                    await TryScheduleRetryAsync(
                            db,
                            mapping,
                            mapping.ErrorCode,
                            now,
                            job.CorrelationId,
                            job.Id,
                            token)
                        .ConfigureAwait(false);
                    ScrubProviderPayload(mapping);
                    failed++;
                    continue;
                }

                if (result.Response is null)
                {
                    mapping.State = "failed";
                    mapping.ErrorCode = result.SafeErrorCode
                        ?? "gemini_batch_item_failed";
                    mapping.CompletedAt = now;
                    mapping.UpdatedAt = now;
                    var safetyBlocked = IsSafetyCode(mapping.ErrorCode);
                    var invalidOutput = IsInvalidOutputCode(
                        mapping.ErrorCode);
                    mapping.AiRequest.State = safetyBlocked
                        ? "safety_blocked"
                        : invalidOutput
                            ? "invalid_output"
                            : "failed";
                    mapping.AiRequest.ErrorCode = mapping.ErrorCode;
                    mapping.AiRequest.CompletedAt = now;
                    mapping.AiRequest.UpdatedAt = now;
                    if (safetyBlocked || invalidOutput)
                    {
                        SettleReservationConservatively(
                            db,
                            mapping.AiRequestId,
                            now);
                    }
                    else
                    {
                        await TryScheduleRetryAsync(
                                db,
                                mapping,
                                mapping.ErrorCode,
                                now,
                                job.CorrelationId,
                                job.Id,
                                token)
                            .ConfigureAwait(false);
                    }

                    ScrubProviderPayload(mapping);
                    failed++;
                    continue;
                }

                var responseJson = result.Response.StructuredOutput.GetRawText();
                if (responseJson.Length > 1_000_000)
                {
                    mapping.State = "failed";
                    mapping.ErrorCode = "gemini_batch_response_too_large";
                    mapping.CompletedAt = now;
                    mapping.UpdatedAt = now;
                    mapping.AiRequest.State = "invalid_output";
                    mapping.AiRequest.ErrorCode = mapping.ErrorCode;
                    mapping.AiRequest.CompletedAt = now;
                    mapping.AiRequest.UpdatedAt = now;
                    SettleReservationConservatively(
                        db,
                        mapping.AiRequestId,
                        now);
                    ScrubProviderPayload(mapping);
                    failed++;
                    continue;
                }

                var responseHash = Sha256(Encoding.UTF8.GetBytes(responseJson));
                ScrubProviderPayload(mapping);
                mapping.State = "response_ready";
                mapping.ProviderResponseId =
                    result.Response.ProviderResponseId;
                mapping.ResponseJson = responseJson;
                mapping.ResponseHash = responseHash;
                mapping.ErrorCode = null;
                mapping.CompletedAt = now;
                mapping.UpdatedAt = now;
                mapping.AiRequest.State = "response_ready";
                mapping.AiRequest.ProviderResponseId =
                    result.Response.ProviderResponseId;
                mapping.AiRequest.ActualModel = result.Response.ActualModel;
                mapping.AiRequest.FinishReason = result.Response.FinishReason;
                mapping.AiRequest.AcceptedResponseHash = responseHash;
                mapping.AiRequest.ValidatedResponseJson = responseJson;
                mapping.AiRequest.ErrorCode = null;
                mapping.AiRequest.CompletedAt = now;
                mapping.AiRequest.UpdatedAt = now;
                if (!await db.AiUsage.AnyAsync(
                        item => item.AiRequestId == mapping.AiRequestId,
                        token)
                    .ConfigureAwait(false))
                {
                    var actualUsd = pricing is null
                        ? 0
                        : CalculateBatchCost(pricing, result.Response.Usage);
                    var exchange = budget?.UsdToJpyMicros ?? 150_000_000;
                    db.AiUsage.Add(new AiUsageEntity
                    {
                        Id = UlidId.New(now),
                        AiRequestId = mapping.AiRequestId,
                        RequestedProvider = AiProviders.GeminiDirect,
                        RequestedModel = GeminiBatchClient.SelectedModel,
                        ActualProvider = result.Response.Provider,
                        ActualModel = result.Response.ActualModel,
                        InputTokens = result.Response.Usage.PromptTokens,
                        CachedTokens = result.Response.Usage.CachedTokens,
                        OutputTokens = result.Response.Usage.OutputTokens,
                        ThinkingTokens = result.Response.Usage.ThinkingTokens,
                        TotalTokens = result.Response.Usage.TotalTokens,
                        PricingSnapshotId = pricing?.Id,
                        EstimatedUsdMicros = actualUsd,
                        EstimatedJpyMicros = ConvertUsdToJpy(
                            actualUsd,
                            exchange),
                        ProviderRequestId =
                            result.Response.ProviderResponseId,
                        MeasuredAt = now,
                    });
                    SettleReservation(
                        db,
                        mapping.AiRequestId,
                        actualUsd,
                        now);
                }

                if (mapping.AiRequest.Purpose == AiTaskTypes.InitialGrading
                    && mapping.AiRequest.EntityType == "submission")
                {
                    await EnqueueInitialGradingApplyJobAsync(
                            db,
                            mapping.AiRequest,
                            responseHash,
                            now,
                            job.CorrelationId,
                            job.Id,
                            token)
                        .ConfigureAwait(false);
                }

                successful++;
            }

            batch.State = "succeeded";
            batch.SuccessfulRequestCount = successful;
            batch.FailedRequestCount = failed;
            batch.PendingRequestCount = 0;
            batch.ProviderOutputFileName = status.OutputFileName;
            batch.CleanupState = "pending";
            batch.CompletedAt = now;
            batch.NextActionAt = null;
            batch.ErrorCode = failed > 0
                ? "gemini_batch_partial_failure"
                : null;
            ApplyRemoteMetadata(batch, status, now);
            CompleteJob(job, now);
            db.OutboxEvents.Add(new OutboxEventEntity
            {
                Id = UlidId.New(now),
                AggregateType = "aiBatch",
                AggregateId = batch.Id,
                EventType = "ai.batch.responses_ready",
                SchemaVersion = 1,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    batchId = batch.Id,
                    successfulRequestCount = successful,
                    failedRequestCount = failed,
                }),
                CorrelationId = job.CorrelationId,
                CausationId = job.Id,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task EnqueueInitialGradingApplyJobAsync(
        OokiGraderDbContext db,
        AiRequestEntity request,
        string responseHash,
        DateTimeOffset now,
        string? correlationId,
        string causationId,
        CancellationToken cancellationToken)
    {
        var target = await db.Submissions
            .AsNoTracking()
            .Where(item => item.Id == request.EntityId)
            .Select(item => new
            {
                item.Id,
                item.PreprocessingManifestHash,
                item.TestSession.TemplateVersionId,
                item.TestSession.Priority,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (target?.PreprocessingManifestHash is not { Length: 64 })
        {
            return;
        }

        var deduplicationKey =
            $"ai-request:{request.Id}:apply:{responseHash}";
        if (db.BackgroundJobs.Local.Any(
                item => item.DeduplicationKey == deduplicationKey)
            || await db.BackgroundJobs.AnyAsync(
                    item => item.DeduplicationKey == deduplicationKey,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = UlidId.New(now),
            Type = AiInitialGradingJobWorker.ApplyJobType,
            SchemaVersion = JobSchemaVersion,
            DeduplicationKey = deduplicationKey,
            Priority = target.Priority == "expedite" ? 100 : 0,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId = target.Id,
                templateVersionId = target.TemplateVersionId,
                manifestHash = target.PreprocessingManifestHash,
                aiRequestId = request.Id,
            }),
            State = "queued",
            AttemptCount = 0,
            MaxAttempts = 8,
            NextAttemptAt = now,
            CorrelationId = correlationId,
            CausationId = causationId,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task<bool> TryScheduleRetryAsync(
        OokiGraderDbContext db,
        AiBatchRequestEntity failedMapping,
        string failureCode,
        DateTimeOffset now,
        string? correlationId,
        string causationId,
        CancellationToken cancellationToken,
        bool possibleProviderCharge = true)
    {
        var failedRequest = failedMapping.AiRequest;
        if (failedRequest.Purpose != AiTaskTypes.InitialGrading
            || failedRequest.EntityType != "submission")
        {
            failedRequest.State = "retry_waiting";
            failedRequest.ErrorCode = failureCode;
            failedRequest.UpdatedAt = now;
            ReleaseReservation(db, failedRequest.Id, now);
            return false;
        }

        var existingRetry = db.AiRequests.Local.SingleOrDefault(
                item => item.RetryOfAiRequestId == failedRequest.Id)
            ?? await db.AiRequests.SingleOrDefaultAsync(
                    item => item.RetryOfAiRequestId == failedRequest.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        if (existingRetry is not null)
        {
            FinalizeFailedReservation(
                db,
                failedRequest.Id,
                now,
                possibleProviderCharge);
            failedRequest.State = "failed";
            failedRequest.ErrorCode = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return true;
        }

        if (failedRequest.AttemptNumber >= _options.MaximumRequestAttempts)
        {
            FinalizeFailedReservation(
                db,
                failedRequest.Id,
                now,
                possibleProviderCharge);
            failedRequest.State = "failed";
            failedRequest.ErrorCode = "gemini_batch_retry_exhausted";
            failedRequest.SafeErrorDetail = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return false;
        }

        var reservation = db.AiBudgetReservations.Local.SingleOrDefault(
                item => item.AiRequestId == failedRequest.Id)
            ?? await db.AiBudgetReservations.SingleOrDefaultAsync(
                    item => item.AiRequestId == failedRequest.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        if (reservation?.State != "reserved")
        {
            failedRequest.State = "failed";
            failedRequest.ErrorCode =
                "gemini_batch_retry_reservation_missing";
            failedRequest.SafeErrorDetail = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return false;
        }

        if (string.IsNullOrWhiteSpace(failedMapping.ProviderRequestJson))
        {
            FinalizeFailedReservation(
                db,
                failedRequest.Id,
                now,
                possibleProviderCharge);
            failedRequest.State = "failed";
            failedRequest.ErrorCode = "gemini_batch_retry_payload_missing";
            failedRequest.SafeErrorDetail = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return false;
        }

        var usageWindow = await GetUsageWindowAsync(
                db,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        var budget = await db.AiBudgetPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == "default",
                cancellationToken)
            .ConfigureAwait(false);
        var reservedUsdMicros = reservation.ReservedUsdMicros;
        if (budget?.Active == true)
        {
            var committed = await db.AiBudgetReservations
                .AsNoTracking()
                .Where(item =>
                    item.AiRequestId != failedRequest.Id
                    && (item.UsageDay == usageWindow.Day
                        || item.UsageMonth == usageWindow.Month)
                    && (item.State == "reserved"
                        || item.State == "settled"))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            static long Amount(AiBudgetReservationEntity item) =>
                item.State == "settled"
                    ? item.ActualUsdMicros
                    : item.ReservedUsdMicros;
            var existingExposure = possibleProviderCharge
                ? reservedUsdMicros
                : 0;
            var daily = committed
                .Where(item => item.UsageDay == usageWindow.Day)
                .Aggregate(
                    existingExposure,
                    (total, item) => checked(total + Amount(item)));
            var monthly = committed
                .Where(item => item.UsageMonth == usageWindow.Month)
                .Aggregate(
                    existingExposure,
                    (total, item) => checked(total + Amount(item)));
            if (WouldExceedHardLimit(
                    daily,
                    reservedUsdMicros,
                    budget.DailyHardUsdMicros)
                || WouldExceedHardLimit(
                    monthly,
                    reservedUsdMicros,
                    budget.MonthlyHardUsdMicros))
            {
                FinalizeFailedReservation(
                    db,
                    failedRequest.Id,
                    now,
                    possibleProviderCharge);
                failedRequest.State = "budget_blocked";
                failedRequest.ErrorCode =
                    "gemini_batch_retry_budget_blocked";
                failedRequest.SafeErrorDetail = failureCode;
                failedRequest.CompletedAt ??= now;
                failedRequest.UpdatedAt = now;
                return false;
            }
        }

        var retryRequestId = UlidId.New(now);
        var retryRequestKey = $"grade_{retryRequestId}";
        string retryJson;
        try
        {
            retryJson = RekeyProviderRequestJson(
                failedMapping.ProviderRequestJson,
                failedMapping.RequestKey,
                retryRequestKey);
        }
        catch (InvalidDataException)
        {
            FinalizeFailedReservation(
                db,
                failedRequest.Id,
                now,
                possibleProviderCharge);
            failedRequest.State = "failed";
            failedRequest.ErrorCode = "gemini_batch_retry_payload_invalid";
            failedRequest.SafeErrorDetail = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return false;
        }

        var retryBytes = Encoding.UTF8.GetByteCount(retryJson);
        if (retryBytes is <= 0 or > 25_000_000)
        {
            FinalizeFailedReservation(
                db,
                failedRequest.Id,
                now,
                possibleProviderCharge);
            failedRequest.State = "failed";
            failedRequest.ErrorCode = "gemini_batch_retry_payload_invalid";
            failedRequest.SafeErrorDetail = failureCode;
            failedRequest.CompletedAt ??= now;
            failedRequest.UpdatedAt = now;
            return false;
        }

        var retryAttempt = checked(failedRequest.AttemptNumber + 1);
        var retryRequest = new AiRequestEntity
        {
            Id = retryRequestId,
            RequestKey = retryRequestKey,
            AiTaskProfileId = failedRequest.AiTaskProfileId,
            TaskProfileRevision = failedRequest.TaskProfileRevision,
            Purpose = failedRequest.Purpose,
            EntityType = failedRequest.EntityType,
            EntityId = failedRequest.EntityId,
            EntityRevision = failedRequest.EntityRevision,
            InputManifestHash = failedRequest.InputManifestHash,
            AttemptNumber = retryAttempt,
            RetryOfAiRequestId = failedRequest.Id,
            State = "prepared",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var retryMapping = new AiBatchRequestEntity
        {
            Id = UlidId.New(now),
            AiRequestId = retryRequest.Id,
            RequestKey = retryRequestKey,
            CompatibilityKey = failedMapping.CompatibilityKey,
            ProviderRequestJson = retryJson,
            ProviderRequestHash = Sha256(Encoding.UTF8.GetBytes(retryJson)),
            ProviderRequestBytes = retryBytes,
            State = "ready",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.AiRequests.Add(retryRequest);
        db.AiBatchRequests.Add(retryMapping);
        db.AiBudgetReservations.Add(new AiBudgetReservationEntity
        {
            Id = UlidId.New(now),
            AiRequestId = retryRequest.Id,
            UsageDay = usageWindow.Day,
            UsageMonth = usageWindow.Month,
            ReservedUsdMicros = reservedUsdMicros,
            ActualUsdMicros = 0,
            State = "reserved",
            CreatedAt = now,
        });
        var retryAt = now.Add(RequestRetryDelay(retryAttempt));
        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = UlidId.New(now),
            Type = PrepareJobType,
            SchemaVersion = JobSchemaVersion,
            DeduplicationKey =
                AiBatchRequestStager.PrepareDeduplicationKey(
                    retryMapping.CompatibilityKey,
                    retryMapping.Id),
            Priority = 0,
            PayloadJson = JsonSerializer.Serialize(new
            {
                compatibilityKey = retryMapping.CompatibilityKey,
            }),
            State = "queued",
            AttemptCount = 0,
            MaxAttempts = 100,
            NextAttemptAt = retryAt,
            CorrelationId = correlationId,
            CausationId = causationId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "aiRequest",
            AggregateId = retryRequest.Id,
            EventType = "ai.request.retry_scheduled",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                aiRequestId = retryRequest.Id,
                retryOfAiRequestId = failedRequest.Id,
                attemptNumber = retryAttempt,
                notBefore = retryAt,
                failureCode,
            }),
            CorrelationId = correlationId,
            CausationId = causationId,
            OccurredAt = now,
        });
        FinalizeFailedReservation(
            db,
            failedRequest.Id,
            now,
            possibleProviderCharge);
        failedRequest.State = "failed";
        failedRequest.ErrorCode = failureCode;
        failedRequest.SafeErrorDetail = null;
        failedRequest.CompletedAt ??= now;
        failedRequest.UpdatedAt = now;
        return true;
    }

    private static string RekeyProviderRequestJson(
        string providerRequestJson,
        string oldRequestKey,
        string newRequestKey)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(providerRequestJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "gemini_batch_retry_payload_invalid",
                exception);
        }

        if (root is not JsonObject request
            || request["key"]?.GetValue<string>() != oldRequestKey)
        {
            throw new InvalidDataException(
                "gemini_batch_retry_payload_invalid");
        }

        RewriteRequestKey(root, oldRequestKey, newRequestKey);
        request["key"] = newRequestKey;
        return root.ToJsonString();
    }

    private static void RewriteRequestKey(
        JsonNode? node,
        string oldRequestKey,
        string newRequestKey)
    {
        if (node is JsonObject valueObject)
        {
            foreach (var property in valueObject.ToArray())
            {
                if (property.Value is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    valueObject[property.Key] = text.Replace(
                        oldRequestKey,
                        newRequestKey,
                        StringComparison.Ordinal);
                }
                else
                {
                    RewriteRequestKey(
                        property.Value,
                        oldRequestKey,
                        newRequestKey);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value
                    && value.TryGetValue<string>(out var text))
                {
                    array[index] = text.Replace(
                        oldRequestKey,
                        newRequestKey,
                        StringComparison.Ordinal);
                }
                else
                {
                    RewriteRequestKey(
                        array[index],
                        oldRequestKey,
                        newRequestKey);
                }
            }
        }
    }

    private Task PersistTerminalFailureAsync(
        string jobId,
        string batchId,
        AiBatchStatus status,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.Requests)
                    .ThenInclude(item => item.AiRequest)
                .SingleAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            batch.State = status.State switch
            {
                AiBatchRemoteState.Cancelled => "cancelled",
                AiBatchRemoteState.Expired => "expired",
                _ => "failed",
            };
            batch.ErrorCode = status.SafeErrorCode
                ?? "gemini_batch_remote_" + batch.State;
            batch.CompletedAt = now;
            batch.NextActionAt = null;
            batch.CleanupState = "pending";
            ApplyRemoteMetadata(batch, status, now);
            foreach (var mapping in batch.Requests)
            {
                var cancelled =
                    status.State == AiBatchRemoteState.Cancelled;
                mapping.State = cancelled ? "cancelled" : "failed";
                mapping.ErrorCode = batch.ErrorCode;
                mapping.CompletedAt = now;
                mapping.UpdatedAt = now;
                mapping.AiRequest.State = cancelled
                    ? "cancelled"
                    : "failed";
                mapping.AiRequest.ErrorCode = batch.ErrorCode;
                mapping.AiRequest.CompletedAt = now;
                mapping.AiRequest.UpdatedAt = now;
                if (cancelled)
                {
                    SettleReservationConservatively(
                        db,
                        mapping.AiRequestId,
                        now);
                }
                else
                {
                    await TryScheduleRetryAsync(
                            db,
                            mapping,
                            batch.ErrorCode,
                            now,
                            job.CorrelationId,
                            job.Id,
                            token)
                        .ConfigureAwait(false);
                }

                ScrubProviderPayload(mapping);
            }

            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private async Task CleanupProviderFilesAsync(
        RemoteClaim claim,
        AiBatchStatus status,
        ReadOnlyMemory<byte> credential,
        CancellationToken cancellationToken)
    {
        var files = new[]
            {
                claim.ProviderInputFileName,
                status.OutputFileName,
            }
            .Where(item => item is not null)
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        string? errorCode = null;
        foreach (var file in files)
        {
            try
            {
                await _batchProvider.DeleteFileAsync(
                        claim.Connection,
                        credential,
                        file,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AiProviderException exception)
            {
                errorCode = exception.SafeErrorCode;
                break;
            }
        }

        await _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleOrDefaultAsync(
                item => item.Id == claim.BatchId,
                token);
            if (batch is null)
            {
                return;
            }

            batch.CleanupState = errorCode is null ? "completed" : "failed";
            if (errorCode is not null && batch.ErrorCode is null)
            {
                batch.ErrorCode = "gemini_batch_cleanup_failed";
                batch.SafeErrorDetail = errorCode;
            }

            batch.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task<ReconcileClaim?> LoadReconcileClaimAsync(
        string jobId,
        string batchId,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(item => item.Id == batchId, token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            if (batch is null)
            {
                FailJob(job, now, "ai_batch_missing");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            if (batch.ProviderBatchName is not null)
            {
                EnqueueBatchJob(
                    db,
                    PollJobType,
                    batch,
                    PollDeduplicationKey(batch),
                    now,
                    job.CorrelationId);
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            if (batch.State is not ("reconcile_required" or "submitting"
                or "manual_review"))
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return null;
            }

            return new ReconcileClaim(
                batch.Id,
                batch.DisplayName,
                batch.CreateAttemptStartedAt ?? batch.CreatedAt,
                batch.ReconciliationDeadlineAt
                    ?? batch.CreatedAt.Add(_options.ReconciliationWindow),
                batch.AiConnection.SecretReference,
                ToConnection(batch.AiConnection));
        }, cancellationToken);
    }

    private Task RecordReconcileReadFailureAsync(
        string jobId,
        string batchId,
        AiProviderException exception,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = _timeProvider.GetUtcNow();
            batch.ReconciliationAttemptCount++;
            batch.ErrorCode = exception.SafeErrorCode;
            batch.UpdatedAt = now;
            var retryAt = now.Add(
                exception.RetryAfter ?? TimeSpan.FromMinutes(2));
            RetryJob(job, exception.SafeErrorCode, retryAt);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task PersistReconciliationAsync(
        string jobId,
        ReconcileClaim claim,
        AiBatchStatus[] matches,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == claim.BatchId,
                token);
            var now = _timeProvider.GetUtcNow();
            batch.ReconciliationAttemptCount++;
            batch.UpdatedAt = now;
            if (matches.Length == 1)
            {
                var adopted = matches[0];
                batch.ProviderBatchName = adopted.ProviderBatchName;
                batch.RemoteCreatedAt = adopted.CreatedAt;
                batch.State = "submitted";
                batch.PossibleDuplicate = false;
                batch.ErrorCode = null;
                batch.SafeErrorDetail = null;
                batch.NextActionAt = now;
                EnqueueBatchJob(
                    db,
                    PollJobType,
                    batch,
                    PollDeduplicationKey(batch),
                    now,
                    job.CorrelationId);
                CompleteJob(job, now);
            }
            else if (matches.Length > 1)
            {
                batch.State = "manual_review";
                batch.PossibleDuplicate = true;
                batch.ErrorCode = "gemini_batch_multiple_remote_matches";
                batch.SafeErrorDetail = JsonSerializer.Serialize(
                    matches.Select(item => item.ProviderBatchName));
                batch.CompletedAt = now;
                batch.NextActionAt = null;
                FailJob(job, now, batch.ErrorCode);
            }
            else if (now >= claim.DeadlineAt)
            {
                batch.State = "manual_review";
                batch.ErrorCode = "gemini_batch_remote_match_not_found";
                batch.CompletedAt = now;
                batch.NextActionAt = null;
                FailJob(job, now, batch.ErrorCode);
            }
            else
            {
                batch.State = "reconcile_required";
                batch.ErrorCode = "gemini_batch_reconcile_pending";
                batch.NextActionAt = now.AddMinutes(2);
                RetryJob(
                    job,
                    batch.ErrorCode,
                    batch.NextActionAt.Value);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RetryJobAsync(
        string jobId,
        string errorCode,
        DateTimeOffset retryAt,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            RetryJob(job, errorCode, retryAt);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task FailJobAsync(
        string jobId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, jobId, token)
                .ConfigureAwait(false);
            FailJob(job, _timeProvider.GetUtcNow(), errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs.SingleOrDefaultAsync(
            item => item.Id == jobId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job '{jobId}' does not exist.");
        if (job.State != "leased")
        {
            throw new InvalidOperationException("The batch job is not leased.");
        }

        return job;
    }

    private void MarkReconcileRequired(
        OokiGraderDbContext db,
        AiBatchEntity batch,
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode,
        string? safeDetail)
    {
        batch.State = "reconcile_required";
        batch.PossibleDuplicate = true;
        batch.ErrorCode = errorCode;
        batch.SafeErrorDetail = safeDetail is { Length: > 2_000 }
            ? safeDetail[..2_000]
            : safeDetail;
        batch.ReconciliationDeadlineAt ??= now.Add(
            _options.ReconciliationWindow);
        batch.NextActionAt = now;
        batch.UpdatedAt = now;
        EnqueueBatchJob(
            db,
            ReconcileJobType,
            batch,
            $"ai-batch:{batch.Id}:reconcile:{batch.SubmissionEpoch}",
            now,
            job.CorrelationId);
        CompleteJob(job, now);
    }

    private static void ValidateCandidates(
        List<AiBatchRequestEntity> candidates)
    {
        var first = candidates[0].AiRequest;
        var profile = first.AiTaskProfile;
        var connection = profile.AiConnection;
        if (!profile.Active
            || profile.ProcessingStrategy != "gemini_batch"
            || profile.ModelId != GeminiBatchClient.SelectedModel
            || connection.State != "active"
            || connection.LastCapabilityProbeState != "passed"
            || connection.LastBatchCapabilityProbeState != "passed"
            || connection.LastBatchCapabilityProbeCredentialRevision
                != connection.CredentialRevision
            || connection.Provider != AiProviders.GeminiDirect
            || connection.ModelId != GeminiBatchClient.SelectedModel
            || profile.ConnectionRevision != connection.CredentialRevision
            || candidates.Any(item =>
                item.AiRequest.AiTaskProfileId != profile.Id
                || item.AiRequest.TaskProfileRevision != profile.Revision
                || item.AiRequest.State != "prepared"))
        {
            throw new InvalidOperationException(
                "A staged request is no longer batch-compatible.");
        }
    }

    private static void ValidateBatchConfiguration(AiBatchEntity batch)
    {
        if (batch.Provider != AiProviders.GeminiDirect
            || batch.ModelId != GeminiBatchClient.SelectedModel
            || batch.AiConnection.Provider != AiProviders.GeminiDirect
            || batch.AiConnection.ModelId != GeminiBatchClient.SelectedModel
            || batch.AiConnection.State != "active"
            || batch.AiConnection.LastCapabilityProbeState != "passed"
            || batch.AiConnection.LastBatchCapabilityProbeState != "passed"
            || batch.AiConnection
                    .LastBatchCapabilityProbeCredentialRevision
                != batch.AiConnection.CredentialRevision
            || batch.AiConnection.CredentialRevision
                != batch.ConnectionRevision
            || batch.AiTaskProfile.Revision != batch.TaskProfileRevision
            || batch.AiTaskProfile.ProcessingStrategy != "gemini_batch")
        {
            throw new InvalidOperationException(
                "The prepared batch configuration is stale.");
        }
    }

    private static AiConnectionSettings ToConnection(
        AiConnectionEntity connection) =>
        new(
            connection.Id,
            connection.Provider,
            GeminiBaseAddress,
            connection.ModelId,
            TimeSpan.FromSeconds(connection.TimeoutSeconds));

    private static void EnqueueBatchJob(
        OokiGraderDbContext db,
        string type,
        AiBatchEntity batch,
        string deduplicationKey,
        DateTimeOffset notBefore,
        string? correlationId)
    {
        if (db.BackgroundJobs.Local.Any(
                item => item.DeduplicationKey == deduplicationKey)
            || db.BackgroundJobs.Any(
                item => item.DeduplicationKey == deduplicationKey))
        {
            return;
        }

        var now = batch.UpdatedAt;
        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = UlidId.New(now),
            Type = type,
            SchemaVersion = JobSchemaVersion,
            DeduplicationKey = deduplicationKey,
            Priority = 0,
            PayloadJson = JsonSerializer.Serialize(new
            {
                batchId = batch.Id,
            }),
            State = "queued",
            AttemptCount = 0,
            MaxAttempts = type == ReconcileJobType ? 100 : 20,
            NextAttemptAt = notBefore,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static string PollDeduplicationKey(AiBatchEntity batch) =>
        $"ai-batch:{batch.Id}:poll:{batch.Revision + 1}";

    private static void ApplyRemoteMetadata(
        AiBatchEntity batch,
        AiBatchStatus status,
        DateTimeOffset now)
    {
        batch.RemoteCreatedAt = status.CreatedAt ?? batch.RemoteCreatedAt;
        batch.RemoteUpdatedAt = status.UpdatedAt ?? batch.RemoteUpdatedAt;
        batch.RemoteEndedAt = status.EndedAt ?? batch.RemoteEndedAt;
        batch.ProviderOutputFileName =
            status.OutputFileName ?? batch.ProviderOutputFileName;
        if (status.Stats is not null)
        {
            batch.SuccessfulRequestCount =
                status.Stats.SuccessfulRequestCount;
            batch.FailedRequestCount = status.Stats.FailedRequestCount;
            batch.PendingRequestCount = status.Stats.PendingRequestCount;
        }

        batch.LastPolledAt = now;
        batch.UpdatedAt = now;
    }

    private static void ScrubProviderPayload(
        AiBatchRequestEntity mapping)
    {
        mapping.ProviderRequestJson = null;
        mapping.ProviderRequestBytes = 0;
    }

    private static void ReleaseReservation(
        OokiGraderDbContext db,
        string aiRequestId,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations.Local.SingleOrDefault(
                item => item.AiRequestId == aiRequestId)
            ?? db.AiBudgetReservations.SingleOrDefault(
                item => item.AiRequestId == aiRequestId);
        if (reservation?.State == "reserved")
        {
            reservation.State = "released";
            reservation.ActualUsdMicros = 0;
            reservation.SettledAt = now;
        }
    }

    private static void FinalizeFailedReservation(
        OokiGraderDbContext db,
        string aiRequestId,
        DateTimeOffset now,
        bool possibleProviderCharge)
    {
        if (possibleProviderCharge)
        {
            SettleReservationConservatively(db, aiRequestId, now);
        }
        else
        {
            ReleaseReservation(db, aiRequestId, now);
        }
    }

    private static void SettleReservationConservatively(
        OokiGraderDbContext db,
        string aiRequestId,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations.Local.SingleOrDefault(
                item => item.AiRequestId == aiRequestId)
            ?? db.AiBudgetReservations.SingleOrDefault(
                item => item.AiRequestId == aiRequestId);
        if (reservation?.State == "reserved")
        {
            reservation.State = "settled";
            reservation.ActualUsdMicros =
                reservation.ReservedUsdMicros;
            reservation.SettledAt = now;
        }
    }

    private static void SettleReservation(
        OokiGraderDbContext db,
        string aiRequestId,
        long actualUsdMicros,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations.Local.SingleOrDefault(
                item => item.AiRequestId == aiRequestId)
            ?? db.AiBudgetReservations.SingleOrDefault(
                item => item.AiRequestId == aiRequestId);
        if (reservation?.State == "reserved")
        {
            reservation.State = "settled";
            reservation.ActualUsdMicros = actualUsdMicros;
            reservation.SettledAt = now;
        }
    }

    private static async Task<(DateOnly Day, string Month)>
        GetUsageWindowAsync(
            OokiGraderDbContext db,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var timeZoneId = await db.SiteSettings
            .AsNoTracking()
            .Where(item => item.Id == "site")
            .Select(item => item.TimeZone)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrWhiteSpace(timeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            timeZone = TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            timeZone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        return (
            DateOnly.FromDateTime(local.DateTime),
            local.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool WouldExceedHardLimit(
        long committed,
        long reservation,
        long hardLimit) =>
        hardLimit >= 0
        && (BigInteger)committed + reservation > hardLimit;

    private static TimeSpan RequestRetryDelay(int attemptNumber) =>
        attemptNumber switch
        {
            <= 2 => TimeSpan.FromSeconds(30),
            3 => TimeSpan.FromMinutes(2),
            4 => TimeSpan.FromMinutes(10),
            _ => TimeSpan.FromMinutes(30),
        };

    private static long CalculateBatchCost(
        PricingSnapshotEntity pricing,
        AiUsage usage)
    {
        var standardNumerator =
            (BigInteger)(usage.PromptTokens ?? 0)
            * pricing.InputUsdMicrosPerMillionTokens
            + (BigInteger)(usage.OutputTokens ?? 0)
            * pricing.OutputUsdMicrosPerMillionTokens
            + (BigInteger)(usage.ThinkingTokens ?? 0)
            * pricing.ThinkingUsdMicrosPerMillionTokens;
        if (standardNumerator <= 0)
        {
            return 0;
        }

        // Google documents the Developer API Batch price as 50% of standard.
        var result = (standardNumerator + 1_999_999) / 2_000_000;
        if (result > long.MaxValue)
        {
            throw new OverflowException("ai_batch_cost_overflow");
        }

        return (long)result;
    }

    private static long ConvertUsdToJpy(
        long usdMicros,
        long usdToJpyMicros)
    {
        var numerator = (BigInteger)usdMicros * usdToJpyMicros;
        if (numerator <= 0)
        {
            return 0;
        }

        var result = (numerator + 999_999) / 1_000_000;
        if (result > long.MaxValue)
        {
            throw new OverflowException("ai_batch_cost_overflow");
        }

        return (long)result;
    }

    internal TimeSpan RemotePollDelay(
        TimeSpan remoteAge,
        string batchId,
        long pollEpoch)
    {
        var baseline = remoteAge < TimeSpan.FromMinutes(5)
            ? _options.InitialRemotePollInterval
            : remoteAge < TimeSpan.FromHours(1)
                ? _options.HourRemotePollInterval
                : _options.LongRemotePollInterval;
        return AddDeterministicJitter(baseline, batchId, pollEpoch);
    }

    internal static TimeSpan AddDeterministicJitter(
        TimeSpan baseline,
        string batchId,
        long pollEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            baseline,
            TimeSpan.Zero);

        var seed = Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{batchId}:{pollEpoch}"));
        var digest = SHA256.HashData(seed);
        var sample = (digest[0] << 8) | digest[1];
        var basisPoints = 9_000 + (sample % 2_001);
        var jitteredTicks =
            (BigInteger)baseline.Ticks * basisPoints / 10_000;
        return TimeSpan.FromTicks((long)jitteredTicks);
    }

    private static bool IsWithinReconciliationWindow(
        ReconcileClaim claim,
        AiBatchStatus batch)
    {
        if (batch.CreatedAt is null)
        {
            return true;
        }

        return batch.CreatedAt >= claim.CreateAttemptAt.AddMinutes(-5)
            && batch.CreatedAt <= claim.DeadlineAt.AddMinutes(5);
    }

    private static bool IsTerminal(string state) =>
        state is "succeeded" or "failed" or "cancelled" or "expired";

    private static bool IsSafetyCode(string errorCode) =>
        errorCode.Contains("safety", StringComparison.Ordinal)
        || errorCode.Contains("blocked", StringComparison.Ordinal);

    private static bool IsInvalidOutputCode(string errorCode) =>
        errorCode is "gemini_candidate_missing"
            or "gemini_finish_reason_invalid"
            or "gemini_content_missing"
            or "gemini_structured_output_missing"
            or "gemini_json_invalid";

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
    }

    private static void RetryJob(
        BackgroundJobEntity job,
        string errorCode,
        DateTimeOffset retryAt)
    {
        if (job.AttemptCount >= job.MaxAttempts)
        {
            FailJob(job, retryAt, errorCode);
            return;
        }

        job.State = "retry_waiting";
        job.NextAttemptAt = retryAt;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = BoundedErrorCode(errorCode);
    }

    private static void FailJob(
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        job.State = "failed";
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.ErrorCode = BoundedErrorCode(errorCode);
    }

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, PayloadOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string BoundedErrorCode(string value) =>
        value.Length <= 200 ? value : value[..200];

    [LoggerMessage(
        EventId = 4501,
        Level = LogLevel.Error,
        Message = "Gemini batch job {JobId} ({JobType}) failed in worker with {ExceptionType}.")]
    private partial void LogWorkerFailure(
        string jobId,
        string jobType,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        string Type,
        int SchemaVersion,
        string PayloadJson,
        string? CorrelationId);

    private sealed record PreparePayload(
        string CompatibilityKey,
        bool SubmitNow = false);

    private sealed record BatchPayload(string BatchId);

    private sealed record SubmitClaim(
        string BatchId,
        string DisplayName,
        string ManifestHash,
        int RequestCount,
        string? ProviderInputFileName,
        byte[] JsonLines,
        string JsonLinesHash,
        AiConnectionSettings Connection,
        string SecretReference);

    private sealed record RemoteClaim(
        string BatchId,
        string ProviderBatchName,
        string? ProviderInputFileName,
        string SecretReference,
        AiConnectionSettings Connection);

    private sealed record ReconcileClaim(
        string BatchId,
        string DisplayName,
        DateTimeOffset CreateAttemptAt,
        DateTimeOffset DeadlineAt,
        string SecretReference,
        AiConnectionSettings Connection);
}
