using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Templates;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Observability;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Jobs;

public sealed record TemplateGenerationUnitJobWorkerOptions
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);
    public int MaximumProviderMediaBytes { get; init; } = 12 * 1024 * 1024;
    public int MaximumStoredResponseCharacters { get; init; } = 1_000_000;

    internal void Validate()
    {
        if (PollInterval < TimeSpan.FromMilliseconds(100)
            || PollInterval > TimeSpan.FromMinutes(1)
            || LeaseDuration < TimeSpan.FromMinutes(2)
            || LeaseDuration > TimeSpan.FromHours(1)
            || MaximumProviderMediaBytes is < 1_024 or > 64 * 1024 * 1024
            || MaximumStoredResponseCharacters is < 10_000 or > 2_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TemplateGenerationUnitJobWorkerOptions),
                "One or more deterministic-unit worker options are invalid.");
        }
    }
}

/// <summary>
/// Runs exactly one orientation-gated extraction request for a deterministic
/// template unit, plus one local-rotation retry only when the first valid
/// response requests a quarter turn.
/// </summary>
public sealed partial class TemplateGenerationUnitJobWorker : BackgroundService
{
    public const string JobType = TemplateGenerationBatchService.UnitJobType;
    public const int JobSchemaVersion =
        TemplateGenerationBatchService.UnitJobSchemaVersion;
    public const string PipelineVersion =
        "deterministic-template-generation-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
    private static readonly HashSet<string> DynamicWarningCodes =
    [
        TemplateNamePolicy.NameRequiredErrorCode,
        TemplateNamePolicy.StepNameMismatchErrorCode,
        TemplateNamePolicy.StepNameAlreadySuffixedErrorCode,
        TemplateNamePolicy.DuplicateNameErrorCode,
        GradeResolutionService.RequiredErrorCode,
        GradeResolutionService.ConflictErrorCode,
    ];

    private readonly IDbContextFactory<OokiGraderDbContext> _dbContextFactory;
    private readonly IWriteCoordinator _writeCoordinator;
    private readonly IContentStore _contentStore;
    private readonly IPdfPageRangeExtractor _pageRangeExtractor;
    private readonly IAiProviderClientResolver _providerResolver;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;
    private readonly IAiPromptBundleCatalog _promptCatalog;
    private readonly IAiSecretStore _secretStore;
    private readonly IUlidGenerator _ids;
    private readonly TimeProvider _timeProvider;
    private readonly TemplateGenerationUnitJobWorkerOptions _options;
    private readonly ILogger<TemplateGenerationUnitJobWorker> _logger;
    private readonly string _workerId = $"template-unit-{Guid.NewGuid():N}";

    public TemplateGenerationUnitJobWorker(
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IContentStore contentStore,
        IPdfPageRangeExtractor pageRangeExtractor,
        IAiProviderClientResolver providerResolver,
        IAiProviderFeaturePolicy providerFeaturePolicy,
        IAiPromptBundleCatalog promptCatalog,
        IAiSecretStore secretStore,
        IUlidGenerator ids,
        TimeProvider timeProvider,
        IOptions<TemplateGenerationUnitJobWorkerOptions> options,
        ILogger<TemplateGenerationUnitJobWorker> logger)
    {
        _dbContextFactory = dbContextFactory;
        _writeCoordinator = writeCoordinator;
        _contentStore = contentStore;
        _pageRangeExtractor = pageRangeExtractor;
        _providerResolver = providerResolver;
        _providerFeaturePolicy = providerFeaturePolicy;
        _promptCatalog = promptCatalog;
        _secretStore = secretStore;
        _ids = ids;
        _timeProvider = timeProvider;
        _options = options.Value;
        _options.Validate();
        _logger = logger;
    }

    public async Task<bool> ProcessNextAsync(
        CancellationToken cancellationToken = default)
    {
        var lease = await LeaseNextAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        DerivedPdfResult? currentMedia = null;
        UnitClaim? claim = null;
        var orientationRetryStarted = false;
        try
        {
            if (lease.RecoveryOnly)
            {
                throw new UnitJobException("AI_PROVIDER_UNAVAILABLE");
            }

            claim = await PrepareAsync(lease, cancellationToken)
                .ConfigureAwait(false);
            if (claim is null)
            {
                return true;
            }

            if (!_providerFeaturePolicy.IsEnabled(claim.Connection.Provider))
            {
                throw new UnitJobException("AI_PROVIDER_UNAVAILABLE");
            }

            currentMedia = await ExtractDerivedAsync(
                    claim,
                    new Dictionary<int, int>(),
                    cancellationToken)
                .ConfigureAwait(false);
            using var credential = await _secretStore.ReadAsync(
                    new AiSecretReference(claim.SecretReference),
                    cancellationToken)
                .ConfigureAwait(false);

            var first = await ExecuteAttemptAsync(
                    claim,
                    currentMedia,
                    attemptNumber: 1,
                    rotationsWereApplied: false,
                    retryOfRequestId: null,
                    credential.Utf8Bytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var providerCallCount = first.ProviderCallMade ? 1 : 0;
            AttemptResult selected = first;
            if (first.Extraction.Action == TemplateExtractionAction.Rotate)
            {
                var rotations = ToOriginalPageRotations(
                    claim.Profile,
                    first.Instruction.Pages,
                    first.Extraction.Orientation);
                await PersistRotationRequestAsync(
                        claim,
                        first.Extraction.Orientation,
                        rotations,
                        cancellationToken)
                    .ConfigureAwait(false);
                TemplateGenerationMetrics.OrientationRetryStarted(
                    claim.Profile,
                    claim.Connection.Provider,
                    claim.Connection.ModelId);
                orientationRetryStarted = true;
                CryptographicOperations.ZeroMemory(currentMedia.Bytes);
                currentMedia = await ExtractDerivedAsync(
                        claim,
                        rotations,
                        cancellationToken)
                    .ConfigureAwait(false);
                await MarkRetryingAfterRotationAsync(
                        claim,
                        cancellationToken)
                    .ConfigureAwait(false);
                var second = await ExecuteAttemptAsync(
                        claim,
                        currentMedia,
                        attemptNumber: 2,
                        rotationsWereApplied: true,
                        retryOfRequestId: first.RequestId,
                        credential.Utf8Bytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (second.ProviderCallMade)
                {
                    providerCallCount++;
                }

                if (second.Extraction.Action != TemplateExtractionAction.Extract
                    || second.Extraction.Extraction is null)
                {
                    throw new UnitJobException(
                        "ORIENTATION_RETRY_EXHAUSTED");
                }

                selected = second;
            }

            if (selected.Extraction.Action != TemplateExtractionAction.Extract
                || selected.Extraction.Extraction is null)
            {
                throw new UnitJobException("TEMPLATE_EXTRACTION_FAILED");
            }

            var success = await PersistSuccessAsync(
                    claim,
                    currentMedia,
                    selected.Extraction.Extraction,
                    providerCallCount,
                    cancellationToken)
                .ConfigureAwait(false);
            TemplateGenerationMetrics.UnitExtractionSucceeded(
                claim.Profile,
                claim.Connection.Provider,
                claim.Connection.ModelId,
                success.UnitUsdMicros);
            if (success.FailedBatchUsdMicros is { } failedBatchUsdMicros)
            {
                TemplateGenerationMetrics.BatchTerminated(
                    claim.Profile.TestType,
                    claim.Profile.PromptSystem,
                    claim.Profile.ProfileVersion,
                    "failed",
                    failedBatchUsdMicros);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnitJobException exception)
        {
            var failure = await PersistFailureAsync(
                    lease.Id,
                    exception.ErrorCode,
                    cancellationToken)
                .ConfigureAwait(false);
            if (failure.UnitOutcome != "cancelled")
            {
                LogUnitFailure(
                    _logger,
                    lease.Id,
                    exception.ErrorCode,
                    exception.GetType().Name);
            }

            RecordFailureMetrics(
                claim,
                exception.ErrorCode,
                orientationRetryStarted,
                failure);
        }
        catch (AiProviderException exception)
        {
            var failure = await PersistFailureAsync(
                    lease.Id,
                    "AI_PROVIDER_UNAVAILABLE",
                    cancellationToken)
                .ConfigureAwait(false);
            if (failure.UnitOutcome != "cancelled")
            {
                LogUnitFailure(
                    _logger,
                    lease.Id,
                    "AI_PROVIDER_UNAVAILABLE",
                    exception.GetType().Name);
            }

            RecordFailureMetrics(
                claim,
                "AI_PROVIDER_UNAVAILABLE",
                orientationRetryStarted,
                failure);
        }
        catch (Exception exception)
        {
            var failure = await PersistFailureAsync(
                    lease.Id,
                    "TEMPLATE_EXTRACTION_FAILED",
                    cancellationToken)
                .ConfigureAwait(false);
            if (failure.UnitOutcome != "cancelled")
            {
                LogUnitFailure(
                    _logger,
                    lease.Id,
                    "TEMPLATE_EXTRACTION_FAILED",
                    exception.GetType().Name);
            }

            RecordFailureMetrics(
                claim,
                "TEMPLATE_EXTRACTION_FAILED",
                orientationRetryStarted,
                failure);
        }
        finally
        {
            if (currentMedia is not null)
            {
                CryptographicOperations.ZeroMemory(currentMedia.Bytes);
            }
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

    [LoggerMessage(
        EventId = 2310,
        Level = LogLevel.Error,
        Message = "Template generation unit job {JobId} failed with {ErrorCode} ({ExceptionType}).")]
    private static partial void LogUnitFailure(
        ILogger logger,
        string jobId,
        string errorCode,
        string exceptionType);

    private Task<JobLease?> LeaseNextAsync(CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs
                .Where(item => item.Type == JobType
                    && item.State == "leased"
                    && item.LeaseExpiresAt <= now
                    && item.AttemptCount >= item.MaxAttempts)
                .OrderByDescending(item => item.Priority)
                .ThenBy(item => item.NextAttemptAt)
                .ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            var recoveryOnly = job is not null;
            job ??= await db.BackgroundJobs
                .Where(item => item.Type == JobType
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
            if (!recoveryOnly)
            {
                job.AttemptCount = checked(job.AttemptCount + 1);
            }

            job.StartedAt ??= now;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new JobLease(
                job.Id,
                job.SchemaVersion,
                job.PayloadJson,
                job.CorrelationId,
                recoveryOnly);
        }, cancellationToken);

    private Task<UnitClaim?> PrepareAsync(
        JobLease lease,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
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
            if (lease.SchemaVersion != JobSchemaVersion
                || job.SchemaVersion != JobSchemaVersion)
            {
                throw new UnitJobException("AI_PROFILE_MISSING");
            }

            var payload = DeserializePayload(lease.PayloadJson);
            var unit = await db.TemplateGenerationUnits
                .Include(item => item.Batch)
                .SingleOrDefaultAsync(item => item.Id == payload.UnitId, token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_MISSING");
            if (!string.Equals(unit.BatchId, payload.BatchId, StringComparison.Ordinal)
                || !string.Equals(unit.ExtractionJobId, lease.Id, StringComparison.Ordinal)
                || !string.Equals(
                    unit.GenerationProfileHash,
                    payload.GenerationProfileHash,
                    StringComparison.Ordinal))
            {
                throw new UnitJobException("SOURCE_CHANGED");
            }

            if (unit.Status is TemplateGenerationUnitStatus.Extracted
                or TemplateGenerationUnitStatus.Confirmed)
            {
                CompleteJob(job, now);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return null;
            }

            if (unit.Batch.Status != TemplateGenerationBatchStatus.Generating
                || unit.Status is not (
                    TemplateGenerationUnitStatus.Queued
                    or TemplateGenerationUnitStatus.Generating
                    or TemplateGenerationUnitStatus.Rotating
                    or TemplateGenerationUnitStatus.RetryingAfterRotation))
            {
                throw new UnitJobException("SOURCE_CHANGED");
            }

            TemplateGenerationProfile profile;
            try
            {
                profile = JsonSerializer.Deserialize<TemplateGenerationProfile>(
                        unit.GenerationProfileJson,
                        JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new UnitJobException("AI_PROFILE_MISSING");
            }

            if (!string.Equals(
                    profile.ComputeHash(),
                    unit.GenerationProfileHash,
                    StringComparison.Ordinal)
                || profile.TestType != unit.TestType
                || profile.PromptSystem != unit.PromptSystem
                || profile.FirstPage != unit.FirstPage
                || profile.LastPage != unit.LastPage)
            {
                throw new UnitJobException("AI_PROFILE_MISSING");
            }

            var selection = await TemplateExtractionAiProfilePolicy
                .FindCurrentUsableAsync(
                    db,
                    _promptCatalog,
                    _providerFeaturePolicy,
                    token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("AI_PROFILE_MISSING");
            var bundle = selection.Bundle;
            if (bundle.PromptVersion != profile.ExtractionPromptVersion
                || bundle.SchemaVersion != profile.ExtractionSchemaVersion)
            {
                throw new UnitJobException("AI_PROFILE_MISSING");
            }

            var taskProfile = selection.Profile;

            var sourceReference = await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleOrDefaultAsync(item =>
                    item.OwnerType == "upload_session"
                    && item.OwnerId == unit.Batch.SourceId
                    && item.Purpose == "template_source", token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_MISSING");
            ValidateSource(unit, sourceReference);

            unit.Status = unit.OrientationAttemptCount == 0
                ? TemplateGenerationUnitStatus.Generating
                : TemplateGenerationUnitStatus.RetryingAfterRotation;
            job.ProgressBasisPoints = Math.Max(job.ProgressBasisPoints, 1_000);
            AddAudit(
                db,
                unit.Batch.CreatedByUserId,
                "TemplateUnitGenerationStarted",
                "template_generation_unit",
                unit.Id,
                lease.CorrelationId,
                new
                {
                    unit.Sequence,
                    unit.FirstPage,
                    unit.LastPage,
                    promptSystem = unit.PromptSystem,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    batchRowVersion = unit.Batch.Revision,
                    profileVersion = profile.ProfileVersion,
                    promptVersion = profile.ExtractionPromptVersion,
                    schemaVersion = profile.ExtractionSchemaVersion,
                });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);

            var file = sourceReference.FileObject;
            return new UnitClaim(
                lease.Id,
                lease.CorrelationId,
                unit.Id,
                unit.BatchId,
                unit.Batch.SourceId,
                unit.Batch.CreatedByUserId,
                unit.Batch.PlanHash,
                profile,
                new SourceFileSnapshot(
                    file.Id,
                    file.Sha256,
                    file.Bytes,
                    file.Extension,
                    file.VerifiedMime),
                taskProfile.Id,
                taskProfile.Revision,
                taskProfile.MaxOutputTokens,
                ToMediaResolution(taskProfile.MediaResolution),
                ToThinkingLevel(taskProfile.ThinkingLevel),
                taskProfile.AiConnection.SecretReference,
                new AiConnectionSettings(
                    taskProfile.AiConnection.Id,
                    taskProfile.AiConnection.Provider,
                    AiProviderCatalog.GetBaseAddress(
                        taskProfile.AiConnection.Provider),
                    taskProfile.AiConnection.ModelId,
                    TimeSpan.FromSeconds(
                        taskProfile.AiConnection.TimeoutSeconds)),
                bundle);
        }, cancellationToken);

    private async Task<DerivedPdfResult> ExtractDerivedAsync(
        UnitClaim claim,
        IReadOnlyDictionary<int, int> rotations,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var source = await _contentStore.OpenReadAsync(
                    new ContentObjectLocator(
                        ContentStorageClass.TemplateSource,
                        claim.Source.Sha256,
                        claim.Source.Bytes,
                        claim.Source.Extension),
                    cancellationToken)
                .ConfigureAwait(false);
            var derived = await _pageRangeExtractor.ExtractAsync(
                    source,
                    "source.pdf",
                    claim.Profile.FirstPage,
                    claim.Profile.LastPage,
                    rotations,
                    cancellationToken)
                .ConfigureAwait(false);
            if (derived.Bytes.Length == 0
                || derived.Bytes.Length > _options.MaximumProviderMediaBytes
                || !string.Equals(
                    derived.Sha256,
                    Sha256(derived.Bytes),
                    StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(derived.Bytes);
                throw new UnitJobException("DERIVED_SOURCE_FAILED");
            }

            return derived;
        }
        catch (PreprocessingException exception)
        {
            throw new UnitJobException("DERIVED_SOURCE_FAILED", exception);
        }
        catch (FileNotFoundException exception)
        {
            throw new UnitJobException("SOURCE_MISSING", exception);
        }
    }

    private async Task<AttemptResult> ExecuteAttemptAsync(
        UnitClaim claim,
        DerivedPdfResult media,
        int attemptNumber,
        bool rotationsWereApplied,
        string? retryOfRequestId,
        ReadOnlyMemory<byte> credentialUtf8,
        CancellationToken cancellationToken)
    {
        var preparation = await PrepareAiAttemptAsync(
                claim,
                media,
                attemptNumber,
                rotationsWereApplied,
                retryOfRequestId,
                cancellationToken)
            .ConfigureAwait(false);
        if (preparation.ReusedValidatedResponseJson is not null)
        {
            using var document = JsonDocument.Parse(
                preparation.ReusedValidatedResponseJson);
            var reused = ValidateEnvelope(
                document.RootElement,
                preparation.RequestKey,
                preparation.Instruction,
                claim.Profile);
            return new AttemptResult(
                preparation.RequestId,
                preparation.Instruction,
                reused,
                ProviderCallMade: false);
        }

        var request = new AiProviderRequest(
            preparation.RequestKey,
            AiTaskTypes.TemplateExtraction,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            claim.Bundle.SystemInstruction,
            preparation.Instruction.UserInstruction,
            claim.Bundle.ResponseJsonSchema,
            [new AiMediaPart("application/pdf", media.Bytes, media.Sha256)],
            claim.MaxOutputTokens,
            claim.MediaResolution,
            claim.ThinkingLevel);
        AiProviderResponse response;
        try
        {
            TemplateGenerationMetrics.ExtractionCallDispatched(
                claim.Profile,
                claim.Connection.Provider,
                claim.Connection.ModelId);
            response = await _providerResolver
                .GetRequired(claim.Connection.Provider)
                .GenerateAsync(
                    claim.Connection,
                    credentialUtf8,
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
            await PersistProviderFailureAsync(
                    preparation,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (!AiResponseMetadataValidator.IsAccepted(
                response,
                claim.Connection.Provider,
                claim.Connection.ModelId))
        {
            await PersistInvalidAttemptAsync(
                    preparation,
                    response,
                    "AI_STRUCTURED_OUTPUT_INVALID",
                    cancellationToken)
                .ConfigureAwait(false);
            throw new UnitJobException("AI_STRUCTURED_OUTPUT_INVALID");
        }

        var responseJson = response.StructuredOutput.GetRawText();
        if (responseJson.Length > _options.MaximumStoredResponseCharacters)
        {
            await PersistInvalidAttemptAsync(
                    preparation,
                    response,
                    "AI_STRUCTURED_OUTPUT_INVALID",
                    cancellationToken)
                .ConfigureAwait(false);
            throw new UnitJobException("AI_STRUCTURED_OUTPUT_INVALID");
        }

        OrientationGatedTemplateExtraction extraction;
        try
        {
            extraction = ValidateEnvelope(
                response.StructuredOutput,
                preparation.RequestKey,
                preparation.Instruction,
                claim.Profile);
        }
        catch (InvalidDataException exception)
        {
            var code = exception.Message is
                "ORIENTATION_RESPONSE_INVALID"
                or "AI_STRUCTURED_OUTPUT_INVALID"
                    ? exception.Message
                    : "TEMPLATE_DRAFT_INVALID";
            await PersistInvalidAttemptAsync(
                    preparation,
                    response,
                    code,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new UnitJobException(code, exception);
        }

        await PersistAttemptSuccessAsync(
                preparation,
                response,
                responseJson,
                cancellationToken)
            .ConfigureAwait(false);
        return new AttemptResult(
            preparation.RequestId,
            preparation.Instruction,
            extraction,
            ProviderCallMade: true);
    }

    private Task<AttemptPreparation> PrepareAiAttemptAsync(
        UnitClaim claim,
        DerivedPdfResult media,
        int attemptNumber,
        bool rotationsWereApplied,
        string? retryOfRequestId,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
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
            var unit = await db.TemplateGenerationUnits
                .SingleOrDefaultAsync(item => item.Id == claim.UnitId, token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_CHANGED");
            if (attemptNumber is < 1 or > 2
                || unit.Status is not (
                    TemplateGenerationUnitStatus.Generating
                    or TemplateGenerationUnitStatus.RetryingAfterRotation))
            {
                throw new UnitJobException("SOURCE_CHANGED");
            }

            var existing = await db.AiRequests
                .AsNoTracking()
                .Where(item => item.EntityType == "template_generation_unit"
                    && item.EntityId == claim.UnitId
                    && item.Purpose == AiTaskTypes.TemplateExtraction
                    && item.RequestKey.StartsWith(
                        GetGenerationRunRequestKeyPrefix(claim.JobId))
                    && item.AttemptNumber == attemptNumber)
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var existingInstruction = TemplateExtractionInstructionBuilder.Build(
                    existing.RequestKey,
                    claim.UnitId,
                    claim.Profile,
                    rotationsWereApplied);
                var expectedHash = ComputeUnitInputHash(
                    claim,
                    media,
                    existingInstruction,
                    attemptNumber);
                if (!string.Equals(
                        existing.InputManifestHash,
                        expectedHash,
                        StringComparison.Ordinal))
                {
                    throw new UnitJobException("SOURCE_CHANGED");
                }

                if (existing.State == "succeeded"
                    && existing.ValidatedResponseJson is not null)
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return new AttemptPreparation(
                        existing.Id,
                        existing.RequestKey,
                        existingInstruction,
                        Pricing: null,
                        UsdToJpyMicros: 150_000_000,
                        existing.ValidatedResponseJson);
                }

                throw new UnitJobException(
                    existing.State == "dispatching"
                        ? "AI_PROVIDER_UNAVAILABLE"
                        : existing.ErrorCode ?? "TEMPLATE_EXTRACTION_FAILED");
            }

            var requestId = _ids.NewId();
            var requestKey =
                $"{GetGenerationRunRequestKeyPrefix(claim.JobId)}{attemptNumber}_{requestId}";
            var instruction = TemplateExtractionInstructionBuilder.Build(
                requestKey,
                claim.UnitId,
                claim.Profile,
                rotationsWereApplied);
            var inputHash = ComputeUnitInputHash(
                claim,
                media,
                instruction,
                attemptNumber);
            var pricing = await db.PricingSnapshots
                .AsNoTracking()
                .Where(item => item.Provider == claim.Connection.Provider
                    && item.ModelId == claim.Connection.ModelId
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
            var reservedUsdMicros = pricing is null
                ? 0
                : EstimateMaximumCost(
                    pricing,
                    claim.MaxOutputTokens,
                    instruction.UserInstruction,
                    media.Bytes.Length);
            var usageWindow = await GetUsageWindowAsync(db, now, token)
                .ConfigureAwait(false);
            var provenanceParentRequestId = retryOfRequestId;
            if (attemptNumber == 1 && provenanceParentRequestId is null)
            {
                provenanceParentRequestId = await db.AiRequests
                    .AsNoTracking()
                    .Where(item => item.EntityType == "template_generation_unit"
                        && item.EntityId == claim.UnitId
                        && item.Purpose == AiTaskTypes.TemplateExtraction
                        && !item.RequestKey.StartsWith(
                            GetGenerationRunRequestKeyPrefix(claim.JobId))
                        && !db.AiRequests.Any(candidate =>
                            candidate.RetryOfAiRequestId == item.Id))
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id)
                    .Select(item => item.Id)
                    .FirstOrDefaultAsync(token)
                    .ConfigureAwait(false);
            }

            if (budget?.Active == true)
            {
                if (pricing is null)
                {
                    throw new UnitJobException("COST_RESERVATION_FAILED");
                }

                var spend = await GetCommittedSpendAsync(db, usageWindow, token)
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
                    throw new UnitJobException("COST_RESERVATION_FAILED");
                }
            }

            db.AiRequests.Add(new AiRequestEntity
            {
                Id = requestId,
                RequestKey = requestKey,
                AiTaskProfileId = claim.TaskProfileId,
                TaskProfileRevision = claim.TaskProfileRevision,
                Purpose = AiTaskTypes.TemplateExtraction,
                EntityType = "template_generation_unit",
                EntityId = claim.UnitId,
                EntityRevision = unit.Revision,
                InputManifestHash = inputHash,
                AttemptNumber = attemptNumber,
                RetryOfAiRequestId = provenanceParentRequestId,
                State = "dispatching",
                DispatchAttempt = 1,
                PossibleDuplicate = false,
                CreatedAt = now,
                UpdatedAt = now,
                DispatchedAt = now,
            });
            db.AiBudgetReservations.Add(new AiBudgetReservationEntity
            {
                Id = _ids.NewId(),
                AiRequestId = requestId,
                UsageDay = usageWindow.Day,
                UsageMonth = usageWindow.Month,
                ReservedUsdMicros = reservedUsdMicros,
                ActualUsdMicros = 0,
                State = "reserved",
                CreatedAt = now,
            });
            job.ProgressBasisPoints = attemptNumber == 1 ? 4_000 : 7_000;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new AttemptPreparation(
                requestId,
                requestKey,
                instruction,
                pricing is null
                    ? null
                    : new PricingInfo(
                        pricing.Id,
                        pricing.InputUsdMicrosPerMillionTokens,
                        pricing.OutputUsdMicrosPerMillionTokens,
                        pricing.ThinkingUsdMicrosPerMillionTokens),
                budget?.UsdToJpyMicros ?? 150_000_000,
                ReusedValidatedResponseJson: null);
        }, cancellationToken);

    private static OrientationGatedTemplateExtraction ValidateEnvelope(
        JsonElement root,
        string requestKey,
        BuiltTemplateExtractionInstruction instruction,
        TemplateGenerationProfile profile)
    {
        var pageCount = profile.LastPage - profile.FirstPage + 1;
        var evidence = new Dictionary<string, TemplateExtractionSourceEvidence>(
            StringComparer.Ordinal)
        {
            [instruction.Pages[0].SourceId] = new(
                instruction.Pages[0].SourceId,
                "unit_test_paper",
                pageCount),
        };
        return OrientationGatedTemplateExtractionValidator.Validate(
            root,
            requestKey,
            instruction.Pages,
            evidence,
            defaultPointsMilli: 1_000,
            targetTotalPointsMilli: null);
    }

    private Task PersistAttemptSuccessAsync(
        AttemptPreparation preparation,
        AiProviderResponse response,
        string validatedResponseJson,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(
                    item => item.Id == preparation.RequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("TEMPLATE_EXTRACTION_FAILED");
            if (request.State != "dispatching" || request.PossibleDuplicate)
            {
                throw new UnitJobException("TEMPLATE_EXTRACTION_FAILED");
            }

            var now = _timeProvider.GetUtcNow();
            request.State = "succeeded";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.AcceptedResponseHash = Sha256(validatedResponseJson);
            request.ValidatedResponseJson = validatedResponseJson;
            request.ErrorCode = null;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(
                db,
                preparation,
                response,
                now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task PersistInvalidAttemptAsync(
        AttemptPreparation preparation,
        AiProviderResponse response,
        string errorCode,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(
                    item => item.Id == preparation.RequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("TEMPLATE_EXTRACTION_FAILED");
            var now = _timeProvider.GetUtcNow();
            request.State = "invalid_output";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.ErrorCode = errorCode;
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            AddUsageAndSettleReservation(
                db,
                preparation,
                response,
                now);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task PersistProviderFailureAsync(
        AttemptPreparation preparation,
        AiProviderException exception,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var request = await db.AiRequests
                .SingleOrDefaultAsync(
                    item => item.Id == preparation.RequestId,
                    token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("TEMPLATE_EXTRACTION_FAILED");
            var reservation = await db.AiBudgetReservations
                .SingleOrDefaultAsync(
                    item => item.AiRequestId == request.Id,
                    token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("COST_RESERVATION_FAILED");
            var now = _timeProvider.GetUtcNow();
            var ambiguous = AiProviderRuntime.IsAmbiguousDispatch(exception);
            request.State = "failed";
            request.PossibleDuplicate = ambiguous;
            request.ErrorCode = "AI_PROVIDER_UNAVAILABLE";
            request.SafeErrorDetail = null;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            if (ambiguous)
            {
                reservation.ActualUsdMicros = reservation.ReservedUsdMicros;
                reservation.State = "settled";
                reservation.SettledAt = now;
            }
            else
            {
                reservation.ActualUsdMicros = 0;
                reservation.State = "released";
                reservation.SettledAt = now;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task PersistRotationRequestAsync(
        UnitClaim claim,
        IReadOnlyList<TemplatePageOrientation> decisions,
        IReadOnlyDictionary<int, int> rotations,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var unit = await db.TemplateGenerationUnits
                .Include(item => item.Batch)
                .SingleOrDefaultAsync(item => item.Id == claim.UnitId, token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_CHANGED");
            if (unit.Status != TemplateGenerationUnitStatus.Generating
                || unit.OrientationAttemptCount != 0)
            {
                throw new UnitJobException("ORIENTATION_RETRY_EXHAUSTED");
            }

            var decisionByPage = decisions.ToDictionary(
                item => item.PageId,
                StringComparer.Ordinal);
            var manifest = rotations
                .OrderBy(item => item.Key)
                .Select(item => new AppliedPageRotation(
                    $"{unit.Id}:page:{item.Key - unit.FirstPage + 1}",
                    item.Key,
                    item.Value,
                    "gemini",
                    decisionByPage[$"{unit.Id}:page:{item.Key - unit.FirstPage + 1}"]
                        .Confidence))
                .ToArray();
            var now = _timeProvider.GetUtcNow();
            unit.Status = TemplateGenerationUnitStatus.Rotating;
            unit.OrientationAttemptCount = 1;
            unit.AppliedRotationsJson = JsonSerializer.Serialize(
                manifest,
                JsonOptions);
            AddWarning(
                unit,
                new GenerationWarning(
                    "ORIENTATION_CORRECTED",
                    GenerationWarningSeverity.Information,
                    "ページの向きを自動で補正しました。"));
            AddAudit(
                db,
                unit.Batch.CreatedByUserId,
                "TemplateUnitRotationRequested",
                "template_generation_unit",
                unit.Id,
                claim.CorrelationId,
                new
                {
                    rotations = rotations.OrderBy(item => item.Key)
                        .Select(item => new
                        {
                            pageNumber = item.Key,
                            clockwiseDegrees = item.Value,
                        }),
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    batchRowVersion = unit.Batch.Revision,
                    profileVersion = claim.Profile.ProfileVersion,
                    promptVersion = claim.Profile.ExtractionPromptVersion,
                    schemaVersion = claim.Profile.ExtractionSchemaVersion,
                });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private Task MarkRetryingAfterRotationAsync(
        UnitClaim claim,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var unit = await db.TemplateGenerationUnits
                .SingleOrDefaultAsync(item => item.Id == claim.UnitId, token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_CHANGED");
            if (unit.Status != TemplateGenerationUnitStatus.Rotating
                || unit.OrientationAttemptCount != 1)
            {
                throw new UnitJobException("ORIENTATION_RETRY_EXHAUSTED");
            }

            unit.Status = TemplateGenerationUnitStatus.RetryingAfterRotation;
            AddAudit(
                db,
                claim.CreatedByUserId,
                "TemplateUnitPagesRotated",
                "template_generation_unit",
                unit.Id,
                claim.CorrelationId,
                new
                {
                    unit.OrientationAttemptCount,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    profileVersion = claim.Profile.ProfileVersion,
                    promptVersion = claim.Profile.ExtractionPromptVersion,
                    schemaVersion = claim.Profile.ExtractionSchemaVersion,
                });
            AddAudit(
                db,
                claim.CreatedByUserId,
                "TemplateUnitGenerationRetried",
                "template_generation_unit",
                unit.Id,
                claim.CorrelationId,
                new
                {
                    automaticRetry = 1,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    profileVersion = claim.Profile.ProfileVersion,
                    promptVersion = claim.Profile.ExtractionPromptVersion,
                    schemaVersion = claim.Profile.ExtractionSchemaVersion,
                });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<SuccessCostSnapshot> PersistSuccessAsync(
        UnitClaim claim,
        DerivedPdfResult media,
        ValidatedTemplateExtraction extraction,
        int providerCallCount,
        CancellationToken cancellationToken)
    {
        var missingPaperName = false;
        var missingGrade = false;
        var gradeConflict = false;
        var stepNameMismatchCount = 0;
        var actualUsdMicros = 0L;
        long? failedBatchUsdMicros = null;
        ContentWriteResult stored;
        await using (var source = new MemoryStream(media.Bytes, writable: false))
        {
            stored = await _contentStore.PutAsync(
                    source,
                    ContentStorageClass.TemplateDerived,
                    "pdf",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.Equals(
                stored.Locator.Sha256,
                media.Sha256,
                StringComparison.Ordinal))
        {
            throw new UnitJobException("DERIVED_SOURCE_FAILED");
        }

        await _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await LoadOwnedJobAsync(db, claim.JobId, token)
                .ConfigureAwait(false);
            var unit = await db.TemplateGenerationUnits
                .Include(item => item.Batch)
                .SingleOrDefaultAsync(item => item.Id == claim.UnitId, token)
                .ConfigureAwait(false)
                ?? throw new UnitJobException("SOURCE_CHANGED");
            if (unit.Status is not (
                    TemplateGenerationUnitStatus.Generating
                    or TemplateGenerationUnitStatus.RetryingAfterRotation)
                || unit.ExtractionDraftHash is not null)
            {
                throw new UnitJobException("SOURCE_CHANGED");
            }

            var now = _timeProvider.GetUtcNow();
            var fileObject = await db.FileObjects
                .SingleOrDefaultAsync(item =>
                    item.StorageClass == ContentStorageClass.TemplateDerived.ToString()
                    && item.Sha256 == stored.Locator.Sha256, token)
                .ConfigureAwait(false);
            if (fileObject is null)
            {
                fileObject = new FileObjectEntity
                {
                    Id = _ids.NewId(),
                    Sha256 = stored.Locator.Sha256,
                    Bytes = stored.Locator.Bytes,
                    VerifiedMime = "application/pdf",
                    Extension = stored.Locator.Extension,
                    RelativeObjectPath = stored.RelativePath,
                    StorageClass = ContentStorageClass.TemplateDerived.ToString(),
                    RetentionClass = "template_source",
                    ManagedScanBytes = false,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 0,
                };
                db.FileObjects.Add(fileObject);
            }
            else if (fileObject.Bytes != stored.Locator.Bytes
                || fileObject.VerifiedMime != "application/pdf"
                || fileObject.Extension != stored.Locator.Extension
                || fileObject.RelativeObjectPath != stored.RelativePath
                || fileObject.RetentionClass != "template_source"
                || fileObject.ManagedScanBytes
                || fileObject.State != "available"
                || fileObject.VerifiedAt is null)
            {
                throw new UnitJobException("DERIVED_SOURCE_FAILED");
            }

            var fileReference = await db.FileReferences
                .SingleOrDefaultAsync(item =>
                    item.OwnerType == "template_generation_unit"
                    && item.OwnerId == unit.Id
                    && item.Purpose == "derived_source", token)
                .ConfigureAwait(false);
            if (fileReference is null)
            {
                fileReference = new FileReferenceEntity
                {
                    Id = _ids.NewId(),
                    FileObjectId = fileObject.Id,
                    OwnerType = "template_generation_unit",
                    OwnerId = unit.Id,
                    Purpose = "derived_source",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                };
                db.FileReferences.Add(fileReference);
                fileObject.ReferenceCountCache = checked(
                    fileObject.ReferenceCountCache + 1);
            }
            else if (fileReference.FileObjectId != fileObject.Id)
            {
                throw new UnitJobException("DERIVED_SOURCE_FAILED");
            }

            var derivedSource = await db.TemplateGenerationDerivedSources
                .SingleOrDefaultAsync(item => item.UnitId == unit.Id, token)
                .ConfigureAwait(false);
            var derivationType = unit.OrientationAttemptCount == 0
                ? "pageRange"
                : "pageRangeAndRotation";
            if (derivedSource is null)
            {
                derivedSource = new TemplateGenerationDerivedSourceEntity
                {
                    Id = _ids.NewId(),
                    UnitId = unit.Id,
                    ParentSourceId = unit.Batch.SourceId,
                    ParentFirstPage = unit.FirstPage,
                    ParentLastPage = unit.LastPage,
                    OriginalContentSha256 = claim.Source.Sha256,
                    DerivationType = derivationType,
                    AppliedRotationsJson = unit.AppliedRotationsJson,
                    DerivationPolicyVersion = media.DerivationPolicyVersion,
                    DerivedContentSha256 = media.Sha256,
                    FileReferenceId = fileReference.Id,
                    CreatedAt = now,
                };
                db.TemplateGenerationDerivedSources.Add(derivedSource);
            }
            else if (!string.Equals(
                    derivedSource.DerivedContentSha256,
                    media.Sha256,
                    StringComparison.Ordinal))
            {
                throw new UnitJobException("DERIVED_SOURCE_FAILED");
            }

            var draft = TemplateGenerationDraftFactory.Create(extraction);
            var draftJson = JsonSerializer.Serialize(draft, JsonOptions);
            var draftHash = Sha256(draftJson);
            unit.DerivedSourceObjectKey = stored.RelativePath;
            unit.DerivedSourceSha256 = media.Sha256;
            unit.ExtractionDraftJson = draftJson;
            unit.ExtractionDraftHash = draftHash;
            unit.PrintedTestName = NormalizePrintedNameOrNull(
                extraction.Metadata.Title);
            ApplyGradeResolution(unit, extraction.Metadata.GradeLabel);
            missingPaperName = unit.PrintedTestName is null;
            var resolutionWarningCodes = ReadWarnings(unit)
                .Select(warning => warning.Code)
                .ToHashSet(StringComparer.Ordinal);
            missingGrade = resolutionWarningCodes.Contains(
                GradeResolutionService.RequiredErrorCode);
            gradeConflict = unit.FilenameGrade != GradeLevel.Unknown
                && unit.PaperGrade != GradeLevel.Unknown
                && unit.FilenameGrade != unit.PaperGrade;
            unit.Status = TemplateGenerationUnitStatus.Extracted;
            unit.Batch.CompletedUnitCount = await db.TemplateGenerationUnits
                .CountAsync(item => item.BatchId == unit.BatchId
                    && (item.Status == TemplateGenerationUnitStatus.Extracted
                        || item.Id == unit.Id), token)
                .ConfigureAwait(false);
            unit.Batch.FailedUnitCount = await db.TemplateGenerationUnits
                .CountAsync(item => item.BatchId == unit.BatchId
                    && item.Status == TemplateGenerationUnitStatus.Failed, token)
                .ConfigureAwait(false);
            actualUsdMicros = await db.AiBudgetReservations
                .AsNoTracking()
                .Where(item => item.State == "settled"
                    && item.AiRequest.EntityType == "template_generation_unit"
                    && item.AiRequest.EntityId == unit.Id
                    && item.AiRequest.RequestKey.StartsWith(
                        GetGenerationRunRequestKeyPrefix(claim.JobId)))
                .SumAsync(item => (long?)item.ActualUsdMicros, token)
                .ConfigureAwait(false)
                ?? 0;
            AddAudit(
                db,
                unit.Batch.CreatedByUserId,
                "TemplateUnitExtracted",
                "template_generation_unit",
                unit.Id,
                claim.CorrelationId,
                new
                {
                    providerCallCount,
                    questionCount = draft.Pages.Sum(page => page.Questions.Count),
                    extractionDraftHash = draftHash,
                    derivedSourceSha256 = media.Sha256,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    batchRowVersion = unit.Batch.Revision,
                    profileVersion = claim.Profile.ProfileVersion,
                    promptVersion = claim.Profile.ExtractionPromptVersion,
                    schemaVersion = claim.Profile.ExtractionSchemaVersion,
                    actualUsdMicros,
                });
            CompleteJob(job, now);

            var allUnits = await db.TemplateGenerationUnits
                .Where(item => item.BatchId == unit.BatchId)
                .OrderBy(item => item.Sequence)
                .ToListAsync(token)
                .ConfigureAwait(false);
            if (allUnits.All(item =>
                    item.Status == TemplateGenerationUnitStatus.Extracted))
            {
                stepNameMismatchCount = PrepareFinalCheck(unit.Batch, allUnits);
            }
            else if (allUnits.All(item => item.Status is
                         TemplateGenerationUnitStatus.Extracted
                         or TemplateGenerationUnitStatus.Failed)
                && allUnits.Any(item =>
                    item.Status == TemplateGenerationUnitStatus.Failed))
            {
                unit.Batch.Status = TemplateGenerationBatchStatus.Failed;
                var observation = await TemplateGenerationCostObservationLedger
                    .PrepareBatchObservationAsync(
                        db,
                        unit.BatchId,
                        unit.Batch.CurrentOperationId ?? claim.JobId,
                        "failed",
                        unit.Batch.CreatedByUserId,
                        _ids,
                        now,
                        token)
                    .ConfigureAwait(false);
                failedBatchUsdMicros = observation?.DeltaActualUsdMicros;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (missingPaperName)
        {
            TemplateGenerationMetrics.MissingPaperName(claim.Profile);
        }

        if (missingGrade)
        {
            TemplateGenerationMetrics.MissingGrade(claim.Profile);
        }

        if (gradeConflict)
        {
            TemplateGenerationMetrics.GradeConflict(claim.Profile);
        }

        for (var index = 0; index < stepNameMismatchCount; index++)
        {
            TemplateGenerationMetrics.StepNameMismatch(
                claim.Profile.PromptSystem,
                claim.Profile.ProfileVersion);
        }

        return new SuccessCostSnapshot(
            actualUsdMicros,
            failedBatchUsdMicros);
    }

    private Task<FailureCostSnapshot> PersistFailureAsync(
        string jobId,
        string errorCode,
        CancellationToken cancellationToken) =>
        _writeCoordinator.ExecuteAsync(async token =>
        {
            await using var db = await _dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var job = await db.BackgroundJobs
                .SingleOrDefaultAsync(item => item.Id == jobId, token)
                .ConfigureAwait(false);
            if (job is null || job.Type != JobType)
            {
                return FailureCostSnapshot.Empty;
            }

            var payload = DeserializePayload(job.PayloadJson);
            var unit = await db.TemplateGenerationUnits
                .Include(item => item.Batch)
                .SingleOrDefaultAsync(item => item.Id == payload.UnitId, token)
                .ConfigureAwait(false);
            if (job.State == "cancelled"
                || unit?.Batch.Status == TemplateGenerationBatchStatus.Cancelled)
            {
                if (unit is null)
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return FailureCostSnapshot.Empty;
                }

                var requestPrefix = GetGenerationRunRequestKeyPrefix(job.Id);
                var unitHasDispatchingRequest = await db.AiRequests
                    .AsNoTracking()
                    .AnyAsync(item => item.EntityType
                            == "template_generation_unit"
                        && item.EntityId == unit.Id
                        && item.RequestKey.StartsWith(requestPrefix)
                        && item.State == "dispatching", token)
                    .ConfigureAwait(false);
                var unitObservation = unitHasDispatchingRequest
                    ? null
                    : await TemplateGenerationCostObservationLedger
                        .PrepareCancelledUnitObservationAsync(
                            db,
                            unit,
                            job.Id,
                            unit.Batch.CreatedByUserId,
                            _ids,
                            _timeProvider.GetUtcNow(),
                            token)
                        .ConfigureAwait(false);
                var batchUnitIds = await db.TemplateGenerationUnits
                    .AsNoTracking()
                    .Where(item => item.BatchId == unit.BatchId)
                    .Select(item => item.Id)
                    .ToArrayAsync(token)
                    .ConfigureAwait(false);
                var batchHasDispatchingRequest = await db.AiRequests
                    .AsNoTracking()
                    .AnyAsync(item => item.EntityType
                            == "template_generation_unit"
                        && batchUnitIds.Contains(item.EntityId)
                        && item.State == "dispatching", token)
                    .ConfigureAwait(false);
                var cancelledBatchObservation = batchHasDispatchingRequest
                    ? null
                    : await TemplateGenerationCostObservationLedger
                        .PrepareBatchObservationAsync(
                            db,
                            unit.BatchId,
                            unit.Batch.CurrentOperationId ?? job.Id,
                            "cancelled",
                            unit.Batch.CreatedByUserId,
                            _ids,
                            _timeProvider.GetUtcNow(),
                            token)
                        .ConfigureAwait(false);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new FailureCostSnapshot(
                    unitObservation?.ActualUsdMicros ?? 0,
                    cancelledBatchObservation?.DeltaActualUsdMicros ?? 0,
                    unitObservation?.Outcome,
                    cancelledBatchObservation?.Outcome,
                    unit.Batch.TestType,
                    unit.Batch.PromptSystem,
                    TemplateGenerationProfile.CurrentProfileVersion,
                    unitObservation?.Provider,
                    unitObservation?.Model);
            }

            var unitBecameFailed = unit is not null
                && unit.Status is not (
                    TemplateGenerationUnitStatus.Extracted
                    or TemplateGenerationUnitStatus.Confirmed
                    or TemplateGenerationUnitStatus.Failed);
            var now = _timeProvider.GetUtcNow();
            job.State = "failed";
            job.ErrorCode = BoundedCode(errorCode);
            job.SafeErrorDetail = null;
            job.ProgressBasisPoints = Math.Min(job.ProgressBasisPoints, 9_000);
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            var recoveredDispatchCount = await RecoverAmbiguousDispatchesAsync(
                    db,
                    job.Id,
                    payload.UnitId,
                    now,
                    token)
                .ConfigureAwait(false);
            if (unitBecameFailed)
            {
                ArgumentNullException.ThrowIfNull(unit);
                unit.Status = TemplateGenerationUnitStatus.Failed;
                AddWarning(
                    unit,
                    new GenerationWarning(
                        BoundedCode(errorCode),
                        GenerationWarningSeverity.Blocking,
                        FailureMessage(errorCode)));
                unit.Batch.LastErrorCode = BoundedCode(errorCode);
                unit.Batch.FailedUnitCount = await db.TemplateGenerationUnits
                    .CountAsync(item => item.BatchId == unit.BatchId
                        && (item.Status == TemplateGenerationUnitStatus.Failed
                            || item.Id == unit.Id), token)
                    .ConfigureAwait(false);
                AddAudit(
                    db,
                    unit.Batch.CreatedByUserId,
                    "TemplateUnitGenerationFailed",
                    "template_generation_unit",
                    unit.Id,
                    job.CorrelationId,
                    new
                    {
                        errorCode = BoundedCode(errorCode),
                        unitRowVersion = unit.Revision,
                        nextUnitRowVersion = checked(unit.Revision + 1),
                        batchRowVersion = unit.Batch.Revision,
                        profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                        promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                        schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                        recoveredAmbiguousDispatchCount = recoveredDispatchCount,
                    });
            }

            var unitUsdMicros = 0L;
            TemplateGenerationBatchCostObservation? batchObservation = null;
            if (unitBecameFailed)
            {
                var runRequestKeyPrefix = GetGenerationRunRequestKeyPrefix(job.Id);
                var runReservations = await db.AiBudgetReservations
                    .Where(item => item.AiRequest.EntityType
                            == "template_generation_unit"
                        && item.AiRequest.EntityId == unit!.Id
                        && item.AiRequest.RequestKey.StartsWith(runRequestKeyPrefix))
                    .ToArrayAsync(token)
                    .ConfigureAwait(false);
                unitUsdMicros = runReservations
                    .Where(item => item.State == "settled")
                    .Aggregate(
                        0L,
                        (total, item) => checked(
                            total + item.ActualUsdMicros));
                var allUnits = await db.TemplateGenerationUnits
                    .Where(item => item.BatchId == unit!.BatchId)
                    .OrderBy(item => item.Sequence)
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                if (allUnits.All(item => item.Status is
                        TemplateGenerationUnitStatus.Extracted
                        or TemplateGenerationUnitStatus.Failed)
                    && allUnits.Any(item =>
                        item.Status == TemplateGenerationUnitStatus.Failed))
                {
                    unit!.Batch.Status = TemplateGenerationBatchStatus.Failed;
                    batchObservation = await
                        TemplateGenerationCostObservationLedger
                            .PrepareBatchObservationAsync(
                                db,
                                unit.BatchId,
                                unit.Batch.CurrentOperationId ?? job.Id,
                                "failed",
                                unit.Batch.CreatedByUserId,
                                _ids,
                                now,
                                token)
                        .ConfigureAwait(false);
                }
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return unitBecameFailed
                ? new FailureCostSnapshot(
                    unitUsdMicros,
                    batchObservation?.DeltaActualUsdMicros ?? 0,
                    UnitOutcome: "failed",
                    batchObservation?.Outcome,
                    unit!.Batch.TestType,
                    unit.Batch.PromptSystem,
                    TemplateGenerationProfile.CurrentProfileVersion,
                    Provider: null,
                    Model: null)
                : FailureCostSnapshot.Empty;
        }, cancellationToken);

    private static async Task<int> RecoverAmbiguousDispatchesAsync(
        OokiGraderDbContext db,
        string jobId,
        string unitId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var requestPrefix = GetGenerationRunRequestKeyPrefix(jobId);
        var requests = await db.AiRequests
            .Where(item => item.EntityType == "template_generation_unit"
                && item.EntityId == unitId
                && item.RequestKey.StartsWith(requestPrefix)
                && item.State == "dispatching")
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (requests.Length == 0)
        {
            return 0;
        }

        var requestIds = requests.Select(item => item.Id).ToArray();
        var reservations = await db.AiBudgetReservations
            .Where(item => requestIds.Contains(item.AiRequestId))
            .ToDictionaryAsync(
                item => item.AiRequestId,
                StringComparer.Ordinal,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var request in requests)
        {
            request.State = "failed";
            request.PossibleDuplicate = true;
            request.ErrorCode = "AI_PROVIDER_UNAVAILABLE";
            request.SafeErrorDetail = "recovered_dispatching_request";
            request.CompletedAt ??= now;
            request.UpdatedAt = now;
            if (reservations.TryGetValue(request.Id, out var reservation)
                && reservation.State == "reserved")
            {
                reservation.ActualUsdMicros = reservation.ReservedUsdMicros;
                reservation.State = "settled";
                reservation.SettledAt = now;
            }
        }

        return requests.Length;
    }

    private static void ApplyGradeResolution(
        TemplateGenerationUnitEntity unit,
        string? printedGradeLabel)
    {
        var warnings = ReadWarnings(unit)
            .Where(warning => warning.Code is not (
                GradeResolutionService.RequiredErrorCode
                or GradeResolutionService.ConflictErrorCode))
            .ToList();
        FileNameGradeResult filename;
        if (warnings.Any(item =>
                item.Code == GradeFromFileNameParser.ConflictErrorCode))
        {
            filename = new FileNameGradeResult(
                GradeLevel.Unknown,
                IsUnambiguous: false,
                MatchedToken: null,
                GradeFromFileNameParser.ConflictErrorCode);
        }
        else
        {
            filename = new FileNameGradeResult(
                unit.FilenameGrade,
                unit.FilenameGrade != GradeLevel.Unknown,
                MatchedToken: null,
                ErrorCode: null);
        }

        PaperGradeResult paper;
        if (string.IsNullOrWhiteSpace(printedGradeLabel))
        {
            paper = new PaperGradeResult(
                GradeLevel.Unknown,
                IsUnambiguous: false);
        }
        else
        {
            var parsed = GradeFromFileNameParser.Parse(
                printedGradeLabel + ".pdf");
            paper = new PaperGradeResult(
                parsed.Grade,
                parsed.IsUnambiguous,
                printedGradeLabel,
                parsed.ErrorCode is null
                    ? null
                    : GradeResolutionService.ConflictErrorCode);
        }

        unit.PaperGrade = paper.IsUnambiguous
            ? paper.Grade
            : GradeLevel.Unknown;
        var resolution = GradeResolutionService.Resolve(
            filename,
            paper,
            userSelection: null);
        unit.ResolvedGrade = resolution.Grade;
        unit.GradeEvidence = resolution.Evidence;
        if (!resolution.IsResolved && resolution.ErrorCode is not null)
        {
            warnings.Add(new GenerationWarning(
                resolution.ErrorCode,
                GenerationWarningSeverity.Blocking,
                resolution.ErrorCode == GradeResolutionService.ConflictErrorCode
                    ? "ファイル名とテスト用紙の学年が一致しません。"
                    : "学年を確認できませんでした。学年を選択してください。"));
        }

        WriteWarnings(unit, warnings);
    }

    private static int PrepareFinalCheck(
        TemplateGenerationBatchEntity batch,
        List<TemplateGenerationUnitEntity> units)
    {
        foreach (var unit in units)
        {
            WriteWarnings(
                unit,
                ReadWarnings(unit)
                    .Where(warning => !DynamicWarningCodes.Contains(warning.Code))
                    .ToList());
            unit.UserConfirmedBaseName = null;
            unit.FinalTemplateName = null;
        }

        if (batch.TestType != TestType.Other)
        {
            foreach (var unit in units)
            {
                if (unit.ResolvedGrade is >= GradeLevel.Grade1
                    and <= GradeLevel.Grade6)
                {
                    unit.FinalTemplateName =
                        TemplateNamePolicy.CreateKnownTestName(
                            batch.TestType,
                            batch.Subject,
                            unit.ResolvedGrade,
                            unit.Sequence,
                            unit.StepSetIndex,
                            unit.StepVariationIndex);
                }
            }
        }
        else
        {
            foreach (var unit in units)
            {
                try
                {
                    unit.FinalTemplateName = TemplateNamePolicy.CreateFinalName(
                        batch.TestType,
                        unit.PrintedTestName);
                }
                catch (DomainValidationException)
                {
                    AddWarning(
                        unit,
                        new GenerationWarning(
                            TemplateNamePolicy.NameRequiredErrorCode,
                            GenerationWarningSeverity.Blocking,
                            "テスト名を入力してください。"));
                }
            }
        }

        var duplicateNames = units
            .Where(unit => unit.FinalTemplateName is not null)
            .GroupBy(unit => unit.FinalTemplateName!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        foreach (var unit in duplicateNames)
        {
            AddWarning(
                unit,
                new GenerationWarning(
                    TemplateNamePolicy.DuplicateNameErrorCode,
                    GenerationWarningSeverity.Blocking,
                    "作成予定のテンプレート名が重複しています。"));
        }

        batch.CompletedUnitCount = units.Count;
        batch.FailedUnitCount = 0;
        batch.Status = TemplateGenerationBatchStatus.NeedsFinalCheck;
        batch.LastErrorCode = null;
        return 0;
    }

    private static string? NormalizePrintedNameOrNull(string? value)
    {
        try
        {
            return TemplateNamePolicy.NormalizePrintedName(value);
        }
        catch (DomainValidationException)
        {
            return null;
        }
    }

    private static Dictionary<int, int> ToOriginalPageRotations(
        TemplateGenerationProfile profile,
        IReadOnlyList<TemplateExtractionPageManifest> suppliedPages,
        IReadOnlyList<TemplatePageOrientation> decisions)
    {
        var localPageById = suppliedPages.ToDictionary(
            page => page.PageId,
            page => page.PageNumber,
            StringComparer.Ordinal);
        return decisions.ToDictionary(
            decision => checked(
                profile.FirstPage + localPageById[decision.PageId] - 1),
            decision => decision.ClockwiseDegreesToUpright);
    }

    private static List<GenerationWarning> ReadWarnings(
        TemplateGenerationUnitEntity unit)
    {
        try
        {
            return JsonSerializer.Deserialize<List<GenerationWarning>>(
                    unit.WarningsJson,
                    JsonOptions)
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddWarning(
        TemplateGenerationUnitEntity unit,
        GenerationWarning warning)
    {
        var warnings = ReadWarnings(unit);
        warnings.RemoveAll(item => item.Code == warning.Code);
        warnings.Add(warning);
        WriteWarnings(unit, warnings);
    }

    private static void WriteWarnings(
        TemplateGenerationUnitEntity unit,
        IReadOnlyCollection<GenerationWarning> warnings)
    {
        unit.WarningsJson = JsonSerializer.Serialize(
            warnings.OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Code, StringComparer.Ordinal),
            JsonOptions);
    }

    private static string FailureMessage(string code) => code switch
    {
        "ORIENTATION_RETRY_EXHAUSTED" =>
            "向き補正後もページの向きを確認できませんでした。PDFを修正して再アップロードしてください。",
        "ORIENTATION_RESPONSE_INVALID" =>
            "ページ向きの応答を安全に確認できませんでした。",
        "SOURCE_MISSING" => "元PDFが見つかりません。",
        "SOURCE_CHANGED" => "元PDFまたは生成設定が変更されました。",
        "DERIVED_SOURCE_FAILED" => "PDFの分割または向き補正に失敗しました。",
        "AI_PROVIDER_UNAVAILABLE" => "AIサービスを利用できませんでした。",
        "COST_RESERVATION_FAILED" => "AI利用予算を確保できませんでした。",
        _ => "テンプレートの抽出に失敗しました。",
    };

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
        catch (TimeZoneNotFoundException exception)
        {
            throw new UnitJobException("COST_RESERVATION_FAILED", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new UnitJobException("COST_RESERVATION_FAILED", exception);
        }

        var local = TimeZoneInfo.ConvertTime(now, timeZone);
        return new UsageWindow(
            DateOnly.FromDateTime(local.DateTime),
            $"{local.Year:0000}-{local.Month:00}");
    }

    private static async Task<CommittedSpend> GetCommittedSpendAsync(
        OokiGraderDbContext db,
        UsageWindow window,
        CancellationToken cancellationToken)
    {
        var reservations = await db.AiBudgetReservations
            .AsNoTracking()
            .Where(item => (item.UsageDay == window.Day
                    || item.UsageMonth == window.Month)
                && (item.State == "reserved" || item.State == "settled"))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        static long Amount(AiBudgetReservationEntity item) =>
            item.State == "settled"
                ? item.ActualUsdMicros
                : item.ReservedUsdMicros;
        return new CommittedSpend(
            reservations.Where(item => item.UsageDay == window.Day)
                .Aggregate(0L, (total, item) => checked(total + Amount(item))),
            reservations.Where(item => item.UsageMonth == window.Month)
                .Aggregate(0L, (total, item) => checked(total + Amount(item))));
    }

    private static long EstimateMaximumCost(
        PricingSnapshotEntity pricing,
        int maxOutputTokens,
        string instruction,
        int mediaBytes)
    {
        var inputTokens = Math.Max(
            1,
            (Encoding.UTF8.GetByteCount(instruction) + mediaBytes + 3L) / 4L);
        return CalculateCost(
            inputTokens,
            maxOutputTokens,
            thinkingTokens: 0,
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
        var numerator = (BigInteger)inputTokens * inputRate
            + (BigInteger)outputTokens * outputRate
            + (BigInteger)thinkingTokens * thinkingRate;
        if (numerator <= 0)
        {
            return 0;
        }

        var result = (numerator + 999_999) / 1_000_000;
        if (result > long.MaxValue)
        {
            throw new UnitJobException("COST_RESERVATION_FAILED");
        }

        return (long)result;
    }

    private static long ConvertUsdToJpy(long usdMicros, long usdToJpyMicros)
    {
        var numerator = (BigInteger)usdMicros * usdToJpyMicros;
        if (numerator <= 0)
        {
            return 0;
        }

        var result = (numerator + 999_999) / 1_000_000;
        if (result > long.MaxValue)
        {
            throw new UnitJobException("COST_RESERVATION_FAILED");
        }

        return (long)result;
    }

    private static bool WouldExceedHardLimit(
        long committed,
        long reservation,
        long hardLimit) =>
        hardLimit >= 0 && (BigInteger)committed + reservation > hardLimit;

    private void AddUsageAndSettleReservation(
        OokiGraderDbContext db,
        AttemptPreparation preparation,
        AiProviderResponse response,
        DateTimeOffset now)
    {
        var reservation = db.AiBudgetReservations
            .SingleOrDefault(item =>
                item.AiRequestId == preparation.RequestId)
            ?? throw new UnitJobException("COST_RESERVATION_FAILED");
        var actualUsdMicros = AiProviderRuntime.ResolveActualUsdMicros(
            response.Usage,
            reservation.ReservedUsdMicros,
            preparation.Pricing is null
                ? null
                : () => CalculateCost(
                    response.Usage.PromptTokens ?? 0,
                    response.Usage.OutputTokens ?? 0,
                    response.Usage.ThinkingTokens ?? 0,
                    preparation.Pricing.InputRate,
                    preparation.Pricing.OutputRate,
                    preparation.Pricing.ThinkingRate));
        db.AiUsage.Add(new AiUsageEntity
        {
            Id = _ids.NewId(),
            AiRequestId = preparation.RequestId,
            RequestedProvider = response.Provider,
            RequestedModel = response.RequestedModel,
            ActualProvider = response.RoutedProvider ?? response.Provider,
            ActualModel = response.ActualModel,
            InputTokens = response.Usage.PromptTokens,
            CachedTokens = response.Usage.CachedTokens,
            OutputTokens = response.Usage.OutputTokens,
            ThinkingTokens = response.Usage.ThinkingTokens,
            TotalTokens = response.Usage.TotalTokens,
            PricingSnapshotId = preparation.Pricing?.Id,
            EstimatedUsdMicros = actualUsdMicros,
            EstimatedJpyMicros = ConvertUsdToJpy(
                actualUsdMicros,
                preparation.UsdToJpyMicros),
            ProviderRequestId = response.ProviderResponseId,
            MeasuredAt = now,
        });
        reservation.ActualUsdMicros = actualUsdMicros;
        reservation.State = "settled";
        reservation.SettledAt = now;
    }

    private static string ComputeUnitInputHash(
        UnitClaim claim,
        DerivedPdfResult media,
        BuiltTemplateExtractionInstruction instruction,
        int attemptNumber)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            claim.BatchPlanHash,
            claim.UnitId,
            profileHash = claim.Profile.ComputeHash(),
            claim.Profile.FirstPage,
            claim.Profile.LastPage,
            claim.Profile.StepSetIndex,
            claim.Profile.StepVariationIndex,
            claim.Source.Sha256,
            derivedSha256 = media.Sha256,
            rotations = media.AppliedRotations,
            instruction.Fingerprint,
            promptBundleHash = claim.Bundle.ContentHash,
            claim.Bundle.PromptVersion,
            claim.Bundle.SchemaVersion,
            provider = claim.Connection.Provider,
            model = claim.Connection.ModelId,
            preprocessingPipeline = PreprocessingOptions.DefaultPipelineVersion,
            attemptNumber,
        }, JsonOptions);
        return Sha256(canonical);
    }

    private static void ValidateSource(
        TemplateGenerationUnitEntity unit,
        FileReferenceEntity sourceReference)
    {
        var file = sourceReference.FileObject;
        if (sourceReference.OwnerType != "upload_session"
            || sourceReference.OwnerId != unit.Batch.SourceId
            || sourceReference.Purpose != "template_source"
            || file.State != "available"
            || file.StorageClass != ContentStorageClass.TemplateSource.ToString()
            || file.VerifiedMime != "application/pdf"
            || file.Bytes <= 0
            || !IsSha256(file.Sha256)
            || unit.FirstPage < 1
            || unit.LastPage < unit.FirstPage
            || unit.LastPage > unit.Batch.SourcePageCount
            || (unit.TestType == TestType.Hop
                && unit.LastPage != unit.FirstPage)
            || (unit.TestType == TestType.Step
                && unit.LastPage != unit.FirstPage + 1))
        {
            throw new UnitJobException("SOURCE_CHANGED");
        }
    }

    private static async Task<BackgroundJobEntity> LoadOwnedJobAsync(
        OokiGraderDbContext db,
        string jobId,
        CancellationToken cancellationToken)
    {
        var job = await db.BackgroundJobs
            .SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnitJobException("SOURCE_MISSING");
        if (job.Type != JobType
            || job.State != "leased"
            || string.IsNullOrWhiteSpace(job.LeaseOwner)
            || job.LeaseExpiresAt is null)
        {
            throw new UnitJobException("SOURCE_CHANGED");
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
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.CompletedAt = now;
        job.UpdatedAt = now;
    }

    private void AddAudit(
        OokiGraderDbContext db,
        string? actor,
        string eventType,
        string objectType,
        string objectId,
        string? correlationId,
        object metadata)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = _ids.NewId(),
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorStaffUserId = actor,
            EventType = eventType,
            ObjectType = objectType,
            ObjectId = objectId,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            SafeMetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
        });
    }

    private static UnitJobPayload DeserializePayload(string json)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<UnitJobPayload>(
                    json,
                    JsonOptions)
                ?? throw new JsonException();
            if (string.IsNullOrWhiteSpace(payload.UnitId)
                || string.IsNullOrWhiteSpace(payload.BatchId)
                || !IsSha256(payload.GenerationProfileHash))
            {
                throw new JsonException();
            }

            return payload;
        }
        catch (JsonException exception)
        {
            throw new UnitJobException("AI_PROFILE_MISSING", exception);
        }
    }

    private static string ToMediaResolution(string value) => value switch
    {
        "low" => "MEDIA_RESOLUTION_LOW",
        "medium" => "MEDIA_RESOLUTION_MEDIUM",
        "high" => "MEDIA_RESOLUTION_HIGH",
        "ultra_high" => "MEDIA_RESOLUTION_ULTRA_HIGH",
        _ => throw new UnitJobException("AI_PROFILE_MISSING"),
    };

    private static string ToThinkingLevel(string value) => value switch
    {
        "minimal" => "MINIMAL",
        "low" => "LOW",
        "medium" => "MEDIUM",
        "high" => "HIGH",
        _ => throw new UnitJobException("AI_PROFILE_MISSING"),
    };

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Sha256(string value) =>
        Sha256(Encoding.UTF8.GetBytes(value));

    private static string BoundedCode(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "TEMPLATE_EXTRACTION_FAILED"
            : value.Length <= 100
                ? value
                : value[..100];

    private static void RecordFailureMetrics(
        UnitClaim? claim,
        string errorCode,
        bool orientationRetryStarted,
        FailureCostSnapshot failure)
    {
        if (failure.UnitOutcome == "failed")
        {
            TemplateGenerationMetrics.UnitExtractionFailed(
                claim?.Profile,
                claim?.Connection.Provider ?? failure.Provider,
                claim?.Connection.ModelId ?? failure.Model,
                errorCode,
                failure.UnitUsdMicros);
            if (orientationRetryStarted)
            {
                TemplateGenerationMetrics.OrientationRetryFailed(
                    claim?.Profile,
                    claim?.Connection.Provider,
                    claim?.Connection.ModelId,
                    errorCode);
            }
        }
        else if (failure.UnitOutcome == "cancelled")
        {
            TemplateGenerationMetrics.UnitExtractionCancelled(
                failure.TestType,
                failure.PromptSystem,
                failure.ProfileVersion,
                claim?.Connection.Provider ?? failure.Provider,
                claim?.Connection.ModelId ?? failure.Model,
                failure.UnitUsdMicros);
        }

        if (failure.BatchOutcome is not null)
        {
            TemplateGenerationMetrics.BatchTerminated(
                failure.TestType,
                failure.PromptSystem,
                failure.ProfileVersion,
                failure.BatchOutcome,
                failure.BatchUsdMicros);
        }
    }

    private static string GetGenerationRunRequestKeyPrefix(string jobId) =>
        $"template_unit_run_{jobId}_";

    private sealed record JobLease(
        string Id,
        int SchemaVersion,
        string PayloadJson,
        string? CorrelationId,
        bool RecoveryOnly);

    private sealed record FailureCostSnapshot(
        long UnitUsdMicros,
        long BatchUsdMicros,
        string? UnitOutcome,
        string? BatchOutcome,
        TestType TestType,
        TemplatePromptSystem PromptSystem,
        int ProfileVersion,
        string? Provider,
        string? Model)
    {
        internal static FailureCostSnapshot Empty { get; } = new(
            UnitUsdMicros: 0,
            BatchUsdMicros: 0,
            UnitOutcome: null,
            BatchOutcome: null,
            TestType.Other,
            TemplatePromptSystem.Standard,
            TemplateGenerationProfile.CurrentProfileVersion,
            Provider: null,
            Model: null);
    }

    private sealed record SuccessCostSnapshot(
        long UnitUsdMicros,
        long? FailedBatchUsdMicros);

    private sealed record UnitJobPayload(
        string UnitId,
        string BatchId,
        string GenerationProfileHash);

    private sealed record SourceFileSnapshot(
        string Id,
        string Sha256,
        long Bytes,
        string Extension,
        string VerifiedMime);

    private sealed record UnitClaim(
        string JobId,
        string? CorrelationId,
        string UnitId,
        string BatchId,
        string SourceId,
        string CreatedByUserId,
        string BatchPlanHash,
        TemplateGenerationProfile Profile,
        SourceFileSnapshot Source,
        string TaskProfileId,
        long TaskProfileRevision,
        int MaxOutputTokens,
        string MediaResolution,
        string ThinkingLevel,
        string SecretReference,
        AiConnectionSettings Connection,
        AiPromptBundle Bundle);

    private sealed record PricingInfo(
        string Id,
        long InputRate,
        long OutputRate,
        long ThinkingRate);

    private sealed record AttemptPreparation(
        string RequestId,
        string RequestKey,
        BuiltTemplateExtractionInstruction Instruction,
        PricingInfo? Pricing,
        long UsdToJpyMicros,
        string? ReusedValidatedResponseJson);

    private sealed record AttemptResult(
        string RequestId,
        BuiltTemplateExtractionInstruction Instruction,
        OrientationGatedTemplateExtraction Extraction,
        bool ProviderCallMade);

    private sealed record UsageWindow(DateOnly Day, string Month);

    private sealed record CommittedSpend(
        long DailyUsdMicros,
        long MonthlyUsdMicros);

    private sealed class UnitJobException : Exception
    {
        public UnitJobException(string errorCode, Exception? inner = null)
            : base(errorCode, inner)
        {
            ErrorCode = BoundedCode(errorCode);
        }

        public string ErrorCode { get; }
    }
}
