using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identity;
using OokiGrader.Application.Identifiers;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Jobs;

public sealed record AiNameTranscriptionJobWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public int MaximumMediaBytes { get; init; } = 8 * 1024 * 1024;
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
                nameof(AiNameTranscriptionJobWorkerOptions),
                "One or more AI name-transcription worker options are invalid.");
        }
    }
}

internal sealed record NameTranscriptionEvidence
{
    [JsonPropertyName("visibleName")]
    public string? VisibleName { get; init; }

    [JsonPropertyName("visibleStudentNumber")]
    public string? VisibleStudentNumber { get; init; }

    [JsonPropertyName("legibility")]
    public string Legibility { get; init; } = string.Empty;

    [JsonPropertyName("providerConfidenceBasisPoints")]
    public int ProviderConfidenceBasisPoints { get; init; }

    [JsonPropertyName("unexpectedContent")]
    public bool UnexpectedContent { get; init; }
}

internal sealed record NameCandidateEvidence
{
    [JsonPropertyName("studentId")]
    public string StudentId { get; init; } = string.Empty;

    [JsonPropertyName("studentNumber")]
    public string StudentNumber { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("kana")]
    public string? Kana { get; init; }

    [JsonPropertyName("classLabel")]
    public string? ClassLabel { get; init; }

    [JsonPropertyName("rankScore")]
    public int RankScore { get; init; }

    [JsonPropertyName("expected")]
    public bool Expected { get; init; }

    [JsonPropertyName("studentNumberConflict")]
    public bool StudentNumberConflict { get; init; }

    [JsonPropertyName("nameSimilarityBasisPoints")]
    public int NameSimilarityBasisPoints { get; init; }

    [JsonPropertyName("evidence")]
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

internal sealed record NameAssignmentEvidence
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "name_assignment_evidence_v1";

    [JsonPropertyName("pipelineVersion")]
    public string PipelineVersion { get; init; } = string.Empty;

    [JsonPropertyName("aiRequestId")]
    public string AiRequestId { get; init; } = string.Empty;

    [JsonPropertyName("inputManifestHash")]
    public string InputManifestHash { get; init; } = string.Empty;

    [JsonPropertyName("transcription")]
    public NameTranscriptionEvidence Transcription { get; init; } = new();

    [JsonPropertyName("matchingPolicyVersion")]
    public string MatchingPolicyVersion { get; init; } = string.Empty;

    [JsonPropertyName("disposition")]
    public string Disposition { get; init; } = "needs_review";

    [JsonPropertyName("normalizedVisibleName")]
    public string? NormalizedVisibleName { get; init; }

    [JsonPropertyName("normalizedVisibleStudentNumber")]
    public string? NormalizedVisibleStudentNumber { get; init; }

    [JsonPropertyName("firstSecondMargin")]
    public int? FirstSecondMargin { get; init; }

    [JsonPropertyName("automaticAssignmentEnabled")]
    public bool AutomaticAssignmentEnabled { get; init; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<NameCandidateEvidence> Candidates { get; init; } = [];
}

/// <summary>
/// Sends complete normalized test pages to the approved direct-Gemini name
/// profile. The returned text is matched to the roster locally and is always
/// presented for teacher review; this worker never assigns a student.
/// </summary>
public sealed partial class AiNameTranscriptionJobWorker : BackgroundService
{
    public const string JobType = "gemini_name_transcription";
    public const string ModelId = AiProviderRuntime.GeminiModel;
    public const string PipelineVersion = "gemini-name-transcription-full-page-v2";

    private const int JobSchemaVersion = 1;
    private const int MaximumStoredResponseCharacters = 64_000;
    private const int MaximumEvidenceCharacters = 16_000;
    private const int MaximumRosterSize = 50_000;
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };
    private static readonly JsonSerializerOptions EvidenceSerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IContentStore _contentStore;
    private readonly IAiProviderClientResolver _providerResolver;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;
    private readonly IAiPromptBundleCatalog _promptCatalog;
    private readonly IAiSecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AiNameTranscriptionJobWorker> _logger;
    private readonly AiNameTranscriptionJobWorkerOptions _options;
    private readonly string _workerId = $"gemini-name-{Guid.NewGuid():N}";

    public AiNameTranscriptionJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IAiProviderClient providerClient,
        IAiPromptBundleCatalog promptCatalog,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        IOptions<AiNameTranscriptionJobWorkerOptions> options,
        ILogger<AiNameTranscriptionJobWorker> logger,
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
        await EnsureQueuedJobsAsync(cancellationToken).ConfigureAwait(false);
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

            var media = await LoadMediaAsync(claim, cancellationToken)
                .ConfigureAwait(false);
            using var secret = await _secretStore
                .ReadAsync(
                    new AiSecretReference(claim.SecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
            var providerRequest = CreateProviderRequest(claim, media);
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
                        "ai_dispatch_outcome_unknown",
                        exception.GetType().Name,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }

            ValidatedTranscription transcription;
            string responseJson;
            try
            {
                responseJson = response.StructuredOutput.GetRawText();
                if (responseJson.Length > MaximumStoredResponseCharacters)
                {
                    throw new InvalidDataException("ai_response_too_large");
                }

                transcription = ValidateResponse(
                    response,
                    claim.RequestKey,
                    claim.Connection);
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

            var match = LocalRosterMatcher.Match(
                new IdentityTranscription(
                    transcription.VisibleName,
                    transcription.VisibleStudentNumber,
                    transcription.Legibility,
                    transcription.ProviderConfidenceBasisPoints),
                claim.Roster.Select(ToRosterCandidate).ToArray());
            var evidence = BuildEvidence(
                claim,
                transcription,
                match);
            await PersistSuccessAsync(
                    claim,
                    response,
                    responseJson,
                    evidence,
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
                    exception.Disposition,
                    dispatchCommitted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            const string errorCode = "ai_name_worker_error";
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

    private Task EnsureQueuedJobsAsync(CancellationToken cancellationToken)
    {
        return _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var bundle = _promptCatalog.GetRequired(AiTaskTypes.NameTranscription);
            var profile = await db.AiTaskProfiles
                .AsNoTracking()
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.NameTranscription
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null
                || !_providerFeaturePolicy.IsEnabled(
                    profile.AiConnection.Provider)
                || !IsApprovedProfile(profile, bundle))
            {
                return;
            }

            var eligible = await db.Submissions
                .AsNoTracking()
                .Where(item =>
                    item.State == "needs_name_review"
                    && item.AssignedStudentId == null
                    && item.VoidedAt == null
                    && item.PreprocessingCompletedAt != null
                    && item.PreprocessingManifestHash != null
                    && item.Pages.Any())
                .OrderBy(item => item.UploadCompletedAt)
                .ThenBy(item => item.Id)
                .Take(_options.QueueBatchSize)
                .Select(item => new
                {
                    item.Id,
                    ManifestHash = item.PreprocessingManifestHash!,
                    item.TestSessionId,
                })
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            if (eligible.Length == 0)
            {
                return;
            }

            var keys = eligible
                .Select(item => DeduplicationKey(
                    item.Id,
                    item.ManifestHash,
                    profile.Revision))
                .ToArray();
            var existing = await db.BackgroundJobs
                .AsNoTracking()
                .Where(item => keys.Contains(item.DeduplicationKey))
                .Select(item => item.DeduplicationKey)
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);
            var now = _timeProvider.GetUtcNow();
            foreach (var item in eligible)
            {
                var key = DeduplicationKey(
                    item.Id,
                    item.ManifestHash,
                    profile.Revision);
                if (existingSet.Contains(key))
                {
                    continue;
                }

                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = UlidId.New(now),
                    Type = JobType,
                    SchemaVersion = JobSchemaVersion,
                    DeduplicationKey = key,
                    Priority = 10,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        submissionId = item.Id,
                        manifestHash = item.ManifestHash,
                    }),
                    State = "queued",
                    MaxAttempts = 8,
                    NextAttemptAt = now,
                    CorrelationId = $"name:{item.Id}",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
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
                throw Permanent("ai_name_job_schema_unsupported");
            }

            var payload = DeserializePayload(lease.PayloadJson);
            if (string.IsNullOrWhiteSpace(payload.SubmissionId)
                || !IsSha256(payload.ManifestHash))
            {
                throw Permanent("ai_name_payload_invalid");
            }

            var bundle = _promptCatalog.GetRequired(AiTaskTypes.NameTranscription);
            var profile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.NameTranscription
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null)
            {
                throw Blocked("ai_name_profile_unavailable");
            }

            ValidateProfile(profile, bundle);
            var submission = await db.Submissions
                .Include(item => item.TestSession)
                    .ThenInclude(session => session.RosterMembers)
                        .ThenInclude(member => member.Student)
                            .ThenInclude(student => student.Aliases)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_name_submission_missing");
            ValidateSubmission(submission, payload);

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
            var artifactSnapshots = pages
                .Select(page => ToArtifactSnapshot(
                    page,
                    submission.PreprocessingManifestHash!))
                .ToArray();
            var roster = await LoadRosterAsync(db, submission, token)
                .ConfigureAwait(false);
            var inputManifestHash = ComputeInputManifestHash(
                submission,
                profile,
                bundle,
                artifactSnapshots,
                roster);

            var request = await db.AiRequests
                .SingleOrDefaultAsync(
                    item => item.EntityType == "submission"
                        && item.EntityId == submission.Id
                        && item.InputManifestHash == inputManifestHash
                        && item.TaskProfileRevision == profile.Revision,
                    token)
                .ConfigureAwait(false);
            if (request is not null)
            {
                if (request.PossibleDuplicate
                    || request.State == "dispatching")
                {
                    MarkAmbiguousRecovery(request, job, now);
                    SettleReservationConservatively(db, request.Id, now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (request.State == "succeeded")
                {
                    if (!EvidenceReferencesRequest(
                            submission.AssignmentEvidenceJson,
                            request.Id,
                            inputManifestHash))
                    {
                        throw Permanent("ai_name_result_missing");
                    }

                    CompleteJob(job, now);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                if (request.State is
                    "invalid_output" or "safety_blocked" or "failed" or "cancelled")
                {
                    BlockJob(
                        job,
                        now,
                        request.ErrorCode ?? "ai_name_request_terminal");
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
                submission,
                profile,
                inputManifestHash);
            var instruction = CreateUserInstruction(
                request.RequestKey,
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
            if (budget?.Active == true)
            {
                if (pricing is null)
                {
                    MarkBudgetBlocked(
                        request,
                        job,
                        now,
                        "ai_pricing_snapshot_missing");
                    AddIfDetached(db, request);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                var committed = await GetCommittedSpendAsync(
                        db,
                        usageWindow,
                        request.Id,
                        token)
                    .ConfigureAwait(false);
                if (WouldExceedHardLimit(
                        committed.DailyUsdMicros,
                        reservedUsdMicros,
                        budget.DailyHardUsdMicros)
                    || WouldExceedHardLimit(
                        committed.MonthlyUsdMicros,
                        reservedUsdMicros,
                        budget.MonthlyHardUsdMicros))
                {
                    MarkBudgetBlocked(
                        request,
                        job,
                        now,
                        "ai_budget_hard_limit");
                    AddIfDetached(db, request);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }
            }

            if (request.State == "budget_blocked")
            {
                request.State = "prepared";
                request.ErrorCode = null;
                request.SafeErrorDetail = null;
                request.UpdatedAt = now;
            }

            AddIfDetached(db, request);
            var reservation = await db.AiBudgetReservations
                .SingleOrDefaultAsync(
                    item => item.AiRequestId == request.Id,
                    token)
                .ConfigureAwait(false);
            if (reservation is null)
            {
                db.AiBudgetReservations.Add(new AiBudgetReservationEntity
                {
                    Id = UlidId.New(now),
                    AiRequestId = request.Id,
                    UsageDay = usageWindow.Day,
                    UsageMonth = usageWindow.Month,
                    ReservedUsdMicros = reservedUsdMicros,
                    ActualUsdMicros = 0,
                    State = "reserved",
                    CreatedAt = now,
                });
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
                throw Permanent("ai_name_budget_reservation_state_invalid");
            }

            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 2_000);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new PreparedClaim(
                lease.Id,
                lease.CorrelationId,
                submission.Id,
                submission.Revision,
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
                    TimeSpan.FromSeconds(
                        profile.AiConnection.TimeoutSeconds)),
                bundle,
                artifactSnapshots,
                roster,
                pricing is null ? null : ToPricingSnapshot(pricing),
                budget?.UsdToJpyMicros ?? 150_000_000);
        }, cancellationToken);
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
            AiTaskTypes.NameTranscription,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            claim.Bundle.SystemInstruction,
            CreateUserInstruction(claim.RequestKey, claim.Artifacts),
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
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(item => item.Id == claim.RequestId, token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_name_request_missing");
            if (request.PossibleDuplicate
                || request.State is not ("prepared" or "retry_waiting")
                || request.DispatchAttempt >= 8)
            {
                throw Permanent("ai_name_dispatch_state_invalid");
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
        NameAssignmentEvidence evidence,
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
                ?? throw Permanent("ai_name_request_missing");
            if (request.State != "dispatching"
                || request.PossibleDuplicate)
            {
                throw Permanent("ai_name_completion_state_invalid");
            }

            var submission = await db.Submissions
                .SingleOrDefaultAsync(
                    item => item.Id == claim.SubmissionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("ai_name_submission_missing");
            if (submission.Revision != claim.SubmissionRevision
                || submission.State != "needs_name_review"
                || submission.AssignedStudentId is not null
                || submission.VoidedAt is not null)
            {
                request.State = "cancelled";
                request.ProviderResponseId = response.ProviderResponseId;
                request.ActualModel = response.ActualModel;
                request.FinishReason = response.FinishReason;
                request.AcceptedResponseHash = Sha256(responseJson);
                request.ErrorCode = "ai_name_submission_changed";
                request.CompletedAt = now;
                request.UpdatedAt = now;
                AddUsageAndSettleReservation(db, claim, response, now);
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            var evidenceJson = JsonSerializer.Serialize(
                evidence,
                EvidenceSerializerOptions);
            if (evidenceJson.Length > MaximumEvidenceCharacters)
            {
                throw Permanent("ai_name_evidence_too_large");
            }

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

            submission.AssignedStudentId = null;
            submission.AssignmentMethod = "none";
            submission.AssignmentConfidenceBasisPoints = null;
            submission.AssignmentPolicyVersion =
                LocalRosterMatcher.PolicyVersion;
            submission.AssignmentEvidenceJson = evidenceJson;
            submission.State = "needs_name_review";
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "submission.name_candidates_created",
                submission.Id,
                evidence.Disposition);
            AddOutbox(
                db,
                now,
                claim.CorrelationId,
                submission.Id,
                evidence.Candidates.Count);
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
                ?? throw Permanent("ai_name_request_missing");
            request.State = "invalid_output";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(db, claim, response, now);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "submission.name_response_rejected",
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
        if (AiProviderRuntime.IsAmbiguousDispatch(exception))
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
                ?? throw Permanent("ai_name_request_missing");
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
                    SettleReservationConservatively(
                        db,
                        claim.RequestId,
                        now);
                }
                else
                {
                    ReleaseReservation(db, claim.RequestId, now);
                }

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
                ?? throw Permanent("ai_name_request_missing");
            request.State = "failed";
            request.PossibleDuplicate = true;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = BoundedSafeDetail(safeDetail);
            request.CompletedAt = now;
            request.UpdatedAt = now;
            SettleReservationConservatively(db, claim.RequestId, now);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "submission.name_dispatch_ambiguous",
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
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == jobId, token)
                .ConfigureAwait(false);
            if (job is null
                || job.State != "leased"
                || job.LeaseOwner != _workerId)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
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
                if (request is not null
                    && request.State is "prepared" or "retry_waiting")
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

    private static ValidatedTranscription ValidateResponse(
        AiProviderResponse response,
        string expectedRequestKey,
        AiConnectionSettings connection)
    {
        if (!AiResponseMetadataValidator.IsAccepted(
                response,
                connection.Provider,
                connection.ModelId))
        {
            throw new InvalidDataException("ai_name_response_metadata_invalid");
        }

        var root = response.StructuredOutput;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("ai_name_response_schema_invalid");
        }

        var expectedProperties = new HashSet<string>(
            [
                "schema_version",
                "request_key",
                "transcribed_name",
                "transcribed_student_number",
                "legibility",
                "confidence",
                "unexpected_content",
            ],
            StringComparer.Ordinal);
        var properties = root.EnumerateObject().ToArray();
        if (properties.Length != expectedProperties.Count
            || properties.Any(item => !expectedProperties.Contains(item.Name)))
        {
            throw new InvalidDataException("ai_name_response_schema_invalid");
        }

        if (!TryRequiredString(root, "schema_version", out var schemaVersion)
            || schemaVersion != "name_transcribe_v1"
            || !TryRequiredString(root, "request_key", out var requestKey)
            || !string.Equals(
                requestKey,
                expectedRequestKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("ai_name_response_identity_invalid");
        }

        var name = ReadNullableBoundedString(
            root,
            "transcribed_name",
            maximumLength: 400);
        var studentNumber = ReadNullableBoundedString(
            root,
            "transcribed_student_number",
            maximumLength: 200);
        if (!TryRequiredString(root, "legibility", out var legibility)
            || legibility is not (
                "clear" or "ambiguous" or "unreadable" or "blank" or "cropped"))
        {
            throw new InvalidDataException("ai_name_legibility_invalid");
        }

        if (!root.TryGetProperty("confidence", out var confidenceElement)
            || confidenceElement.ValueKind != JsonValueKind.Number
            || !confidenceElement.TryGetDouble(out var confidence)
            || !double.IsFinite(confidence)
            || confidence is < 0 or > 1)
        {
            throw new InvalidDataException("ai_name_confidence_invalid");
        }

        if (!root.TryGetProperty(
                "unexpected_content",
                out var unexpectedElement)
            || unexpectedElement.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException("ai_name_response_schema_invalid");
        }

        if ((legibility is "blank" or "unreadable")
            && (name is not null || studentNumber is not null))
        {
            throw new InvalidDataException("ai_name_legibility_contradiction");
        }

        if (legibility == "clear"
            && name is null
            && studentNumber is null)
        {
            throw new InvalidDataException("ai_name_legibility_contradiction");
        }

        return new ValidatedTranscription(
            name,
            studentNumber,
            legibility,
            Math.Clamp(
                (int)Math.Round(
                    confidence * 10_000,
                    MidpointRounding.AwayFromZero),
                0,
                10_000),
            unexpectedElement.GetBoolean());
    }

    private static NameAssignmentEvidence BuildEvidence(
        PreparedClaim claim,
        ValidatedTranscription transcription,
        IdentityMatchResult match)
    {
        var rosterById = claim.Roster.ToDictionary(
            item => item.StudentId,
            StringComparer.Ordinal);
        var candidates = match.Candidates.Select(candidate =>
        {
            if (!rosterById.TryGetValue(candidate.StudentId, out var roster))
            {
                throw Permanent("ai_name_roster_candidate_missing");
            }

            return new NameCandidateEvidence
            {
                StudentId = candidate.StudentId,
                StudentNumber = roster.StudentNumber,
                DisplayName = roster.DisplayName,
                Kana = JoinKana(roster.FamilyNameKana, roster.GivenNameKana),
                ClassLabel = roster.ClassLabel,
                RankScore = candidate.RankScore,
                Expected = candidate.Expected,
                StudentNumberConflict = candidate.StudentNumberConflict,
                NameSimilarityBasisPoints =
                    candidate.NameSimilarityBasisPoints,
                Evidence = candidate.Evidence,
            };
        }).ToArray();

        return new NameAssignmentEvidence
        {
            PipelineVersion = PipelineVersion,
            AiRequestId = claim.RequestId,
            InputManifestHash = claim.InputManifestHash,
            Transcription = new NameTranscriptionEvidence
            {
                VisibleName = transcription.VisibleName,
                VisibleStudentNumber =
                    transcription.VisibleStudentNumber,
                Legibility = transcription.Legibility,
                ProviderConfidenceBasisPoints =
                    transcription.ProviderConfidenceBasisPoints,
                UnexpectedContent = transcription.UnexpectedContent,
            },
            MatchingPolicyVersion = match.PolicyVersion,
            Disposition = match.Disposition,
            NormalizedVisibleName = match.NormalizedVisibleName,
            NormalizedVisibleStudentNumber =
                match.NormalizedVisibleStudentNumber,
            FirstSecondMargin = match.FirstSecondMargin,
            AutomaticAssignmentEnabled = false,
            Candidates = candidates,
        };
    }

    private static RosterIdentityCandidate ToRosterCandidate(
        RosterSnapshot item)
    {
        return new RosterIdentityCandidate(
            item.StudentId,
            item.StudentNumber,
            item.FamilyName,
            item.GivenName,
            item.DisplayName,
            item.FamilyNameKana,
            item.GivenNameKana,
            item.Expected,
            item.Aliases.Select(alias =>
                new RosterIdentityAlias(
                    alias.Value,
                    alias.RecognitionEnabled)).ToArray());
    }

    private static async Task<IReadOnlyList<RosterSnapshot>> LoadRosterAsync(
        OokiGraderDbContext db,
        SubmissionEntity submission,
        CancellationToken cancellationToken)
    {
        var expectedByStudent = submission.TestSession.RosterMembers
            .ToDictionary(
                item => item.StudentId,
                item => submission.TestSession.ExpectedRosterEnabled
                    && item.Expected,
                StringComparer.Ordinal);
        var expectedIds = expectedByStudent.Keys.ToArray();
        var students = await db.Students
            .AsNoTracking()
            .Include(item => item.Aliases)
            .Where(item =>
                item.Status == "active"
                || expectedIds.Contains(item.Id))
            .OrderBy(item => item.Id)
            .Take(MaximumRosterSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (students.Length > MaximumRosterSize)
        {
            throw Blocked("ai_name_roster_too_large");
        }

        return students.Select(student => new RosterSnapshot(
            student.Id,
            student.Revision,
            student.StudentNumber,
            student.FamilyName,
            student.GivenName,
            student.DisplayName,
            student.FamilyNameKana,
            student.GivenNameKana,
            student.SchoolClass,
            expectedByStudent.GetValueOrDefault(student.Id),
            student.Aliases
                .OrderBy(alias => alias.Id, StringComparer.Ordinal)
                .Select(alias => new AliasSnapshot(
                    alias.Id,
                    alias.DisplayValue,
                    alias.RecognitionEnabled))
                .ToArray())).ToArray();
    }

    private static string ComputeInputManifestHash(
        SubmissionEntity submission,
        AiTaskProfileEntity profile,
        AiPromptBundle bundle,
        IEnumerable<ArtifactSnapshot> artifacts,
        IEnumerable<RosterSnapshot> roster)
    {
        var canonical = new StringBuilder();
        AppendManifest(canonical, "purpose", AiTaskTypes.NameTranscription);
        AppendManifest(canonical, "submission", submission.Id);
        AppendManifest(
            canonical,
            "preprocessing",
            submission.PreprocessingManifestHash ?? string.Empty);
        AppendManifest(canonical, "profile", profile.Id);
        AppendManifest(
            canonical,
            "profile-revision",
            profile.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(canonical, "prompt", bundle.ContentHash);
        foreach (var artifact in artifacts
                     .OrderBy(item => item.ArtifactType, StringComparer.Ordinal)
                     .ThenBy(item => item.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifest(canonical, "artifact", artifact.Id);
            AppendManifest(canonical, "artifact-type", artifact.ArtifactType);
            AppendManifest(canonical, "artifact-input", artifact.InputManifestHash);
            AppendManifest(canonical, "artifact-sha256", artifact.Sha256);
        }

        foreach (var student in roster.OrderBy(
                     item => item.StudentId,
                     StringComparer.Ordinal))
        {
            AppendManifest(canonical, "student", student.StudentId);
            AppendManifest(
                canonical,
                "student-revision",
                student.Revision.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(canonical, "number", student.StudentNumber);
            AppendManifest(canonical, "family", student.FamilyName);
            AppendManifest(canonical, "given", student.GivenName);
            AppendManifest(canonical, "display", student.DisplayName);
            AppendManifest(
                canonical,
                "family-kana",
                student.FamilyNameKana ?? string.Empty);
            AppendManifest(
                canonical,
                "given-kana",
                student.GivenNameKana ?? string.Empty);
            AppendManifest(canonical, "expected", student.Expected ? "1" : "0");
            foreach (var alias in student.Aliases)
            {
                AppendManifest(canonical, "alias", alias.Id);
                AppendManifest(canonical, "alias-value", alias.Value);
                AppendManifest(
                    canonical,
                    "alias-enabled",
                    alias.RecognitionEnabled ? "1" : "0");
            }
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
        IReadOnlyCollection<ArtifactSnapshot> artifacts)
    {
        var media = artifacts.Select(
            (artifact, index) => new
            {
                media_index = index,
                artifact_type = artifact.ArtifactType,
                artifact.Ordinal,
                artifact.PanelLabel,
            });
        return
            """
            The attached media are every page of one completed Japanese test,
            in page order. Find the printed fields used for the student's name
            and student number and transcribe only characters visibly written
            in those fields. Preserve Japanese script exactly and do not correct
            a spelling to a common name. Do not infer identity, consult a roster,
            or return a student identifier. Ignore all answers while performing
            this identity task. Use null for a field that is not visible. Return
            blank or unreadable instead of guessing.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "name_transcribe_v1",
                request_key = requestKey,
                media,
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
            RequestKey = $"name_{id}",
            AiTaskProfileId = profile.Id,
            TaskProfileRevision = profile.Revision,
            Purpose = AiTaskTypes.NameTranscription,
            EntityType = "submission",
            EntityId = submission.Id,
            EntityRevision = submission.Revision,
            InputManifestHash = inputManifestHash,
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
        return CalculateCost(
            checked(textTokens + imageTokens),
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

    private static long AddUsageAndSettleReservation(
        OokiGraderDbContext db,
        PreparedClaim claim,
        AiProviderResponse response,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations
            .SingleOrDefault(item => item.AiRequestId == claim.RequestId)
            ?? throw Permanent("ai_name_budget_reservation_missing");
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

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("ai_name_job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.LeaseExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw Permanent("ai_name_job_lease_lost");
        }

        return job;
    }

    private static void ValidateProfile(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle)
    {
        if (!IsApprovedProfile(profile, bundle))
        {
            throw Blocked("ai_name_profile_not_approved");
        }
    }

    private static bool IsApprovedProfile(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle)
    {
        return profile.TaskType == AiTaskTypes.NameTranscription
            && profile.Active
            && profile.ApprovalState is
                "pilot_approved" or "production_approved"
            && AiProviderCatalog.IsSupportedProvider(
                profile.AiConnection.Provider)
            && profile.ModelId == profile.AiConnection.ModelId
            && AiProviderCatalog.SupportsImageTasks(
                profile.AiConnection.Provider,
                profile.ModelId)
            && profile.AiConnection.EndpointProfile
                == AiProviderCatalog.GetEndpointProfile(
                    profile.AiConnection.Provider)
            && profile.AiConnection.State == "active"
            && profile.AiConnection.LastCapabilityProbeState == "passed"
            && profile.ConnectionRevision
                == profile.AiConnection.CredentialRevision
            && profile.PromptVersion == bundle.PromptVersion
            && profile.SchemaVersion == bundle.SchemaVersion
            && profile.PromptContentHash == bundle.ContentHash
            && profile.ThinkingLevel == "minimal"
            && profile.ProcessingStrategy is
                "queued_standard" or "expedite_standard";
    }

    private static void ValidateSubmission(
        SubmissionEntity submission,
        NamePayload payload)
    {
        if (submission.AssignedStudentId is not null
            || submission.AssignmentMethod != "none"
            || submission.State != "needs_name_review"
            || submission.VoidedAt is not null
            || submission.ScanPayloadState != "scan_available"
            || submission.PreprocessingCompletedAt is null
            || !IsSha256(submission.PreprocessingManifestHash)
            || !string.Equals(
                submission.PreprocessingManifestHash,
                payload.ManifestHash,
                StringComparison.Ordinal))
        {
            throw Permanent("ai_name_submission_state_invalid");
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
            "full_page",
            page.PageNumber - 1,
            $"PAGE_{page.PageNumber}",
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

    private static string? ReadNullableBoundedString(
        JsonElement root,
        string propertyName,
        int maximumLength)
    {
        if (!root.TryGetProperty(propertyName, out var element))
        {
            throw new InvalidDataException("ai_name_response_schema_invalid");
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("ai_name_response_schema_invalid");
        }

        var value = element.GetString()
            ?? throw new InvalidDataException("ai_name_response_schema_invalid");
        if (value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException("ai_name_transcription_invalid");
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool TryRequiredString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null;
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
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        request.State = "budget_blocked";
        request.ErrorCode = errorCode;
        request.SafeErrorDetail = null;
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
        request.ErrorCode = "ai_dispatch_outcome_unknown";
        request.SafeErrorDetail = "recovered_dispatching_request";
        request.CompletedAt = now;
        request.UpdatedAt = now;
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

    private static void AddOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string submissionId,
        int candidateCount)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "submission",
            AggregateId = submissionId,
            EventType = "submission.identity_candidates_ready",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                submissionId,
                state = "needs_name_review",
                candidateCount,
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static string DeduplicationKey(
        string submissionId,
        string manifestHash,
        long profileRevision)
    {
        return $"submission:{submissionId}:gemini-name:{manifestHash}:r{profileRevision}";
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
        PricingSnapshotEntity entity)
    {
        return new PricingSnapshot(
            entity.Id,
            entity.InputUsdMicrosPerMillionTokens,
            entity.OutputUsdMicrosPerMillionTokens,
            entity.ThinkingUsdMicrosPerMillionTokens);
    }

    private static NamePayload DeserializePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NamePayload>(
                       json,
                       PayloadSerializerOptions)
                   ?? throw Permanent("ai_name_payload_invalid");
        }
        catch (JsonException)
        {
            throw Permanent("ai_name_payload_invalid");
        }
    }

    private static bool EvidenceReferencesRequest(
        string? json,
        string requestId,
        string inputManifestHash)
    {
        if (string.IsNullOrWhiteSpace(json)
            || json.Length > MaximumEvidenceCharacters)
        {
            return false;
        }

        try
        {
            var evidence = JsonSerializer.Deserialize<NameAssignmentEvidence>(
                json,
                EvidenceSerializerOptions);
            return evidence?.SchemaVersion == "name_assignment_evidence_v1"
                && evidence.PipelineVersion == PipelineVersion
                && evidence.AiRequestId == requestId
                && evidence.InputManifestHash == inputManifestHash
                && !evidence.AutomaticAssignmentEnabled;
        }
        catch (JsonException)
        {
            return false;
        }
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
            return "ai_name_response_invalid";
        }

        return value.Length <= 200
            ? value
            : "ai_name_response_invalid";
    }

    private static string BoundedSafeDetail(string value)
    {
        return value.Length <= 2_000
            ? value
            : value[..2_000];
    }

    private static string? JoinKana(string? familyName, string? givenName)
    {
        var values = new[] { familyName, givenName }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return values.Length == 0 ? null : string.Join(' ', values);
    }

    private static JobHandlingException Permanent(string errorCode)
    {
        return new JobHandlingException(
            errorCode,
            FailureDisposition.Permanent);
    }

    private static JobHandlingException Blocked(string errorCode)
    {
        return new JobHandlingException(
            errorCode,
            FailureDisposition.Blocked);
    }

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
        EventId = 1311,
        Level = LogLevel.Warning,
        Message =
            "AI name-transcription job {JobId} failed with {ErrorCode} " +
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

    private sealed record NamePayload(
        string SubmissionId,
        string ManifestHash);

    private sealed record PreparedClaim(
        string JobId,
        string? CorrelationId,
        string SubmissionId,
        long SubmissionRevision,
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
        IReadOnlyList<ArtifactSnapshot> Artifacts,
        IReadOnlyList<RosterSnapshot> Roster,
        PricingSnapshot? Pricing,
        long UsdToJpyMicros);

    private sealed record ArtifactSnapshot(
        string Id,
        string ArtifactType,
        int Ordinal,
        string? PanelLabel,
        string InputManifestHash,
        string MimeType,
        string Sha256,
        long Bytes,
        int WidthPixels,
        int HeightPixels,
        ContentObjectLocator Locator);

    private sealed record RosterSnapshot(
        string StudentId,
        long Revision,
        string StudentNumber,
        string FamilyName,
        string GivenName,
        string DisplayName,
        string? FamilyNameKana,
        string? GivenNameKana,
        string? ClassLabel,
        bool Expected,
        IReadOnlyList<AliasSnapshot> Aliases);

    private sealed record AliasSnapshot(
        string Id,
        string Value,
        bool RecognitionEnabled);

    private sealed record ValidatedTranscription(
        string? VisibleName,
        string? VisibleStudentNumber,
        string Legibility,
        int ProviderConfidenceBasisPoints,
        bool UnexpectedContent);

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
