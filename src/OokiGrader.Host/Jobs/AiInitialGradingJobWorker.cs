using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using DomainQuestionDefinition =
    OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.Host.Jobs;

public sealed record AiInitialGradingJobWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumMediaBytes { get; init; } = 17 * 1024 * 1024;
    public int EstimatedImageTokensPerTile { get; init; } = 2_048;
    public int QueueBatchSize { get; init; } = 25;

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || MaximumMediaBytes is < 1_024 or > 18 * 1024 * 1024
            || EstimatedImageTokensPerTile is < 256 or > 32_768
            || QueueBatchSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AiInitialGradingJobWorkerOptions),
                "One or more AI initial-grading worker options are invalid.");
        }
    }
}

/// <summary>
/// Applies the approved direct-Gemini initial-grading profile to complete
/// normalized pages. Provider I/O never occurs while the serialized SQLite
/// writer is held.
/// </summary>
public sealed partial class AiInitialGradingJobWorker : BackgroundService
{
    public const string JobType = "gemini_initial_grade";
    public const string ApplyJobType = "gemini_initial_grade_apply";
    public const string ModelId = AiProviderRuntime.GeminiModel;
    public const string PipelineVersion = "gemini-submission-analysis-page-chunks-v5";

    public const int JobSchemaVersion = 1;
    private const int MaximumStoredResponseCharacters = 1_000_000;
    private const int MaximumIdentityEvidenceCharacters = 16_000;
    private const int MaximumSerializedRequestBytes = 18 * 1024 * 1024;
    private const int MaximumChunkMediaBytes = 12 * 1024 * 1024;
    private const int MaximumMediaPartsPerChunk = 32;
    private const int MaximumUserInstructionCharacters = 100_000;
    private const int MaximumSystemInstructionCharacters = 20_000;
    private const int SerializedRequestEnvelopeReserveBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };
    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IContentStore _contentStore;
    private readonly IAiProviderClientResolver _providerResolver;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;
    private readonly IAiPromptBundleCatalog _promptCatalog;
    private readonly IAiSecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AiInitialGradingJobWorker> _logger;
    private readonly AiInitialGradingJobWorkerOptions _options;
    private readonly AiBatchRequestStager? _batchRequestStager;
    private readonly AiAdjudicationJobScheduler? _adjudicationJobScheduler;
    private readonly string _workerId = $"gemini-grade-{Guid.NewGuid():N}";

    public AiInitialGradingJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IAiProviderClient providerClient,
        IAiPromptBundleCatalog promptCatalog,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        IOptions<AiInitialGradingJobWorkerOptions> options,
        ILogger<AiInitialGradingJobWorker> logger,
        AiBatchRequestStager? batchRequestStager = null,
        AiAdjudicationJobScheduler? adjudicationJobScheduler = null,
        IAiProviderClientResolver? providerResolver = null,
        IAiProviderFeaturePolicy? providerFeaturePolicy = null)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _contentStore = contentStore;
        _providerResolver = providerResolver
            ?? new AiProviderClientResolver([providerClient]);
        _providerFeaturePolicy = providerFeaturePolicy
            ?? AiProviderFeaturePolicy.AllowAll;
        _promptCatalog = promptCatalog;
        _secretStore = secretStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
        _batchRequestStager = batchRequestStager;
        _adjudicationJobScheduler = adjudicationJobScheduler;
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

        PreparedClaim? claim = null;
        var dispatchCommitted = false;
        try
        {
            claim = await PrepareAsync(lease, cancellationToken)
                .ConfigureAwait(false);
            if (claim is null)
            {
                return true;
            }

            if (claim.StoredResponse is not null)
            {
                await ApplyStoredResponseAsync(
                        claim,
                        claim.StoredResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            if (!_providerFeaturePolicy.IsEnabled(
                    claim.Connection.Provider))
            {
                throw Blocked("ai_provider_feature_disabled");
            }

            var media = await LoadMediaAsync(claim, cancellationToken)
                .ConfigureAwait(false);
            var request = CreateProviderRequest(claim, media);
            if (claim.ProcessingStrategy == "gemini_batch")
            {
                if (_batchRequestStager is null)
                {
                    throw Blocked("ai_batch_stager_unavailable");
                }

                await _batchRequestStager.StageAsync(
                        new AiBatchStageRequest(
                            claim.RequestId,
                            ComputeBatchCompatibilityKey(claim),
                            request,
                            claim.Priority,
                            claim.CorrelationId),
                        cancellationToken)
                    .ConfigureAwait(false);
                await CompleteStagedBatchJobAsync(
                        claim,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            using var secret = await _secretStore
                .ReadAsync(
                    new AiSecretReference(claim.SecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
            await MarkDispatchingAsync(claim, cancellationToken)
                .ConfigureAwait(false);
            dispatchCommitted = true;

            AiProviderResponse response;
            try
            {
                response = await _providerResolver
                    .GetRequired(claim.Connection.Provider)
                    .GenerateAsync(
                        claim.Connection,
                        secret.Utf8Bytes,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AiProviderException exception)
            {
                await RecordProviderFailureAsync(
                        claim,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                await RecordAmbiguousDispatchAsync(
                        claim,
                        "ai_dispatch_outcome_unknown",
                        exception.GetType().Name,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            ValidatedAiGradingResponse validated;
            string responseJson;
            try
            {
                AiResponseMetadataValidator.Validate(
                    response,
                    claim.Connection.Provider,
                    claim.Connection.ModelId);
                responseJson = response.StructuredOutput.GetRawText();
                if (responseJson.Length > MaximumStoredResponseCharacters)
                {
                    throw new InvalidDataException("ai_response_too_large");
                }

                validated = ValidateCombinedResponse(
                    response.StructuredOutput,
                    claim.RequestKey,
                    claim.Questions.ToDictionary(
                        item => item.Id,
                        item => item.Definition,
                        StringComparer.Ordinal),
                    claim.ChunkIndex,
                    claim.Artifacts.Count);
            }
            catch (InvalidDataException exception)
            {
                var identity = AiGradingResponseValidator
                    .ValidateIdentityComponent(
                        response.StructuredOutput,
                        claim.RequestKey,
                        identityExpected: claim.ChunkIndex == 0);
                await RecordInvalidResponseAsync(
                        claim,
                        response,
                        BoundedErrorCode(exception.Message),
                        identity.IsApplicable
                            ? identity.Transcription
                            : null,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            await PersistSuccessAsync(
                    claim,
                    response,
                    responseJson,
                    validated,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JobHandlingException exception)
        {
            LogJobFailure(
                lease.Id,
                exception.ErrorCode,
                exception.GetType().Name);
            await RecordJobFailureAsync(
                    lease.Id,
                    claim?.RequestId,
                    exception.ErrorCode,
                    exception.Kind,
                    dispatchCommitted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            const string errorCode = "ai_initial_grading_worker_error";
            LogJobFailure(lease.Id, errorCode, exception.GetType().Name);
            await RecordJobFailureAsync(
                    lease.Id,
                    claim?.RequestId,
                    errorCode,
                    FailureDisposition.Transient,
                    dispatchCommitted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await EnsureQueuedJobsAsync(stoppingToken).ConfigureAwait(false);
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static string RootJobDeduplicationKey(
        string submissionId,
        string manifestHash,
        string profileId,
        long profileRevision,
        string promptContentHash) =>
        $"submission:{submissionId}:gemini-analyze:{manifestHash}:" +
        $"{profileId}:{profileRevision}:{promptContentHash}";

    private Task EnsureQueuedJobsAsync(CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var bundle = _promptCatalog.GetRequired(AiTaskTypes.InitialGrading);
            var profile = await db.AiTaskProfiles
                .AsNoTracking()
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.InitialGrading
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null
                || !_providerFeaturePolicy.IsEnabled(
                    profile.AiConnection.Provider)
                || !AiTaskProfileRuntimePolicy.IsReadyApprovalState(
                    profile.ApprovalState)
                || profile.PromptVersion != bundle.PromptVersion
                || profile.SchemaVersion != bundle.SchemaVersion
                || profile.PromptContentHash != bundle.ContentHash
                || profile.ConnectionRevision
                    != profile.AiConnection.CredentialRevision
                || profile.AiConnection.State != "active"
                || profile.AiConnection.LastCapabilityProbeState != "passed")
            {
                return;
            }

            var eligible = await db.Submissions
                .AsNoTracking()
                .Where(item =>
                    item.VoidedAt == null
                    && item.CurrentGradingRunId == null
                    && item.ScanPayloadState == "scan_available"
                    && item.PreprocessingCompletedAt != null
                    && item.PreprocessingManifestHash != null
                    && item.Pages.Any()
                    && !item.GradingRuns.Any(run =>
                        run.PipelineVersion == PipelineVersion
                        && run.State == "awaiting_identity")
                    && (item.State == "needs_name_review"
                        || item.State == "grading"
                        || item.State == "awaiting_grading"))
                .OrderBy(item => item.UploadCompletedAt)
                .ThenBy(item => item.Id)
                .Take(_options.QueueBatchSize)
                .Select(item => new
                {
                    item.Id,
                    ManifestHash = item.PreprocessingManifestHash!,
                    item.TestSession.TemplateVersionId,
                    item.TestSession.Priority,
                })
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            if (eligible.Length == 0)
            {
                return;
            }

            var keys = eligible.Select(item => RootJobDeduplicationKey(
                    item.Id,
                    item.ManifestHash,
                    profile.Id,
                    profile.Revision,
                    bundle.ContentHash))
                .ToArray();
            var existingKeys = await db.BackgroundJobs
                .AsNoTracking()
                .Where(item => keys.Contains(item.DeduplicationKey))
                .Select(item => item.DeduplicationKey)
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            var existing = existingKeys.ToHashSet(StringComparer.Ordinal);
            var now = _timeProvider.GetUtcNow();
            var queuedSubmissionIds = new List<string>(eligible.Length);
            foreach (var item in eligible)
            {
                var key = RootJobDeduplicationKey(
                    item.Id,
                    item.ManifestHash,
                    profile.Id,
                    profile.Revision,
                    bundle.ContentHash);
                if (existing.Contains(key))
                {
                    continue;
                }

                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = UlidId.New(now),
                    Type = JobType,
                    SchemaVersion = JobSchemaVersion,
                    DeduplicationKey = key,
                    Priority = item.Priority == "expedite" ? 100 : 0,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        submissionId = item.Id,
                        templateVersionId = item.TemplateVersionId,
                        manifestHash = item.ManifestHash,
                    }),
                    State = "queued",
                    MaxAttempts = 8,
                    NextAttemptAt = now,
                    CorrelationId = $"analysis:{item.Id}",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                queuedSubmissionIds.Add(item.Id);
            }

            if (queuedSubmissionIds.Count > 0)
            {
                var nameJobPrefixes = queuedSubmissionIds
                    .Select(id => $"submission:{id}:name:")
                    .ToArray();
                var legacyNameJobs = await db.BackgroundJobs
                    .Where(job => job.Type
                            == AiNameTranscriptionJobWorker.JobType
                        && (job.State == "queued"
                            || job.State == "retry_waiting"))
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                foreach (var nameJob in legacyNameJobs.Where(job =>
                             nameJobPrefixes.Any(prefix =>
                                 job.DeduplicationKey.StartsWith(
                                     prefix,
                                     StringComparison.Ordinal))))
                {
                    nameJob.State = "cancelled";
                    nameJob.CompletedAt = now;
                    nameJob.ErrorCode = "superseded_by_submission_analysis";
                    nameJob.SafeErrorDetail = null;
                }
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
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
                    (item.Type == JobType || item.Type == ApplyJobType)
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
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 500);
            job.ErrorCode = null;
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.Type,
                job.SchemaVersion,
                job.PayloadJson,
                job.Priority,
                job.CorrelationId);
        }, cancellationToken);
    }

    private Task<PreparedClaim?> PrepareAsync(
        JobLease lease,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, lease.Id, token)
                .ConfigureAwait(false);
            if (lease.SchemaVersion != JobSchemaVersion
                || job.SchemaVersion != JobSchemaVersion)
            {
                throw Permanent("ai_grading_job_schema_unsupported");
            }

            var payload = DeserializePayload(lease.PayloadJson);
            if (string.IsNullOrWhiteSpace(payload.SubmissionId)
                || string.IsNullOrWhiteSpace(payload.TemplateVersionId)
                || !IsSha256(payload.ManifestHash)
                || (lease.Type == ApplyJobType
                    && string.IsNullOrWhiteSpace(payload.AiRequestId))
                || (lease.Type != JobType && lease.Type != ApplyJobType))
            {
                throw Permanent("ai_grading_payload_invalid");
            }

            var bundle = _promptCatalog.GetRequired(AiTaskTypes.InitialGrading);
            var profile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.InitialGrading
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null)
            {
                throw Blocked("ai_initial_profile_unavailable");
            }

            ValidateProfile(profile, bundle);
            var submission = await db.Submissions
                .Include(item => item.TestSession)
                    .ThenInclude(session => session.TemplateVersion)
                        .ThenInclude(version => version.Sources)
                .Include(item => item.TestSession)
                    .ThenInclude(session => session.TemplateVersion)
                        .ThenInclude(version => version.Questions)
                            .ThenInclude(question => question.AcceptedAnswers)
                .Include(item => item.GradingRuns)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_grading_submission_missing");
            var version = submission.TestSession.TemplateVersion;
            var questions = version.Questions
                .OrderBy(item => item.OrderIndex)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            ValidateSubmission(
                submission,
                version,
                questions,
                payload);

            var pages = await db.SubmissionPages
                .AsNoTracking()
                .Include(item => item.NormalizedFileReference)
                    .ThenInclude(reference => reference.FileObject)
                .Where(item => item.SubmissionId == submission.Id)
                .OrderBy(item => item.PageNumber)
                .ThenBy(item => item.Id)
                .ToListAsync(token)
                .ConfigureAwait(false);
            ValidatePages(submission, pages);

            var questionSnapshots = questions
                .Select(question => new QuestionSnapshot(
                    question.Id,
                    question.OrderIndex,
                    question.DisplayLabel,
                    question.QuestionText,
                    question.QuestionType,
                    question.GradingMode,
                    question.MaxPointsMilli,
                    question.PointIncrementMilli,
                    question.AllowNonKanji,
                    question.RequiresCompleteAnswer,
                    question.AnswerOrderInsensitive,
                    question.RubricText,
                    question.AcceptedAnswers
                        .OrderBy(answer => answer.Id, StringComparer.Ordinal)
                        .Select(answer => answer.AnswerText)
                        .ToArray(),
                    QuestionEntityDomainMapper.Map(question, version)))
                .ToArray();
            var artifactSnapshots = pages
                .Select(page => ToArtifactSnapshot(
                    page,
                    submission.PreprocessingManifestHash!))
                .ToArray();
            var inputManifestHash = ComputeInputManifestHash(
                submission,
                version,
                profile,
                bundle,
                questions,
                artifactSnapshots);
            var chunks = CreateArtifactChunks(
                inputManifestHash,
                artifactSnapshots,
                questionSnapshots,
                bundle);
            var existingRun = submission.GradingRuns.SingleOrDefault(
                run => run.PipelineVersion == PipelineVersion
                    && run.CanonicalInputManifestHash == inputManifestHash);
            if (existingRun is not null)
            {
                if (submission.CurrentGradingRunId is not null
                    && submission.CurrentGradingRunId != existingRun.Id)
                {
                    throw Permanent("ai_grading_run_conflict");
                }

                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (submission.CurrentGradingRunId is not null
                || submission.State == "needs_grade_review")
            {
                throw Permanent("ai_grading_run_conflict");
            }

            var chunkHashes = chunks
                .Select(item => item.InputManifestHash)
                .ToArray();
            var requestRows = await db.AiRequests
                .Include(item => item.Usage)
                .Include(item => item.BatchRequest)
                .Where(item => item.EntityType == "submission"
                        && item.EntityId == submission.Id
                        && item.Purpose == AiTaskTypes.InitialGrading
                        && item.AiTaskProfileId == profile.Id
                        && chunkHashes.Contains(item.InputManifestHash)
                        && item.TaskProfileRevision == profile.Revision)
                .ToListAsync(token)
                .ConfigureAwait(false);
            var currentRequestByManifest = requestRows
                .GroupBy(item => item.InputManifestHash, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.AttemptNumber)
                        .ThenByDescending(item => item.CreatedAt)
                        .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                        .First(),
                    StringComparer.Ordinal);
            var questionDefinitions = questionSnapshots.ToDictionary(
                item => item.Id,
                item => item.Definition,
                StringComparer.Ordinal);
            var completedChunks = new List<CompletedChunk>(chunks.Length);
            foreach (var chunk in chunks)
            {
                if (currentRequestByManifest.TryGetValue(
                        chunk.InputManifestHash,
                        out var current)
                    && current.State == "succeeded")
                {
                    completedChunks.Add(CreateCompletedChunk(
                        current,
                        chunk,
                        questionDefinitions));
                }
            }

            ArtifactChunk? selectedChunk = null;
            AiRequestEntity? requestEntity = null;
            if (payload.AiRequestId is not null)
            {
                requestEntity = requestRows.SingleOrDefault(
                    item => item.Id == payload.AiRequestId)
                    ?? throw Permanent("ai_request_missing");
                selectedChunk = chunks.SingleOrDefault(
                    item => item.InputManifestHash
                        == requestEntity.InputManifestHash)
                    ?? throw Permanent("ai_request_identity_invalid");
                if (!currentRequestByManifest.TryGetValue(
                        selectedChunk.InputManifestHash,
                        out var current)
                    || current.Id != requestEntity.Id)
                {
                    throw Permanent("ai_request_superseded");
                }
            }
            else
            {
                selectedChunk = chunks.FirstOrDefault(chunk =>
                    !currentRequestByManifest.TryGetValue(
                        chunk.InputManifestHash,
                        out var current)
                    || current.State != "succeeded");
                if (selectedChunk is not null)
                {
                    currentRequestByManifest.TryGetValue(
                        selectedChunk.InputManifestHash,
                        out requestEntity);
                }
            }

            if (completedChunks.Count == chunks.Length)
            {
                await CreateGradingRunAsync(
                        db,
                        submission,
                        version.Id,
                        profile.AiConnection.Provider,
                        profile.AiConnection.ModelId,
                        bundle,
                        questionSnapshots,
                        artifactSnapshots,
                        inputManifestHash,
                        completedChunks,
                        lease.CorrelationId,
                        job,
                        now,
                        token)
                    .ConfigureAwait(false);
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (selectedChunk is null)
            {
                throw Permanent("ai_grading_chunk_state_invalid");
            }

            if (requestEntity?.State == "succeeded")
            {
                CompleteJob(job, now);
                await EnqueueContinuationAsync(
                        db,
                        lease,
                        payload,
                        inputManifestHash,
                        chunks,
                        completedChunks,
                        now,
                        token)
                    .ConfigureAwait(false);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (requestEntity is not null)
            {
                if (requestEntity.PossibleDuplicate
                    || requestEntity.State == "dispatching")
                {
                    MarkAmbiguousRecovery(
                        requestEntity,
                        submission,
                        job,
                        now);
                    SettleReservationConservatively(
                        db,
                        requestEntity.Id,
                        now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (requestEntity.State == "succeeded")
                {
                    throw Permanent("ai_request_result_missing");
                }

                if (requestEntity.State == "response_ready")
                {
                    if (profile.ProcessingStrategy != "gemini_batch"
                        || requestEntity.BatchRequest?.State
                            != "response_ready")
                    {
                        throw Permanent("ai_batch_response_state_invalid");
                    }

                    var storedResponse = CreateStoredResponse(requestEntity);
                    job.ProgressBasisPoints = Math.Max(
                        job.ProgressBasisPoints,
                        8_000);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return CreatePreparedClaim(
                        lease,
                        submission,
                        version,
                        profile,
                        requestEntity,
                        inputManifestHash,
                        bundle,
                        questionSnapshots,
                        selectedChunk,
                        chunks,
                        completedChunks,
                        pricing: null,
                        usdToJpyMicros: 150_000_000,
                        forceExpedite: payload.ForceExpedite,
                        storedResponse: storedResponse);
                }

                if (requestEntity.State is
                    "invalid_output" or "safety_blocked" or "failed" or "cancelled")
                {
                    BlockJob(job, now, requestEntity.ErrorCode ?? "ai_request_terminal");
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
            }

            if (submission.State == "needs_attention")
            {
                throw Permanent("ai_grading_submission_state_invalid");
            }

            requestEntity ??= CreateRequest(
                now,
                submission,
                profile,
                selectedChunk.InputManifestHash);
            if (requestEntity.Id.Length == 0)
            {
                throw Permanent("ai_request_identity_invalid");
            }

            var pricing = await db.PricingSnapshots
                .AsNoTracking()
                .Where(item =>
                    item.Provider == profile.AiConnection.Provider
                    && item.ModelId == profile.ModelId
                    && item.EffectiveAt <= now)
                .OrderByDescending(item => item.EffectiveAt)
                .ThenByDescending(item => item.CapturedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            var budget = await db.AiBudgetPolicies
                .SingleOrDefaultAsync(item => item.Id == "default", token)
                .ConfigureAwait(false);
            var instruction = CreateUserInstruction(
                requestEntity.RequestKey,
                questionSnapshots,
                selectedChunk.Artifacts,
                selectedChunk.Index,
                chunks.Length);
            var reservedUsdMicros = pricing is null
                ? 0
                : EstimateMaximumCost(
                    pricing,
                    profile.MaxOutputTokens,
                    instruction,
                    selectedChunk.Artifacts);
            var usageWindow = await GetUsageWindowAsync(db, now, token)
                .ConfigureAwait(false);

            if (budget?.Active == true)
            {
                if (pricing is null)
                {
                    MarkBudgetBlocked(
                        requestEntity,
                        submission,
                        job,
                        now,
                        "ai_pricing_snapshot_missing");
                    AddIfDetached(db, requestEntity);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                var spent = await GetCommittedSpendAsync(
                        db,
                        usageWindow,
                        requestEntity.Id,
                        token)
                    .ConfigureAwait(false);
                if (WouldExceedHardLimit(
                        spent.DailyUsdMicros,
                        reservedUsdMicros,
                        budget.DailyHardUsdMicros)
                    || WouldExceedHardLimit(
                        spent.MonthlyUsdMicros,
                        reservedUsdMicros,
                        budget.MonthlyHardUsdMicros))
                {
                    MarkBudgetBlocked(
                        requestEntity,
                        submission,
                        job,
                        now,
                        "ai_budget_hard_limit");
                    AddIfDetached(db, requestEntity);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
            }

            if (requestEntity.State == "budget_blocked")
            {
                requestEntity.State = "prepared";
                requestEntity.ErrorCode = null;
                requestEntity.SafeErrorDetail = null;
                requestEntity.UpdatedAt = now;
            }

            AddIfDetached(db, requestEntity);
            var reservation = await db.AiBudgetReservations
                .SingleOrDefaultAsync(
                    item => item.AiRequestId == requestEntity.Id,
                    token)
                .ConfigureAwait(false);
            if (reservation is null)
            {
                reservation = new AiBudgetReservationEntity
                {
                    Id = UlidId.New(now),
                    AiRequestId = requestEntity.Id,
                    UsageDay = usageWindow.Day,
                    UsageMonth = usageWindow.Month,
                    ReservedUsdMicros = reservedUsdMicros,
                    ActualUsdMicros = 0,
                    State = "reserved",
                    CreatedAt = now,
                };
                db.AiBudgetReservations.Add(reservation);
            }
            else if (reservation.State == "released")
            {
                reservation.UsageDay = usageWindow.Day;
                reservation.UsageMonth = usageWindow.Month;
                reservation.ReservedUsdMicros = reservedUsdMicros;
                reservation.ActualUsdMicros = 0;
                reservation.State = "reserved";
                reservation.SettledAt = null;
            }
            else if (reservation.State == "reserved")
            {
                reservation.UsageDay = usageWindow.Day;
                reservation.UsageMonth = usageWindow.Month;
                reservation.ReservedUsdMicros = reservedUsdMicros;
            }
            else
            {
                throw Permanent("ai_budget_reservation_state_invalid");
            }

            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 2_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);

            return CreatePreparedClaim(
                lease,
                submission,
                version,
                profile,
                requestEntity,
                inputManifestHash,
                bundle,
                questionSnapshots,
                selectedChunk,
                chunks,
                completedChunks,
                pricing is null ? null : ToPricingSnapshot(pricing),
                budget?.UsdToJpyMicros ?? 150_000_000,
                payload.ForceExpedite,
                storedResponse: null);
        }, cancellationToken);
    }

    private static PreparedClaim CreatePreparedClaim(
        JobLease lease,
        SubmissionEntity submission,
        TemplateVersionEntity version,
        AiTaskProfileEntity profile,
        AiRequestEntity request,
        string inputManifestHash,
        AiPromptBundle bundle,
        IReadOnlyList<QuestionSnapshot> questions,
        ArtifactChunk chunk,
        IReadOnlyList<ArtifactChunk> chunks,
        IReadOnlyList<CompletedChunk> completedChunks,
        PricingSnapshot? pricing,
        long usdToJpyMicros,
        bool forceExpedite,
        StoredAiResponse? storedResponse)
    {
        return new PreparedClaim(
            lease.Id,
            lease.CorrelationId,
            lease.Priority,
            submission.Id,
            submission.Revision,
            version.Id,
            profile.Id,
            profile.Revision,
            profile.ConnectionRevision,
            forceExpedite
                || submission.TestSession.Priority == "expedite"
                ? "expedite_standard"
                : profile.ProcessingStrategy,
            request.Id,
            request.RequestKey,
            inputManifestHash,
            profile.MaxOutputTokens,
            ToMediaResolution(profile.MediaResolution),
            profile.AiConnection.SecretReference,
            new AiConnectionSettings(
                profile.AiConnection.Id,
                profile.AiConnection.Provider,
                AiProviderCatalog.GetBaseAddress(profile.AiConnection.Provider),
                profile.AiConnection.ModelId,
                TimeSpan.FromSeconds(profile.AiConnection.TimeoutSeconds)),
            bundle,
            questions,
            chunk.Artifacts,
            chunk.Index,
            chunks,
            completedChunks,
            pricing,
            usdToJpyMicros,
            forceExpedite,
            storedResponse);
    }

    private static StoredAiResponse CreateStoredResponse(
        AiRequestEntity request)
    {
        var mapping = request.BatchRequest
            ?? throw Permanent("ai_batch_response_mapping_missing");
        var usage = request.Usage
            ?? throw Permanent("ai_batch_response_usage_missing");
        var json = request.ValidatedResponseJson;
        if (string.IsNullOrWhiteSpace(json)
            || json.Length > MaximumStoredResponseCharacters
            || mapping.ResponseJson != json)
        {
            throw Permanent("ai_batch_response_payload_invalid");
        }

        var hash = Sha256(json);
        if (!IsSha256(request.AcceptedResponseHash)
            || request.AcceptedResponseHash != hash
            || mapping.ResponseHash != hash)
        {
            throw Permanent("ai_batch_response_hash_mismatch");
        }

        if (request.ProviderResponseId != mapping.ProviderResponseId
            || (usage.ProviderRequestId is not null
                && usage.ProviderRequestId != request.ProviderResponseId)
            || (usage.ActualModel is not null
                && request.ActualModel is not null
                && usage.ActualModel != request.ActualModel))
        {
            throw Permanent("ai_batch_response_metadata_inconsistent");
        }

        JsonElement output;
        try
        {
            using var document = JsonDocument.Parse(json);
            output = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Permanent("ai_batch_response_json_invalid");
        }

        return new StoredAiResponse(
            new AiProviderResponse(
                usage.ActualProvider ?? string.Empty,
                usage.RequestedModel,
                request.ActualModel ?? usage.ActualModel,
                request.ProviderResponseId,
                request.FinishReason ?? string.Empty,
                output,
                new AiUsage(
                    usage.InputTokens,
                    usage.CachedTokens,
                    usage.OutputTokens,
                    usage.ThinkingTokens,
                    usage.TotalTokens),
                TimeSpan.Zero),
            usage.EstimatedUsdMicros);
    }

    private ArtifactChunk[] CreateArtifactChunks(
        string canonicalInputManifestHash,
        IReadOnlyList<ArtifactSnapshot> artifacts,
        IReadOnlyList<QuestionSnapshot> questions,
        AiPromptBundle bundle)
    {
        var groups = new List<IReadOnlyList<ArtifactSnapshot>>();
        var current = new List<ArtifactSnapshot>();
        var currentBytes = 0L;
        var maximumInstructionArtifacts = artifacts
            .OrderByDescending(item => item.Ordinal)
            .ThenByDescending(item => item.Id, StringComparer.Ordinal)
            .Take(MaximumMediaPartsPerChunk)
            .OrderBy(item => item.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var conservativeInstruction = CreateUserInstruction(
            $"grade_{new string('0', 26)}",
            questions,
            maximumInstructionArtifacts,
            chunkIndex: 199,
            chunkCount: 200);
        if (bundle.SystemInstruction.Length
                > MaximumSystemInstructionCharacters
            || conservativeInstruction.Length
                > MaximumUserInstructionCharacters)
        {
            throw Permanent("ai_grading_prompt_too_large");
        }

        var serializedOverhead = checked(
            Encoding.UTF8.GetByteCount(bundle.SystemInstruction)
            + Encoding.UTF8.GetByteCount(conservativeInstruction)
            + Encoding.UTF8.GetByteCount(
                bundle.ResponseJsonSchema.GetRawText())
            + SerializedRequestEnvelopeReserveBytes);
        var base64Budget = MaximumSerializedRequestBytes
            - serializedOverhead;
        if (base64Budget <= 0)
        {
            throw Permanent("ai_grading_prompt_too_large");
        }

        var rawBudget = checked(base64Budget / 4 * 3);
        var effectiveMediaLimit = Math.Min(
            Math.Min(_options.MaximumMediaBytes, MaximumChunkMediaBytes),
            rawBudget);
        foreach (var artifact in artifacts
                     .OrderBy(item => item.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            if (artifact.Bytes <= 0
                || artifact.Bytes > effectiveMediaLimit)
            {
                throw Permanent("ai_submission_page_too_large");
            }

            if (current.Count > 0
                && (current.Count >= MaximumMediaPartsPerChunk
                    || currentBytes + artifact.Bytes > effectiveMediaLimit))
            {
                groups.Add(current.ToArray());
                current = [];
                currentBytes = 0;
            }

            current.Add(artifact);
            currentBytes = checked(currentBytes + artifact.Bytes);
        }

        if (current.Count > 0)
        {
            groups.Add(current.ToArray());
        }

        if (groups.Count == 0)
        {
            throw Permanent("ai_submission_pages_missing");
        }

        return groups
            .Select((group, index) => new ArtifactChunk(
                index,
                ComputeChunkInputManifestHash(
                    canonicalInputManifestHash,
                    index,
                    groups.Count,
                    group),
                group))
            .ToArray();
    }

    private static string ComputeChunkInputManifestHash(
        string canonicalInputManifestHash,
        int chunkIndex,
        int chunkCount,
        IReadOnlyList<ArtifactSnapshot> artifacts)
    {
        var canonical = new StringBuilder();
        AppendManifest(canonical, "pipeline", PipelineVersion);
        AppendManifest(
            canonical,
            "canonical-input",
            canonicalInputManifestHash);
        AppendManifest(
            canonical,
            "chunk-index",
            chunkIndex.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(
            canonical,
            "chunk-count",
            chunkCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        foreach (var artifact in artifacts)
        {
            AppendManifest(canonical, "artifact-id", artifact.Id);
            AppendManifest(
                canonical,
                "artifact-page",
                artifact.Ordinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(canonical, "artifact-sha256", artifact.Sha256);
            AppendManifest(
                canonical,
                "artifact-bytes",
                artifact.Bytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
        }

        return Sha256(canonical.ToString());
    }

    private static CompletedChunk CreateCompletedChunk(
        AiRequestEntity request,
        ArtifactChunk chunk,
        IReadOnlyDictionary<string, DomainQuestionDefinition> questions)
    {
        var json = request.ValidatedResponseJson;
        var usage = request.Usage;
        if (request.State != "succeeded"
            || request.PossibleDuplicate
            || request.InputManifestHash != chunk.InputManifestHash
            || string.IsNullOrWhiteSpace(json)
            || json.Length > MaximumStoredResponseCharacters
            || !IsSha256(request.AcceptedResponseHash)
            || request.AcceptedResponseHash != Sha256(json)
            || usage is null
            || (usage.ProviderRequestId is not null
                && request.ProviderResponseId != usage.ProviderRequestId)
            || (usage.ActualModel is not null
                && request.ActualModel is not null
                && usage.ActualModel != request.ActualModel))
        {
            throw Permanent("ai_succeeded_chunk_invalid");
        }

        ValidatedAiGradingResponse validated;
        try
        {
            using var document = JsonDocument.Parse(json);
            validated = ValidateCombinedResponse(
                document.RootElement,
                request.RequestKey,
                questions,
                chunk.Index,
                chunk.Artifacts.Count);
        }
        catch (JsonException)
        {
            throw Permanent("ai_succeeded_chunk_json_invalid");
        }
        catch (InvalidDataException exception)
        {
            throw Permanent(BoundedErrorCode(exception.Message));
        }

        return new CompletedChunk(
            chunk.Index,
            request.Id,
            request.RequestKey,
            request.InputManifestHash,
            validated,
            request.ActualModel ?? usage.ActualModel,
            new UsageSnapshot(
                usage.InputTokens,
                usage.CachedTokens,
                usage.OutputTokens,
                usage.ThinkingTokens,
                usage.TotalTokens,
                usage.EstimatedUsdMicros),
            chunk.Artifacts);
    }

    private static ValidatedAiGradingResponse ValidateCombinedResponse(
        JsonElement response,
        string expectedRequestKey,
        IReadOnlyDictionary<string, DomainQuestionDefinition> questions,
        int chunkIndex,
        int mediaPartCount)
    {
        var identity = AiGradingResponseValidator.ValidateIdentityComponent(
            response,
            expectedRequestKey,
            identityExpected: chunkIndex == 0);
        var grading = AiGradingResponseValidator.Validate(
            response,
            expectedRequestKey,
            questions,
            mediaPartCount);
        return grading with
        {
            Identity = identity.Transcription,
            IdentityValidationError = identity.IsValid
                ? null
                : identity.ErrorCode,
            UnexpectedContent = grading.UnexpectedContent,
        };
    }

    private static UsageSnapshot ToUsageSnapshot(
        AiUsage usage,
        long estimatedUsdMicros) =>
        new(
            usage.PromptTokens,
            usage.CachedTokens,
            usage.OutputTokens,
            usage.ThinkingTokens,
            usage.TotalTokens,
            estimatedUsdMicros);

    private async Task<IReadOnlyList<AiMediaPart>> LoadMediaAsync(
        PreparedClaim claim,
        CancellationToken cancellationToken)
    {
        var media = new List<AiMediaPart>(claim.Artifacts.Count);
        var totalBytes = 0L;
        foreach (var artifact in claim.Artifacts)
        {
            totalBytes = checked(totalBytes + artifact.Bytes);
            if (artifact.Bytes <= 0
                || totalBytes > _options.MaximumMediaBytes)
            {
                throw Permanent("ai_submission_pages_too_large");
            }

            await using var source = await _contentStore
                .OpenReadAsync(artifact.Locator, cancellationToken)
                .ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(
                    source,
                    artifact.Bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash),
                    Encoding.ASCII.GetBytes(artifact.Sha256)))
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw Permanent("ai_submission_page_hash_mismatch");
            }

            media.Add(new AiMediaPart(
                artifact.MimeType,
                bytes,
                artifact.Sha256));
        }

        return media;
    }

    private static AiProviderRequest CreateProviderRequest(
        PreparedClaim claim,
        IReadOnlyList<AiMediaPart> media)
    {
        if (media.Count != claim.Artifacts.Count)
        {
            throw Permanent("ai_submission_page_count_mismatch");
        }

        return new AiProviderRequest(
            claim.RequestKey,
            AiTaskTypes.InitialGrading,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            claim.Bundle.SystemInstruction,
            CreateUserInstruction(
                claim.RequestKey,
                claim.Questions,
                claim.Artifacts,
                claim.ChunkIndex,
                claim.Chunks.Count),
            claim.Bundle.ResponseJsonSchema,
            media,
            claim.MaxOutputTokens,
            claim.MediaResolution);
    }

    private async Task ApplyStoredResponseAsync(
        PreparedClaim claim,
        StoredAiResponse stored,
        CancellationToken cancellationToken)
    {
        ValidatedAiGradingResponse validated;
        string responseJson;
        try
        {
            AiResponseMetadataValidator.Validate(stored.Response, ModelId);
            responseJson = stored.Response.StructuredOutput.GetRawText();
            if (responseJson.Length > MaximumStoredResponseCharacters)
            {
                throw new InvalidDataException("ai_response_too_large");
            }

            validated = ValidateCombinedResponse(
                stored.Response.StructuredOutput,
                claim.RequestKey,
                claim.Questions.ToDictionary(
                    item => item.Id,
                    item => item.Definition,
                    StringComparer.Ordinal),
                claim.ChunkIndex,
                claim.Artifacts.Count);
        }
        catch (InvalidDataException exception)
        {
            var identity = AiGradingResponseValidator.ValidateIdentityComponent(
                stored.Response.StructuredOutput,
                claim.RequestKey,
                identityExpected: claim.ChunkIndex == 0);
            await RecordInvalidStoredResponseAsync(
                    claim,
                    BoundedErrorCode(exception.Message),
                    identity.IsApplicable
                        ? identity.Transcription
                        : null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await PersistSuccessAsync(
                claim,
                stored.Response,
                responseJson,
                validated,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task CompleteStagedBatchJobAsync(
        PreparedClaim claim,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .Include(item => item.BatchRequest)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.RequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            if (request.State != "prepared"
                || request.BatchRequest?.State != "ready")
            {
                throw Permanent("ai_batch_staging_state_invalid");
            }

            CompleteJob(job, now);
            db.OutboxEvents.Add(new OutboxEventEntity
            {
                Id = UlidId.New(now),
                AggregateType = "aiRequest",
                AggregateId = request.Id,
                EventType = "ai.request.batch_staged",
                SchemaVersion = 1,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    aiRequestId = request.Id,
                    submissionId = claim.SubmissionId,
                }),
                CorrelationId = claim.CorrelationId,
                CausationId = job.Id,
                OccurredAt = now,
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static string ComputeBatchCompatibilityKey(
        PreparedClaim claim)
    {
        return Sha256(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            provider = AiProviders.GeminiDirect,
            modelId = ModelId,
            connectionId = claim.Connection.ConnectionId,
            connectionRevision = claim.ConnectionRevision,
            taskType = AiTaskTypes.InitialGrading,
            taskProfileId = claim.TaskProfileId,
            taskProfileRevision = claim.TaskProfileRevision,
            promptVersion = claim.Bundle.PromptVersion,
            schemaVersionId = claim.Bundle.SchemaVersion,
            promptContentHash = claim.Bundle.ContentHash,
            mediaResolution = claim.MediaResolution,
            safetyProfile = "default",
            site = "local",
            dataHandling = "private",
        }));
    }

    private Task MarkDispatchingAsync(
        PreparedClaim claim,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            if (request.PossibleDuplicate
                || request.State is not ("prepared" or "retry_waiting")
                || request.DispatchAttempt >= 8)
            {
                throw Permanent("ai_request_dispatch_state_invalid");
            }

            request.State = "dispatching";
            request.DispatchAttempt = checked(request.DispatchAttempt + 1);
            request.DispatchedAt = now;
            request.UpdatedAt = now;
            request.ErrorCode = null;
            request.SafeErrorDetail = null;
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 4_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task PersistSuccessAsync(
        PreparedClaim claim,
        AiProviderResponse response,
        string responseJson,
        ValidatedAiGradingResponse validated,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .Include(item => item.Usage)
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            var storedResponse = claim.StoredResponse;
            var expectedState = storedResponse is null
                ? "dispatching"
                : "response_ready";
            if (request.State != expectedState
                || request.PossibleDuplicate)
            {
                throw Permanent("ai_request_completion_state_invalid");
            }

            var submission = await db.Submissions
                .Include(item => item.GradingRuns)
                .Include(item => item.TestSession)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_grading_submission_missing");
            var abandoned = submission.VoidedAt is not null;
            if ((!abandoned
                    && (submission.CurrentGradingRunId is not null
                        || submission.State is not (
                            "needs_name_review" or "grading"
                            or "awaiting_grading")))
                || submission.Revision < claim.SubmissionRevision
                || submission.ScanPayloadState != "scan_available"
                || submission.PreprocessingManifestHash
                    != claim.Artifacts[0].InputManifestHash
                || submission.TestSession.TemplateVersionId
                    != claim.TemplateVersionId)
            {
                throw Permanent("ai_grading_submission_changed");
            }

            var existingRun = submission.GradingRuns.SingleOrDefault(
                run => run.PipelineVersion == PipelineVersion
                    && run.CanonicalInputManifestHash
                        == claim.CanonicalInputManifestHash);
            if (existingRun is not null)
            {
                throw Permanent("ai_grading_run_conflict");
            }

            request.State = "succeeded";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.AcceptedResponseHash = Sha256(responseJson);
            request.ValidatedResponseJson = responseJson;
            request.ErrorCode = null;
            request.SafeErrorDetail = null;
            request.CompletedAt ??= now;
            request.UpdatedAt = now;
            var actualCost = storedResponse is null
                ? AddUsageAndSettleReservation(
                    db,
                    claim,
                    response,
                    now)
                : request.Usage?.EstimatedUsdMicros
                    ?? throw Permanent("ai_batch_response_usage_missing");

            await PersistIdentityEvidenceAsync(
                    db,
                    claim,
                    validated.Identity,
                    now,
                    token)
                .ConfigureAwait(false);
            if (abandoned)
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var currentChunk = new CompletedChunk(
                claim.ChunkIndex,
                request.Id,
                request.RequestKey,
                request.InputManifestHash,
                validated,
                response.ActualModel,
                ToUsageSnapshot(response.Usage, actualCost),
                claim.Artifacts);
            var completedChunks = claim.CompletedChunks
                .Where(item => item.Index != currentChunk.Index)
                .Append(currentChunk)
                .OrderBy(item => item.Index)
                .ToArray();
            if (completedChunks.Length == claim.Chunks.Count)
            {
                await CreateGradingRunAsync(
                        db,
                        submission,
                        claim.TemplateVersionId,
                        claim.Connection.Provider,
                        claim.Connection.ModelId,
                        claim.Bundle,
                        claim.Questions,
                        claim.Chunks.SelectMany(item => item.Artifacts).ToArray(),
                        claim.CanonicalInputManifestHash,
                        completedChunks,
                        claim.CorrelationId,
                        job,
                        now,
                        token)
                    .ConfigureAwait(false);
            }
            else
            {
                await EnqueueContinuationAsync(
                        db,
                        new JobLease(
                            job.Id,
                            job.Type,
                            job.SchemaVersion,
                            job.PayloadJson,
                            job.Priority,
                            job.CorrelationId),
                        new GradingPayload(
                            claim.SubmissionId,
                            claim.TemplateVersionId,
                            claim.Artifacts[0].InputManifestHash,
                            ForceExpedite: claim.ForceExpedite),
                        claim.CanonicalInputManifestHash,
                        claim.Chunks,
                        completedChunks,
                        now,
                        token)
                    .ConfigureAwait(false);
            }

            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task EnqueueContinuationAsync(
        OokiGraderDbContext db,
        JobLease lease,
        GradingPayload payload,
        string canonicalInputManifestHash,
        IReadOnlyList<ArtifactChunk> chunks,
        IReadOnlyCollection<CompletedChunk> completedChunks,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var completedIndexes = completedChunks
            .Select(item => item.Index)
            .ToHashSet();
        var next = chunks.FirstOrDefault(
            item => !completedIndexes.Contains(item.Index));
        if (next is null)
        {
            return;
        }

        var deduplicationKey =
            $"submission:{payload.SubmissionId}:gemini-analyze:" +
            $"{payload.ManifestHash}:{canonicalInputManifestHash}:" +
            $"chunk:{next.Index + 1}";
        if (db.BackgroundJobs.Local.Any(
                item => item.DeduplicationKey == deduplicationKey)
            || await db.BackgroundJobs.AsNoTracking().AnyAsync(
                    item => item.DeduplicationKey == deduplicationKey,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        db.BackgroundJobs.Add(new BackgroundJobEntity
        {
            Id = UlidId.New(now),
            Type = JobType,
            SchemaVersion = JobSchemaVersion,
            DeduplicationKey = deduplicationKey,
            Priority = lease.Priority,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId = payload.SubmissionId,
                templateVersionId = payload.TemplateVersionId,
                manifestHash = payload.ManifestHash,
                forceExpedite = payload.ForceExpedite,
            }),
            State = "queued",
            AttemptCount = 0,
            MaxAttempts = 8,
            NextAttemptAt = now,
            CorrelationId = lease.CorrelationId,
            CausationId = lease.Id,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private async Task CreateGradingRunAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        string templateVersionId,
        string provider,
        string requestedModel,
        AiPromptBundle bundle,
        IReadOnlyList<QuestionSnapshot> questions,
        IReadOnlyList<ArtifactSnapshot> artifacts,
        string canonicalInputManifestHash,
        IReadOnlyList<CompletedChunk> completedChunks,
        string? correlationId,
        BackgroundJobEntity job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (completedChunks.Count == 0
            || completedChunks.Select(item => item.Index).Distinct().Count()
                != completedChunks.Count
            || completedChunks.Any(item =>
                !IsSha256(item.InputManifestHash)))
        {
            throw Permanent("ai_grading_chunk_aggregate_invalid");
        }

        var observations = completedChunks
            .SelectMany(chunk => chunk.Response.Observations.Select(
                observation => new ChunkObservation(chunk, observation)))
            .GroupBy(item => item.Observation.QuestionId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Chunk.Index).ToArray(),
                StringComparer.Ordinal);
        var unique = observations
            .Where(item => item.Value.Length == 1)
            .ToDictionary(
                item => item.Key,
                item => item.Value[0],
                StringComparer.Ordinal);
        var conflicts = observations
            .Where(item => item.Value.Length > 1)
            .ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal);
        var unexpectedContent = completedChunks.Any(
            item => item.Response.UnexpectedContent);
        var actualModels = completedChunks
            .Select(item => item.ActualModel)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var totalCost = completedChunks.Aggregate(
            0L,
            static (total, item) =>
                checked(total + item.Usage.EstimatedUsdMicros));
        var run = new GradingRunEntity
        {
            Id = UlidId.New(now),
            SubmissionId = submission.Id,
            RunNumber = checked(
                submission.GradingRuns.Select(item => item.RunNumber)
                    .DefaultIfEmpty()
                    .Max() + 1),
            TemplateVersionId = templateVersionId,
            Reason = "gemini_submission_analysis",
            State = "needs_grade_review",
            Provider = provider,
            Model = actualModels.Length == 1
                ? actualModels[0]!
                : requestedModel,
            PromptVersion = bundle.PromptVersion,
            SchemaVersion = bundle.SchemaVersion,
            PipelineVersion = PipelineVersion,
            CanonicalInputManifestHash = canonicalInputManifestHash,
            EarnedPointsMilli = 0,
            PossiblePointsMilli = 0,
            ResultSourceRevision = 1,
            AiUsageAggregationJson = JsonSerializer.Serialize(new
            {
                aiRequestId = completedChunks[0].RequestId,
                aiRequestIds = completedChunks.Select(item => item.RequestId),
                chunkCount = completedChunks.Count,
                identityIncluded = completedChunks[0].Response.Identity is not null,
                identityValidationError =
                    completedChunks[0].Response.IdentityValidationError,
                chunks = completedChunks.Select(item => new
                {
                    chunkIndex = item.Index + 1,
                    requestId = item.RequestId,
                    inputManifestHash = item.InputManifestHash,
                }),
                promptTokens = SumNullable(
                    completedChunks.Select(item => item.Usage.PromptTokens)),
                cachedTokens = SumNullable(
                    completedChunks.Select(item => item.Usage.CachedTokens)),
                outputTokens = SumNullable(
                    completedChunks.Select(item => item.Usage.OutputTokens)),
                thinkingTokens = SumNullable(
                    completedChunks.Select(item => item.Usage.ThinkingTokens)),
                totalTokens = SumNullable(
                    completedChunks.Select(item => item.Usage.TotalTokens)),
                estimatedUsdMicros = totalCost,
            }),
            CreatedAt = now,
            FinishedAt = now,
        };
        db.GradingRuns.Add(run);

        var defaultPageReferenceId = artifacts
            .OrderBy(item => item.Ordinal)
            .Select(item => item.FileReferenceId)
            .FirstOrDefault();
        var results = new List<(QuestionResultEntity Result, long Points)>(
            questions.Count);
        foreach (var question in questions)
        {
            QuestionResultEntity result;
            long points;
            if (unique.TryGetValue(question.Id, out var evidence))
            {
                var observation = evidence.Observation;
                var reviewRequired = unexpectedContent
                    || observation.ProviderReviewRecommended;
                points = observation.ProposedPointsMilli;
                result = new QuestionResultEntity
                {
                    Id = UlidId.New(now),
                    GradingRunId = run.Id,
                    QuestionId = question.Id,
                    TranscribedAnswer =
                        observation.Observation.Transcription,
                    NormalizedAnswer = JapaneseTextNormalizer
                        .NormalizeForComparison(
                            observation.Observation.Transcription),
                    ProposedPointsMilli = points,
                    MaximumPointsMilli = question.MaximumPointsMilli,
                    Outcome = observation.ProposedOutcome,
                    Method = "ai_pilot",
                    ConfidenceBasisPoints =
                        observation.ProviderConfidenceBasisPoints,
                    KanjiCheck =
                        observation.Observation.ScriptObservationUncertain
                            ? "uncertain"
                            : "not_applicable",
                    ReasonCode = unexpectedContent
                        ? "ai_unexpected_content"
                        : observation.ProviderReasonCode
                            ?? "ai_pilot_proposal",
                    Explanation = observation.BoundedExplanation,
                    AnswerCropFileReferenceId = EvidencePageReferenceId(
                        evidence,
                        defaultPageReferenceId),
                    ReviewRequired = reviewRequired,
                    ReviewStatus = reviewRequired ? "pending" : "not_required",
                    ModelResponseItemHash = observation.CanonicalItemHash,
                    CreatedAt = now,
                };
            }
            else if (conflicts.TryGetValue(
                         question.Id,
                         out var conflictEvidence))
            {
                points = 0;
                result = new QuestionResultEntity
                {
                    Id = UlidId.New(now),
                    GradingRunId = run.Id,
                    QuestionId = question.Id,
                    ProposedPointsMilli = 0,
                    MaximumPointsMilli = question.MaximumPointsMilli,
                    Outcome = "review",
                    Method = "manual",
                    ConfidenceBasisPoints = 0,
                    KanjiCheck = "not_applicable",
                    ReasonCode = "ai_chunk_observation_conflict",
                    Explanation =
                        "Multiple page chunks returned an observation; " +
                        "teacher selection is required.",
                    AnswerCropFileReferenceId = EvidencePageReferenceId(
                        conflictEvidence[0],
                        defaultPageReferenceId),
                    ReviewRequired = true,
                    ReviewStatus = "pending",
                    ModelResponseItemHash = Sha256(string.Join(
                        '\n',
                        conflictEvidence.Select(item =>
                            $"{item.Chunk.RequestId}:" +
                            item.Observation.CanonicalItemHash))),
                    CreatedAt = now,
                };
            }
            else
            {
                points = 0;
                result = new QuestionResultEntity
                {
                    Id = UlidId.New(now),
                    GradingRunId = run.Id,
                    QuestionId = question.Id,
                    ProposedPointsMilli = 0,
                    MaximumPointsMilli = question.MaximumPointsMilli,
                    Outcome = "unreadable",
                    Method = "manual",
                    ConfidenceBasisPoints = 0,
                    KanjiCheck = "not_applicable",
                    ReasonCode = "ai_missing_question",
                    AnswerCropFileReferenceId = defaultPageReferenceId,
                    ReviewRequired = true,
                    ReviewStatus = "pending",
                    CreatedAt = now,
                };
            }

            db.QuestionResults.Add(result);
            results.Add((result, points));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (result, points) in results)
        {
            var revision = new ResultRevisionEntity
            {
                Id = UlidId.New(now),
                QuestionResultId = result.Id,
                RevisionNumber = 1,
                AwardedPointsMilli = points,
                Outcome = result.Outcome,
                AnswerTextCorrection = result.TranscribedAnswer,
                ReasonCode = result.ReasonCode,
                Source = "initial",
                CreatedAt = now,
            };
            db.ResultRevisions.Add(revision);
            result.CurrentRevisionId = revision.Id;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var aggregateResponse = new ValidatedAiGradingResponse(
            $"aggregate_{canonicalInputManifestHash}",
            unique.Values
                .OrderBy(item => item.Chunk.Index)
                .Select(item => item.Observation)
                .ToArray(),
            unexpectedContent || conflicts.Count > 0);
        var identityConfirmed = submission.AssignedStudentId is not null
            || IsExplicitlyUnidentified(submission);
        if (_adjudicationJobScheduler is not null)
        {
            await _adjudicationJobScheduler.EnqueueAmbiguousAsync(
                    db,
                    submission,
                    run,
                    results.Select(item => item.Result).ToArray(),
                    aggregateResponse,
                    questions
                        .Select(item => new AiAdjudicationArtifactCandidate(
                            item.Id,
                            ProviderDisclosureAllowed: true))
                        .ToArray(),
                    correlationId,
                    job.Id,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!identityConfirmed)
            {
                foreach (var adjudicationJob in db.BackgroundJobs.Local.Where(
                             item => item.Type
                                    == AiAdjudicationJobWorker.JobType
                                 && item.CausationId == job.Id
                                 && item.State == "queued"))
                {
                    adjudicationJob.State = "blocked";
                    adjudicationJob.ErrorCode = "awaiting_identity";
                }
            }
        }

        run.EarnedPointsMilli = results.Aggregate(
            0L,
            static (total, item) => checked(total + item.Points));
        run.PossiblePointsMilli = results.Aggregate(
            0L,
            static (total, item) =>
                checked(total + item.Result.MaximumPointsMilli));
        var blockingReview = results.Any(item => item.Result.ReviewRequired);
        var activatedState = blockingReview
            ? "needs_grade_review"
            : "ready_to_finalize";
        run.State = identityConfirmed ? activatedState : "awaiting_identity";
        if (identityConfirmed)
        {
            submission.CurrentGradingRunId = run.Id;
            submission.State = activatedState;
            run.ActivatedAt = now;
        }

        submission.UpdatedAt = now;
        AddAudit(
            db,
            now,
            correlationId,
            "grading.gemini_pilot_created",
            submission.Id,
            !identityConfirmed
                ? "awaiting_identity"
                : blockingReview
                    ? "teacher_review_required"
                    : "ready_to_finalize");
        if (identityConfirmed)
        {
            AddStatusOutbox(
                db,
                now,
                correlationId,
                submission.Id,
                submission.State);
        }
    }

    private static string? EvidencePageReferenceId(
        ChunkObservation evidence,
        string? fallback)
    {
        if (evidence.Observation.EvidenceMediaIndex is { } mediaIndex
            && mediaIndex >= 0
            && mediaIndex < evidence.Chunk.Artifacts.Count)
        {
            return evidence.Chunk.Artifacts[mediaIndex].FileReferenceId;
        }

        return evidence.Chunk.Artifacts
            .OrderBy(item => item.Ordinal)
            .Select(item => item.FileReferenceId)
            .FirstOrDefault() ?? fallback;
    }

    private static long? SumNullable(IEnumerable<long?> values)
    {
        var total = 0L;
        var found = false;
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            total = checked(total + value.Value);
            found = true;
        }

        return found ? total : null;
    }

    private Task RecordInvalidResponseAsync(
        PreparedClaim claim,
        AiProviderResponse response,
        string errorCode,
        ValidatedAiIdentityTranscription? identity,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            request.State = "invalid_output";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(db, claim, response, now);
            await PersistIdentityEvidenceAsync(
                    db,
                    claim,
                    identity,
                    now,
                    token)
                .ConfigureAwait(false);
            await MarkSubmissionNeedsAttentionAsync(
                    db,
                    claim.SubmissionId,
                    now,
                    token)
                .ConfigureAwait(false);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.ai_response_rejected",
                claim.SubmissionId,
                errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordInvalidStoredResponseAsync(
        PreparedClaim claim,
        string errorCode,
        ValidatedAiIdentityTranscription? identity,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .Include(item => item.BatchRequest)
                    .ThenInclude(item => item!.AiBatch)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.RequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            if (request.State != "response_ready"
                || request.BatchRequest is null)
            {
                throw Permanent("ai_batch_response_state_invalid");
            }

            request.State = "invalid_output";
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = null;
            request.CompletedAt ??= now;
            request.UpdatedAt = now;
            var mapping = request.BatchRequest;
            mapping.State = "failed";
            mapping.ErrorCode = errorCode;
            mapping.CompletedAt ??= now;
            mapping.UpdatedAt = now;
            if (mapping.AiBatch is { } batch)
            {
                batch.SuccessfulRequestCount = Math.Max(
                    0,
                    batch.SuccessfulRequestCount - 1);
                batch.FailedRequestCount = checked(
                    batch.FailedRequestCount + 1);
                batch.ErrorCode = "gemini_batch_partial_failure";
                batch.UpdatedAt = now;
            }

            await PersistIdentityEvidenceAsync(
                    db,
                    claim,
                    identity,
                    now,
                    token)
                .ConfigureAwait(false);

            await MarkSubmissionNeedsAttentionAsync(
                    db,
                    claim.SubmissionId,
                    now,
                    token)
                .ConfigureAwait(false);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.ai_batch_response_rejected",
                claim.SubmissionId,
                errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordProviderFailureAsync(
        PreparedClaim claim,
        AiProviderException exception,
        CancellationToken cancellationToken)
    {
        var ambiguous = AiProviderRuntime.IsAmbiguousDispatch(exception);
        if (ambiguous)
        {
            return RecordAmbiguousDispatchAsync(
                claim,
                "ai_dispatch_outcome_unknown",
                exception.SafeErrorCode,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            request.ErrorCode = exception.SafeErrorCode;
            request.SafeErrorDetail = null;
            request.UpdatedAt = now;

            if (AiProviderRuntime.ShouldRetry(exception))
            {
                request.State = "retry_waiting";
                job.State = "retry_waiting";
                job.NextAttemptAt = now.Add(
                    exception.RetryAfter
                    ?? AiRetrySchedule.Delay(job.AttemptCount, job.Id));
                job.ErrorCode = exception.SafeErrorCode;
                job.SafeErrorDetail = null;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
            }
            else
            {
                request.State = exception.Kind switch
                {
                    AiFailureKind.SafetyBlocked => "safety_blocked",
                    AiFailureKind.BudgetBlocked => "budget_blocked",
                    AiFailureKind.InvalidResponse => "invalid_output",
                    _ => "failed",
                };
                request.CompletedAt = now;
                if (exception.Kind is
                    AiFailureKind.SafetyBlocked or AiFailureKind.InvalidResponse)
                {
                    SettleReservationConservatively(
                        db,
                        claim.RequestId,
                        now);
                }
                else
                {
                    ReleaseReservation(db, claim.RequestId, now);
                }

                await MarkSubmissionNeedsAttentionAsync(
                        db,
                        claim.SubmissionId,
                        now,
                        token)
                    .ConfigureAwait(false);
                BlockJob(job, now, exception.SafeErrorCode);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordAmbiguousDispatchAsync(
        PreparedClaim claim,
        string errorCode,
        string safeDetail,
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_request_missing");
            request.State = "failed";
            request.PossibleDuplicate = true;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = BoundedSafeDetail(safeDetail);
            request.CompletedAt = now;
            request.UpdatedAt = now;
            SettleReservationConservatively(db, claim.RequestId, now);
            await MarkSubmissionNeedsAttentionAsync(
                    db,
                    claim.SubmissionId,
                    now,
                    token)
                .ConfigureAwait(false);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.ai_dispatch_ambiguous",
                claim.SubmissionId,
                errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordJobFailureAsync(
        string jobId,
        string? requestId,
        string errorCode,
        FailureDisposition disposition,
        bool dispatchCommitted,
        CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == jobId, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId)
            {
                return;
            }

            AiRequestEntity? request = null;
            if (requestId is not null)
            {
                request = await db.AiRequests
                    .SingleOrDefaultAsync(item => item.Id == requestId, token)
                    .ConfigureAwait(false);
            }

            if (dispatchCommitted)
            {
                if (request is not null)
                {
                    request.State = "failed";
                    request.PossibleDuplicate = true;
                    request.ErrorCode = "ai_dispatch_outcome_unknown";
                    request.SafeErrorDetail = BoundedSafeDetail(errorCode);
                    request.CompletedAt = now;
                    request.UpdatedAt = now;
                    SettleReservationConservatively(db, request.Id, now);
                    await MarkSubmissionNeedsAttentionAsync(
                            db,
                            request.EntityId,
                            now,
                            token)
                        .ConfigureAwait(false);
                }

                BlockJob(job, now, "ai_dispatch_outcome_unknown");
            }
            else if (disposition == FailureDisposition.Transient
                     && job.AttemptCount < job.MaxAttempts)
            {
                job.State = "retry_waiting";
                job.NextAttemptAt = now.Add(
                    AiRetrySchedule.Delay(job.AttemptCount, job.Id));
                job.ErrorCode = errorCode;
                job.SafeErrorDetail = null;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                if (request is not null && request.State == "prepared")
                {
                    request.State = "retry_waiting";
                    request.ErrorCode = errorCode;
                    request.UpdatedAt = now;
                }
            }
            else
            {
                if (request is not null
                    && request.State is "prepared" or "retry_waiting")
                {
                    request.State = "failed";
                    request.ErrorCode = errorCode;
                    request.SafeErrorDetail = null;
                    request.CompletedAt = now;
                    request.UpdatedAt = now;
                    ReleaseReservation(db, request.Id, now);
                }

                if (disposition == FailureDisposition.Blocked)
                {
                    BlockJob(job, now, errorCode);
                }
                else
                {
                    FailJob(job, now, errorCode);
                }
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static long AddUsageAndSettleReservation(
        OokiGraderDbContext db,
        PreparedClaim claim,
        AiProviderResponse response,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations.Local
            .SingleOrDefault(item => item.AiRequestId == claim.RequestId)
            ?? db.AiBudgetReservations
                .SingleOrDefault(item => item.AiRequestId == claim.RequestId);
        if (reservation is null)
        {
            throw Permanent("ai_budget_reservation_missing");
        }

        var actualUsdMicros = AiProviderRuntime.ResolveActualUsdMicros(
            response.Usage,
            reservation.ReservedUsdMicros,
            claim.Connection.Provider != AiProviders.GeminiDirect
                || claim.Pricing is null
                ? null
                : () => CalculateActualCost(
                    claim.Pricing,
                    response.Usage));
        var estimatedJpyMicros = ConvertUsdToJpy(
            actualUsdMicros,
            claim.UsdToJpyMicros);
        db.AiUsage.Add(new AiUsageEntity
        {
            Id = UlidId.New(now),
            AiRequestId = claim.RequestId,
            RequestedProvider = claim.Connection.Provider,
            RequestedModel = claim.Connection.ModelId,
            ActualProvider = response.RoutedProvider ?? response.Provider,
            ActualModel = response.ActualModel,
            InputTokens = response.Usage.PromptTokens,
            CachedTokens = response.Usage.CachedTokens,
            OutputTokens = response.Usage.OutputTokens,
            ThinkingTokens = response.Usage.ThinkingTokens,
            TotalTokens = response.Usage.TotalTokens,
            PricingSnapshotId = claim.Pricing?.Id,
            EstimatedUsdMicros = actualUsdMicros,
            EstimatedJpyMicros = estimatedJpyMicros,
            ProviderRequestId = response.ProviderResponseId,
            MeasuredAt = now,
        });
        reservation.ActualUsdMicros = actualUsdMicros;
        reservation.State = "settled";
        reservation.SettledAt = now;
        return actualUsdMicros;
    }

    private static void SettleReservationConservatively(
        OokiGraderDbContext db,
        string requestId,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations
            .SingleOrDefault(item => item.AiRequestId == requestId);
        if (reservation is null || reservation.State != "reserved")
        {
            return;
        }

        reservation.ActualUsdMicros = reservation.ReservedUsdMicros;
        reservation.State = "settled";
        reservation.SettledAt = now;
    }

    private static void ReleaseReservation(
        OokiGraderDbContext db,
        string requestId,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations
            .SingleOrDefault(item => item.AiRequestId == requestId);
        if (reservation is null || reservation.State != "reserved")
        {
            return;
        }

        reservation.ActualUsdMicros = 0;
        reservation.State = "released";
        reservation.SettledAt = now;
    }

    private static async Task MarkSubmissionNeedsAttentionAsync(
        OokiGraderDbContext db,
        string submissionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var submission = await db.Submissions
            .SingleOrDefaultAsync(item => item.Id == submissionId, cancellationToken)
            .ConfigureAwait(false);
        if (submission is not null
            && submission.State is "grading" or "awaiting_grading")
        {
            submission.State = "needs_attention";
            submission.UpdatedAt = now;
        }
    }

    private static async Task PersistIdentityEvidenceAsync(
        OokiGraderDbContext db,
        PreparedClaim claim,
        ValidatedAiIdentityTranscription? identity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (identity is null || claim.ChunkIndex != 0)
        {
            return;
        }

        var submission = await db.Submissions
            .SingleOrDefaultAsync(
                item => item.Id == claim.SubmissionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (submission is null
            || submission.VoidedAt is not null
            || IsExplicitlyUnidentified(submission))
        {
            return;
        }

        var evidence = await CombinedIdentityEvidenceBuilder.BuildAsync(
                db,
                submission.TestSessionId,
                identity,
                claim.RequestId,
                claim.Artifacts[0].InputManifestHash,
                PipelineVersion,
                cancellationToken)
            .ConfigureAwait(false);
        var evidenceJson = JsonSerializer.Serialize(evidence);
        if (evidenceJson.Length > MaximumIdentityEvidenceCharacters)
        {
            throw Permanent("ai_identity_evidence_too_large");
        }

        submission.AssignmentEvidenceJson = evidenceJson;
        if (submission.AssignedStudentId is null)
        {
            submission.AssignmentMethod = "none";
            submission.AssignmentConfidenceBasisPoints = null;
            submission.AssignmentPolicyVersion = evidence.MatchingPolicyVersion;
        }

        submission.UpdatedAt = now;
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("ai_grading_job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.LeaseExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw Permanent("ai_grading_job_lease_lost");
        }

        return job;
    }

    private static void ValidateProfile(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle)
    {
        if (profile.TaskType != AiTaskTypes.InitialGrading
            || !profile.Active
            || !AiTaskProfileRuntimePolicy.IsReadyApprovalState(
                profile.ApprovalState)
            || !AiProviderCatalog.IsSupportedProvider(
                profile.AiConnection.Provider)
            || profile.ModelId != profile.AiConnection.ModelId
            || !AiProviderCatalog.SupportsImageTasks(
                profile.AiConnection.Provider,
                profile.ModelId)
            || profile.AiConnection.EndpointProfile
                != AiProviderCatalog.GetEndpointProfile(
                    profile.AiConnection.Provider)
            || profile.AiConnection.State != "active"
            || profile.AiConnection.LastCapabilityProbeState != "passed"
            || profile.ConnectionRevision
                != profile.AiConnection.CredentialRevision
            || (profile.ProcessingStrategy == "gemini_batch"
                && (profile.AiConnection.Provider != AiProviders.GeminiDirect
                    || profile.ModelId != ModelId
                    || profile.AiConnection
                        .LastBatchCapabilityProbeState != "passed"
                    || profile.AiConnection
                            .LastBatchCapabilityProbeCredentialRevision
                        != profile.AiConnection.CredentialRevision))
            || profile.PromptVersion != bundle.PromptVersion
            || profile.SchemaVersion != bundle.SchemaVersion
            || profile.PromptContentHash != bundle.ContentHash
            || profile.ThinkingLevel != "minimal"
            || profile.ProcessingStrategy is not (
                "queued_standard" or "expedite_standard" or "gemini_batch"))
        {
            throw Blocked("ai_initial_profile_not_approved");
        }
    }

    private static void ValidateSubmission(
        SubmissionEntity submission,
        TemplateVersionEntity version,
        QuestionEntity[] questions,
        GradingPayload payload)
    {
        if ((submission.AssignedStudentId is null
                && !IsExplicitlyUnidentified(submission)
                && submission.State != "needs_name_review")
            || submission.State is not (
                "needs_name_review" or "grading" or "awaiting_grading"
                or "needs_attention" or "needs_grade_review")
            || submission.ScanPayloadState != "scan_available"
            || submission.PreprocessingCompletedAt is null
            || !IsSha256(submission.PreprocessingManifestHash)
            || !string.Equals(
                submission.PreprocessingManifestHash,
                payload.ManifestHash,
                StringComparison.Ordinal))
        {
            throw Permanent("ai_grading_submission_state_invalid");
        }

        if (version.Id != payload.TemplateVersionId
            || version.Id != submission.TestSession.TemplateVersionId
            || !TemplateVersionUsePolicy.IsImmutablePublishedSnapshot(version.State)
            || questions.Length == 0
            || questions.Length > 300
            || questions.Any(question =>
                question.MaxPointsMilli < 0
                || question.PointIncrementMilli <= 0
                || !question.TeacherVerified)
            || questions.Select(question => question.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != questions.Length)
        {
            throw Permanent("ai_grading_template_invalid");
        }
    }

    private static void ValidatePages(
        SubmissionEntity submission,
        List<SubmissionPageEntity> pages)
    {
        if (pages.Count is < 1 or > 200
            || pages.Any(page =>
                page.SubmissionId != submission.Id
                || page.PageNumber <= 0
                || page.WidthPixels <= 0
                || page.HeightPixels <= 0
                || page.NormalizedFileReference.OwnerType != "submission_page"
                || page.NormalizedFileReference.OwnerId != page.Id
                || page.NormalizedFileReference.Purpose != "normalized_page"
                || page.NormalizedFileReference.FileObject.State != "available"
                || page.NormalizedFileReference.FileObject.StorageClass
                    != ContentStorageClass.ManagedScanDerived.ToString()
                || page.NormalizedFileReference.FileObject.VerifiedMime
                    is not ("image/png" or "image/jpeg")
                || !IsSha256(page.NormalizedFileReference.FileObject.Sha256)
                || page.NormalizedFileReference.FileObject.Bytes <= 0))
        {
            throw Permanent("ai_submission_page_disclosure_invalid");
        }
    }

    private static ArtifactSnapshot ToArtifactSnapshot(
        SubmissionPageEntity page,
        string inputManifestHash)
    {
        var fileObject = page.NormalizedFileReference.FileObject;
        return new ArtifactSnapshot(
            page.Id,
            page.PageNumber - 1,
            $"PAGE_{page.PageNumber}",
            page.NormalizedFileReferenceId,
            inputManifestHash,
            fileObject.VerifiedMime,
            fileObject.Sha256,
            fileObject.Bytes,
            page.WidthPixels,
            page.HeightPixels,
            new ContentObjectLocator(
                ContentStorageClass.ManagedScanDerived,
                fileObject.Sha256,
                fileObject.Bytes,
                fileObject.Extension));
    }

    private static string ComputeInputManifestHash(
        SubmissionEntity submission,
        TemplateVersionEntity version,
        AiTaskProfileEntity profile,
        AiPromptBundle bundle,
        IEnumerable<QuestionEntity> questions,
        IEnumerable<ArtifactSnapshot> artifacts)
    {
        var canonical = new StringBuilder();
        AppendManifest(canonical, "submission", submission.Id);
        AppendManifest(
            canonical,
            "preprocessing",
            submission.PreprocessingManifestHash ?? string.Empty);
        AppendManifest(canonical, "template", version.Id);
        AppendManifest(
            canonical,
            "template-content",
            version.ContentHash ?? string.Empty);
        AppendManifest(canonical, "profile", profile.Id);
        AppendManifest(
            canonical,
            "profile-revision",
            profile.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(canonical, "prompt", bundle.ContentHash);
        foreach (var question in questions
                     .OrderBy(item => item.OrderIndex)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifest(canonical, "question", question.Id);
            AppendManifest(
                canonical,
                "question-revision",
                question.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(
                canonical,
                "maximum",
                question.MaxPointsMilli.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(
                canonical,
                "increment",
                question.PointIncrementMilli.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            foreach (var answer in question.AcceptedAnswers
                         .OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                AppendManifest(canonical, "answer", answer.Id);
                AppendManifest(
                    canonical,
                    "answer-revision",
                    answer.Revision.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                AppendManifest(canonical, "answer-text", answer.AnswerText);
            }
        }

        foreach (var artifact in artifacts
                     .OrderBy(item => item.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifest(canonical, "artifact", artifact.Id);
            AppendManifest(
                canonical,
                "artifact-page",
                artifact.Ordinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(
                canonical,
                "artifact-input",
                artifact.InputManifestHash);
            AppendManifest(
                canonical,
                "artifact-sha256",
                artifact.Sha256);
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendManifest(
        StringBuilder destination,
        string name,
        string value)
    {
        destination.Append(name.Length)
            .Append(':')
            .Append(name)
            .Append('=')
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('\n');
    }

    private static string CreateUserInstruction(
        string requestKey,
        IReadOnlyCollection<QuestionSnapshot> questions,
        IReadOnlyCollection<ArtifactSnapshot> artifacts,
        int chunkIndex,
        int chunkCount)
    {
        var media = artifacts.Select(
            (artifact, index) => new
            {
                media_index = index,
                page_number = artifact.Ordinal + 1,
                page_label = artifact.PanelLabel,
            });
        var rubric = questions.Select(question => new
        {
            question_id = question.Id,
            order_index = question.OrderIndex,
            display_label = question.DisplayLabel,
            question_text = question.QuestionText,
            question_type = question.QuestionType,
            grading_mode = question.GradingMode,
            maximum_points_milli = question.MaximumPointsMilli,
            point_increment_milli = question.PointIncrementMilli,
            allow_non_kanji = question.AllowNonKanji,
            requires_complete_answer = question.RequiresCompleteAnswer,
            answer_order_insensitive = question.AnswerOrderInsensitive,
            rubric_text = question.RubricText,
            accepted_answers = question.AcceptedAnswers,
        });
        return
            """
            The attached media are one consecutive page chunk from a completed
            Japanese test, in original test page order. Other page chunks may
            contain answers that are not visible here. Match only visible answers
            to the supplied questions using printed question labels, question
            text, and document layout. For every returned result, set
            evidence_media_index to the media item containing that visible answer.
            Read and grade directly from the original page pixels in one integrated
            inspection. Transcribe each visible answer exactly, preserving Japanese
            script and every visible line boundary as \n. The transcription is an
            audit record, not the sole input to grading. Grade only against the
            teacher-supplied rubric and accepted answers. A visual line wrap,
            indentation, or surrounding layout whitespace alone must never make
            otherwise identical content incorrect. Ignore only layout placement;
            never omit, reorder, or merge distinct answer components.
            When requires_complete_answer is true, award either zero or the full
            maximum; a clear response missing any required component is incorrect,
            never partial. This does not override unreadable, cropped, or ambiguous
            review states. When answer_order_insensitive is true, compare the
            complete multiset of components separated by Japanese/ASCII commas,
            slashes, semicolons, middle dots, or line breaks. Order may differ,
            but no component may be missing and duplicate counts must match.
            Include every question ID either once in results or once in
            missing_question_ids; use missing_question_ids when its answer is not
            visible in this chunk. Recommend review whenever evidence is
            ambiguous, incomplete, subjective, unexpected, or unreadable.

            When identity_required is true, transcribe only the visibly written
            name and student number from PAGE_1's printed identity field into
            identity. Do not infer a student, use a roster, or return a student ID.
            When identity_required is false, return identity=null and ignore all
            names while grading this chunk.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "submission_analysis_v2",
                request_key = requestKey,
                chunk_index = chunkIndex + 1,
                chunk_count = chunkCount,
                identity_required = chunkIndex == 0,
                identity_page_number = chunkIndex == 0 ? 1 : (int?)null,
                media,
                questions = rubric,
            });
    }

    private static AiRequestEntity CreateRequest(
        DateTimeOffset now,
        SubmissionEntity submission,
        AiTaskProfileEntity profile,
        string inputManifestHash)
    {
        var id = UlidId.New(now);
        return new AiRequestEntity
        {
            Id = id,
            RequestKey = $"grade_{id}",
            AiTaskProfileId = profile.Id,
            TaskProfileRevision = profile.Revision,
            Purpose = AiTaskTypes.InitialGrading,
            EntityType = "submission",
            EntityId = submission.Id,
            EntityRevision = submission.Revision,
            InputManifestHash = inputManifestHash,
            AttemptNumber = 1,
            State = "prepared",
            DispatchAttempt = 0,
            PossibleDuplicate = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static async Task<UsageWindow> GetUsageWindowAsync(
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
            throw Blocked("ai_budget_timezone_invalid");
        }
        catch (InvalidTimeZoneException)
        {
            throw Blocked("ai_budget_timezone_invalid");
        }

        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        return new UsageWindow(
            DateOnly.FromDateTime(local.DateTime),
            $"{local.Year:0000}-{local.Month:00}");
    }

    private static async Task<CommittedSpend> GetCommittedSpendAsync(
        OokiGraderDbContext db,
        UsageWindow window,
        string currentRequestId,
        CancellationToken cancellationToken)
    {
        var reservations = await db.AiBudgetReservations
            .AsNoTracking()
            .Where(item =>
                item.AiRequestId != currentRequestId
                && (item.UsageDay == window.Day
                    || item.UsageMonth == window.Month)
                && (item.State == "reserved" || item.State == "settled"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        static long Amount(AiBudgetReservationEntity item) =>
            item.State == "settled"
                ? item.ActualUsdMicros
                : item.ReservedUsdMicros;
        return new CommittedSpend(
            reservations
                .Where(item => item.UsageDay == window.Day)
                .Aggregate(0L, (total, item) => checked(total + Amount(item))),
            reservations
                .Where(item => item.UsageMonth == window.Month)
                .Aggregate(0L, (total, item) => checked(total + Amount(item))));
    }

    private long EstimateMaximumCost(
        PricingSnapshotEntity pricing,
        int maxOutputTokens,
        string instruction,
        IReadOnlyCollection<ArtifactSnapshot> artifacts)
    {
        var textTokens = Math.Max(
            1,
            (Encoding.UTF8.GetByteCount(instruction) + 3L) / 4L);
        var imageTokens = artifacts.Aggregate(
            0L,
            (total, artifact) =>
            {
                var horizontalTiles = Math.Max(
                    1,
                    (artifact.WidthPixels + 767L) / 768L);
                var verticalTiles = Math.Max(
                    1,
                    (artifact.HeightPixels + 767L) / 768L);
                return checked(
                    total
                    + checked(
                        horizontalTiles
                        * verticalTiles
                        * _options.EstimatedImageTokensPerTile));
            });
        var estimatedInputTokens = checked(textTokens + imageTokens);
        return CalculateCost(
            estimatedInputTokens,
            maxOutputTokens,
            0,
            pricing.InputUsdMicrosPerMillionTokens,
            pricing.OutputUsdMicrosPerMillionTokens,
            pricing.ThinkingUsdMicrosPerMillionTokens);
    }

    private static long CalculateActualCost(
        PricingSnapshot pricing,
        AiUsage usage)
    {
        return CalculateCost(
            usage.PromptTokens ?? 0,
            usage.OutputTokens ?? 0,
            usage.ThinkingTokens ?? 0,
            pricing.InputUsdMicrosPerMillionTokens,
            pricing.OutputUsdMicrosPerMillionTokens,
            pricing.ThinkingUsdMicrosPerMillionTokens);
    }

    private static long CalculateCost(
        long inputTokens,
        long outputTokens,
        long thinkingTokens,
        long inputRate,
        long outputRate,
        long thinkingRate)
    {
        var numerator =
            (BigInteger)inputTokens * inputRate
            + (BigInteger)outputTokens * outputRate
            + (BigInteger)thinkingTokens * thinkingRate;
        if (numerator <= 0)
        {
            return 0;
        }

        var result = (numerator + 999_999) / 1_000_000;
        if (result > long.MaxValue)
        {
            throw Blocked("ai_cost_overflow");
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
            throw Blocked("ai_cost_overflow");
        }

        return (long)result;
    }

    private static bool WouldExceedHardLimit(
        long committed,
        long reservation,
        long hardLimit)
    {
        return hardLimit >= 0
            && (BigInteger)committed + reservation > hardLimit;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is <= 0 or > int.MaxValue)
        {
            throw Permanent("ai_submission_page_size_invalid");
        }

        using var destination = new MemoryStream(checked((int)expectedBytes));
        await source.CopyToAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        if (destination.Length != expectedBytes)
        {
            throw Permanent("ai_submission_page_size_mismatch");
        }

        return destination.ToArray();
    }

    private static void MarkBudgetBlocked(
        AiRequestEntity request,
        SubmissionEntity submission,
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        request.State = "budget_blocked";
        request.ErrorCode = errorCode;
        request.SafeErrorDetail = null;
        request.UpdatedAt = now;
        submission.State = "awaiting_grading";
        BlockJob(job, now, errorCode);
    }

    private static void MarkAmbiguousRecovery(
        AiRequestEntity request,
        SubmissionEntity submission,
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        request.State = "failed";
        request.PossibleDuplicate = true;
        request.ErrorCode = "ai_dispatch_outcome_unknown";
        request.SafeErrorDetail = "recovered_dispatching_request";
        request.CompletedAt = now;
        request.UpdatedAt = now;
        submission.State = "needs_attention";
        BlockJob(job, now, "ai_dispatch_outcome_unknown");
    }

    private static void CompleteJob(
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        job.State = "succeeded";
        job.ProgressBasisPoints = 10_000;
        job.ErrorCode = null;
        job.SafeErrorDetail = null;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }

    private static void BlockJob(
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        job.State = "blocked";
        job.ErrorCode = errorCode;
        job.SafeErrorDetail = null;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }

    private static void FailJob(
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        job.State = "failed";
        job.ErrorCode = errorCode;
        job.SafeErrorDetail = null;
        job.CompletedAt = now;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
    }

    private static void AddAudit(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string eventType,
        string submissionId,
        string reasonCode)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = eventType,
            ObjectType = "submission",
            ObjectId = submissionId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
        });
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string submissionId,
        string state)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = "submission.status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId,
                state,
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static string ToMediaResolution(string value) => value switch
    {
        "low" => "MEDIA_RESOLUTION_LOW",
        "medium" => "MEDIA_RESOLUTION_MEDIUM",
        "high" => "MEDIA_RESOLUTION_HIGH",
        "ultra_high" => "MEDIA_RESOLUTION_ULTRA_HIGH",
        _ => throw Blocked("ai_media_resolution_invalid"),
    };

    private static PricingSnapshot ToPricingSnapshot(
        PricingSnapshotEntity entity) =>
        new(
            entity.Id,
            entity.InputUsdMicrosPerMillionTokens,
            entity.OutputUsdMicrosPerMillionTokens,
            entity.ThinkingUsdMicrosPerMillionTokens);

    private static GradingPayload DeserializePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GradingPayload>(
                       json,
                       PayloadSerializerOptions)
                   ?? throw Permanent("ai_grading_payload_invalid");
        }
        catch (JsonException)
        {
            throw Permanent("ai_grading_payload_invalid");
        }
    }

    private static bool IsExplicitlyUnidentified(SubmissionEntity submission)
    {
        return submission.AssignedStudentId is null
            && submission.AssignmentMethod == "none"
            && submission.AssignmentEvidenceJson
                == """{"disposition":"unidentified"}""";
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    private static string Sha256(string value)
    {
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
    }

    private static string BoundedErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ai_response_semantics_invalid";
        }

        return value.Length <= 200
            ? value
            : "ai_response_semantics_invalid";
    }

    private static string BoundedSafeDetail(string value)
    {
        return value.Length <= 2_000
            ? value
            : value[..2_000];
    }

    private static JobHandlingException Permanent(string errorCode) =>
        new(errorCode, FailureDisposition.Permanent);

    private static JobHandlingException Blocked(string errorCode) =>
        new(errorCode, FailureDisposition.Blocked);

    private static void AddIfDetached(
        OokiGraderDbContext db,
        AiRequestEntity request)
    {
        if (db.Entry(request).State == EntityState.Detached)
        {
            db.AiRequests.Add(request);
        }
    }

    [LoggerMessage(
        EventId = 1301,
        Level = LogLevel.Warning,
        Message =
            "AI initial-grading job {JobId} failed with {ErrorCode} " +
            "({ExceptionType}).")]
    private partial void LogJobFailure(
        string jobId,
        string errorCode,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        string Type,
        int SchemaVersion,
        string PayloadJson,
        int Priority,
        string? CorrelationId);

    private sealed record GradingPayload(
        string SubmissionId,
        string TemplateVersionId,
        string ManifestHash,
        string? AiRequestId = null,
        bool ForceExpedite = false);

    private sealed record PreparedClaim(
        string JobId,
        string? CorrelationId,
        int Priority,
        string SubmissionId,
        long SubmissionRevision,
        string TemplateVersionId,
        string TaskProfileId,
        long TaskProfileRevision,
        long ConnectionRevision,
        string ProcessingStrategy,
        string RequestId,
        string RequestKey,
        string CanonicalInputManifestHash,
        int MaxOutputTokens,
        string MediaResolution,
        string SecretReference,
        AiConnectionSettings Connection,
        AiPromptBundle Bundle,
        IReadOnlyList<QuestionSnapshot> Questions,
        IReadOnlyList<ArtifactSnapshot> Artifacts,
        int ChunkIndex,
        IReadOnlyList<ArtifactChunk> Chunks,
        IReadOnlyList<CompletedChunk> CompletedChunks,
        PricingSnapshot? Pricing,
        long UsdToJpyMicros,
        bool ForceExpedite,
        StoredAiResponse? StoredResponse);

    private sealed record StoredAiResponse(
        AiProviderResponse Response,
        long EstimatedUsdMicros);

    private sealed record QuestionSnapshot(
        string Id,
        int OrderIndex,
        string DisplayLabel,
        string QuestionText,
        string QuestionType,
        string GradingMode,
        long MaximumPointsMilli,
        long PointIncrementMilli,
        bool AllowNonKanji,
        bool RequiresCompleteAnswer,
        bool AnswerOrderInsensitive,
        string? RubricText,
        IReadOnlyList<string> AcceptedAnswers,
        DomainQuestionDefinition Definition);

    private sealed record ArtifactSnapshot(
        string Id,
        int Ordinal,
        string? PanelLabel,
        string FileReferenceId,
        string InputManifestHash,
        string MimeType,
        string Sha256,
        long Bytes,
        int WidthPixels,
        int HeightPixels,
        ContentObjectLocator Locator);

    private sealed record ArtifactChunk(
        int Index,
        string InputManifestHash,
        IReadOnlyList<ArtifactSnapshot> Artifacts);

    private sealed record CompletedChunk(
        int Index,
        string RequestId,
        string RequestKey,
        string InputManifestHash,
        ValidatedAiGradingResponse Response,
        string? ActualModel,
        UsageSnapshot Usage,
        IReadOnlyList<ArtifactSnapshot> Artifacts);

    private sealed record UsageSnapshot(
        long? PromptTokens,
        long? CachedTokens,
        long? OutputTokens,
        long? ThinkingTokens,
        long? TotalTokens,
        long EstimatedUsdMicros);

    private sealed record ChunkObservation(
        CompletedChunk Chunk,
        ValidatedAiQuestionObservation Observation);

    private sealed record PricingSnapshot(
        string Id,
        long InputUsdMicrosPerMillionTokens,
        long OutputUsdMicrosPerMillionTokens,
        long ThinkingUsdMicrosPerMillionTokens);

    private sealed record UsageWindow(DateOnly Day, string Month);

    private sealed record CommittedSpend(
        long DailyUsdMicros,
        long MonthlyUsdMicros);

    private enum FailureDisposition
    {
        Transient,
        Permanent,
        Blocked,
    }

    private sealed class JobHandlingException(
        string errorCode,
        FailureDisposition kind) : Exception(errorCode)
    {
        public string ErrorCode { get; } = errorCode;
        public FailureDisposition Kind { get; } = kind;
    }
}
