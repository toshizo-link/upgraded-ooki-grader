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
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using DomainQuestionDefinition =
    OokiGrader.Domain.Templates.QuestionDefinition;

namespace OokiGrader.Host.Jobs;

/// <summary>
/// Rechecks a single ambiguous answer against the complete normalized page.
/// The result remains teacher-gated. An AI proposal is appended only
/// when the exact source revision is still current, so an intervening teacher
/// decision is never overwritten.
/// </summary>
public sealed partial class AiAdjudicationJobWorker : BackgroundService
{
    public const string JobType = "gemini_answer_adjudication";
    public const int JobSchemaVersion = 1;
    public const string ModelId = AiProviderRuntime.GeminiModel;
    public const string PipelineVersion = "gemini-answer-adjudication-full-page-v3";

    private const int MaximumStoredResponseCharacters = 100_000;
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
    private readonly ILogger<AiAdjudicationJobWorker> _logger;
    private readonly AiAdjudicationJobWorkerOptions _options;
    private readonly string _workerId = $"gemini-adjudicate-{Guid.NewGuid():N}";

    public AiAdjudicationJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IAiProviderClient providerClient,
        IAiPromptBundleCatalog promptCatalog,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        IOptions<AiAdjudicationJobWorkerOptions> options,
        ILogger<AiAdjudicationJobWorker> logger,
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

            if (!_providerFeaturePolicy.IsEnabled(
                    claim.Connection.Provider))
            {
                throw Blocked("ai_provider_feature_disabled");
            }

            var media = new List<AiMediaPart>(claim.Artifacts.Count);
            foreach (var artifact in claim.Artifacts)
            {
                media.Add(await LoadMediaAsync(artifact, cancellationToken)
                    .ConfigureAwait(false));
            }
            var providerRequest = CreateProviderRequest(claim, media);
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
                        providerRequest,
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
                        "ai_adjudication_dispatch_outcome_unknown",
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
                    throw new InvalidDataException(
                        "ai_adjudication_response_too_large");
                }

                validated = AiGradingResponseValidator.Validate(
                    response.StructuredOutput,
                    claim.RequestKey,
                    new Dictionary<string, DomainQuestionDefinition>(
                        StringComparer.Ordinal)
                    {
                        [claim.Question.Id] = claim.Question.Definition,
                    });
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
            await RecordJobFailureAsync(
                    lease.Id,
                    claim?.RequestId,
                    exception.ErrorCode,
                    exception.Disposition,
                    dispatchCommitted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            const string errorCode = "ai_adjudication_worker_error";
            LogJobFailure(
                lease.Id,
                errorCode,
                exception.GetType().Name);
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
                    item.Type == JobType
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
                job.SchemaVersion,
                job.PayloadJson,
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
                throw Permanent("ai_adjudication_job_schema_unsupported");
            }

            var payload = DeserializePayload(lease.PayloadJson);
            if (string.IsNullOrWhiteSpace(payload.SubmissionId)
                || string.IsNullOrWhiteSpace(payload.GradingRunId)
                || string.IsNullOrWhiteSpace(payload.QuestionResultId)
                || string.IsNullOrWhiteSpace(payload.SourceRevisionId))
            {
                throw Permanent("ai_adjudication_payload_invalid");
            }

            var bundle = _promptCatalog.GetRequired(AiTaskTypes.Adjudication);
            var profile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.Adjudication
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null)
            {
                throw Blocked("ai_adjudication_profile_unavailable");
            }

            ValidateProfile(profile, bundle);
            var result = await db.QuestionResults
                .Include(item => item.Revisions)
                .Include(item => item.Question)
                    .ThenInclude(question => question.AcceptedAnswers)
                .Include(item => item.GradingRun)
                    .ThenInclude(run => run.TemplateVersion)
                        .ThenInclude(version => version.Sources)
                .Include(item => item.GradingRun)
                    .ThenInclude(run => run.Submission)
                        .ThenInclude(submission => submission.TestSession)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.QuestionResultId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_result_missing");
            ValidateResult(result, payload);
            var sourceRevision = result.Revisions.Single(
                item => item.Id == payload.SourceRevisionId);

            var pages = await db.SubmissionPages
                .AsNoTracking()
                .Include(item => item.NormalizedFileReference)
                    .ThenInclude(reference => reference.FileObject)
                .Where(item => item.SubmissionId == payload.SubmissionId)
                .OrderBy(item => item.PageNumber)
                .ThenBy(item => item.Id)
                .ToListAsync(token)
                .ConfigureAwait(false);
            if (pages.Count == 0)
            {
                throw Permanent("ai_adjudication_page_missing");
            }

            foreach (var page in pages)
            {
                ValidatePage(result, page);
            }
            var manifest = result.GradingRun.Submission.PreprocessingManifestHash
                ?? throw Permanent("ai_adjudication_manifest_missing");
            var artifacts = pages
                .Select(page => ToArtifactSnapshot(page, manifest))
                .ToArray();
            var question = ToQuestionSnapshot(
                result.Question,
                result.GradingRun.TemplateVersion);
            var inputManifestHash = ComputeInputManifestHash(
                result,
                sourceRevision,
                profile,
                bundle,
                artifacts);
            var request = await db.AiRequests
                .Include(item => item.Usage)
                .Where(item =>
                    item.EntityType == "questionResult"
                    && item.EntityId == result.Id
                    && item.InputManifestHash == inputManifestHash
                    && item.TaskProfileRevision == profile.Revision)
                .OrderByDescending(item => item.AttemptNumber)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (request is not null)
            {
                if (request.PossibleDuplicate || request.State == "dispatching")
                {
                    MarkAmbiguousRecovery(request, job, now);
                    SettleReservationConservatively(db, request.Id, now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (request.State == "succeeded")
                {
                    CompleteJob(job, now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (request.State is
                    "invalid_output" or "safety_blocked" or "failed"
                    or "cancelled" or "budget_blocked")
                {
                    BlockJob(
                        job,
                        now,
                        request.ErrorCode ?? "ai_adjudication_request_terminal");
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
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
            request ??= CreateRequest(
                now,
                result,
                sourceRevision,
                profile,
                inputManifestHash);
            var instruction = CreateUserInstruction(
                request.RequestKey,
                question,
                artifacts);
            var reservedUsdMicros = pricing is null
                ? 0
                : EstimateMaximumCost(
                    pricing,
                    profile.MaxOutputTokens,
                    instruction,
                    artifacts);
            var usageWindow = await GetUsageWindowAsync(db, now, token)
                .ConfigureAwait(false);

            if (pricing is null)
            {
                MarkBudgetBlocked(
                    request,
                    job,
                    now,
                    "ai_adjudication_pricing_snapshot_missing");
                AddIfDetached(db, request);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (budget?.Active == true)
            {
                var spent = await GetCommittedSpendAsync(
                        db,
                        usageWindow,
                        request.Id,
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
                        request,
                        job,
                        now,
                        "ai_adjudication_budget_hard_limit");
                    AddIfDetached(db, request);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
            }

            AddIfDetached(db, request);
            var reservation = await db.AiBudgetReservations
                .SingleOrDefaultAsync(
                    item => item.AiRequestId == request.Id,
                    token)
                .ConfigureAwait(false);
            if (reservation is null)
            {
                reservation = new AiBudgetReservationEntity
                {
                    Id = UlidId.New(now),
                    AiRequestId = request.Id,
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
                throw Permanent("ai_adjudication_reservation_state_invalid");
            }

            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 2_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new PreparedClaim(
                job.Id,
                job.CorrelationId,
                result.GradingRun.SubmissionId,
                result.GradingRunId,
                result.Id,
                sourceRevision.Id,
                sourceRevision.RevisionNumber,
                profile.Id,
                profile.Revision,
                request.Id,
                request.RequestKey,
                inputManifestHash,
                profile.MaxOutputTokens,
                ToMediaResolution(profile.MediaResolution),
                profile.AiConnection.SecretReference,
                new AiConnectionSettings(
                    profile.AiConnection.Id,
                    profile.AiConnection.Provider,
                    AiProviderCatalog.GetBaseAddress(
                        profile.AiConnection.Provider),
                    profile.AiConnection.ModelId,
                    TimeSpan.FromSeconds(profile.AiConnection.TimeoutSeconds)),
                bundle,
                question,
                artifacts,
                pricing is null ? null : ToPricingSnapshot(pricing),
                budget?.UsdToJpyMicros ?? 150_000_000);
        }, cancellationToken);
    }

    private async Task<AiMediaPart> LoadMediaAsync(
        ArtifactSnapshot artifact,
        CancellationToken cancellationToken)
    {
        if (artifact.Bytes <= 0 || artifact.Bytes > _options.MaximumMediaBytes)
        {
            throw Permanent("ai_adjudication_page_too_large");
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
            throw Permanent("ai_adjudication_page_hash_mismatch");
        }

        return new AiMediaPart(artifact.MimeType, bytes, artifact.Sha256);
    }

    private static AiProviderRequest CreateProviderRequest(
        PreparedClaim claim,
        IReadOnlyList<AiMediaPart> media)
    {
        return new AiProviderRequest(
            claim.RequestKey,
            AiTaskTypes.Adjudication,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            claim.Bundle.SystemInstruction,
            CreateUserInstruction(
                claim.RequestKey,
                claim.Question,
                claim.Artifacts),
            claim.Bundle.ResponseJsonSchema,
            media,
            claim.MaxOutputTokens,
            claim.MediaResolution);
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
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_request_missing");
            if (request.PossibleDuplicate
                || request.State is not ("prepared" or "retry_waiting")
                || request.DispatchAttempt >= 8)
            {
                throw Permanent("ai_adjudication_dispatch_state_invalid");
            }

            request.State = "dispatching";
            request.DispatchAttempt = checked(request.DispatchAttempt + 1);
            request.DispatchedAt = _timeProvider.GetUtcNow();
            request.UpdatedAt = request.DispatchedAt.Value;
            request.ErrorCode = null;
            request.SafeErrorDetail = null;
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 4_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
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
                ?? throw Permanent("ai_adjudication_request_missing");
            if (request.State != "dispatching" || request.PossibleDuplicate)
            {
                throw Permanent("ai_adjudication_completion_state_invalid");
            }

            var result = await db.QuestionResults
                .Include(item => item.Revisions)
                .Include(item => item.GradingRun)
                    .ThenInclude(run => run.QuestionResults)
                        .ThenInclude(item => item.Revisions)
                .Include(item => item.GradingRun)
                    .ThenInclude(run => run.Submission)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.QuestionResultId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_result_missing");

            request.State = "succeeded";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.AcceptedResponseHash = Sha256(responseJson);
            request.ValidatedResponseJson = responseJson;
            request.ErrorCode = null;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(db, claim, response, now);

            var sourceRevision = result.Revisions.SingleOrDefault(
                item => item.Id == claim.SourceRevisionId);
            var sourceIsStillCurrent = sourceRevision is not null
                && sourceRevision.RevisionNumber == claim.SourceRevisionNumber
                && result.CurrentRevisionId == claim.SourceRevisionId
                && sourceRevision.Source is "initial" or "regrade_adoption"
                && result.ReviewRequired
                && result.ReviewStatus == "pending"
                && result.GradingRunId == claim.GradingRunId
                && result.GradingRun.Submission.CurrentGradingRunId
                    == result.GradingRunId
                && result.GradingRun.Submission.FinalizedAt is null
                && result.GradingRun.Submission.VoidedAt is null;
            if (!sourceIsStillCurrent)
            {
                CompleteJob(job, now);
                AddAudit(
                    db,
                    now,
                    claim.CorrelationId,
                    "grading.adjudication_stale_skipped",
                    claim.QuestionResultId,
                    "source_revision_changed");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var observation = validated.Observations.SingleOrDefault();
            if (observation is null)
            {
                CompleteJob(job, now);
                AddAudit(
                    db,
                    now,
                    claim.CorrelationId,
                    "grading.adjudication_no_proposal",
                    claim.QuestionResultId,
                    "provider_reported_missing");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var currentSource = sourceRevision!;
            var disagrees = currentSource.AwardedPointsMilli
                    != observation.ProposedPointsMilli
                || currentSource.Outcome != observation.ProposedOutcome
                || !string.Equals(
                    currentSource.AnswerTextCorrection,
                    observation.Observation.Transcription,
                    StringComparison.Ordinal);
            var reasonCode = validated.UnexpectedContent
                ? "ai_adjudication_unexpected_content"
                : observation.ProviderReasonCode
                    ?? (disagrees
                        ? "ai_adjudication_disagreement"
                        : "ai_adjudication_confirmation");
            var proposal = new ResultRevisionEntity
            {
                Id = UlidId.New(now),
                QuestionResultId = result.Id,
                RevisionNumber = checked(currentSource.RevisionNumber + 1),
                AwardedPointsMilli = observation.ProposedPointsMilli,
                Outcome = observation.ProposedOutcome,
                AnswerTextCorrection = observation.Observation.Transcription,
                ReasonCode = reasonCode,
                Source = "regrade_adoption",
                CreatedAt = now,
                SupersedesRevisionId = currentSource.Id,
            };
            db.ResultRevisions.Add(proposal);
            result.CurrentRevisionId = proposal.Id;
            result.TranscribedAnswer = observation.Observation.Transcription;
            result.NormalizedAnswer = JapaneseTextNormalizer.NormalizeForComparison(
                observation.Observation.Transcription);
            result.ProposedPointsMilli = observation.ProposedPointsMilli;
            result.Outcome = observation.ProposedOutcome;
            result.Method = "ai_adjudication";
            result.ConfidenceBasisPoints =
                observation.ProviderConfidenceBasisPoints;
            result.KanjiCheck =
                observation.Observation.ScriptObservationUncertain
                    ? "uncertain"
                    : "not_applicable";
            result.ReasonCode = reasonCode;
            result.Explanation = observation.BoundedExplanation;
            result.ModelResponseItemHash = observation.CanonicalItemHash;
            result.ReviewRequired = true;
            result.ReviewStatus = "pending";

            var run = result.GradingRun;
            run.EarnedPointsMilli = run.QuestionResults.Aggregate(
                0L,
                (total, item) => checked(
                    total
                    + (item.Id == result.Id
                        ? proposal.AwardedPointsMilli
                        : CurrentAwardedPoints(item))));
            run.ResultSourceRevision = checked(run.ResultSourceRevision + 1);
            run.State = "needs_grade_review";
            run.Submission.State = "needs_grade_review";
            CompleteJob(job, now);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.adjudication_proposed",
                result.Id,
                reasonCode);
            AddOutbox(
                db,
                now,
                claim.CorrelationId,
                result,
                run);
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_request_missing");
            request.State = "invalid_output";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.ErrorCode = errorCode;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(db, claim, response, now);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.adjudication_response_rejected",
                claim.QuestionResultId,
                errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private Task RecordProviderFailureAsync(
        PreparedClaim claim,
        AiProviderException exception,
        CancellationToken cancellationToken)
    {
        if (AiProviderRuntime.IsAmbiguousDispatch(exception))
        {
            return RecordAmbiguousDispatchAsync(
                claim,
                "ai_adjudication_dispatch_outcome_unknown",
                exception.SafeErrorCode,
                cancellationToken);
        }

        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_request_missing");
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
                    SettleReservationConservatively(db, request.Id, now);
                }
                else
                {
                    ReleaseReservation(db, request.Id, now);
                }

                BlockJob(job, now, exception.SafeErrorCode);
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
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
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_adjudication_request_missing");
            request.State = "failed";
            request.PossibleDuplicate = true;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = BoundedSafeDetail(safeDetail);
            request.CompletedAt = now;
            request.UpdatedAt = now;
            SettleReservationConservatively(db, request.Id, now);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "grading.adjudication_dispatch_ambiguous",
                claim.QuestionResultId,
                errorCode);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
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
                    request.ErrorCode =
                        "ai_adjudication_dispatch_outcome_unknown";
                    request.SafeErrorDetail = BoundedSafeDetail(errorCode);
                    request.CompletedAt = now;
                    request.UpdatedAt = now;
                    SettleReservationConservatively(db, request.Id, now);
                }

                BlockJob(
                    job,
                    now,
                    "ai_adjudication_dispatch_outcome_unknown");
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

    private static void ValidateProfile(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle)
    {
        if (profile.TaskType != AiTaskTypes.Adjudication
            || !profile.Active
            || !AiTaskProfileRuntimePolicy.IsReadyApprovalState(
                profile.ApprovalState)
            || (profile.ApprovalState is
                    "pilot_approved" or "production_approved"
                && string.IsNullOrWhiteSpace(profile.AccuracyEvaluationId))
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
            || profile.PromptVersion != bundle.PromptVersion
            || profile.SchemaVersion != bundle.SchemaVersion
            || profile.PromptContentHash != bundle.ContentHash
            || profile.ThinkingLevel != "minimal"
            || profile.ProcessingStrategy is not (
                "queued_standard" or "expedite_standard"))
        {
            throw Blocked("ai_adjudication_profile_not_approved");
        }
    }

    private static void ValidateResult(
        QuestionResultEntity result,
        AdjudicationPayload payload)
    {
        var submission = result.GradingRun.Submission;
        var sourceRevision = result.Revisions.SingleOrDefault(
            item => item.Id == payload.SourceRevisionId);
        if (result.GradingRunId != payload.GradingRunId
            || result.GradingRun.SubmissionId != payload.SubmissionId
            || submission.CurrentGradingRunId != result.GradingRunId
            || submission.FinalizedAt is not null
            || submission.VoidedAt is not null
            || submission.ScanPayloadState != "scan_available"
            || submission.State != "needs_grade_review"
            || result.GradingRun.State != "needs_grade_review"
            || !result.ReviewRequired
            || result.ReviewStatus != "pending"
            || sourceRevision is null
            || result.CurrentRevisionId != sourceRevision.Id
            || sourceRevision.Source is not ("initial" or "regrade_adoption")
            || !result.Question.TeacherVerified
            || result.Question.MaxPointsMilli != result.MaximumPointsMilli
            || result.Question.PointIncrementMilli <= 0)
        {
            throw Permanent("ai_adjudication_source_changed");
        }
    }

    private static void ValidatePage(
        QuestionResultEntity result,
        SubmissionPageEntity page)
    {
        if (page.SubmissionId != result.GradingRun.SubmissionId
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
            || page.NormalizedFileReference.FileObject.Bytes <= 0)
        {
            throw Permanent("ai_adjudication_page_disclosure_invalid");
        }
    }

    private static QuestionSnapshot ToQuestionSnapshot(
        QuestionEntity question,
        TemplateVersionEntity version)
    {
        return new QuestionSnapshot(
            question.Id,
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
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => item.AnswerText)
                .ToArray(),
            QuestionEntityDomainMapper.Map(question, version));
    }

    private static ArtifactSnapshot ToArtifactSnapshot(
        SubmissionPageEntity page,
        string inputManifestHash)
    {
        var fileObject = page.NormalizedFileReference.FileObject;
        return new ArtifactSnapshot(
            page.Id,
            page.PageNumber,
            inputManifestHash,
            fileObject.VerifiedMime,
            fileObject.Sha256,
            fileObject.Bytes,
            fileObject.Extension,
            page.WidthPixels,
            page.HeightPixels,
            new ContentObjectLocator(
                ContentStorageClass.ManagedScanDerived,
                fileObject.Sha256,
                fileObject.Bytes,
                fileObject.Extension));
    }

    private static string ComputeInputManifestHash(
        QuestionResultEntity result,
        ResultRevisionEntity sourceRevision,
        AiTaskProfileEntity profile,
        AiPromptBundle bundle,
        IReadOnlyCollection<ArtifactSnapshot> artifacts)
    {
        var canonical = new StringBuilder();
        AppendManifest(canonical, "pipeline", PipelineVersion);
        AppendManifest(canonical, "submission", result.GradingRun.SubmissionId);
        AppendManifest(canonical, "run", result.GradingRunId);
        AppendManifest(canonical, "result", result.Id);
        AppendManifest(canonical, "source-revision", sourceRevision.Id);
        AppendManifest(
            canonical,
            "source-revision-number",
            sourceRevision.RevisionNumber.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(canonical, "question", result.QuestionId);
        AppendManifest(
            canonical,
            "question-revision",
            result.Question.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        foreach (var answer in result.Question.AcceptedAnswers
                     .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifest(canonical, "accepted-answer", answer.Id);
            AppendManifest(
                canonical,
                "accepted-answer-revision",
                answer.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(canonical, "accepted-answer-text", answer.AnswerText);
        }

        foreach (var artifact in artifacts.OrderBy(item => item.PageNumber))
        {
            AppendManifest(canonical, "artifact", artifact.Id);
            AppendManifest(
                canonical,
                "artifact-input",
                artifact.InputManifestHash);
            AppendManifest(canonical, "artifact-sha256", artifact.Sha256);
        }
        AppendManifest(canonical, "profile", profile.Id);
        AppendManifest(
            canonical,
            "profile-revision",
            profile.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(canonical, "prompt", bundle.ContentHash);
        return Sha256(canonical.ToString());
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
        QuestionSnapshot question,
        IReadOnlyCollection<ArtifactSnapshot> artifacts)
    {
        return
            """
            The attached images are all complete pages from the student's test
            for an independent second assessment. Locate the requested answer using the
            printed question label and question text below. Do not infer or return
            student identity. Do not assume the first grader was correct; it is
            intentionally omitted. Transcribe exactly, preserving Japanese script,
            and grade only against the teacher-supplied rubric. Return this one
            question either once in results or once in missing_question_ids. When
            requires_complete_answer is true, award either zero or the full
            maximum; do not turn genuine unreadable or ambiguous evidence into an
            incorrect result. When answer_order_insensitive is true, compare the
            complete multiset separated by Japanese/ASCII commas, slashes,
            semicolons, middle dots, or line breaks. Duplicate counts matter and
            no component may be omitted.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = requestKey,
                media = artifacts
                    .OrderBy(artifact => artifact.PageNumber)
                    .Select((artifact, mediaIndex) => new
                    {
                        media_index = mediaIndex,
                        page_number = artifact.PageNumber,
                        artifact_id = artifact.Id,
                    }),
                questions =
                    new[]
                    {
                        new
                        {
                            question_id = question.Id,
                            question.DisplayLabel,
                            question.QuestionText,
                            question.QuestionType,
                            question.GradingMode,
                            maximum_points_milli =
                                question.MaximumPointsMilli,
                            point_increment_milli =
                                question.PointIncrementMilli,
                            allow_non_kanji = question.AllowNonKanji,
                            requires_complete_answer =
                                question.RequiresCompleteAnswer,
                            answer_order_insensitive =
                                question.AnswerOrderInsensitive,
                            rubric_text = question.RubricText,
                            accepted_answers = question.AcceptedAnswers,
                        },
                    },
            });
    }

    private static AiRequestEntity CreateRequest(
        DateTimeOffset now,
        QuestionResultEntity result,
        ResultRevisionEntity sourceRevision,
        AiTaskProfileEntity profile,
        string inputManifestHash)
    {
        var id = UlidId.New(now);
        return new AiRequestEntity
        {
            Id = id,
            RequestKey = $"adjudicate_{id}",
            AiTaskProfileId = profile.Id,
            TaskProfileRevision = profile.Revision,
            Purpose = AiTaskTypes.Adjudication,
            EntityType = "questionResult",
            EntityId = result.Id,
            EntityRevision = sourceRevision.RevisionNumber,
            InputManifestHash = inputManifestHash,
            AttemptNumber = 1,
            State = "prepared",
            CreatedAt = now,
            UpdatedAt = now,
        };
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
        long imageTokens = 0;
        foreach (var artifact in artifacts)
        {
            var horizontalTiles = Math.Max(
                1,
                (artifact.WidthPixels + 767L) / 768L);
            var verticalTiles = Math.Max(
                1,
                (artifact.HeightPixels + 767L) / 768L);
            imageTokens = checked(
                imageTokens
                + horizontalTiles
                * verticalTiles
                * _options.EstimatedImageTokensPerTile);
        }
        return CalculateCost(
            checked(textTokens + imageTokens),
            maxOutputTokens,
            0,
            pricing.InputUsdMicrosPerMillionTokens,
            pricing.OutputUsdMicrosPerMillionTokens,
            pricing.ThinkingUsdMicrosPerMillionTokens);
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
            throw Blocked("ai_adjudication_budget_timezone_invalid");
        }
        catch (InvalidTimeZoneException)
        {
            throw Blocked("ai_adjudication_budget_timezone_invalid");
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

    private static long AddUsageAndSettleReservation(
        OokiGraderDbContext db,
        PreparedClaim claim,
        AiProviderResponse response,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations
            .SingleOrDefault(item => item.AiRequestId == claim.RequestId)
            ?? throw Permanent("ai_adjudication_reservation_missing");
        var actualUsdMicros = AiProviderRuntime.ResolveActualUsdMicros(
            response.Usage,
            reservation.ReservedUsdMicros,
            claim.Connection.Provider != AiProviders.GeminiDirect
                || claim.Pricing is null
                ? null
                : () => CalculateActualCost(
                    claim.Pricing,
                    response.Usage));
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
            EstimatedJpyMicros = ConvertUsdToJpy(
                actualUsdMicros,
                claim.UsdToJpyMicros),
            ProviderRequestId = response.ProviderResponseId,
            MeasuredAt = now,
        });
        reservation.ActualUsdMicros = actualUsdMicros;
        reservation.State = "settled";
        reservation.SettledAt = now;
        return actualUsdMicros;
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
            throw Blocked("ai_adjudication_cost_overflow");
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
            throw Blocked("ai_adjudication_cost_overflow");
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

    private static long CurrentAwardedPoints(QuestionResultEntity result)
    {
        var revision = result.Revisions.SingleOrDefault(
            item => item.Id == result.CurrentRevisionId);
        return revision?.AwardedPointsMilli ?? result.ProposedPointsMilli;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is <= 0 or > int.MaxValue)
        {
            throw Permanent("ai_adjudication_page_size_invalid");
        }

        using var destination = new MemoryStream(checked((int)expectedBytes));
        await source.CopyToAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        if (destination.Length != expectedBytes)
        {
            throw Permanent("ai_adjudication_page_size_mismatch");
        }

        return destination.ToArray();
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("ai_adjudication_job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.LeaseExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw Permanent("ai_adjudication_job_lease_lost");
        }

        return job;
    }

    private static void MarkBudgetBlocked(
        AiRequestEntity request,
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        request.State = "budget_blocked";
        request.ErrorCode = errorCode;
        request.UpdatedAt = now;
        BlockJob(job, now, errorCode);
    }

    private static void MarkAmbiguousRecovery(
        AiRequestEntity request,
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        request.State = "failed";
        request.PossibleDuplicate = true;
        request.ErrorCode = "ai_adjudication_dispatch_outcome_unknown";
        request.SafeErrorDetail = "recovered_dispatching_request";
        request.CompletedAt = now;
        request.UpdatedAt = now;
        BlockJob(job, now, request.ErrorCode);
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
        string resultId,
        string reasonCode)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = eventType,
            ObjectType = "questionResult",
            ObjectId = resultId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
        });
    }

    private static void AddOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        QuestionResultEntity result,
        GradingRunEntity run)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "submission",
            AggregateId = run.SubmissionId,
            EventType = "grading.adjudicationProposed",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId = run.SubmissionId,
                gradingRunId = run.Id,
                questionResultId = result.Id,
                resultSourceRevision = run.ResultSourceRevision,
                reviewStatus = result.ReviewStatus,
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
        _ => throw Blocked("ai_adjudication_media_resolution_invalid"),
    };

    private static PricingSnapshot ToPricingSnapshot(
        PricingSnapshotEntity entity) =>
        new(
            entity.Id,
            entity.InputUsdMicrosPerMillionTokens,
            entity.OutputUsdMicrosPerMillionTokens,
            entity.ThinkingUsdMicrosPerMillionTokens);

    private static AdjudicationPayload DeserializePayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 20_000)
        {
            throw Permanent("ai_adjudication_payload_invalid");
        }

        try
        {
            return JsonSerializer.Deserialize<AdjudicationPayload>(
                    json,
                    PayloadSerializerOptions)
                ?? throw Permanent("ai_adjudication_payload_invalid");
        }
        catch (JsonException)
        {
            throw Permanent("ai_adjudication_payload_invalid");
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f');

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string BoundedErrorCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ai_adjudication_response_invalid";
        }

        return value.Length <= 200
            ? value
            : value[..200];
    }

    private static string BoundedSafeDetail(string value) =>
        value.Length <= 2_000 ? value : value[..2_000];

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
        EventId = 1351,
        Level = LogLevel.Error,
        Message =
            "AI adjudication job {JobId} failed with {ErrorCode} " +
            "({ExceptionType}).")]
    private partial void LogJobFailure(
        string jobId,
        string errorCode,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        string? CorrelationId);

    private sealed record AdjudicationPayload(
        string SubmissionId,
        string GradingRunId,
        string QuestionResultId,
        string SourceRevisionId);

    private sealed record PreparedClaim(
        string JobId,
        string? CorrelationId,
        string SubmissionId,
        string GradingRunId,
        string QuestionResultId,
        string SourceRevisionId,
        int SourceRevisionNumber,
        string TaskProfileId,
        long TaskProfileRevision,
        string RequestId,
        string RequestKey,
        string InputManifestHash,
        int MaxOutputTokens,
        string MediaResolution,
        string SecretReference,
        AiConnectionSettings Connection,
        AiPromptBundle Bundle,
        QuestionSnapshot Question,
        IReadOnlyList<ArtifactSnapshot> Artifacts,
        PricingSnapshot? Pricing,
        long UsdToJpyMicros);

    private sealed record QuestionSnapshot(
        string Id,
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
        int PageNumber,
        string InputManifestHash,
        string MimeType,
        string Sha256,
        long Bytes,
        string Extension,
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
        FailureDisposition disposition) : Exception(errorCode)
    {
        public string ErrorCode { get; } = errorCode;

        public FailureDisposition Disposition { get; } = disposition;
    }
}
