using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Jobs;

public sealed record TemplateExtractionJobWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumMediaBytes { get; init; } = 12 * 1024 * 1024;
    public int MaximumSources { get; init; } = 16;

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || MaximumMediaBytes is < 1_024 or > 12 * 1024 * 1024
            || MaximumSources is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TemplateExtractionJobWorkerOptions),
                "One or more template-extraction worker options are invalid.");
        }
    }
}

/// <summary>
/// Creates an editable Gemini-assisted grading-key draft. Source reads,
/// preprocessing, secret access, and provider I/O occur outside the serialized
/// SQLite write coordinator.
/// </summary>
public sealed partial class TemplateExtractionJobWorker : BackgroundService
{
    public const string JobType = "gemini_template_extract";
    public const string ModelId = AiProviderRuntime.GeminiModel;
    public const string PipelineVersion =
        "gemini-template-extraction-auto-detail-qc-v10";

    private const int JobSchemaVersion = 1;
    private const int MaximumProviderPasses = 3;
    private const int QualityControlDetailViewCount = 4;
    private const int MaximumStoredResponseCharacters = 1_000_000;
    private const double MetadataApplicationConfidence = 0.85;
    private const string IndependentSlotAuditInstruction =
        """
        INTERNAL QUALITY-CONTROL PASS. Independently inspect the attached page
        pixels again; do not assume that a previous extraction count was right.
        Make one visual sweep for physical writable curricular slots and a
        separate sweep for printed prompts, then match the two inventories.
        Pay special attention to several boxes inside one sentence and repeated
        printed labels. A printed term, furigana, diagram caption, name field,
        score field, or ordinary text without a visible writable boundary is not
        a new answer slot. Conversely, every distinct visible writable boundary
        answering a prompt is one slot. Return the complete schema exactly as in
        the task below, with one object per visually confirmed slot. Do not select
        a count because it is larger or smaller; use only visible page evidence.
        For an authoritative filled slot, zoom into its detail view and copy each
        visible kana or Kanji character exactly; never replace kana with a
        semantically equivalent Kanji spelling. Never splice two alternative
        spellings into one expected_answer. Before returning every question,
        re-read its source_role from the source manifest. A readable answer on
        contains_model_answers or separate_answer_key must be returned as
        provided_model_answer with that exact source_id and page. If it cannot be
        read or matched from those pixels, return unavailable; never substitute
        ai_proposed for an authoritative source.
        """;
    private const string ValidationRecoveryInstruction =
        """
        INTERNAL VALIDATION-RECOVERY PASS. A prior attempt could not be used
        because it did not satisfy the approved structured response contract.
        Do not quote, reconstruct, or rely on any prior response. Start over
        from the attached page pixels and the task instructions below. Return
        one complete response with exactly the required schema and properties.
        Every string required by the schema must be present, non-null, non-empty,
        and within its allowed length. In particular, display_label must be a
        concise non-empty transcription of the visible printed question label;
        never put a student name, class, date, or score there. Re-inventory all
        physical curricular answer slots, keep one question object per slot,
        preserve source/page identity, and mechanically re-resolve answer
        provenance from source_role. Use only page evidence. Do not include any
        commentary outside the structured response.
        """;
    private static readonly JsonSerializerOptions PayloadSerializerOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
        };

    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IContentStore _contentStore;
    private readonly IPreprocessingService _preprocessingService;
    private readonly IAiProviderClientResolver _providerResolver;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;
    private readonly IAiPromptBundleCatalog _promptCatalog;
    private readonly IAiSecretStore _secretStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TemplateExtractionJobWorker> _logger;
    private readonly TemplateExtractionJobWorkerOptions _options;
    private readonly string _workerId = $"gemini-template-{Guid.NewGuid():N}";

    public TemplateExtractionJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IPreprocessingService preprocessingService,
        IAiProviderClient providerClient,
        IAiPromptBundleCatalog promptCatalog,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        IOptions<TemplateExtractionJobWorkerOptions> options,
        ILogger<TemplateExtractionJobWorker> logger,
        IAiProviderClientResolver? providerResolver = null,
        IAiProviderFeaturePolicy? providerFeaturePolicy = null)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _contentStore = contentStore;
        _preprocessingService = preprocessingService;
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
        IReadOnlyList<PreparedSourceMedia>? preparedMedia = null;
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

            preparedMedia = await LoadAndInspectMediaAsync(
                    claim,
                    cancellationToken)
                .ConfigureAwait(false);
            using var secret = await _secretStore
                .ReadAsync(
                    new AiSecretReference(claim.SecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
            var request = CreateProviderRequest(claim, preparedMedia);
            if (!await MarkDispatchingAsync(claim, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
            dispatchCommitted = true;

            var providerResponses = new List<AiProviderResponse>(
                MaximumProviderPasses);
            ExtractionCandidate selected;
            AiProviderResponse? lastResponse = null;
            try
            {
                lastResponse = await _providerResolver
                    .GetRequired(claim.Connection.Provider)
                    .GenerateAsync(
                        claim.Connection,
                        secret.Utf8Bytes,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
                providerResponses.Add(lastResponse);
                var recoveredFromInvalidInitialResponse = false;
                try
                {
                    selected = ValidateCandidate(
                        lastResponse,
                        claim,
                        preparedMedia);
                }
                catch (InvalidDataException)
                {
                    recoveredFromInvalidInitialResponse = true;
                    selected = await RecoverFromInvalidInitialResponseAsync(
                            claim,
                            preparedMedia,
                            secret.Utf8Bytes,
                            providerResponses,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!recoveredFromInvalidInitialResponse
                    && TemplateExtractionResponseValidator
                    .RequiresIndependentSlotAudit(selected.Validated))
                {
                    var auditRequest = CreateProviderRequest(
                        claim,
                        preparedMedia,
                        IndependentSlotAuditInstruction);
                    lastResponse = await _providerResolver
                        .GetRequired(claim.Connection.Provider)
                        .GenerateAsync(
                            claim.Connection,
                            secret.Utf8Bytes,
                            auditRequest,
                            cancellationToken)
                        .ConfigureAwait(false);
                    providerResponses.Add(lastResponse);
                    var audit = ValidateCandidate(
                        lastResponse,
                        claim,
                        preparedMedia);
                    var inventoriesAgree =
                        TemplateExtractionResponseValidator
                            .SlotInventoriesAgree(
                                selected.Validated,
                                audit.Validated);
                    var needsRepair =
                        TemplateExtractionResponseValidator
                            .HasRepairableSlotStructureIssue(
                                selected.Validated)
                        || TemplateExtractionResponseValidator
                            .HasRepairableSlotStructureIssue(
                                audit.Validated)
                        || TemplateExtractionResponseValidator
                            .HasAnswerAuthorityIssue(
                                selected.Validated)
                        || TemplateExtractionResponseValidator
                            .HasAnswerAuthorityIssue(
                                audit.Validated);
                    if (inventoriesAgree && !needsRepair)
                    {
                        selected = PreferCandidate(selected, audit);
                    }
                    else
                    {
                        var reconciliationRequest = CreateProviderRequest(
                            claim,
                            preparedMedia,
                            CreateSlotReconciliationInstruction(
                                selected.Validated,
                                audit.Validated));
                        lastResponse = await _providerResolver
                            .GetRequired(claim.Connection.Provider)
                            .GenerateAsync(
                                claim.Connection,
                                secret.Utf8Bytes,
                                reconciliationRequest,
                                cancellationToken)
                            .ConfigureAwait(false);
                        providerResponses.Add(lastResponse);
                        var reconciliation = ValidateCandidate(
                            lastResponse,
                            claim,
                            preparedMedia);
                        if (!TemplateExtractionResponseValidator
                                .HasRepairableSlotStructureIssue(
                                    reconciliation.Validated)
                            && !TemplateExtractionResponseValidator
                                .HasAnswerAuthorityIssue(
                                    reconciliation.Validated)
                            && TemplateExtractionResponseValidator
                                .SlotInventoriesAgree(
                                    selected.Validated,
                                    reconciliation.Validated))
                        {
                            selected = PreferCandidate(
                                selected,
                                reconciliation);
                        }
                        else if (!TemplateExtractionResponseValidator
                                     .HasRepairableSlotStructureIssue(
                                         reconciliation.Validated)
                                 && !TemplateExtractionResponseValidator
                                     .HasAnswerAuthorityIssue(
                                         reconciliation.Validated)
                                 && TemplateExtractionResponseValidator
                                     .SlotInventoriesAgree(
                                         audit.Validated,
                                         reconciliation.Validated))
                        {
                            selected = PreferCandidate(
                                audit,
                                reconciliation);
                        }
                        else
                        {
                            selected = reconciliation;
                        }
                    }
                }
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
            catch (InvalidDataException exception)
            {
                lastResponse = providerResponses.LastOrDefault()
                    ?? lastResponse;
                if (lastResponse is null)
                {
                    throw;
                }
                await RecordInvalidResponseAsync(
                        claim,
                        AggregateProviderResponses(
                            providerResponses,
                            lastResponse),
                        BoundedErrorCode(exception.Message),
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

            await PersistSuccessAsync(
                    claim,
                    AggregateProviderResponses(
                        providerResponses,
                        selected.Response),
                    selected.ResponseJson,
                    selected.Validated,
                    providerResponses.Count,
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
                exception,
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
            const string errorCode = "template_extract_worker_error";
            LogJobFailure(
                exception,
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
        finally
        {
            ZeroQualityControlDetailViews(preparedMedia);
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
                throw Permanent("template_extract_job_schema_unsupported");
            }

            var payload = DeserializePayload(lease.PayloadJson);
            if (string.IsNullOrWhiteSpace(payload.TemplateVersionId)
                || payload.GenerationRevision <= 0)
            {
                throw Permanent("template_extract_payload_invalid");
            }
            var replaceableMetadataFields =
                NormalizeReplaceableMetadataFields(
                    payload.ReplaceableMetadataFields);

            var version = await db.TemplateVersions
                .Include(item => item.TestTemplate)
                .Include(item => item.Sources)
                .Include(item => item.Questions)
                .SingleOrDefaultAsync(
                    item => item.Id == payload.TemplateVersionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("template_extract_version_missing");
            if (version.State == "draft"
                && version.AiGenerationProvenanceId is not null
                && version.Questions.Count > 0)
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (version.TestTemplate.State == "archived")
            {
                var requests = await db.AiRequests
                    .Where(item => item.EntityType == "template_version"
                        && item.EntityId == version.Id
                        && (item.State == "prepared"
                            || item.State == "budget_blocked"
                            || item.State == "retry_waiting"))
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                foreach (var request in requests)
                {
                    request.State = "cancelled";
                    request.ErrorCode = "template_extract_template_archived";
                    request.SafeErrorDetail = null;
                    request.CompletedAt = now;
                    request.UpdatedAt = now;
                    ReleaseReservation(db, request.Id, now);
                }

                ResetGeneratingVersion(version, now);
                BlockJob(job, now, "template_extract_template_archived");
                AddAudit(
                    db,
                    now,
                    lease.CorrelationId,
                    "template.ai_generation_cancelled",
                    version.Id,
                    "template_extract_template_archived");
                AddStatusOutbox(
                    db,
                    now,
                    lease.CorrelationId,
                    version.Id,
                    "cancelled");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            ValidateVersion(version, payload, _options.MaximumSources);
            var bundle = _promptCatalog.GetRequired(
                AiTaskTypes.TemplateExtraction);
            var profile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleOrDefaultAsync(
                    item => item.TaskType == AiTaskTypes.TemplateExtraction
                        && item.Active,
                    token)
                .ConfigureAwait(false);
            if (profile is null)
            {
                throw Blocked("template_extract_profile_unavailable");
            }

            ValidateProfile(profile, bundle);
            var sourceSnapshots = new List<SourceSnapshot>(
                version.Sources.Count);
            foreach (var source in version.Sources
                         .OrderBy(item => item.Ordinal)
                         .ThenBy(item => item.Id, StringComparer.Ordinal))
            {
                var reference = source.FileReferenceId is null
                    ? null
                    : await db.FileReferences
                        .AsNoTracking()
                        .Include(item => item.FileObject)
                        .SingleOrDefaultAsync(
                            item => item.Id == source.FileReferenceId,
                            token)
                        .ConfigureAwait(false);
                sourceSnapshots.Add(ToSourceSnapshot(source, reference));
            }

            ValidateSources(sourceSnapshots, _options.MaximumMediaBytes);
            var inputManifestHash = ComputeInputManifestHash(
                version,
                profile,
                bundle,
                sourceSnapshots,
                replaceableMetadataFields);
            var requestEntity = await db.AiRequests
                .SingleOrDefaultAsync(
                    item => item.EntityType == "template_version"
                        && item.EntityId == version.Id
                        && item.InputManifestHash == inputManifestHash
                        && item.TaskProfileRevision == profile.Revision,
                    token)
                .ConfigureAwait(false);
            if (requestEntity is not null)
            {
                if (requestEntity.PossibleDuplicate
                    || requestEntity.State == "dispatching")
                {
                    MarkAmbiguousRecovery(
                        requestEntity,
                        version,
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
                    throw Permanent("template_extract_result_missing");
                }

                if (requestEntity.State is
                    "invalid_output" or "safety_blocked" or "failed" or "cancelled")
                {
                    ResetGeneratingVersion(version, now);
                    BlockJob(
                        job,
                        now,
                        requestEntity.ErrorCode
                        ?? "template_extract_request_terminal");
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
            requestEntity ??= CreateRequest(
                now,
                version,
                profile,
                inputManifestHash);
            var instruction = CreateUserInstruction(
                requestEntity.RequestKey,
                version,
                sourceSnapshots,
                replaceableMetadataFields,
                pageCounts: null);
            var reservedUsdMicros = pricing is null
                ? 0
                : EstimateMaximumCost(
                    pricing,
                    profile.MaxOutputTokens,
                    instruction,
                    _options.MaximumMediaBytes,
                    MaximumProviderPasses);
            var usageWindow = await GetUsageWindowAsync(db, now, token)
                .ConfigureAwait(false);

            if (budget?.Active == true)
            {
                if (pricing is null)
                {
                    MarkBudgetBlocked(
                        requestEntity,
                        version,
                        job,
                        now,
                        "ai_pricing_snapshot_missing");
                    AddIfDetached(db, requestEntity);
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                var spend = await GetCommittedSpendAsync(
                        db,
                        usageWindow,
                        requestEntity.Id,
                        token)
                    .ConfigureAwait(false);
                if (WouldExceedHardLimit(
                        spend.DailyUsdMicros,
                        reservedUsdMicros,
                        budget.DailyHardUsdMicros)
                    || WouldExceedHardLimit(
                        spend.MonthlyUsdMicros,
                        reservedUsdMicros,
                        budget.MonthlyHardUsdMicros))
                {
                    MarkBudgetBlocked(
                        requestEntity,
                        version,
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
            return new PreparedClaim(
                lease.Id,
                lease.CorrelationId,
                version.Id,
                version.Revision,
                version.DefaultPointsMilli,
                version.TargetTotalPointsMilli,
                ToMetadataSnapshot(version.TestTemplate),
                replaceableMetadataFields,
                profile.Id,
                profile.Revision,
                requestEntity.Id,
                requestEntity.RequestKey,
                inputManifestHash,
                profile.MaxOutputTokens,
                ToMediaResolution(profile.MediaResolution),
                ToThinkingLevel(profile.ThinkingLevel),
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
                sourceSnapshots,
                pricing is null ? null : ToPricingSnapshot(pricing),
                budget?.UsdToJpyMicros ?? 150_000_000);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PreparedSourceMedia>>
        LoadAndInspectMediaAsync(
            PreparedClaim claim,
            CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedSourceMedia>(claim.Sources.Count);
        var totalSourceBytes = 0L;
        var totalProviderBytes = 0L;
        var totalPages = 0;
        foreach (var source in claim.Sources)
        {
            totalSourceBytes = checked(totalSourceBytes + source.Bytes);
            if (source.Bytes <= 0
                || totalSourceBytes > _options.MaximumMediaBytes)
            {
                throw Permanent("template_extract_sources_too_large");
            }

            await using var content = await _contentStore
                .OpenReadAsync(source.Locator, cancellationToken)
                .ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(
                    content,
                    source.Bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            if (!FixedTimeEquals(actualHash, source.Sha256))
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw Permanent("template_extract_source_hash_mismatch");
            }

            await using var inspectionInput = new MemoryStream(
                bytes,
                writable: false);
            PreprocessingResult inspection;
            try
            {
                inspection = await _preprocessingService
                    .ProcessAsync(
                        inspectionInput,
                        new PreprocessingInput(
                            source.MimeType,
                            source.DisplayName,
                            MaximumPages: 200),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PreprocessingException exception)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw Permanent(
                    BoundedErrorCode(
                        $"template_source_{exception.Code}"));
            }

            totalPages = checked(totalPages + inspection.Pages.Count);
            if (inspection.Pages.Count == 0 || totalPages > 200)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw Permanent("template_extract_page_limit");
            }

            AiMediaPart media;
            if (source.MimeType == "image/tiff")
            {
                foreach (var page in inspection.Pages)
                {
                    var normalized = page.NormalizedPng;
                    var normalizedHash = Convert.ToHexString(
                            SHA256.HashData(normalized.Bytes))
                        .ToLowerInvariant();
                    if (normalized.MimeType != "image/png"
                        || normalized.Bytes.Length == 0
                        || !IsSha256(normalized.Sha256)
                        || !FixedTimeEquals(
                            normalizedHash,
                            normalized.Sha256))
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                        throw Permanent(
                            "template_extract_tiff_normalization_invalid");
                    }
                }

                byte[] providerBytes;
                string providerMimeType;
                string providerHash;
                if (inspection.Pages.Count == 1)
                {
                    var normalized = inspection.Pages[0].NormalizedPng;
                    providerBytes = normalized.Bytes;
                    providerMimeType = normalized.MimeType;
                    providerHash = normalized.Sha256;
                }
                else
                {
                    try
                    {
                        providerBytes = PreprocessedDocumentEncoder.ToPdf(
                            inspection.Pages,
                            cancellationToken);
                    }
                    catch (PreprocessingException exception)
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                        throw Permanent(
                            BoundedErrorCode(
                                $"template_source_{exception.Code}"));
                    }

                    providerMimeType = "application/pdf";
                    providerHash = Convert.ToHexString(
                            SHA256.HashData(providerBytes))
                        .ToLowerInvariant();
                }

                totalProviderBytes = checked(
                    totalProviderBytes + providerBytes.Length);
                if (totalProviderBytes > _options.MaximumMediaBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw Permanent("template_extract_sources_too_large");
                }

                media = new AiMediaPart(
                    providerMimeType,
                    providerBytes,
                    providerHash);
                CryptographicOperations.ZeroMemory(bytes);
            }
            else
            {
                totalProviderBytes = checked(
                    totalProviderBytes + bytes.Length);
                if (totalProviderBytes > _options.MaximumMediaBytes)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    throw Permanent("template_extract_sources_too_large");
                }

                media = new AiMediaPart(
                    source.MimeType,
                    bytes,
                    source.Sha256);
            }

            IReadOnlyList<AiMediaPart> qualityControlDetailViews = [];
            if (claim.Sources.Count == 1
                && inspection.Pages.Count == 1
                && inspection.Pages[0].Height
                    >= QualityControlDetailViewCount
                && IsQualityControlDetailViewMimeType(source.MimeType))
            {
                IReadOnlyList<ImageArtifact> artifacts;
                try
                {
                    artifacts = PreprocessedDocumentEncoder
                        .ToVerticalPngTiles(
                            inspection.Pages[0],
                            QualityControlDetailViewCount,
                            cancellationToken);
                }
                catch (PreprocessingException exception)
                {
                    throw Permanent(BoundedErrorCode(
                        $"template_detail_{exception.Code}"));
                }

                var candidateProviderBytes = totalProviderBytes;
                var detailViews = new List<AiMediaPart>(
                    QualityControlDetailViewCount);
                try
                {
                    foreach (var artifact in artifacts)
                    {
                        var actualArtifactHash = Convert.ToHexString(
                                SHA256.HashData(artifact.Bytes))
                            .ToLowerInvariant();
                        if (artifact.MimeType != "image/png"
                            || artifact.Bytes.Length == 0
                            || artifact.Width != inspection.Pages[0].Width
                            || artifact.Height <= 0
                            || !IsSha256(artifact.Sha256)
                            || !FixedTimeEquals(
                                actualArtifactHash,
                                artifact.Sha256))
                        {
                            throw Permanent(
                                "template_extract_detail_view_invalid");
                        }

                        candidateProviderBytes = checked(
                            candidateProviderBytes + artifact.Bytes.Length);
                        detailViews.Add(new AiMediaPart(
                            "image/png",
                            artifact.Bytes,
                            artifact.Sha256));
                    }

                    if (detailViews.Count != QualityControlDetailViewCount
                        || artifacts.Sum(item => item.Height)
                            != inspection.Pages[0].Height)
                    {
                        throw Permanent(
                            "template_extract_detail_view_invalid");
                    }

                    if (candidateProviderBytes <= _options.MaximumMediaBytes)
                    {
                        totalProviderBytes = candidateProviderBytes;
                        qualityControlDetailViews = detailViews;
                    }
                    else
                    {
                        ZeroImageArtifacts(artifacts);
                    }
                }
                catch
                {
                    ZeroImageArtifacts(artifacts);
                    throw;
                }
            }

            prepared.Add(
                new PreparedSourceMedia(
                    source,
                    inspection.Pages.Count,
                    media,
                    qualityControlDetailViews));
        }

        return prepared;
    }

    private static AiProviderRequest CreateProviderRequest(
        PreparedClaim claim,
        IReadOnlyList<PreparedSourceMedia> media,
        string? qualityControlInstruction = null)
    {
        if (media.Count != claim.Sources.Count)
        {
            throw Permanent("template_extract_source_count_mismatch");
        }

        var pageCounts = media.ToDictionary(
            item => item.Source.Id,
            item => item.PageCount,
            StringComparer.Ordinal);
        var userInstruction = CreateUserInstruction(
            claim.RequestKey,
            claim.DefaultPointsMilli,
            claim.TargetTotalPointsMilli,
            claim.CurrentMetadata,
            claim.ReplaceableMetadataFields,
            claim.Sources,
            pageCounts);
        var providerMedia = media
            .Select(item => item.Media)
            .ToList();
        if (!string.IsNullOrWhiteSpace(qualityControlInstruction))
        {
            var qualityControlPreamble = qualityControlInstruction.Trim();
            if (media.Count == 1
                && media[0].QualityControlDetailViews.Count > 0)
            {
                if (media[0].QualityControlDetailViews.Count
                    != QualityControlDetailViewCount)
                {
                    throw Permanent(
                        "template_extract_detail_view_count_mismatch");
                }

                providerMedia.AddRange(media[0].QualityControlDetailViews);
                qualityControlPreamble += "\n\n"
                    + CreateQualityControlViewManifest(media[0]);
            }

            userInstruction = qualityControlPreamble + "\n\n"
                + userInstruction;
        }

        return new AiProviderRequest(
            claim.RequestKey,
            AiTaskTypes.TemplateExtraction,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            claim.Bundle.SystemInstruction,
            userInstruction,
            claim.Bundle.ResponseJsonSchema,
            providerMedia,
            claim.MaxOutputTokens,
            claim.MediaResolution,
            claim.ThinkingLevel);
    }

    private static string CreateQualityControlViewManifest(
        PreparedSourceMedia media) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            INTERNAL VIEW MANIFEST. The five attached images are alternate
            views of exactly one source page, not five sources or five pages:
            media_index 0 is the complete page for source_id
            {media.Source.Id}, page_number 1; media_index 1, 2, 3, and 4 are
            non-overlapping top-to-bottom quarters of that same source_id and
            page. The detail views exist only to make small writable boundaries
            legible. Match and deduplicate physical slots across the complete
            page and all four quarters. Return every physical curricular slot
            exactly once. Never create extra question objects because the same
            slot appears in both a full-page view and a detail view. source_key,
            source_id, page_number, and answer_source must always identify the
            original source/page from the source manifest; a detail-view
            media_index is never a source id or page number.
            """);

    private async Task<ExtractionCandidate>
        RecoverFromInvalidInitialResponseAsync(
            PreparedClaim claim,
            IReadOnlyList<PreparedSourceMedia> preparedMedia,
            ReadOnlyMemory<byte> credentialUtf8,
            List<AiProviderResponse> providerResponses,
            CancellationToken cancellationToken)
    {
        if (providerResponses.Count != 1)
        {
            throw new InvalidDataException(
                "template_extract_recovery_pass_limit");
        }

        var recoveryRequest = CreateProviderRequest(
            claim,
            preparedMedia,
            ValidationRecoveryInstruction);
        var recoveryResponse = await _providerResolver
            .GetRequired(claim.Connection.Provider)
            .GenerateAsync(
                claim.Connection,
                credentialUtf8,
                recoveryRequest,
                cancellationToken)
            .ConfigureAwait(false);
        providerResponses.Add(recoveryResponse);

        ExtractionCandidate recovery;
        try
        {
            recovery = ValidateCandidate(
                recoveryResponse,
                claim,
                preparedMedia);
        }
        catch (InvalidDataException)
        {
            var finalRecoveryRequest = CreateProviderRequest(
                claim,
                preparedMedia,
                ValidationRecoveryInstruction + "\n\n"
                    + IndependentSlotAuditInstruction);
            var finalRecoveryResponse = await _providerResolver
                .GetRequired(claim.Connection.Provider)
                .GenerateAsync(
                    claim.Connection,
                    credentialUtf8,
                    finalRecoveryRequest,
                    cancellationToken)
                .ConfigureAwait(false);
            providerResponses.Add(finalRecoveryResponse);
            _ = ValidateCandidate(
                finalRecoveryResponse,
                claim,
                preparedMedia);
            throw new InvalidDataException(
                "template_extract_recovery_confirmation_missing");
        }

        var auditRequest = CreateProviderRequest(
            claim,
            preparedMedia,
            IndependentSlotAuditInstruction);
        var auditResponse = await _providerResolver
            .GetRequired(claim.Connection.Provider)
            .GenerateAsync(
                claim.Connection,
                credentialUtf8,
                auditRequest,
                cancellationToken)
            .ConfigureAwait(false);
        providerResponses.Add(auditResponse);
        var audit = ValidateCandidate(
            auditResponse,
            claim,
            preparedMedia);

        var needsRepair = TemplateExtractionResponseValidator
                .HasRepairableSlotStructureIssue(recovery.Validated)
            || TemplateExtractionResponseValidator
                .HasRepairableSlotStructureIssue(audit.Validated)
            || TemplateExtractionResponseValidator
                .HasAnswerAuthorityIssue(recovery.Validated)
            || TemplateExtractionResponseValidator
                .HasAnswerAuthorityIssue(audit.Validated);
        if (needsRepair
            || !TemplateExtractionResponseValidator.SlotInventoriesAgree(
                recovery.Validated,
                audit.Validated))
        {
            throw new InvalidDataException(
                "template_extract_recovery_audit_disagreement");
        }

        return PreferCandidate(recovery, audit);
    }

    private static ExtractionCandidate ValidateCandidate(
        AiProviderResponse response,
        PreparedClaim claim,
        IReadOnlyList<PreparedSourceMedia> preparedMedia)
    {
        if (!AiResponseMetadataValidator.IsAccepted(
                response,
                claim.Connection.Provider,
                claim.Connection.ModelId))
        {
            throw new InvalidDataException(
                "template_extract_response_metadata_invalid");
        }

        var responseJson = response.StructuredOutput.GetRawText();
        if (responseJson.Length > MaximumStoredResponseCharacters)
        {
            throw new InvalidDataException(
                "template_extract_response_too_large");
        }

        var evidence = preparedMedia.ToDictionary(
            item => item.Source.Id,
            item => new TemplateExtractionSourceEvidence(
                item.Source.Id,
                item.Source.Role,
                item.PageCount),
            StringComparer.Ordinal);
        ValidatedTemplateExtraction validated;
        var responseSchemaVersion = response.StructuredOutput
            .TryGetProperty("schema_version", out var schemaElement)
                ? schemaElement.GetString()
                : null;
        if (string.Equals(
                responseSchemaVersion,
                "template_extract_v5",
                StringComparison.Ordinal))
        {
            var suppliedPages = preparedMedia
                .SelectMany(item => Enumerable.Range(1, item.PageCount)
                    .Select(pageNumber => new TemplateExtractionPageManifest(
                        $"{item.Source.Id}:page:{pageNumber}",
                        item.Source.Id,
                        pageNumber)))
                .ToArray();
            var envelope = OrientationGatedTemplateExtractionValidator.Validate(
                response.StructuredOutput,
                claim.RequestKey,
                suppliedPages,
                evidence,
                claim.DefaultPointsMilli,
                claim.TargetTotalPointsMilli);
            if (envelope.Action != TemplateExtractionAction.Extract
                || envelope.Extraction is null)
            {
                // The legacy draft worker cannot safely mutate its already-bound
                // source set. New creation uses TemplateGenerationUnitJobWorker,
                // which owns the bounded local rotation retry.
                throw new InvalidDataException(
                    "template_extract_orientation_requires_batch_flow");
            }

            validated = envelope.Extraction;
        }
        else
        {
            // Kept solely so durable legacy jobs created with the v4 snapshot
            // remain readable/retryable during the additive migration.
            validated = TemplateExtractionResponseValidator.Validate(
                response.StructuredOutput,
                claim.RequestKey,
                evidence,
                claim.DefaultPointsMilli,
                claim.TargetTotalPointsMilli);
        }
        return new ExtractionCandidate(response, responseJson, validated);
    }

    private static ExtractionCandidate PreferCandidate(
        ExtractionCandidate primary,
        ExtractionCandidate audit)
    {
        static (int BlockingIssues, double NegativeConfidence) Quality(
            ValidatedTemplateExtraction candidate)
        {
            var questions = candidate.Pages
                .SelectMany(page => page.Questions)
                .ToArray();
            var blockingIssues = candidate.ReviewIssues.Count(issue =>
                    issue.Blocking)
                + questions.Sum(question => question.ReviewIssues.Count(issue =>
                    issue.Blocking));
            var averageConfidence = questions.Length == 0
                ? 0
                : questions.Average(question => question.Confidence);
            return (blockingIssues, -averageConfidence);
        }

        return Quality(audit.Validated).CompareTo(Quality(primary.Validated)) < 0
            ? audit
            : primary;
    }

    private static string CreateSlotReconciliationInstruction(
        ValidatedTemplateExtraction primary,
        ValidatedTemplateExtraction audit)
    {
        static string Summarize(ValidatedTemplateExtraction candidate) =>
            string.Join(
                "; ",
                candidate.Pages.Select((page, index) =>
                    string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"media-page-{index + 1}: declared="
                        + $"{page.DetectedAnswerSlotCount}, objects="
                        + $"{page.Questions.Count}, separated="
                        + $"{page.Questions.Count(question =>
                            question.AnswerSlotCount == 1)}, embedded="
                        + $"{page.Questions.Count(question =>
                            question.IsEmbeddedFillBlank)}")));

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"""
            INTERNAL FINAL SLOT RECONCILIATION. Two independent extractions of
            the same attached pages disagreed or retained a structural slot
            warning. Candidate A inventory: {Summarize(primary)}. Candidate B
            inventory: {Summarize(audit)}. Neither candidate count is authority.
            Inspect the actual page pixels from scratch in visual reading order.
            First mark only visible writable curricular boundaries; next match
            each boundary to its printed prompt; finally exclude name, class,
            number, date, score, diagram text, furigana, and printed terminology.
            For a sentence with several real writable boxes, return one object per
            box. Do not invent an extra box by splitting a printed compound word.
            Re-resolve answer provenance from the source manifest for every
            object. On contains_model_answers or separate_answer_key, visually
            transcribe a readable supplied answer as provided_model_answer with
            its exact source_id and page. If the supplied answer cannot be read,
            matched, or reconciled from the pixels, return unavailable with a
            warning; never use ai_proposed as a fallback. Do not change an answer
            provenance label without independently checking the visible answer.
            Return one complete corrected schema for the task below. Use only the
            image, not majority vote, maximum count, or minimum count.
            """);
    }

    private static AiProviderResponse AggregateProviderResponses(
        List<AiProviderResponse> responses,
        AiProviderResponse selected)
    {
        if (responses.Count == 0)
        {
            return selected;
        }

        static int? Sum(
            IEnumerable<AiProviderResponse> items,
            Func<AiUsage, int?> selector)
        {
            var values = items
                .Select(item => selector(item.Usage))
                .ToArray();
            return values.Length == 0 || values.Any(value => value is null)
                ? null
                : values.Aggregate(
                    0,
                    checked((total, value) => total + value!.Value));
        }

        static long? SumProviderCost(
            IReadOnlyCollection<AiProviderResponse> items)
        {
            if (items.Any(item =>
                    item.Usage.ProviderCostUsdMicros is null))
            {
                return null;
            }

            return items.Aggregate(
                0L,
                checked((total, item) =>
                    total + item.Usage.ProviderCostUsdMicros!.Value));
        }

        var totalLatencyTicks = responses.Aggregate(
            0L,
            checked((total, item) => total + item.Latency.Ticks));
        var routedProviders = responses
            .Select(item => item.RoutedProvider)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var routedProvider = routedProviders.Length == 1
            && routedProviders[0] is not null
                ? routedProviders[0]
                : null;
        return selected with
        {
            Usage = new AiUsage(
                Sum(responses, usage => usage.PromptTokens),
                Sum(responses, usage => usage.CachedTokens),
                Sum(responses, usage => usage.OutputTokens),
                Sum(responses, usage => usage.ThinkingTokens),
                Sum(responses, usage => usage.TotalTokens),
                SumProviderCost(responses)),
            Latency = TimeSpan.FromTicks(totalLatencyTicks),
            RoutedProvider = routedProvider,
        };
    }

    private Task<bool> MarkDispatchingAsync(
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
                ?? throw Permanent("template_extract_request_missing");
            var version = await db.TemplateVersions
                .Include(item => item.TestTemplate)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.TemplateVersionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("template_extract_version_missing");
            if (version.TestTemplate.State == "archived")
            {
                request.State = "cancelled";
                request.ErrorCode = "template_extract_template_archived";
                request.SafeErrorDetail = null;
                request.CompletedAt = now;
                request.UpdatedAt = now;
                ReleaseReservation(db, request.Id, now);
                ResetGeneratingVersion(version, now);
                BlockJob(job, now, "template_extract_template_archived");
                AddAudit(
                    db,
                    now,
                    claim.CorrelationId,
                    "template.ai_generation_cancelled",
                    version.Id,
                    "template_extract_template_archived");
                AddStatusOutbox(
                    db,
                    now,
                    claim.CorrelationId,
                    version.Id,
                    "cancelled");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return false;
            }

            if (version.State != "generating"
                || version.Revision != claim.VersionRevision
                || request.PossibleDuplicate
                || request.State is not ("prepared" or "retry_waiting")
                || request.DispatchAttempt >= 8)
            {
                throw Permanent("template_extract_dispatch_state_invalid");
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
            return true;
        }, cancellationToken);
    }

    private Task PersistSuccessAsync(
        PreparedClaim claim,
        AiProviderResponse response,
        string responseJson,
        ValidatedTemplateExtraction validated,
        int providerPassCount,
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
                ?? throw Permanent("template_extract_request_missing");
            var version = await db.TemplateVersions
                .Include(item => item.TestTemplate)
                .Include(item => item.Questions)
                .Include(item => item.Sources)
                .SingleOrDefaultAsync(
                    item => item.Id == claim.TemplateVersionId,
                    token)
                .ConfigureAwait(false)
                ?? throw Permanent("template_extract_version_missing");
            if (version.TestTemplate.State == "archived")
            {
                if (request.State != "dispatching"
                    || request.PossibleDuplicate
                    || version.State != "generating")
                {
                    throw Permanent("template_extract_completion_state_invalid");
                }

                request.State = "cancelled";
                request.ProviderResponseId = response.ProviderResponseId;
                request.ActualModel = response.ActualModel;
                request.FinishReason = response.FinishReason;
                request.ErrorCode = "template_extract_template_archived";
                request.SafeErrorDetail = null;
                request.CompletedAt = now;
                request.UpdatedAt = now;
                AddUsageAndSettleReservation(db, claim, response, now);
                ResetGeneratingVersion(version, now);
                BlockJob(job, now, "template_extract_template_archived");
                AddAudit(
                    db,
                    now,
                    claim.CorrelationId,
                    "template.ai_generation_cancelled",
                    version.Id,
                    "template_extract_template_archived");
                AddStatusOutbox(
                    db,
                    now,
                    claim.CorrelationId,
                    version.Id,
                    "cancelled");
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return;
            }

            if (request.State != "dispatching"
                || request.PossibleDuplicate
                || version.State != "generating"
                || version.Revision != claim.VersionRevision
                || version.Questions.Count != 0
                || version.AiGenerationProvenanceId is not null)
            {
                throw Permanent("template_extract_completion_state_invalid");
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

            PersistDraftProposal(
                db,
                version,
                claim,
                validated,
                now);
            var appliedMetadataFields = ApplyInferredMetadata(
                version.TestTemplate,
                validated.Metadata,
                claim.ReplaceableMetadataFields,
                now);
            version.State = "draft";
            version.AiGenerationProvenanceId = request.Id;
            version.PipelineVersion = PipelineVersion;
            version.UpdatedAt = now;
            CompleteJob(job, now);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "template.ai_draft_created",
                version.Id,
                "teacher_review_required",
                new
                {
                    appliedMetadataFields,
                    metadataConfidence = validated.Metadata.Confidence,
                    providerPassCount,
                    reviewIssueCount = validated.ReviewIssues.Count
                        + validated.Pages.Sum(page =>
                            page.Questions.Sum(question =>
                                question.ReviewIssues.Count)),
                });
            AddStatusOutbox(
                db,
                now,
                claim.CorrelationId,
                version.Id,
                "completed");
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static void PersistDraftProposal(
        OokiGraderDbContext db,
        TemplateVersionEntity version,
        PreparedClaim claim,
        ValidatedTemplateExtraction validated,
        DateTimeOffset now)
    {
        var sourceById = version.Sources.ToDictionary(
            source => source.Id,
            StringComparer.Ordinal);
        var globalWarnings = validated.GlobalWarnings
            .Take(20)
            .ToArray();
        var globalReviewNotes = validated.ReviewIssues
            .Select(ToReviewNote)
            .ToArray();
        var questionOrdinal = 0;

        foreach (var page in validated.Pages
                     .OrderBy(item => SourceOrdinal(claim.Sources, item.SourceId))
                     .ThenBy(item => item.PageNumber))
        {
            foreach (var proposal in page.Questions)
            {
                var questionId = UlidId.New(now.AddTicks(questionOrdinal + 1L));
                var confidence = checked(
                    (int)Math.Round(
                        proposal.Confidence * 10_000,
                        MidpointRounding.AwayFromZero));
                var warnings = globalWarnings
                    .Concat(globalReviewNotes)
                    .Concat(proposal.Warnings)
                    .Concat(proposal.ReviewIssues
                        .Where(issue => issue.Code !=
                            "question.repeated_printed_label_disambiguated")
                        .Select(ToReviewNote))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (proposal.AnswerProvenance == "ai_proposed")
                {
                    warnings.Add(
                        "正答はAIによる提案です。先生が根拠資料と照合してください。");
                }
                else if (proposal.AnswerProvenance
                         == "provided_model_answer")
                {
                    warnings.Add(
                        "模範解答の転記候補です。原資料との照合が必要です。");
                }
                else
                {
                    warnings.Add("正答が未解決です。先生が入力してください。");
                }

                var question = new QuestionEntity
                {
                    Id = questionId,
                    TemplateVersionId = version.Id,
                    LogicalQuestionId = UlidId.New(
                        now.AddTicks(1_000L + questionOrdinal)),
                    OrderIndex = questionOrdinal,
                    DisplayLabel = proposal.DisplayLabel,
                    QuestionText = proposal.QuestionText,
                    QuestionType = proposal.QuestionType,
                    GradingMode = GradingModeFor(proposal.QuestionType),
                    MaxPointsMilli = proposal.SuggestedPointsMilli,
                    PointIncrementMilli =
                        QuestionGradingDefaultPolicy.PointIncrementMilliFor(
                            proposal.SuggestedPointsMilli),
                    AllowNonKanji =
                        proposal.AllowNonKanjiSuggestion,
                    RequiresCompleteAnswer =
                        proposal.RequiresCompleteAnswerSuggestion,
                    AnswerOrderInsensitive =
                        proposal.AnswerOrderInsensitiveSuggestion,
                    RubricText =
                        QuestionGradingDefaultPolicy.BuildDefaultRubric(
                            proposal.QuestionType,
                            proposal.ExpectedAnswer),
                    KanjiPolicyNote =
                        "AIによる表記方針の提案です。先生の確認が必要です。",
                    TeacherNote = BoundedTeacherNote(warnings),
                    RequiresReviewAlways = RequiresPermanentReview(proposal),
                    AiConfidenceBasisPoints = confidence,
                    TeacherVerified = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Questions.Add(question);
                version.Questions.Add(question);

                if (proposal.ExpectedAnswer is not null)
                {
                    var source = proposal.AnswerSource is null
                        ? null
                        : sourceById[proposal.AnswerSource.SourceId];
                    db.AcceptedAnswers.Add(
                        CreateAnswer(
                            question.Id,
                            proposal.ExpectedAnswer,
                            "canonical",
                            proposal.AnswerProvenance,
                            source?.FileReferenceId,
                            proposal.AnswerSource?.PageNumber,
                            null,
                            now));
                    foreach (var variant in proposal.AcceptedVariants)
                    {
                        db.AcceptedAnswers.Add(
                            CreateAnswer(
                                question.Id,
                                variant,
                                "equivalent",
                                "derived_variant",
                                null,
                                null,
                                null,
                                now));
                    }
                }

                questionOrdinal = checked(questionOrdinal + 1);
            }
        }
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
                ?? throw Permanent("template_extract_request_missing");
            request.State = "invalid_output";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(db, claim, response, now);
            await ResetGeneratingVersionAsync(
                    db,
                    claim.TemplateVersionId,
                    now,
                    token)
                .ConfigureAwait(false);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "template.ai_response_rejected",
                claim.TemplateVersionId,
                errorCode);
            AddStatusOutbox(
                db,
                now,
                claim.CorrelationId,
                claim.TemplateVersionId,
                "failed");
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
                ?? throw Permanent("template_extract_request_missing");
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

                await ResetGeneratingVersionAsync(
                        db,
                        claim.TemplateVersionId,
                        now,
                        token)
                    .ConfigureAwait(false);
                BlockJob(job, now, exception.SafeErrorCode);
                AddStatusOutbox(
                    db,
                    now,
                    claim.CorrelationId,
                    claim.TemplateVersionId,
                    "failed");
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
                ?? throw Permanent("template_extract_request_missing");
            request.State = "failed";
            request.PossibleDuplicate = true;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = BoundedSafeDetail(safeDetail);
            request.CompletedAt = now;
            request.UpdatedAt = now;
            SettleReservationConservatively(db, claim.RequestId, now);
            await ResetGeneratingVersionAsync(
                    db,
                    claim.TemplateVersionId,
                    now,
                    token)
                .ConfigureAwait(false);
            BlockJob(job, now, errorCode);
            AddAudit(
                db,
                now,
                claim.CorrelationId,
                "template.ai_dispatch_ambiguous",
                claim.TemplateVersionId,
                errorCode);
            AddStatusOutbox(
                db,
                now,
                claim.CorrelationId,
                claim.TemplateVersionId,
                "failed");
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
                    .SingleOrDefaultAsync(
                        item => item.Id == requestId,
                        token)
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
                    await ResetGeneratingVersionAsync(
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
                    await ResetGeneratingVersionAsync(
                            db,
                            request.EntityId,
                            now,
                            token)
                        .ConfigureAwait(false);
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

    private static void ValidateVersion(
        TemplateVersionEntity version,
        TemplateExtractionPayload payload,
        int maximumSources)
    {
        if (version.State != "generating"
            || version.Revision != payload.GenerationRevision
            || version.AiGenerationProvenanceId is not null
            || version.Questions.Count != 0
            || version.Sources.Count == 0
            || version.Sources.Count > maximumSources
            || version.DefaultPointsMilli <= 0
            || version.TargetTotalPointsMilli is < 0)
        {
            throw Permanent("template_extract_version_state_invalid");
        }
    }

    private static void ValidateProfile(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle)
    {
        if (profile.TaskType != AiTaskTypes.TemplateExtraction
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
            || profile.PromptVersion != bundle.PromptVersion
            || profile.SchemaVersion != bundle.SchemaVersion
            || profile.PromptContentHash != bundle.ContentHash
            || profile.ThinkingLevel != "medium"
            || profile.ProcessingStrategy is not (
                "queued_standard" or "expedite_standard"))
        {
            throw Blocked("template_extract_profile_not_approved");
        }
    }

    private static SourceSnapshot ToSourceSnapshot(
        TemplateSourceEntity source,
        FileReferenceEntity? reference)
    {
        if (reference is null
            || source.FileReferenceId != reference.Id
            || reference.OwnerType != "upload_session"
            || reference.OwnerId != source.UploadSessionId
            || reference.Purpose != "template_source"
            || reference.FileObject.State != "available"
            || reference.FileObject.StorageClass
                != ContentStorageClass.TemplateSource.ToString()
            || reference.FileObject.VerifiedMime is not (
                "image/png"
                or "image/jpeg"
                or "image/webp"
                or "image/tiff"
                or "application/pdf")
            || !IsSha256(reference.FileObject.Sha256)
            || reference.FileObject.Bytes <= 0
            || source.SourceRole is not (
                "blank_test"
                or "contains_model_answers"
                or "contains_non_model_answers"
                or "separate_answer_key"))
        {
            throw Permanent("template_extract_source_disclosure_invalid");
        }

        var fileObject = reference.FileObject;
        return new SourceSnapshot(
            source.Id,
            source.SourceRole,
            source.Ordinal,
            source.DisplayName,
            reference.Id,
            fileObject.VerifiedMime,
            fileObject.Sha256,
            fileObject.Bytes,
            new ContentObjectLocator(
                ContentStorageClass.TemplateSource,
                fileObject.Sha256,
                fileObject.Bytes,
                fileObject.Extension));
    }

    private static void ValidateSources(
        IReadOnlyCollection<SourceSnapshot> sources,
        int maximumMediaBytes)
    {
        if (sources.Count == 0
            || sources.Select(item => item.Id)
                .Distinct(StringComparer.Ordinal)
                .Count() != sources.Count
            || sources.Select(item => item.Ordinal).Distinct().Count()
                != sources.Count)
        {
            throw Permanent("template_extract_sources_invalid");
        }

        var bytes = sources.Aggregate(
            0L,
            (total, source) => checked(total + source.Bytes));
        if (bytes <= 0 || bytes > maximumMediaBytes)
        {
            throw Permanent("template_extract_sources_too_large");
        }
    }

    private static string ComputeInputManifestHash(
        TemplateVersionEntity version,
        AiTaskProfileEntity profile,
        AiPromptBundle bundle,
        IReadOnlyCollection<SourceSnapshot> sources,
        IReadOnlyCollection<string> replaceableMetadataFields)
    {
        var canonical = new StringBuilder();
        AppendManifest(canonical, "pipeline", PipelineVersion);
        AppendManifest(canonical, "version", version.Id);
        AppendManifest(
            canonical,
            "version-revision",
            version.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(
            canonical,
            "default-points",
            version.DefaultPointsMilli.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(
            canonical,
            "target-points",
            version.TargetTotalPointsMilli?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty);
        AppendManifest(
            canonical,
            "template-revision",
            version.TestTemplate.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(
            canonical,
            "template-title",
            version.TestTemplate.Title);
        AppendManifest(
            canonical,
            "template-subject",
            version.TestTemplate.Subject ?? string.Empty);
        AppendManifest(
            canonical,
            "template-category",
            version.TestTemplate.Category ?? string.Empty);
        AppendManifest(
            canonical,
            "template-grade-label",
            version.TestTemplate.GradeLabel ?? string.Empty);
        AppendManifest(
            canonical,
            "template-course",
            version.TestTemplate.Course ?? string.Empty);
        foreach (var field in replaceableMetadataFields
                     .Order(StringComparer.Ordinal))
        {
            AppendManifest(canonical, "replaceable-metadata", field);
        }
        AppendManifest(canonical, "profile", profile.Id);
        AppendManifest(
            canonical,
            "profile-revision",
            profile.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        AppendManifest(canonical, "prompt", bundle.PromptVersion);
        AppendManifest(canonical, "schema", bundle.SchemaVersion);
        AppendManifest(canonical, "prompt-hash", bundle.ContentHash);
        foreach (var source in sources
                     .OrderBy(item => item.Ordinal)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendManifest(canonical, "source", source.Id);
            AppendManifest(canonical, "role", source.Role);
            AppendManifest(
                canonical,
                "ordinal",
                source.Ordinal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            AppendManifest(canonical, "file-reference", source.FileReferenceId);
            AppendManifest(canonical, "sha256", source.Sha256);
            AppendManifest(canonical, "mime", source.MimeType);
            AppendManifest(
                canonical,
                "bytes",
                source.Bytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
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
        TemplateVersionEntity version,
        IReadOnlyCollection<SourceSnapshot> sources,
        IReadOnlyCollection<string> replaceableMetadataFields,
        IReadOnlyDictionary<string, int>? pageCounts)
    {
        return CreateUserInstruction(
            requestKey,
            version.DefaultPointsMilli,
            version.TargetTotalPointsMilli,
            ToMetadataSnapshot(version.TestTemplate),
            replaceableMetadataFields,
            sources,
            pageCounts);
    }

    private static string CreateUserInstruction(
        string requestKey,
        long defaultPointsMilli,
        long? targetTotalPointsMilli,
        TemplateMetadataSnapshot currentMetadata,
        IReadOnlyCollection<string> replaceableMetadataFields,
        IReadOnlyCollection<SourceSnapshot> sources,
        IReadOnlyDictionary<string, int>? pageCounts)
    {
        var sourceManifest = sources
            .OrderBy(item => item.Ordinal)
            .Select(
                (source, mediaIndex) => new
                {
                    media_index = mediaIndex,
                    source_id = source.Id,
                    source_role = source.Role,
                    page_count = pageCounts is not null
                        && pageCounts.TryGetValue(
                            source.Id,
                            out var pageCount)
                            ? pageCount
                            : (int?)null,
                    source.DisplayName,
                });
        var pageManifest = sources
            .OrderBy(item => item.Ordinal)
            .SelectMany(source => Enumerable.Range(
                    1,
                    pageCounts is not null
                        && pageCounts.TryGetValue(source.Id, out var count)
                            ? count
                            : 0)
                .Select(pageNumber => new
                {
                    page_id = $"{source.Id}:page:{pageNumber}",
                    source_id = source.Id,
                    page_number = pageNumber,
                }));
        return
            """
            The attached primary media are teacher-supplied test sources in the
            exact media_index order below. During an explicitly marked internal
            quality-control pass, additional media may be detail views governed
            by the INTERNAL VIEW MANIFEST; those views are not new sources or
            pages. Treat source_role as authoritative metadata.
            First inventory every physical writable answer slot on each primary
            question page. A box embedded in a sentence, an underline, a table
            cell, and each separately writable blank are each one slot. Count only
            curricular response slots whose contents answer a test question. Do
            not count administrative or scoring fields such as 氏名, 名前, 組,
            番号, 学籍番号, 日付, 得点, 点数, teacher marks, handwritten scores,
            page references, diagram labels, worked examples, or decorative
            boxes. A filled name box or a handwritten score is not an answer slot.
            Cross-check every counted slot against a printed curricular prompt.
            Set detected_answer_slot_count to that inventory count, then return
            exactly one question object for every curricular slot in visual reading
            order. Never omit a curricular slot merely to make the count and question
            array agree; instead re-check whether an unmatched field is administrative.
            answer_slot_ordinal must be the consecutive 1-based slot position and
            answer_slot_count must be 1. Never combine two blanks into one object,
            even when they share a sentence, concept, printed number, or answer.

            Preserve each visible printed Japanese question label exactly in
            display_label. Printed labels may repeat across subsections or within
            a page; a repeated label is a distinct occurrence, not an instruction
            to invent the next number. Never renumber ⑧ as ⑨ or as 2①. Keep the
            repeated label as ⑧ and make source_key unique with the source, page,
            visual slot ordinal, printed label, and occurrence (for example,
            page-1-slot-10-printed-⑧-occurrence-2). Enumerate each printed question
            slot exactly once, on its primary source page, in visual reading order.

            Questions, reference maps/tables, and writable answer areas may be
            interwoven on the same sheet. Do not assume a separate answer sheet.
            Identify questions from their printed labels, wording, and reading
            order. Do not return coordinates or regions. The application sends
            complete pages to later AI tasks, so teachers never need to draw or
            adjust boxes.

            For a writable blank embedded in printed text, set
            is_embedded_fill_blank true and normally use question_type
            exact_short_text. Copy enough surrounding printed text to identify
            the prompt, but replace the contents of exactly that one physical answer
            slot with the exact token ［　］. The resulting question_text must
            contain exactly one ［　］ token. Never put the expected answer, model
            answer, handwriting, or any other visible filled response inside that
            token or elsewhere as part of question_text. Set filled_answer_removed
            true only after doing this; use true for an already-empty source slot.
            If one printed sentence contains several blanks, emit several question
            objects: each object focuses on one slot and replaces only its target
            with ［　］, while other slots are described as context without copying
            their filled responses. Do not use multi_part to merge physical slots.

            Apply source-role provenance mechanically, not by judgment:
            blank_test without an authoritative answer source => ai_proposed and
            answer_source null; contains_non_model_answers without an authoritative
            answer source => ai_proposed and answer_source null;
            contains_model_answers => provided_model_answer with this exact
            source_id and page; separate_answer_key => provided_model_answer with
            that exact source_id and page. It is invalid to return ai_proposed for
            a readable answer on contains_model_answers or separate_answer_key.

            For blank_test sources, when this request contains no authoritative
            answer source, independently solve each printed question and return
            its expected answer as ai_proposed with answer_source null. When an
            authoritative source is present, use the matched supplied answer
            instead.

            For contains_non_model_answers sources, visible filled answers are ordinary
            student/non-model work and must be ignored as answer authority. Never copy
            or cite those filled answers as the expected
            answer, an accepted variant, rubric evidence, or printed question
            text. Never return provided_model_answer from this source role. When
            this request contains no authoritative answer source, independently
            solve the printed questions and return each expected answer as
            ai_proposed with answer_source null. Return one coherent Japanese
            answer form. Never concatenate or splice kana and Kanji alternatives;
            put other complete acceptable forms in accepted_variants instead.

            For contains_model_answers and separate_answer_key sources,
            transcribe visible supplied answers exactly as provided_model_answer
            and include answer_source with the exact source_id and page. Copy the
            visible script character by character: kana remains kana even when an
            equivalent Kanji spelling is known. expected_answer must be one
            coherent transcription, never a splice of alternative spellings. Never
            replace a supplied answer with an independently solved answer. If an
            authoritative source is present but its answer is missing, unreadable,
            unmatched, or conflicting, return unavailable and a warning. Put any
            independent comparison only in warnings.

            Suggested accepted variants are unverified proposals. Use zero points
            only when printed points cannot be determined; the application will
            apply its configured default. Every AI field remains a teacher-review
            draft and must never be treated as published.

            Copy printed grading-rule instructions conservatively. Set
            requires_complete_answer_suggestion true only when the visible prompt
            explicitly requires 完答 or otherwise explicitly says every listed
            component is required for credit. Set
            answer_order_insensitive_suggestion true only when the visible prompt
            explicitly says 順不同 or that component order does not matter. These
            flags are independent; do not infer either one merely because an
            expected answer contains a list. Otherwise return false.

            Finally reread every question_text against the image at high resolution.
            Remove scan noise and impossible extra kana, but do not paraphrase or
            invent wording. Japanese text must remain grammatical enough to identify
            the printed prompt.

            Return printed_test_name only when the top-level test name is visibly
            printed and safely readable. Return printed_grade_label only when an
            explicit grade is visibly printed. Do not infer grade from difficulty,
            vocabulary, question numbers, subject, or a filename. Do not classify
            subject, category, answer style, test type, split boundaries, or STEP
            variation. Use null for a name or grade that is not safely visible.

            FINAL SOURCE-ROLE GATE. Immediately before returning JSON, verify each
            answer against the source manifest: readable answers from
            contains_model_answers or separate_answer_key are
            provided_model_answer with their exact source_id and page; unreadable,
            missing, unmatched, or conflicting supplied answers are unavailable.
            ai_proposed is never a fallback for an authoritative source. Do not
            merely relabel an independently solved answer as source-provided.

            """
            + JsonSerializer.Serialize(new
            {
                schema_version = "template_extract_v5",
                request_key = requestKey,
                default_points_milli = defaultPointsMilli,
                target_total_points_milli = targetTotalPointsMilli,
                current_metadata = new
                {
                    currentMetadata.Title,
                    currentMetadata.Subject,
                    currentMetadata.Category,
                    grade_label = currentMetadata.GradeLabel,
                    currentMetadata.Course,
                },
                replaceable_metadata_fields = replaceableMetadataFields
                    .Order(StringComparer.Ordinal),
                sources = sourceManifest,
                pages = pageManifest,
            });
    }

    private static AiRequestEntity CreateRequest(
        DateTimeOffset now,
        TemplateVersionEntity version,
        AiTaskProfileEntity profile,
        string inputManifestHash)
    {
        var id = UlidId.New(now);
        return new AiRequestEntity
        {
            Id = id,
            RequestKey = $"template_{id}",
            AiTaskProfileId = profile.Id,
            TaskProfileRevision = profile.Revision,
            Purpose = AiTaskTypes.TemplateExtraction,
            EntityType = "template_version",
            EntityId = version.Id,
            EntityRevision = version.Revision,
            InputManifestHash = inputManifestHash,
            State = "prepared",
            DispatchAttempt = 0,
            PossibleDuplicate = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static AcceptedAnswerEntity CreateAnswer(
        string questionId,
        string answer,
        string variantType,
        string provenance,
        string? sourceFileReferenceId,
        int? sourcePageNumber,
        string? sourceRegionId,
        DateTimeOffset now) =>
        new()
        {
            Id = UlidId.New(now),
            QuestionId = questionId,
            AnswerText = answer,
            NormalizedText =
                JapaneseTextNormalizer.NormalizeForComparison(answer),
            VariantType = variantType,
            TeacherVerified = false,
            AnswerProvenance = provenance,
            SourceFileReferenceId = sourceFileReferenceId,
            SourcePageNumber = sourcePageNumber,
            SourceRegionId = sourceRegionId,
            Locale = "ja-JP",
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static string GradingModeFor(string questionType) =>
        QuestionGradingDefaultPolicy.GradingModeFor(questionType);

    private static bool RequiresPermanentReview(
        ValidatedTemplateQuestion proposal) =>
        proposal.QuestionType == "unsupported";

    private static string ToReviewNote(
        TemplateExtractionReviewIssue issue) =>
        $"[{issue.Code}] {issue.Message}";

    private static TemplateMetadataSnapshot ToMetadataSnapshot(
        TestTemplateEntity template) =>
        new(
            template.Title,
            template.Subject,
            template.Category,
            template.GradeLabel,
            template.Course);

    private static List<string> ApplyInferredMetadata(
        TestTemplateEntity template,
        ValidatedTemplateMetadata metadata,
        IReadOnlyCollection<string> replaceableMetadataFields,
        DateTimeOffset now)
    {
        if (metadata.Confidence < MetadataApplicationConfidence)
        {
            return [];
        }

        var applied = new List<string>(5);
        if (CanApplyInferredValue(
                "title",
                template.Title,
                replaceableMetadataFields,
                title: true)
            && metadata.Title is not null)
        {
            template.Title = metadata.Title;
            applied.Add("title");
        }

        if (CanApplyInferredValue(
                "subject",
                template.Subject,
                replaceableMetadataFields)
            && metadata.Subject is not null)
        {
            template.Subject = metadata.Subject;
            applied.Add("subject");
        }

        if (CanApplyInferredValue(
                "category",
                template.Category,
                replaceableMetadataFields)
            && metadata.Category is not null)
        {
            template.Category = metadata.Category;
            applied.Add("category");
        }

        if (CanApplyInferredValue(
                "gradeLabel",
                template.GradeLabel,
                replaceableMetadataFields)
            && metadata.GradeLabel is not null)
        {
            template.GradeLabel = metadata.GradeLabel;
            applied.Add("gradeLabel");
        }

        if (CanApplyInferredValue(
                "course",
                template.Course,
                replaceableMetadataFields)
            && metadata.Course is not null)
        {
            template.Course = metadata.Course;
            applied.Add("course");
        }

        if (applied.Count > 0)
        {
            template.Source = "manual_ai_assisted";
            template.UpdatedAt = now;
        }

        return applied;
    }

    private static bool CanApplyInferredValue(
        string field,
        string? current,
        IReadOnlyCollection<string> replaceableMetadataFields,
        bool title = false) =>
        replaceableMetadataFields.Contains(field, StringComparer.Ordinal)
        || ShouldApplyInferredValue(current, title);

    private static bool ShouldApplyInferredValue(
        string? current,
        bool title = false)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var normalized = current.Trim().Normalize(
            NormalizationForm.FormKC);
        return normalized is
                "未設定"
                or "不明"
                or "（未設定）"
                or "自動判定中"
            || title
            && normalized is
                "無題"
                or "新規テスト"
                or "新しいテスト"
                or "Untitled";
    }

    private static int SourceOrdinal(
        IReadOnlyCollection<SourceSnapshot> sources,
        string sourceId) =>
        sources.Single(item => item.Id == sourceId).Ordinal;

    private static string? BoundedTeacherNote(
        List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        var joined = string.Join(
            "\n",
            warnings.Select(warning => $"[AI確認] {warning}"));
        return joined.Length <= 4_000 ? joined : joined[..4_000];
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

    private static long EstimateMaximumCost(
        PricingSnapshotEntity pricing,
        int maxOutputTokens,
        string instruction,
        int maximumMediaBytes,
        int maximumPasses)
    {
        if (maximumPasses is < 1 or > MaximumProviderPasses)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPasses));
        }

        var textTokens = Math.Max(
            1,
            (Encoding.UTF8.GetByteCount(instruction) + 3L) / 4L);
        var mediaTokens = Math.Max(1, (maximumMediaBytes + 3L) / 4L);
        return CalculateCost(
            checked((textTokens + mediaTokens) * maximumPasses),
            checked((long)maxOutputTokens * maximumPasses),
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
            ?? throw Permanent("ai_budget_reservation_missing");
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

    private static void MarkBudgetBlocked(
        AiRequestEntity request,
        TemplateVersionEntity version,
        BackgroundJobEntity job,
        DateTimeOffset now,
        string errorCode)
    {
        request.State = "budget_blocked";
        request.ErrorCode = errorCode;
        request.SafeErrorDetail = null;
        request.UpdatedAt = now;
        ResetGeneratingVersion(version, now);
        BlockJob(job, now, errorCode);
    }

    private static void MarkAmbiguousRecovery(
        AiRequestEntity request,
        TemplateVersionEntity version,
        BackgroundJobEntity job,
        DateTimeOffset now)
    {
        request.State = "failed";
        request.PossibleDuplicate = true;
        request.ErrorCode = "ai_dispatch_outcome_unknown";
        request.SafeErrorDetail = "recovered_dispatching_request";
        request.CompletedAt = now;
        request.UpdatedAt = now;
        ResetGeneratingVersion(version, now);
        BlockJob(job, now, "ai_dispatch_outcome_unknown");
    }

    private static async Task ResetGeneratingVersionAsync(
        OokiGraderDbContext db,
        string versionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var version = await db.TemplateVersions
            .SingleOrDefaultAsync(
                item => item.Id == versionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (version?.State == "generating")
        {
            ResetGeneratingVersion(version, now);
        }
    }

    private static void ResetGeneratingVersion(
        TemplateVersionEntity version,
        DateTimeOffset now)
    {
        if (version.State == "generating")
        {
            version.State = "draft";
            version.UpdatedAt = now;
        }
    }

    private async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(
                item => item.Id == jobId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw Permanent("template_extract_job_missing");
        if (job.State != "leased"
            || job.LeaseOwner != _workerId
            || job.LeaseExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw Permanent("template_extract_job_lease_lost");
        }

        return job;
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
        string versionId,
        string reasonCode,
        object? safeMetadata = null)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            EventType = eventType,
            ObjectType = "template_version",
            ObjectId = versionId,
            Outcome = "succeeded",
            ReasonCode = reasonCode,
            CorrelationId = correlationId,
            SafeMetadataJson = safeMetadata is null
                ? null
                : JsonSerializer.Serialize(safeMetadata),
        });
    }

    private static void AddStatusOutbox(
        OokiGraderDbContext db,
        DateTimeOffset now,
        string? correlationId,
        string versionId,
        string state)
    {
        db.OutboxEvents.Add(new OutboxEventEntity
        {
            Id = UlidId.New(now),
            AggregateType = "template_version",
            AggregateId = versionId,
            EventType = "template.generation_status",
            SchemaVersion = 1,
            PayloadJson = JsonSerializer.Serialize(new
            {
                templateVersionId = versionId,
                state,
            }),
            CorrelationId = correlationId,
            OccurredAt = now,
        });
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes is <= 0 or > int.MaxValue)
        {
            throw Permanent("template_extract_source_size_invalid");
        }

        using var destination = new MemoryStream(checked((int)expectedBytes));
        await source.CopyToAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        if (destination.Length != expectedBytes)
        {
            throw Permanent("template_extract_source_size_mismatch");
        }

        return destination.ToArray();
    }

    private static TemplateExtractionPayload DeserializePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TemplateExtractionPayload>(
                       json,
                       PayloadSerializerOptions)
                   ?? throw Permanent("template_extract_payload_invalid");
        }
        catch (JsonException)
        {
            throw Permanent("template_extract_payload_invalid");
        }
    }

    private static string[] NormalizeReplaceableMetadataFields(
        IReadOnlyList<string>? values)
    {
        var fields = values?.ToArray() ?? [];
        if (fields.Length > 5
            || fields.Any(field => field is not (
                "title" or "subject" or "category" or "gradeLabel" or "course"))
            || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length)
        {
            throw Permanent("template_extract_payload_invalid");
        }

        Array.Sort(fields, StringComparer.Ordinal);
        return fields;
    }

    private static string ToMediaResolution(string value) => value switch
    {
        "low" => "MEDIA_RESOLUTION_LOW",
        "medium" => "MEDIA_RESOLUTION_MEDIUM",
        "high" => "MEDIA_RESOLUTION_HIGH",
        "ultra_high" => "MEDIA_RESOLUTION_ULTRA_HIGH",
        _ => throw Blocked("ai_media_resolution_invalid"),
    };

    private static string ToThinkingLevel(string value) => value switch
    {
        "minimal" => "MINIMAL",
        "low" => "LOW",
        "medium" => "MEDIUM",
        "high" => "HIGH",
        _ => throw Blocked("ai_thinking_level_invalid"),
    };

    private static PricingSnapshot ToPricingSnapshot(
        PricingSnapshotEntity entity) =>
        new(
            entity.Id,
            entity.InputUsdMicrosPerMillionTokens,
            entity.OutputUsdMicrosPerMillionTokens,
            entity.ThinkingUsdMicrosPerMillionTokens);

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character =>
                character is >= '0' and <= '9'
                or >= 'a' and <= 'f');
    }

    private static bool IsQualityControlDetailViewMimeType(string mimeType) =>
        mimeType is "image/png" or "image/jpeg" or "image/webp";

    private static void ZeroImageArtifacts(
        IEnumerable<ImageArtifact> artifacts)
    {
        foreach (var artifact in artifacts)
        {
            CryptographicOperations.ZeroMemory(artifact.Bytes);
        }
    }

    private static void ZeroQualityControlDetailViews(
        IReadOnlyList<PreparedSourceMedia>? preparedMedia)
    {
        if (preparedMedia is null)
        {
            return;
        }

        foreach (var detailView in preparedMedia.SelectMany(item =>
                     item.QualityControlDetailViews))
        {
            if (MemoryMarshal.TryGetArray(
                    detailView.Bytes,
                    out ArraySegment<byte> bytes)
                && bytes.Array is not null)
            {
                CryptographicOperations.ZeroMemory(bytes.AsSpan());
            }
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return left.Length == right.Length
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(left),
                Encoding.ASCII.GetBytes(right));
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
            return "template_extract_response_semantics_invalid";
        }

        return value.Length <= 200
            ? value
            : "template_extract_response_semantics_invalid";
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
        EventId = 1302,
        Level = LogLevel.Warning,
        Message =
            "Template extraction job {JobId} failed with {ErrorCode} " +
            "({ExceptionType}).")]
    private partial void LogJobFailure(
        Exception exception,
        string jobId,
        string errorCode,
        string exceptionType);

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        string? CorrelationId);

    private sealed record TemplateExtractionPayload(
        string TemplateVersionId,
        long GenerationRevision,
        IReadOnlyList<string>? ReplaceableMetadataFields);

    private sealed record PreparedClaim(
        string JobId,
        string? CorrelationId,
        string TemplateVersionId,
        long VersionRevision,
        long DefaultPointsMilli,
        long? TargetTotalPointsMilli,
        TemplateMetadataSnapshot CurrentMetadata,
        IReadOnlyList<string> ReplaceableMetadataFields,
        string TaskProfileId,
        long TaskProfileRevision,
        string RequestId,
        string RequestKey,
        string InputManifestHash,
        int MaxOutputTokens,
        string MediaResolution,
        string ThinkingLevel,
        string SecretReference,
        AiConnectionSettings Connection,
        AiPromptBundle Bundle,
        IReadOnlyList<SourceSnapshot> Sources,
        PricingSnapshot? Pricing,
        long UsdToJpyMicros);

    private sealed record TemplateMetadataSnapshot(
        string? Title,
        string? Subject,
        string? Category,
        string? GradeLabel,
        string? Course);

    private sealed record SourceSnapshot(
        string Id,
        string Role,
        int Ordinal,
        string DisplayName,
        string FileReferenceId,
        string MimeType,
        string Sha256,
        long Bytes,
        ContentObjectLocator Locator);

    private sealed record PreparedSourceMedia(
        SourceSnapshot Source,
        int PageCount,
        AiMediaPart Media,
        IReadOnlyList<AiMediaPart> QualityControlDetailViews);

    private sealed record ExtractionCandidate(
        AiProviderResponse Response,
        string ResponseJson,
        ValidatedTemplateExtraction Validated);

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
