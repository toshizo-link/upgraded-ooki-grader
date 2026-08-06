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

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || MaximumMediaBytes is < 1_024 or > 18 * 1024 * 1024
            || EstimatedImageTokensPerTile is < 256 or > 32_768)
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
    public const string PipelineVersion = "gemini-initial-grading-full-page-v2";

    public const int JobSchemaVersion = 1;
    private const int MaximumStoredResponseCharacters = 1_000_000;
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

                validated = AiGradingResponseValidator.Validate(
                    response.StructuredOutput,
                    claim.RequestKey,
                    claim.Questions.ToDictionary(
                        item => item.Id,
                        item => item.Definition,
                        StringComparer.Ordinal));
            }
            catch (InvalidDataException exception)
            {
                await RecordInvalidResponseAsync(
                        claim,
                        response,
                        BoundedErrorCode(exception.Message),
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
            if (!await ProcessNextAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(_options.PollInterval, stoppingToken)
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
            var existingRun = submission.GradingRuns.SingleOrDefault(
                run => run.PipelineVersion == PipelineVersion
                    && run.CanonicalInputManifestHash == inputManifestHash);
            if (existingRun is not null)
            {
                if (submission.CurrentGradingRunId != existingRun.Id)
                {
                    throw Permanent("ai_grading_run_conflict");
                }

                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            var requestEntity = await db.AiRequests
                .Include(item => item.Usage)
                .Include(item => item.BatchRequest)
                .Where(item => item.EntityType == "submission"
                        && item.EntityId == submission.Id
                        && item.InputManifestHash == inputManifestHash
                        && item.TaskProfileRevision == profile.Revision
                        && (payload.AiRequestId == null
                            || item.Id == payload.AiRequestId))
                .OrderByDescending(item => item.AttemptNumber)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
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
                        artifactSnapshots,
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
                requestEntity?.RequestKey ?? "pending",
                questionSnapshots,
                artifactSnapshots);
            var reservedUsdMicros = pricing is null
                ? 0
                : EstimateMaximumCost(
                    pricing,
                    profile.MaxOutputTokens,
                    instruction,
                    artifactSnapshots);
            var usageWindow = await GetUsageWindowAsync(db, now, token)
                .ConfigureAwait(false);

            requestEntity ??= CreateRequest(
                now,
                submission,
                profile,
                inputManifestHash);
            if (requestEntity.Id.Length == 0)
            {
                throw Permanent("ai_request_identity_invalid");
            }

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
                artifactSnapshots,
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
        IReadOnlyList<ArtifactSnapshot> artifacts,
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
            artifacts,
            pricing,
            usdToJpyMicros,
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
                claim.Artifacts),
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

            validated = AiGradingResponseValidator.Validate(
                stored.Response.StructuredOutput,
                claim.RequestKey,
                claim.Questions.ToDictionary(
                    item => item.Id,
                    item => item.Definition,
                    StringComparer.Ordinal));
        }
        catch (InvalidDataException exception)
        {
            await RecordInvalidStoredResponseAsync(
                    claim,
                    BoundedErrorCode(exception.Message),
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
                .SingleOrDefaultAsync(
                    item => item.Id == claim.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_grading_submission_missing");
            if (submission.CurrentGradingRunId is not null
                || submission.State is not ("grading" or "awaiting_grading")
                || submission.Revision < claim.SubmissionRevision)
            {
                throw Permanent("ai_grading_submission_changed");
            }

            var existingRun = submission.GradingRuns.SingleOrDefault(
                run => run.PipelineVersion == PipelineVersion
                    && run.CanonicalInputManifestHash == claim.InputManifestHash);
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

            var observations = validated.Observations.ToDictionary(
                item => item.QuestionId,
                StringComparer.Ordinal);
            var possiblePoints = claim.Questions.Aggregate(
                0L,
                static (total, question) =>
                    checked(total + question.MaximumPointsMilli));
            var earnedPoints = validated.Observations.Aggregate(
                0L,
                static (total, observation) =>
                    checked(total + observation.ProposedPointsMilli));
            var run = new GradingRunEntity
            {
                Id = UlidId.New(now),
                SubmissionId = submission.Id,
                RunNumber = checked(
                    submission.GradingRuns.Select(item => item.RunNumber)
                        .DefaultIfEmpty()
                        .Max() + 1),
                TemplateVersionId = claim.TemplateVersionId,
                Reason = "gemini_initial_pilot",
                State = "needs_grade_review",
                Provider = claim.Connection.Provider,
                Model = response.ActualModel ?? claim.Connection.ModelId,
                PromptVersion = claim.Bundle.PromptVersion,
                SchemaVersion = claim.Bundle.SchemaVersion,
                PipelineVersion = PipelineVersion,
                CanonicalInputManifestHash = claim.InputManifestHash,
                EarnedPointsMilli = earnedPoints,
                PossiblePointsMilli = possiblePoints,
                ResultSourceRevision = 1,
                AiUsageAggregationJson = JsonSerializer.Serialize(new
                {
                    aiRequestId = request.Id,
                    response.Usage.PromptTokens,
                    response.Usage.CachedTokens,
                    response.Usage.OutputTokens,
                    response.Usage.ThinkingTokens,
                    response.Usage.TotalTokens,
                    estimatedUsdMicros = actualCost,
                }),
                CreatedAt = now,
                FinishedAt = now,
            };
            db.GradingRuns.Add(run);

            var results = new List<(QuestionResultEntity Result, long Points)>(
                claim.Questions.Count);
            var pageReferenceId = claim.Artifacts
                .OrderBy(item => item.Ordinal)
                .Select(item => item.FileReferenceId)
                .FirstOrDefault();
            foreach (var question in claim.Questions)
            {
                QuestionResultEntity result;
                long points;
                if (observations.TryGetValue(question.Id, out var observation))
                {
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
                        ReasonCode = validated.UnexpectedContent
                            ? "ai_unexpected_content"
                            : observation.ProviderReasonCode
                                ?? "ai_pilot_proposal",
                        Explanation = observation.BoundedExplanation,
                        AnswerCropFileReferenceId = pageReferenceId,
                        ReviewRequired = true,
                        ReviewStatus = "pending",
                        ModelResponseItemHash =
                            observation.CanonicalItemHash,
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
                        AnswerCropFileReferenceId = pageReferenceId,
                        ReviewRequired = true,
                        ReviewStatus = "pending",
                        CreatedAt = now,
                    };
                }

                db.QuestionResults.Add(result);
                results.Add((result, points));
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
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

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            if (_adjudicationJobScheduler is not null)
            {
                await _adjudicationJobScheduler.EnqueueAmbiguousAsync(
                        db,
                        submission,
                        run,
                        results.Select(item => item.Result).ToArray(),
                        validated,
                        claim.Questions
                            .Select(item => new AiAdjudicationArtifactCandidate(
                                item.Id,
                                ProviderDisclosureAllowed: true))
                            .ToArray(),
                        claim.CorrelationId,
                        job.Id,
                        now,
                        token)
                    .ConfigureAwait(false);
            }

            run.EarnedPointsMilli = results.Aggregate(
                0L,
                static (total, item) => checked(total + item.Points));
            run.PossiblePointsMilli = results.Aggregate(
                0L,
                static (total, item) =>
                    checked(total + item.Result.MaximumPointsMilli));
            submission.CurrentGradingRunId = run.Id;
            submission.State = "needs_grade_review";
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.gemini_pilot_created",
                submission.Id,
                "teacher_review_required");
            AddStatusOutbox(
                db,
                now,
                claim.CorrelationId,
                submission.Id,
                submission.State);
            CompleteJob(job, now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordInvalidResponseAsync(
        PreparedClaim claim,
        AiProviderResponse response,
        string errorCode,
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
            || profile.ApprovalState is not (
                "pilot_approved" or "production_approved")
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
                && !IsExplicitlyUnidentified(submission))
            || submission.CurrentGradingRunId is not null
            || submission.State is not (
                "grading" or "awaiting_grading" or "needs_attention")
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
            || version.State != "published"
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
        IReadOnlyCollection<ArtifactSnapshot> artifacts)
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
            rubric_text = question.RubricText,
            accepted_answers = question.AcceptedAnswers,
        });
        return
            """
            The attached media are every page of one completed Japanese test,
            in page order. Match answers to the supplied questions using printed
            question labels, question text, and document layout. Do not infer or
            return student identity. Transcribe each visible answer exactly,
            preserving Japanese script. Grade only against the teacher-supplied
            rubric and accepted answers. Include every question ID either once
            in results or once in missing_question_ids. Recommend review whenever
            evidence is ambiguous, incomplete, subjective, unexpected, or
            unreadable.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = requestKey,
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
        string InputManifestHash,
        int MaxOutputTokens,
        string MediaResolution,
        string SecretReference,
        AiConnectionSettings Connection,
        AiPromptBundle Bundle,
        IReadOnlyList<QuestionSnapshot> Questions,
        IReadOnlyList<ArtifactSnapshot> Artifacts,
        PricingSnapshot? Pricing,
        long UsdToJpyMicros,
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
