using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Templates;
using OokiGrader.Domain.Common;
using OokiGrader.Domain.Grading;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Observability;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Preprocessing;

namespace OokiGrader.Host.Services;

public sealed record UpdateTemplateGenerationUnitCommand(
    string BatchId,
    string UnitId,
    string? BaseTestName,
    GradeLevel? ResolvedGrade,
    bool? GradeConfirmedByUser,
    string? TeacherNote,
    long ExpectedRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record UpdateTemplateGenerationStepSetCommand(
    string BatchId,
    int SetIndex,
    string BaseTestName,
    IReadOnlyDictionary<string, long> ExpectedUnitRowVersions,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record RetryTemplateGenerationBatchCommand(
    string BatchId,
    long ExpectedRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record CancelTemplateGenerationBatchCommand(
    string BatchId,
    long ExpectedRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record ConfirmTemplateGenerationBatchCommand(
    string BatchId,
    long ExpectedRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

/// <summary>
/// Owns the teacher final-check mutations and the all-or-nothing conversion of
/// canonical unit drafts into independent editable template versions.
/// </summary>
public sealed class TemplateGenerationFinalizationService
{
    private const int MaximumTeacherNoteLength = 4_000;
    private const long DefaultGeneratedQuestionPointsMilli = 1_000;
    private const string UnitJobDeduplicationPrefix =
        "template-generation-unit:";
    private const int UlidTextLength = 26;
    private static readonly TemplateUnitPlanner UnitPlanner = new();

    private static readonly HashSet<string> SupportedQuestionTypes =
    [
        "multiple_choice",
        "boolean",
        "numeric",
        "exact_short_text",
        "semantic_short_text",
        "multi_part",
        "subjective",
        "unsupported",
    ];

    private static readonly HashSet<string> SupportedAnswerProvenances =
    [
        "provided_model_answer",
        "ai_proposed",
        "unavailable",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly HashSet<string> NameWarningCodes =
    [
        TemplateNamePolicy.NameRequiredErrorCode,
        TemplateNamePolicy.StepNameMismatchErrorCode,
        TemplateNamePolicy.StepNameAlreadySuffixedErrorCode,
        TemplateNamePolicy.DuplicateNameErrorCode,
    ];

    private static readonly HashSet<string> GradeWarningCodes =
    [
        GradeResolutionService.RequiredErrorCode,
        GradeResolutionService.ConflictErrorCode,
        GradeFromFileNameParser.ConflictErrorCode,
    ];

    private static readonly HashSet<string> RetryClearedWarningCodes =
    [
        "SOURCE_MISSING",
        "SOURCE_CHANGED",
        "DERIVED_SOURCE_FAILED",
        "AI_PROFILE_MISSING",
        "AI_PROVIDER_UNAVAILABLE",
        "AI_STRUCTURED_OUTPUT_INVALID",
        "ORIENTATION_RESPONSE_INVALID",
        "ORIENTATION_CORRECTED",
        "TEMPLATE_EXTRACTION_FAILED",
        "TEMPLATE_DRAFT_INVALID",
        "COST_RESERVATION_FAILED",
    ];

    private readonly OokiGraderDbContext _db;
    private readonly IUlidGenerator _ids;
    private readonly TimeProvider _timeProvider;
    private readonly TemplateGenerationBatchService _batchService;

    public TemplateGenerationFinalizationService(
        OokiGraderDbContext db,
        IUlidGenerator ids,
        TimeProvider timeProvider,
        TemplateGenerationBatchService batchService)
    {
        _db = db;
        _ids = ids;
        _timeProvider = timeProvider;
        _batchService = batchService;
    }

    public async Task<TemplateGenerationBatchSnapshot> UpdateUnitAsync(
        UpdateTemplateGenerationUnitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await LoadBatchAsync(command.BatchId, cancellationToken)
            .ConfigureAwait(false);
        ValidateAccess(batch, command.StaffUserId, command.IsAdministrator);
        var operationKey = OperationKey(
            "unit-edit",
            command.OperationId,
            command.UnitId);
        if (batch.CurrentOperationId == operationKey)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetRequiredSnapshotAsync(
                    batch.Id,
                    command.StaffUserId,
                    command.IsAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        RequireFinalCheck(batch);
        var unit = batch.Units.SingleOrDefault(item => item.Id == command.UnitId)
            ?? throw NotFound("UNIT_NOT_FOUND");
        if (unit.Status != TemplateGenerationUnitStatus.Extracted)
        {
            throw StateConflict(batch.Revision);
        }

        if (unit.Revision != command.ExpectedRowVersion)
        {
            throw Stale(unit.Revision);
        }

        var changed = false;
        if (command.BaseTestName is not null)
        {
            if (batch.TestType != TestType.Other)
            {
                throw Invalid(
                    TemplateNamePolicy.KnownTestNameImmutableErrorCode,
                    "テスト名は自動で決まります",
                    "HOP、STEP、クラス分けテストは、教科・学年・セット番号から統一名を作成するため変更できません。");
            }

            var normalizedName = NormalizeName(command.BaseTestName);
            unit.UserConfirmedBaseName = normalizedName;
            unit.FinalTemplateName = CreateFinalName(
                batch.TestType,
                normalizedName);
            RemoveWarnings(unit, NameWarningCodes);
            changed = true;
        }

        if (command.ResolvedGrade is { } selectedGrade)
        {
            if (selectedGrade is < GradeLevel.Grade1 or > GradeLevel.Grade6)
            {
                throw Invalid(
                    "GRADE_INVALID",
                    "学年を確認してください",
                    "1年生から6年生のいずれかを選択してください。");
            }

            unit.ResolvedGrade = selectedGrade;
            unit.GradeEvidence = GradeEvidence.User;
            unit.GradeConfirmedByUser = true;
            RemoveWarnings(unit, GradeWarningCodes);
            if (batch.TestType != TestType.Other)
            {
                ApplyKnownTestName(batch, unit);
                RemoveWarnings(unit, NameWarningCodes);
            }

            changed = true;
        }
        else if (command.GradeConfirmedByUser == true)
        {
            throw Invalid(
                "GRADE_REQUIRED",
                "学年を選択してください",
                "確認済みにする学年を指定してください。");
        }

        if (command.TeacherNote is not null)
        {
            unit.TeacherNote = NormalizeTeacherNote(command.TeacherNote);
            changed = true;
        }

        if (!changed)
        {
            throw Invalid(
                "FINAL_CHECK_EDIT_EMPTY",
                "変更内容がありません",
                "テスト名、学年、または先生用メモを指定してください。");
        }

        RecomputeDuplicateWarnings(batch.Units);
        batch.CurrentOperationId = operationKey;
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationFinalCheckEdited",
            "template_generation_unit",
            unit.Id,
            command.CorrelationId,
            new
            {
                batchId = batch.Id,
                nameChanged = command.BaseTestName is not null,
                gradeChanged = command.ResolvedGrade is not null,
                teacherNoteChanged = command.TeacherNote is not null,
                expectedUnitRowVersion = command.ExpectedRowVersion,
                unitRowVersion = unit.Revision,
                nextUnitRowVersion = checked(unit.Revision + 1),
                batchRowVersion = batch.Revision,
                nextBatchRowVersion = checked(batch.Revision + 1),
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
            });
        if (command.ResolvedGrade is not null)
        {
            AddAudit(
                command.StaffUserId,
                "TemplateGenerationGradeResolved",
                "template_generation_unit",
                unit.Id,
                command.CorrelationId,
                new
                {
                    grade = unit.ResolvedGrade,
                    evidence = unit.GradeEvidence,
                    expectedUnitRowVersion = command.ExpectedRowVersion,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                    promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                    schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                });
        }

        await SaveAsync(batch.Revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredSnapshotAsync(
                batch.Id,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TemplateGenerationBatchSnapshot> UpdateStepSetAsync(
        UpdateTemplateGenerationStepSetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await LoadBatchAsync(command.BatchId, cancellationToken)
            .ConfigureAwait(false);
        ValidateAccess(batch, command.StaffUserId, command.IsAdministrator);
        RequireFinalCheck(batch);
        if (batch.TestType != TestType.Step)
        {
            throw Invalid(
                "STEP_SET_NOT_ALLOWED",
                "STEP以外ではセット名を変更できません",
                "テスト単位の編集を使用してください。");
        }

        throw Invalid(
            TemplateNamePolicy.KnownTestNameImmutableErrorCode,
            "STEPのテスト名は自動で決まります",
            "教科・学年・STEPセット番号と枝番から統一名を作成するため変更できません。");
    }

    public async Task<TemplateGenerationBatchSnapshot> RetryAsync(
        RetryTemplateGenerationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await LoadBatchAsync(command.BatchId, cancellationToken)
            .ConfigureAwait(false);
        ValidateAccess(batch, command.StaffUserId, command.IsAdministrator);
        if (batch.CurrentOperationId == command.OperationId
            && batch.Status is TemplateGenerationBatchStatus.Generating
                or TemplateGenerationBatchStatus.Failed)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetRequiredSnapshotAsync(
                    batch.Id,
                    command.StaffUserId,
                    command.IsAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Revision != command.ExpectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        var failedUnits = batch.Units
            .Where(item => item.Status == TemplateGenerationUnitStatus.Failed)
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (batch.Status != TemplateGenerationBatchStatus.Failed
            || failedUnits.Length == 0)
        {
            throw StateConflict(batch.Revision);
        }

        if (failedUnits.Any(item =>
                ReadWarnings(item).Any(warning =>
                    warning.Code == "ORIENTATION_RETRY_EXHAUSTED")))
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "ORIENTATION_RETRY_EXHAUSTED",
                "向きを自動補正できませんでした",
                "元PDFを正しい向きに直して再アップロードしてください。",
                batch.Revision);
        }

        var now = _timeProvider.GetUtcNow();
        var supersededJobs = await CancelSupersededCurrentUnitJobsAsync(
                failedUnits,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var unit in failedUnits)
        {
            var jobId = _ids.NewId();
            _db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = TemplateGenerationBatchService.UnitJobType,
                SchemaVersion = TemplateGenerationBatchService.UnitJobSchemaVersion,
                DeduplicationKey =
                    $"template-generation-unit:{unit.Id}:{unit.GenerationProfileHash}:retry:{command.OperationId}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(
                    new
                    {
                        unitId = unit.Id,
                        batchId = batch.Id,
                        generationProfileHash = unit.GenerationProfileHash,
                    },
                    JsonOptions),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = command.CorrelationId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            unit.ExtractionJobId = jobId;
            unit.Status = TemplateGenerationUnitStatus.Queued;
            unit.OrientationAttemptCount = 0;
            unit.AppliedRotationsJson = "[]";
            WriteWarnings(
                unit,
                ReadWarnings(unit)
                    .Where(item => !RetryClearedWarningCodes.Contains(item.Code))
                    .ToList());
        }

        batch.Status = TemplateGenerationBatchStatus.Generating;
        batch.FailedUnitCount = 0;
        batch.LastErrorCode = null;
        batch.CurrentOperationId = command.OperationId;
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationStarted",
            "template_generation_batch",
            batch.Id,
            command.CorrelationId,
            new
            {
                retry = true,
                failedUnitCount = failedUnits.Length,
                supersededJobCount = supersededJobs.Count,
                supersededJobPreviousStates = supersededJobs.PreviousStateCounts,
                expectedBatchRowVersion = command.ExpectedRowVersion,
                batchRowVersion = batch.Revision,
                nextBatchRowVersion = checked(batch.Revision + 1),
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
            });
        await SaveAsync(batch.Revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredSnapshotAsync(
                batch.Id,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TemplateGenerationBatchSnapshot> CancelAsync(
        CancelTemplateGenerationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await LoadBatchAsync(command.BatchId, cancellationToken)
            .ConfigureAwait(false);
        ValidateAccess(batch, command.StaffUserId, command.IsAdministrator);
        if (batch.Status == TemplateGenerationBatchStatus.Cancelled)
        {
            var replayNow = _timeProvider.GetUtcNow();
            var replayCancelledJobs = await CancelOwnedUnitJobsAsync(
                    batch,
                    replayNow,
                    cancellationToken)
                .ConfigureAwait(false);
            var replayRecoveredDispatchCount = await RecoverCancelledDispatchesAsync(
                    batch,
                    replayNow,
                    cancellationToken)
                .ConfigureAwait(false);
            var replayCosts = await PrepareCancellationCostObservationsAsync(
                    batch,
                    batch.CurrentOperationId ?? command.OperationId,
                    command.StaffUserId,
                    replayNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replayCancelledJobs.Count > 0)
            {
                AddAudit(
                    command.StaffUserId,
                    "TemplateGenerationCancelledJobsReconciled",
                    "template_generation_batch",
                    batch.Id,
                    command.CorrelationId,
                    new
                    {
                        cancelledJobCount = replayCancelledJobs.Count,
                        previousJobStates = replayCancelledJobs.PreviousStateCounts,
                        batchRowVersion = batch.Revision,
                    });
            }

            if (replayCancelledJobs.Count > 0
                || replayRecoveredDispatchCount > 0
                || replayCosts.HasNewObservation)
            {
                await SaveAsync(batch.Revision, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                RecordCancellationCostMetrics(batch, replayCosts);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await GetRequiredSnapshotAsync(
                    batch.Id,
                    command.StaffUserId,
                    command.IsAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Status == TemplateGenerationBatchStatus.Completed)
        {
            throw StateConflict(batch.Revision);
        }

        if (batch.Revision != command.ExpectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        var now = _timeProvider.GetUtcNow();
        var cancelledJobs = await CancelOwnedUnitJobsAsync(
                batch,
                now,
                cancellationToken)
            .ConfigureAwait(false);

        batch.Status = TemplateGenerationBatchStatus.Cancelled;
        batch.CurrentOperationId = command.OperationId;
        batch.CompletedAt = now;
        var recoveredDispatchCount = await RecoverCancelledDispatchesAsync(
                batch,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        var costs = await PrepareCancellationCostObservationsAsync(
                batch,
                command.OperationId,
                command.StaffUserId,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationBatchCancelled",
            "template_generation_batch",
            batch.Id,
            command.CorrelationId,
            new
            {
                cancelledJobCount = cancelledJobs.Count,
                previousJobStates = cancelledJobs.PreviousStateCounts,
                recoveredAmbiguousDispatchCount = recoveredDispatchCount,
                expectedBatchRowVersion = command.ExpectedRowVersion,
                batchRowVersion = batch.Revision,
                nextBatchRowVersion = checked(batch.Revision + 1),
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                actualUsdMicros = costs.TotalActualUsdMicros,
                costMetricDeferred = false,
            });
        await SaveAsync(batch.Revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        RecordCancellationCostMetrics(batch, costs);
        return await GetRequiredSnapshotAsync(
                batch.Id,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TemplateGenerationBatchSnapshot> ConfirmAsync(
        ConfirmTemplateGenerationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await _db.TemplateGenerationBatches
            .Include(item => item.Source)
            .Include(item => item.Units)
            .ThenInclude(item => item.DerivedSource)
            .ThenInclude(item => item!.FileReference)
            .ThenInclude(item => item!.FileObject)
            .SingleOrDefaultAsync(item => item.Id == command.BatchId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound();
        ValidateAccess(batch, command.StaffUserId, command.IsAdministrator);
        if (batch.Status == TemplateGenerationBatchStatus.Completed)
        {
            ValidateCompletedBatch(batch);
            var replayNow = _timeProvider.GetUtcNow();
            var reconciledNames = await ReconcileCompletedKnownNamesAsync(
                    batch,
                    replayNow,
                    cancellationToken)
                .ConfigureAwait(false);
            var replayReconciledJobs = await CancelOwnedUnitJobsAsync(
                    batch,
                    replayNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (reconciledNames.RenamedUnitCount > 0)
            {
                AddAudit(
                    command.StaffUserId,
                    "TemplateGenerationNamesReconciled",
                    "template_generation_batch",
                    batch.Id,
                    command.CorrelationId,
                    new
                    {
                        reconciledNames.RenamedUnitCount,
                        reconciledNames.RenamedSourceCount,
                        reconciledNames.SkippedNonDraftCount,
                        namingPolicyVersion =
                            TemplateGenerationProfile.CurrentNamingPolicyVersion,
                        batchRowVersion = batch.Revision,
                    });
            }

            if (replayReconciledJobs.Count > 0)
            {
                AddAudit(
                    command.StaffUserId,
                    "TemplateGenerationConfirmedJobsReconciled",
                    "template_generation_batch",
                    batch.Id,
                    command.CorrelationId,
                    new
                    {
                        cancelledJobCount = replayReconciledJobs.Count,
                        previousJobStates = replayReconciledJobs.PreviousStateCounts,
                        batchRowVersion = batch.Revision,
                    });
            }

            if (replayReconciledJobs.Count > 0
                || reconciledNames.RenamedUnitCount > 0)
            {
                await SaveAsync(batch.Revision, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await GetRequiredSnapshotAsync(
                    batch.Id,
                    command.StaffUserId,
                    command.IsAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Revision != command.ExpectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        RequireFinalCheck(batch);
        var orderedUnits = batch.Units.OrderBy(item => item.Sequence).ToArray();
        var prepared = await ValidateForConfirmationAsync(
                batch,
                orderedUnits,
                cancellationToken)
            .ConfigureAwait(false);
        var finalCheckReadyAt = await GetFinalCheckReadyAtAsync(
                orderedUnits.Select(item => item.Id).ToArray(),
                batch.UpdatedAt,
                cancellationToken)
            .ConfigureAwait(false);
        var confirmationHash = ComputeConfirmationHash(
            batch,
            orderedUnits,
            command.ExpectedRowVersion);
        var now = _timeProvider.GetUtcNow();
        var reconciledJobs = await CancelOwnedUnitJobsAsync(
                batch,
                now,
                cancellationToken)
            .ConfigureAwait(false);
        var batchCost = await TemplateGenerationCostObservationLedger
            .PrepareBatchObservationAsync(
                _db,
                batch.Id,
                command.OperationId,
                "succeeded",
                command.StaffUserId,
                _ids,
                now,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The batch cost observation already exists before confirmation.");
        var actualUsdMicros = batchCost.TotalActualUsdMicros;
        batch.Status = TemplateGenerationBatchStatus.Confirming;
        foreach (var item in prepared)
        {
            CreateTemplateGraph(
                batch,
                item.Unit,
                item.Draft,
                item.DerivedFileReferenceId,
                item.AiRequestId,
                command.StaffUserId,
                now);
        }

        foreach (var unit in orderedUnits)
        {
            unit.Status = TemplateGenerationUnitStatus.Confirmed;
            AddAudit(
                command.StaffUserId,
                "TemplateCreatedFromGenerationUnit",
                "template_generation_unit",
                unit.Id,
                command.CorrelationId,
                new
                {
                    templateId = unit.CreatedTemplateId,
                    templateVersionId = unit.CreatedTemplateVersionId,
                    generationProfileHash = unit.GenerationProfileHash,
                    extractionDraftHash = unit.ExtractionDraftHash,
                    unitRowVersion = unit.Revision,
                    nextUnitRowVersion = checked(unit.Revision + 1),
                    profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                    promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                    schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                });
        }

        batch.Status = TemplateGenerationBatchStatus.Completed;
        batch.CompletedUnitCount = orderedUnits.Length;
        batch.FailedUnitCount = 0;
        batch.LastErrorCode = null;
        batch.CurrentOperationId = command.OperationId;
        batch.CompletedAt = now;
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationBatchConfirmed",
            "template_generation_batch",
            batch.Id,
            command.CorrelationId,
            new
            {
                confirmationHash,
                templateCount = orderedUnits.Length,
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                expectedBatchRowVersion = command.ExpectedRowVersion,
                batchRowVersion = batch.Revision,
                nextBatchRowVersion = checked(batch.Revision + 1),
                promptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                schemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                cancelledJobCount = reconciledJobs.Count,
                previousJobStates = reconciledJobs.PreviousStateCounts,
                actualUsdMicros,
            });
        await SaveAsync(batch.Revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        TemplateGenerationMetrics.BatchConfirmed(
            batch.TestType,
            batch.PromptSystem,
            TemplateGenerationProfile.CurrentProfileVersion,
            now - finalCheckReadyAt,
            orderedUnits.Length,
            batchCost.DeltaActualUsdMicros);
        return await GetRequiredSnapshotAsync(
                batch.Id,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<List<PreparedUnit>> ValidateForConfirmationAsync(
        TemplateGenerationBatchEntity batch,
        TemplateGenerationUnitEntity[] units,
        CancellationToken cancellationToken)
    {
        if (units.Length != batch.ExpectedUnitCount
            || units.Length == 0
            || units.Any(item => item.Status != TemplateGenerationUnitStatus.Extracted)
            || units.Any(item => item.CreatedTemplateId is not null
                || item.CreatedTemplateVersionId is not null))
        {
            throw StateConflict(batch.Revision);
        }

        IReadOnlyList<TemplateUnitPlan> plannedUnits;
        try
        {
            plannedUnits = UnitPlanner.Plan(batch.TestType, batch.SourcePageCount);
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }

        if (plannedUnits.Count != units.Length
            || plannedUnits.Zip(units).Any(pair =>
                pair.First.Sequence != pair.Second.Sequence
                || pair.First.FirstPage != pair.Second.FirstPage
                || pair.First.LastPage != pair.Second.LastPage
                || pair.First.StepSetIndex != pair.Second.StepSetIndex
                || pair.First.StepVariationIndex != pair.Second.StepVariationIndex
                || pair.First.DeterministicSuffix
                    != pair.Second.DeterministicSuffix))
        {
            throw Invalid(
                "GENERATION_PLAN_INVALID",
                "ページ分割の記録が一致しません",
                "このバッチを作り直してください。");
        }

        foreach (var unit in units)
        {
            ApplyKnownTestName(batch, unit);
        }

        try
        {
            TemplateNamePolicy.EnsureUniqueFinalNames(
                units.Select(item => item.FinalTemplateName));
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }

        var unitIds = units.Select(item => item.Id).ToArray();
        var aiRequests = await _db.AiRequests
            .AsNoTracking()
            .Where(item => item.EntityType == "template_generation_unit"
                && unitIds.Contains(item.EntityId)
                && item.Purpose == AiTaskTypes.TemplateExtraction
                && item.State == "succeeded")
            .OrderBy(item => item.EntityId)
            .ThenByDescending(item => item.AttemptNumber)
            .ThenByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latestAiRequestByUnit = aiRequests
            .GroupBy(item => item.EntityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.Ordinal);
        var prepared = new List<PreparedUnit>(units.Length);
        foreach (var unit in units)
        {
            ValidateUnitProfile(batch, unit);
            if (string.IsNullOrWhiteSpace(unit.FinalTemplateName)
                || unit.ResolvedGrade is < GradeLevel.Grade1 or > GradeLevel.Grade6
                || string.IsNullOrWhiteSpace(unit.ExtractionDraftJson)
                || string.IsNullOrWhiteSpace(unit.ExtractionDraftHash)
                || !string.Equals(
                    Sha256(unit.ExtractionDraftJson),
                    unit.ExtractionDraftHash,
                    StringComparison.Ordinal)
                || !HasValidDerivedSource(batch, unit)
                || ReadWarnings(unit).Any(item =>
                    item.Severity == GenerationWarningSeverity.Blocking))
            {
                throw Invalid(
                    "FINAL_CHECK_INCOMPLETE",
                    "最終確認が完了していません",
                    "すべてのテスト名、学年、警告を確認してください。");
            }

            CanonicalTemplateGenerationDraft draft;
            try
            {
                draft = JsonSerializer.Deserialize<CanonicalTemplateGenerationDraft>(
                        unit.ExtractionDraftJson,
                        JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new TemplateGenerationBatchServiceException(
                    StatusCodes.Status409Conflict,
                    "TEMPLATE_DRAFT_INVALID",
                    "AIが生成した下書きの形式を確認できませんでした",
                    "入力内容ではなく生成結果の問題です。失敗した項目だけ再試行してください。",
                    batch.Revision);
            }

            ValidateCanonicalDraft(unit, draft);

            prepared.Add(new PreparedUnit(
                unit,
                draft,
                unit.DerivedSource!.FileReferenceId!,
                latestAiRequestByUnit.GetValueOrDefault(unit.Id)));
        }

        return prepared;
    }

    private async Task<DateTimeOffset> GetFinalCheckReadyAtAsync(
        string[] unitIds,
        DateTimeOffset fallback,
        CancellationToken cancellationToken)
    {
        var extractionTimes = await _db.AuditEvents
            .AsNoTracking()
            .Where(item =>
                item.EventType == "TemplateUnitExtracted"
                && item.ObjectType == "template_generation_unit"
                && item.Outcome == "succeeded"
                && unitIds.Contains(item.ObjectId))
            .Select(item => item.OccurredAt)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return extractionTimes.Length == 0
            ? fallback
            : extractionTimes.Max();
    }

    private void CreateTemplateGraph(
        TemplateGenerationBatchEntity batch,
        TemplateGenerationUnitEntity unit,
        CanonicalTemplateGenerationDraft draft,
        string derivedFileReferenceId,
        string? aiRequestId,
        string staffUserId,
        DateTimeOffset now)
    {
        var templateId = _ids.NewId();
        var versionId = _ids.NewId();
        var questions = draft.Pages
            .OrderBy(page => page.PageNumber)
            .SelectMany(page => page.Questions.Select(question => (page, question)))
            .ToArray();
        var effectiveTotalPointsMilli = questions.Aggregate(
            0L,
            (total, item) => checked(
                total + EffectivePointsMilli(item.question.SuggestedPointsMilli)));
        var template = new TestTemplateEntity
        {
            Id = templateId,
            Title = unit.FinalTemplateName!,
            Subject = batch.Subject,
            Category = CategoryLabel(batch.TestType),
            GradeLabel = GradeLabel(unit.ResolvedGrade),
            Source = "template_generation_batch",
            Notes = NormalizeTeacherNote(unit.TeacherNote ?? string.Empty),
            DefaultPointsMilli = 1_000,
            State = "draft",
            CreatedByStaffUserId = staffUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var version = new TemplateVersionEntity
        {
            Id = versionId,
            TestTemplateId = templateId,
            VersionNumber = 1,
            State = "draft",
            TargetTotalPointsMilli = effectiveTotalPointsMilli,
            DefaultPointsMilli = DefaultGeneratedQuestionPointsMilli,
            DefaultAllowNonKanji = false,
            PipelineVersion = TemplateGenerationUnitJobWorker.PipelineVersion,
            ExpectedSubmissionPageCount = checked(
                unit.LastPage - unit.FirstPage + 1),
            AiGenerationProvenanceId = aiRequestId,
            TestType = batch.TestType,
            AnswerStyle = batch.AnswerStyle,
            PromptSystem = batch.PromptSystem,
            OriginatingBatchId = batch.Id,
            OriginatingUnitId = unit.Id,
            GenerationProfileVersion = TemplateGenerationProfile.CurrentProfileVersion,
            GenerationProfileJson = unit.GenerationProfileJson,
            GenerationProfileHash = unit.GenerationProfileHash,
            StepSetIndex = unit.StepSetIndex,
            StepVariationIndex = unit.StepVariationIndex,
            PrintedTestName = unit.PrintedTestName,
            ResolvedGrade = unit.ResolvedGrade,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var containsModelAnswers = questions.Any(item =>
            item.question.AnswerProvenance == "provided_model_answer");
        var source = new TemplateSourceEntity
        {
            Id = _ids.NewId(),
            TemplateVersionId = versionId,
            UploadSessionId = batch.SourceId,
            FileReferenceId = derivedFileReferenceId,
            SourceRole = containsModelAnswers
                ? "contains_model_answers"
                : "blank_test",
            DisplayName = $"{unit.FinalTemplateName}.pdf",
            Ordinal = 0,
            UploadedByStaffUserId = staffUserId,
            CreatedAt = now,
        };
        _db.TestTemplates.Add(template);
        _db.TemplateVersions.Add(version);
        _db.TemplateSources.Add(source);
        var questionOrdinal = 0;
        foreach (var item in questions)
        {
            var proposal = item.question;
            var questionId = _ids.NewId();
            var confidence = checked((int)Math.Round(
                proposal.Confidence * 10_000,
                MidpointRounding.AwayFromZero));
            var reviewNotes = draft.Metadata.Warnings
                .Concat(draft.ReviewIssues.Select(ToReviewNote))
                .Concat(proposal.Warnings)
                .Concat(proposal.ReviewIssues.Select(ToReviewNote))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (proposal.AnswerProvenance == "ai_proposed")
            {
                reviewNotes.Add(
                    "正答はAIによる提案です。先生が根拠資料と照合してください。");
            }
            else if (proposal.AnswerProvenance == "provided_model_answer")
            {
                reviewNotes.Add(
                    "模範解答の転記候補です。原資料との照合が必要です。");
            }
            else
            {
                reviewNotes.Add("正答が未解決です。先生が入力してください。");
            }

            var question = new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = versionId,
                LogicalQuestionId = _ids.NewId(),
                OrderIndex = questionOrdinal,
                DisplayLabel = proposal.DisplayLabel,
                QuestionText = proposal.QuestionText,
                QuestionType = proposal.QuestionType,
                GradingMode = GradingModeFor(proposal.QuestionType),
                MaxPointsMilli = EffectivePointsMilli(
                    proposal.SuggestedPointsMilli),
                PointIncrementMilli =
                    QuestionGradingDefaultPolicy.PointIncrementMilliFor(
                        EffectivePointsMilli(proposal.SuggestedPointsMilli)),
                AllowNonKanji = proposal.AllowNonKanjiSuggestion,
                RequiresCompleteAnswer =
                    proposal.RequiresCompleteAnswerSuggestion,
                AnswerOrderInsensitive =
                    proposal.AnswerOrderInsensitiveSuggestion,
                RubricText = QuestionGradingDefaultPolicy.BuildDefaultRubric(
                    proposal.QuestionType,
                    proposal.ExpectedAnswer),
                KanjiPolicyNote =
                    "AIによる表記方針の提案です。先生の確認が必要です。",
                TeacherNote = BoundedTeacherNote(reviewNotes),
                RequiresReviewAlways = RequiresPermanentReview(proposal),
                AiConfidenceBasisPoints = confidence,
                TeacherVerified = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Questions.Add(question);
            if (proposal.ExpectedAnswer is not null)
            {
                _db.AcceptedAnswers.Add(CreateAnswer(
                    questionId,
                    proposal.ExpectedAnswer,
                    "canonical",
                    proposal.AnswerProvenance,
                    proposal.AnswerProvenance == "provided_model_answer"
                        ? derivedFileReferenceId
                        : null,
                    proposal.AnswerProvenance == "provided_model_answer"
                        ? proposal.AnswerSource!.PageNumber
                        : null,
                    now));
                foreach (var variant in proposal.AcceptedVariants)
                {
                    _db.AcceptedAnswers.Add(CreateAnswer(
                        questionId,
                        variant,
                        "equivalent",
                        "derived_variant",
                        sourceFileReferenceId: null,
                        sourcePageNumber: null,
                        now));
                }
            }

            questionOrdinal = checked(questionOrdinal + 1);
        }

        unit.CreatedTemplateId = templateId;
        unit.CreatedTemplateVersionId = versionId;
    }

    private AcceptedAnswerEntity CreateAnswer(
        string questionId,
        string answer,
        string variantType,
        string provenance,
        string? sourceFileReferenceId,
        int? sourcePageNumber,
        DateTimeOffset now) =>
        new()
        {
            Id = _ids.NewId(),
            QuestionId = questionId,
            AnswerText = answer,
            NormalizedText = JapaneseTextNormalizer.NormalizeForComparison(answer),
            VariantType = variantType,
            TeacherVerified = false,
            AnswerProvenance = provenance,
            SourceFileReferenceId = sourceFileReferenceId,
            SourcePageNumber = sourcePageNumber,
            Locale = "ja-JP",
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static void ValidateUnitProfile(
        TemplateGenerationBatchEntity batch,
        TemplateGenerationUnitEntity unit)
    {
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
            throw Invalid(
                "AI_PROFILE_MISSING",
                "生成条件を確認できません",
                "このバッチを作り直してください。");
        }

        TemplatePromptSystem expectedPrompt;
        try
        {
            expectedPrompt = TemplatePromptRouter.Resolve(
                batch.TestType,
                batch.AnswerStyle);
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }

        var stepValid = batch.TestType != TestType.Step
            ? unit.StepSetIndex is null
                && unit.StepVariationIndex is null
                && unit.DeterministicSuffix is null
            : unit.LastPage == unit.FirstPage + 1
                && unit.StepSetIndex is > 0
                && unit.StepVariationIndex is >= 1 and <= 3
                && unit.DeterministicSuffix == $"-{unit.StepVariationIndex}"
                && HasSuffixExactlyOnce(
                    unit.FinalTemplateName,
                    unit.DeterministicSuffix);
        var hopValid = batch.TestType != TestType.Hop
            || unit.FirstPage == unit.LastPage;
        if (profile.ComputeHash() != unit.GenerationProfileHash
            || unit.TestType != batch.TestType
            || unit.AnswerStyle != batch.AnswerStyle
            || unit.PromptSystem != expectedPrompt
            || batch.PromptSystem != expectedPrompt
            || profile.TestType != batch.TestType
            || profile.Subject != batch.Subject
            || profile.AnswerStyle != batch.AnswerStyle
            || profile.PromptSystem != expectedPrompt
            || profile.SourcePageCount != batch.SourcePageCount
            || profile.UnitSequence != unit.Sequence
            || profile.FirstPage != unit.FirstPage
            || profile.LastPage != unit.LastPage
            || profile.StepSetIndex != unit.StepSetIndex
            || profile.StepVariationIndex != unit.StepVariationIndex
            || profile.DeterministicSuffix != unit.DeterministicSuffix
            || profile.ProfileVersion != TemplateGenerationProfile.CurrentProfileVersion
            || profile.SplitPolicyVersion
                != TemplateGenerationProfile.CurrentSplitPolicyVersion
            || !TemplateGenerationProfile.IsSupportedNamingPolicyVersion(
                profile.NamingPolicyVersion)
            || profile.ExtractionPromptVersion
                != TemplateGenerationBatchService.ExtractionPromptVersion
            || profile.ExtractionSchemaVersion
                != TemplateGenerationBatchService.ExtractionSchemaVersion
            || !hopValid
            || !stepValid)
        {
            throw Invalid(
                "GENERATION_PROFILE_INVALID",
                "生成条件が一致しません",
                "このバッチを作り直してください。");
        }
    }

    private static bool HasValidDerivedSource(
        TemplateGenerationBatchEntity batch,
        TemplateGenerationUnitEntity unit)
    {
        var source = unit.DerivedSource;
        var reference = source?.FileReference;
        var fileObject = reference?.FileObject;
        if (source is null
            || reference is null
            || fileObject is null
            || source.UnitId != unit.Id
            || source.ParentSourceId != batch.SourceId
            || source.ParentFirstPage != unit.FirstPage
            || source.ParentLastPage != unit.LastPage
            || !string.Equals(
                source.OriginalContentSha256,
                batch.Source.FinalSha256,
                StringComparison.Ordinal)
            || source.DerivationPolicyVersion
                != PdfPageRangeDerivationPolicy.CurrentVersion
            || string.IsNullOrWhiteSpace(source.FileReferenceId)
            || source.FileReferenceId != reference.Id
            || reference.OwnerType != "template_generation_unit"
            || reference.OwnerId != unit.Id
            || reference.Purpose != "derived_source"
            || reference.FileObjectId != fileObject.Id
            || fileObject.StorageClass
                != ContentStorageClass.TemplateDerived.ToString()
            || fileObject.State != "available"
            || fileObject.VerifiedMime != "application/pdf"
            || !string.Equals(fileObject.Extension, "pdf", StringComparison.Ordinal)
            || fileObject.Bytes <= 0
            || !IsSha256(fileObject.Sha256)
            || !string.Equals(
                fileObject.Sha256,
                source.DerivedContentSha256,
                StringComparison.Ordinal)
            || !string.Equals(
                source.DerivedContentSha256,
                unit.DerivedSourceSha256,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(unit.DerivedSourceObjectKey)
            || !string.Equals(
                fileObject.RelativeObjectPath,
                unit.DerivedSourceObjectKey,
                StringComparison.Ordinal)
            || !string.Equals(
                source.AppliedRotationsJson,
                unit.AppliedRotationsJson,
                StringComparison.Ordinal)
            || unit.OrientationAttemptCount is < 0 or > 1)
        {
            return false;
        }

        AppliedPageRotation[] rotations;
        try
        {
            rotations = JsonSerializer.Deserialize<AppliedPageRotation[]>(
                    unit.AppliedRotationsJson,
                    JsonOptions)
                ?? [];
        }
        catch (JsonException)
        {
            return false;
        }

        var rotationManifestValid = rotations
            .Select(item => item.OriginalPageNumber)
            .Distinct()
            .Count() == rotations.Length
            && rotations.All(item =>
                item.OriginalPageNumber >= unit.FirstPage
                && item.OriginalPageNumber <= unit.LastPage
                && item.ClockwiseDegrees is 0 or 90 or 180 or 270
                && item.PageId == $"{unit.Id}:page:" +
                    (item.OriginalPageNumber - unit.FirstPage + 1)
                && item.Source == "gemini"
                && double.IsFinite(item.Confidence)
                && item.Confidence is >= 0 and <= 1);
        return rotationManifestValid
            && (unit.OrientationAttemptCount == 0
                ? rotations.Length == 0 && source.DerivationType == "pageRange"
                : rotations.Length == unit.LastPage - unit.FirstPage + 1
                    && rotations.Any(item => item.ClockwiseDegrees != 0)
                    && source.DerivationType == "pageRangeAndRotation");
    }

    private static void ValidateCanonicalDraft(
        TemplateGenerationUnitEntity unit,
        CanonicalTemplateGenerationDraft draft)
    {
        if (!IsValidCanonicalDraft(unit, draft))
        {
            throw Invalid(
                "TEMPLATE_DRAFT_INVALID",
                "AIが生成した下書きの形式を確認できませんでした",
                "入力内容ではなく生成結果の問題です。失敗した項目だけ再試行してください。");
        }
    }

    private static bool IsValidCanonicalDraft(
        TemplateGenerationUnitEntity unit,
        CanonicalTemplateGenerationDraft draft)
    {
        var expectedPageCount = unit.LastPage - unit.FirstPage + 1;
        if (draft.SchemaVersion
                != TemplateGenerationBatchService.ExtractionSchemaVersion
            || draft.Metadata is null
            || draft.Pages is null
            || draft.ReviewIssues is null
            || draft.Pages.Count != expectedPageCount
            || !IsConfidence(draft.Metadata.GradeConfidence)
            || !HasValidWarnings(draft.Metadata.Warnings)
            || !HasValidReviewIssues(draft.ReviewIssues)
            || (draft.Metadata.PrintedTestName is not null
                && string.IsNullOrWhiteSpace(draft.Metadata.PrintedTestName))
            || (draft.Metadata.PrintedGradeLabel is not null
                && string.IsNullOrWhiteSpace(draft.Metadata.PrintedGradeLabel)))
        {
            return false;
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        long rawTotalPointsMilli = 0;
        var questionCount = 0;
        try
        {
            for (var pageIndex = 0; pageIndex < draft.Pages.Count; pageIndex++)
            {
                var page = draft.Pages[pageIndex];
                if (page is null
                    || page.SourceId != unit.Id
                    || page.PageNumber != pageIndex + 1
                    || page.DetectedAnswerSlotCount < 0
                    || page.Questions is null)
                {
                    return false;
                }

                foreach (var question in page.Questions)
                {
                    if (question is null
                        || string.IsNullOrWhiteSpace(question.SourceKey)
                        || !sourceKeys.Add(question.SourceKey)
                        || string.IsNullOrWhiteSpace(question.DisplayLabel)
                        || string.IsNullOrWhiteSpace(question.QuestionText)
                        || !SupportedQuestionTypes.Contains(question.QuestionType)
                        || question.AnswerSlotCount <= 0
                        || question.AnswerSlotOrdinal <= 0
                        || question.SuggestedPointsMilli < 0
                        || !IsConfidence(question.Confidence)
                        || question.AcceptedVariants is null
                        || !HasValidWarnings(question.Warnings)
                        || !HasValidReviewIssues(question.ReviewIssues)
                        || !HasValidAnswer(unit, question)
                        || !HasValidAcceptedVariants(question))
                    {
                        return false;
                    }

                    rawTotalPointsMilli = checked(
                        rawTotalPointsMilli + question.SuggestedPointsMilli);
                    questionCount++;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return questionCount > 0
            && rawTotalPointsMilli == draft.TotalPointsMilli;
    }

    private static bool HasValidAnswer(
        TemplateGenerationUnitEntity unit,
        CanonicalTemplateGenerationQuestion question)
    {
        if (!SupportedAnswerProvenances.Contains(question.AnswerProvenance))
        {
            return false;
        }

        return question.AnswerProvenance switch
        {
            "provided_model_answer" =>
                !string.IsNullOrWhiteSpace(question.ExpectedAnswer)
                && question.AnswerSource is not null
                && question.AnswerSource.SourceId == unit.Id
                && question.AnswerSource.PageNumber > 0
                && question.AnswerSource.PageNumber
                    <= unit.LastPage - unit.FirstPage + 1,
            "ai_proposed" =>
                !string.IsNullOrWhiteSpace(question.ExpectedAnswer)
                && question.AnswerSource is null,
            "unavailable" => question.ExpectedAnswer is null
                && question.AnswerSource is null
                && question.AcceptedVariants.Count == 0,
            _ => false,
        };
    }

    private static bool HasValidAcceptedVariants(
        CanonicalTemplateGenerationQuestion question)
    {
        var expected = question.ExpectedAnswer is null
            ? null
            : JapaneseTextNormalizer.NormalizeForComparison(
                question.ExpectedAnswer);
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var variant in question.AcceptedVariants)
        {
            if (string.IsNullOrWhiteSpace(variant))
            {
                return false;
            }

            var value = JapaneseTextNormalizer.NormalizeForComparison(variant);
            if (value.Length == 0
                || value == expected
                || !normalized.Add(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasValidWarnings(IReadOnlyList<string>? warnings) =>
        warnings is not null
        && warnings.All(item => !string.IsNullOrWhiteSpace(item));

    private static bool HasValidReviewIssues(
        IReadOnlyList<TemplateExtractionReviewIssue>? issues) =>
        issues is not null
        && issues.All(item => item is not null
            && !string.IsNullOrWhiteSpace(item.Code)
            && !string.IsNullOrWhiteSpace(item.Message));

    private static bool IsConfidence(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    private static bool HasSuffixExactlyOnce(
        string? finalName,
        string? suffix)
    {
        if (string.IsNullOrWhiteSpace(finalName)
            || string.IsNullOrWhiteSpace(suffix)
            || !finalName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        return finalName.IndexOf(suffix, StringComparison.Ordinal)
            == finalName.LastIndexOf(suffix, StringComparison.Ordinal);
    }

    private static long EffectivePointsMilli(long suggestedPointsMilli) =>
        suggestedPointsMilli == 0
            ? DefaultGeneratedQuestionPointsMilli
            : suggestedPointsMilli;

    private async Task<CancelledJobSummary> CancelSupersededCurrentUnitJobsAsync(
        IReadOnlyCollection<TemplateGenerationUnitEntity> units,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var currentJobIds = units
            .Select(item => item.ExtractionJobId)
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
        if (currentJobIds.Length == 0)
        {
            return CancelledJobSummary.Empty;
        }

        var jobs = await _db.BackgroundJobs
            .Where(item => item.Type == TemplateGenerationBatchService.UnitJobType
                && currentJobIds.Contains(item.Id)
                && (item.State == "failed" || item.State == "blocked"))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return CancelJobs(jobs, now);
    }

    private async Task<CancelledJobSummary> CancelOwnedUnitJobsAsync(
        TemplateGenerationBatchEntity batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var unitIds = batch.Units
            .Select(item => item.Id)
            .ToArray();
        if (unitIds.Length == 0)
        {
            return CancelledJobSummary.Empty;
        }

        // ExtractionJobId identifies the current attempt. The stable unit-id
        // segment in the deduplication key also finds terminal attempts that
        // were replaced by RetryAsync, so cancelling a batch cannot leave its
        // earlier failed/blocked jobs degrading system health indefinitely.
        var currentJobIds = batch.Units
            .Select(item => item.ExtractionJobId)
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
        var jobs = await _db.BackgroundJobs
            .Where(item => item.Type == TemplateGenerationBatchService.UnitJobType
                && item.State != "succeeded"
                && item.State != "cancelled"
                && ((currentJobIds.Length > 0 && currentJobIds.Contains(item.Id))
                    || (item.DeduplicationKey.StartsWith(UnitJobDeduplicationPrefix)
                        && unitIds.Contains(item.DeduplicationKey.Substring(
                            UnitJobDeduplicationPrefix.Length,
                            UlidTextLength)))))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return CancelJobs(jobs, now);
    }

    private static CancelledJobSummary CancelJobs(
        BackgroundJobEntity[] jobs,
        DateTimeOffset now)
    {
        if (jobs.Length == 0)
        {
            return CancelledJobSummary.Empty;
        }

        var previousStateCounts = jobs
            .GroupBy(item => item.State, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            job.State = "cancelled";
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.CompletedAt ??= now;
        }

        return new CancelledJobSummary(jobs.Length, previousStateCounts);
    }

    private async Task<int> RecoverCancelledDispatchesAsync(
        TemplateGenerationBatchEntity batch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var unitIds = batch.Units.Select(item => item.Id).ToArray();
        var dispatchingRequests = await _db.AiRequests
            .Where(item => item.EntityType == "template_generation_unit"
                && unitIds.Contains(item.EntityId)
                && item.State == "dispatching")
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (dispatchingRequests.Length == 0)
        {
            return 0;
        }

        var requestIds = dispatchingRequests.Select(item => item.Id).ToArray();
        var reservations = await _db.AiBudgetReservations
            .Where(item => requestIds.Contains(item.AiRequestId))
            .ToDictionaryAsync(
                item => item.AiRequestId,
                StringComparer.Ordinal,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var request in dispatchingRequests)
        {
            request.State = "cancelled";
            request.PossibleDuplicate = true;
            request.ErrorCode = "TEMPLATE_GENERATION_CANCELLED";
            request.SafeErrorDetail = "cancelled_after_ambiguous_dispatch";
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

        return dispatchingRequests.Length;
    }

    private async Task<CancellationCostObservations>
        PrepareCancellationCostObservationsAsync(
            TemplateGenerationBatchEntity batch,
            string operationId,
            string actorStaffUserId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        var unitObservations = new List<TemplateGenerationUnitCostObservation>();
        foreach (var unit in batch.Units.Where(item => item.Status is not (
                     TemplateGenerationUnitStatus.Extracted
                     or TemplateGenerationUnitStatus.Failed
                     or TemplateGenerationUnitStatus.Confirmed)))
        {
            if (unit.ExtractionJobId is not { } jobId)
            {
                continue;
            }

            var requestPrefix = $"template_unit_run_{jobId}_";
            var unitRequests = await _db.AiRequests
                .Where(item => item.EntityType == "template_generation_unit"
                    && item.EntityId == unit.Id
                    && item.RequestKey.StartsWith(requestPrefix))
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var hasDispatchingRequest = unitRequests.Any(item =>
                item.State == "dispatching");
            if (hasDispatchingRequest)
            {
                continue;
            }

            var observation = await TemplateGenerationCostObservationLedger
                .PrepareCancelledUnitObservationAsync(
                    _db,
                    unit,
                    jobId,
                    actorStaffUserId,
                    _ids,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            if (observation is not null)
            {
                unitObservations.Add(observation);
            }
        }

        var unitIds = batch.Units.Select(item => item.Id).ToArray();
        var batchRequests = await _db.AiRequests
            .Where(item => item.EntityType == "template_generation_unit"
                && unitIds.Contains(item.EntityId))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasBatchDispatchingRequest = batchRequests.Any(item =>
            item.State == "dispatching");
        var batchObservation = hasBatchDispatchingRequest
            ? null
            : await TemplateGenerationCostObservationLedger
                .PrepareBatchObservationAsync(
                    _db,
                    batch.Id,
                    operationId,
                    "cancelled",
                    actorStaffUserId,
                    _ids,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        var totalActualUsdMicros = batchObservation?.TotalActualUsdMicros
            ?? await TemplateGenerationCostObservationLedger
                .ReadBatchSettledCostAsync(
                    _db,
                    batch.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        return new CancellationCostObservations(
            unitObservations,
            batchObservation,
            totalActualUsdMicros);
    }

    private static void RecordCancellationCostMetrics(
        TemplateGenerationBatchEntity batch,
        CancellationCostObservations costs)
    {
        foreach (var unit in costs.Units)
        {
            TemplateGenerationMetrics.UnitExtractionCancelled(
                unit.TestType,
                unit.PromptSystem,
                unit.ProfileVersion,
                unit.Provider,
                unit.Model,
                unit.ActualUsdMicros);
        }

        if (costs.Batch is { } batchCost)
        {
            TemplateGenerationMetrics.BatchTerminated(
                batch.TestType,
                batch.PromptSystem,
                TemplateGenerationProfile.CurrentProfileVersion,
                "cancelled",
                batchCost.DeltaActualUsdMicros);
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string ComputeConfirmationHash(
        TemplateGenerationBatchEntity batch,
        IEnumerable<TemplateGenerationUnitEntity> units,
        long expectedRowVersion)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                batchId = batch.Id,
                expectedRowVersion,
                units = units.Select(item => new
                {
                    item.Id,
                    item.ExtractionDraftHash,
                    item.FinalTemplateName,
                    item.ResolvedGrade,
                    item.GradeConfirmedByUser,
                    item.GenerationProfileHash,
                    item.DerivedSourceSha256,
                }),
            },
            JsonOptions);
        return Sha256(canonical);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private async Task<TemplateGenerationBatchEntity> LoadBatchAsync(
        string batchId,
        CancellationToken cancellationToken) =>
        await _db.TemplateGenerationBatches
            .Include(item => item.Units)
            .SingleOrDefaultAsync(item => item.Id == batchId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw NotFound();

    private sealed record CancellationCostObservations(
        IReadOnlyList<TemplateGenerationUnitCostObservation> Units,
        TemplateGenerationBatchCostObservation? Batch,
        long TotalActualUsdMicros)
    {
        internal bool HasNewObservation => Units.Count > 0 || Batch is not null;
    }

    private sealed record CancelledJobSummary(
        int Count,
        IReadOnlyDictionary<string, int> PreviousStateCounts)
    {
        internal static CancelledJobSummary Empty { get; } = new(
            0,
            new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private sealed record NameReconciliationSummary(
        int RenamedUnitCount,
        int RenamedSourceCount,
        int SkippedNonDraftCount);

    private async Task<TemplateGenerationBatchSnapshot> GetRequiredSnapshotAsync(
        string batchId,
        string staffUserId,
        bool isAdministrator,
        CancellationToken cancellationToken) =>
        await _batchService.GetAsync(
                batchId,
                staffUserId,
                isAdministrator,
                cancellationToken)
            .ConfigureAwait(false)
        ?? throw NotFound();

    private async Task<NameReconciliationSummary>
        ReconcileCompletedKnownNamesAsync(
            TemplateGenerationBatchEntity batch,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        if (batch.TestType == TestType.Other)
        {
            return new NameReconciliationSummary(0, 0, 0);
        }

        var versionIds = batch.Units
            .Select(item => item.CreatedTemplateVersionId)
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
        var versions = await _db.TemplateVersions
            .Include(item => item.TestTemplate)
            .Include(item => item.Sources)
            .Where(item => versionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var renamedUnitCount = 0;
        var renamedSourceCount = 0;
        var skippedNonDraftCount = 0;
        foreach (var unit in batch.Units.OrderBy(item => item.Sequence))
        {
            if (unit.ResolvedGrade is < GradeLevel.Grade1 or > GradeLevel.Grade6
                || unit.CreatedTemplateId is null
                || unit.CreatedTemplateVersionId is null
                || !versions.TryGetValue(
                    unit.CreatedTemplateVersionId,
                    out var version)
                || version.TestTemplateId != unit.CreatedTemplateId
                || version.OriginatingBatchId != batch.Id
                || version.OriginatingUnitId != unit.Id)
            {
                skippedNonDraftCount++;
                continue;
            }

            var template = version.TestTemplate;
            if (!string.Equals(template.State, "draft", StringComparison.Ordinal)
                || !string.Equals(version.State, "draft", StringComparison.Ordinal)
                || template.ActiveVersionId is not null
                || version.PublishedAt is not null
                || version.PublishedByStaffUserId is not null)
            {
                skippedNonDraftCount++;
                continue;
            }

            string expectedName;
            try
            {
                expectedName = TemplateNamePolicy.CreateKnownTestName(
                    batch.TestType,
                    batch.Subject,
                    unit.ResolvedGrade,
                    unit.Sequence,
                    unit.StepSetIndex,
                    unit.StepVariationIndex);
            }
            catch (ArgumentException)
            {
                skippedNonDraftCount++;
                continue;
            }
            catch (DomainValidationException)
            {
                skippedNonDraftCount++;
                continue;
            }

            var changed = false;
            if (!string.Equals(
                    unit.FinalTemplateName,
                    expectedName,
                    StringComparison.Ordinal)
                || unit.UserConfirmedBaseName is not null)
            {
                unit.FinalTemplateName = expectedName;
                unit.UserConfirmedBaseName = null;
                changed = true;
            }

            if (!string.Equals(
                    template.Title,
                    expectedName,
                    StringComparison.Ordinal))
            {
                template.Title = expectedName;
                template.UpdatedAt = now;
                changed = true;
            }

            var expectedSourceName = $"{expectedName}.pdf";
            foreach (var source in version.Sources.Where(item =>
                         item.UploadSessionId == batch.SourceId
                         && item.Ordinal == 0
                         && item.FileReferenceId == unit.DerivedSource?.FileReferenceId))
            {
                if (string.Equals(
                        source.DisplayName,
                        expectedSourceName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                source.DisplayName = expectedSourceName;
                renamedSourceCount++;
                version.UpdatedAt = now;
                changed = true;
            }

            if (changed)
            {
                renamedUnitCount++;
            }
        }

        if (renamedUnitCount > 0)
        {
            batch.UpdatedAt = now;
        }

        return new NameReconciliationSummary(
            renamedUnitCount,
            renamedSourceCount,
            skippedNonDraftCount);
    }

    private static void ValidateCompletedBatch(TemplateGenerationBatchEntity batch)
    {
        if (batch.Units.Count != batch.ExpectedUnitCount
            || batch.Units.Any(item =>
                item.Status != TemplateGenerationUnitStatus.Confirmed
                || item.CreatedTemplateId is null
                || item.CreatedTemplateVersionId is null))
        {
            throw StateConflict(batch.Revision);
        }
    }

    private static void RequireFinalCheck(TemplateGenerationBatchEntity batch)
    {
        if (batch.Status != TemplateGenerationBatchStatus.NeedsFinalCheck)
        {
            throw StateConflict(batch.Revision);
        }
    }

    private static void ValidateAccess(
        TemplateGenerationBatchEntity batch,
        string staffUserId,
        bool isAdministrator)
    {
        if (!isAdministrator
            && !string.Equals(
                batch.CreatedByUserId,
                staffUserId,
                StringComparison.Ordinal))
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status403Forbidden,
                "BATCH_FORBIDDEN",
                "この生成バッチを操作できません",
                "作成者または管理者として操作してください。");
        }
    }

    private async Task SaveAsync(
        long currentBatchRevision,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale(currentBatchRevision);
        }
        catch (DbUpdateException)
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "TEMPLATE_GENERATION_CONFLICT",
                "変更を保存できませんでした",
                "最新の状態を読み込んでからやり直してください。",
                currentBatchRevision);
        }
    }

    private void AddAudit(
        string actorStaffUserId,
        string eventType,
        string objectType,
        string objectId,
        string correlationId,
        object safeMetadata)
    {
        _db.AuditEvents.Add(new AuditEventEntity
        {
            Id = _ids.NewId(),
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorStaffUserId = actorStaffUserId,
            EventType = eventType,
            ObjectType = objectType,
            ObjectId = objectId,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            SafeMetadataJson = JsonSerializer.Serialize(safeMetadata, JsonOptions),
        });
    }

    private static void RecomputeDuplicateWarnings(
        IEnumerable<TemplateGenerationUnitEntity> units)
    {
        var materialized = units.ToArray();
        foreach (var unit in materialized)
        {
            RemoveWarnings(
                unit,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    TemplateNamePolicy.DuplicateNameErrorCode,
                });
        }

        foreach (var duplicate in materialized
                     .Where(item => !string.IsNullOrWhiteSpace(item.FinalTemplateName))
                     .GroupBy(item => item.FinalTemplateName!, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .SelectMany(group => group))
        {
            AddWarning(
                duplicate,
                new GenerationWarning(
                    TemplateNamePolicy.DuplicateNameErrorCode,
                    GenerationWarningSeverity.Blocking,
                    "作成予定のテンプレート名が重複しています。"));
        }
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
            return
            [
                new GenerationWarning(
                    "TEMPLATE_DRAFT_INVALID",
                    GenerationWarningSeverity.Blocking,
                    "保存済みの警告情報を確認できません。"),
            ];
        }
    }

    private static void RemoveWarnings(
        TemplateGenerationUnitEntity unit,
        HashSet<string> warningCodes) =>
        WriteWarnings(
            unit,
            ReadWarnings(unit)
                .Where(item => !warningCodes.Contains(item.Code))
                .ToList());

    private static void AddWarning(
        TemplateGenerationUnitEntity unit,
        GenerationWarning warning)
    {
        var warnings = ReadWarnings(unit);
        if (warnings.All(item => item.Code != warning.Code))
        {
            warnings.Add(warning);
            WriteWarnings(unit, warnings);
        }
    }

    private static void WriteWarnings(
        TemplateGenerationUnitEntity unit,
        IReadOnlyCollection<GenerationWarning> warnings) =>
        unit.WarningsJson = JsonSerializer.Serialize(warnings, JsonOptions);

    private static string NormalizeName(string value)
    {
        try
        {
            return TemplateNamePolicy.NormalizePrintedName(value);
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }
    }

    private static string CreateFinalName(
        TestType testType,
        string baseName,
        int? stepVariationIndex = null)
    {
        try
        {
            return TemplateNamePolicy.CreateFinalName(
                testType,
                baseName,
                stepVariationIndex);
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }
    }

    private static bool ApplyKnownTestName(
        TemplateGenerationBatchEntity batch,
        TemplateGenerationUnitEntity unit)
    {
        if (batch.TestType == TestType.Other)
        {
            return false;
        }

        string? expectedName = null;
        if (unit.ResolvedGrade is >= GradeLevel.Grade1
            and <= GradeLevel.Grade6)
        {
            try
            {
                expectedName = TemplateNamePolicy.CreateKnownTestName(
                    batch.TestType,
                    batch.Subject,
                    unit.ResolvedGrade,
                    unit.Sequence,
                    unit.StepSetIndex,
                    unit.StepVariationIndex);
            }
            catch (DomainValidationException exception)
            {
                throw FromDomainValidation(exception);
            }
            catch (ArgumentException)
            {
                throw Invalid(
                    "GENERATION_PLAN_INVALID",
                    "ページ分割の記録が一致しません",
                    "このバッチを作り直してください。");
            }
        }

        var changed = unit.UserConfirmedBaseName is not null
            || !string.Equals(
                unit.FinalTemplateName,
                expectedName,
                StringComparison.Ordinal);
        unit.UserConfirmedBaseName = null;
        unit.FinalTemplateName = expectedName;
        RemoveWarnings(unit, NameWarningCodes);
        return changed;
    }

    private static string OperationKey(
        string operation,
        string operationId,
        string scope) =>
        $"{operation}:{Sha256($"{scope}\n{operationId}")}";

    private static string? NormalizeTeacherNote(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= MaximumTeacherNoteLength
            ? normalized
            : normalized[..MaximumTeacherNoteLength];
    }

    private static string? BoundedTeacherNote(List<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return null;
        }

        var value = string.Join(
            "\n",
            warnings.Select(item => $"[AI確認] {item}"));
        return value.Length <= MaximumTeacherNoteLength
            ? value
            : value[..MaximumTeacherNoteLength];
    }

    private static bool RequiresPermanentReview(
        CanonicalTemplateGenerationQuestion question) =>
        question.QuestionType == "unsupported";

    private static string ToReviewNote(TemplateExtractionReviewIssue issue) =>
        $"[{issue.Code}] {issue.Message}";

    private static string GradingModeFor(string questionType) =>
        QuestionGradingDefaultPolicy.GradingModeFor(questionType);

    private static string CategoryLabel(TestType testType) =>
        testType switch
        {
            TestType.Hop => "HOP",
            TestType.Step => "STEP",
            TestType.ClassPlacement => "クラス分けテスト",
            TestType.Other => "その他",
            _ => throw new ArgumentOutOfRangeException(nameof(testType)),
        };

    private static string GradeLabel(GradeLevel grade) =>
        $"{(int)grade}年生";

    private static TemplateGenerationBatchServiceException FromDomainValidation(
        DomainValidationException exception)
    {
        var error = exception.Errors[0];
        return Invalid(error.Code, "入力内容を確認してください", error.Message);
    }

    private static TemplateGenerationBatchServiceException Invalid(
        string code,
        string title,
        string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, title, detail);

    private static TemplateGenerationBatchServiceException NotFound(
        string code = "BATCH_NOT_FOUND") =>
        new(
            StatusCodes.Status404NotFound,
            code,
            "対象が見つかりません",
            "指定された対象は存在しないか、利用できません。");

    private static TemplateGenerationBatchServiceException StateConflict(
        long currentRowVersion) =>
        new(
            StatusCodes.Status409Conflict,
            "BATCH_STATE_INVALID",
            "現在の状態では操作できません",
            "最新の状態を読み込んでからやり直してください。",
            currentRowVersion);

    private static TemplateGenerationBatchServiceException Stale(
        long currentRowVersion) =>
        new(
            StatusCodes.Status409Conflict,
            "STALE_ROW_VERSION",
            "他の操作によって更新されています",
            "最新の状態を読み込んでからやり直してください。",
            currentRowVersion);

    private sealed record PreparedUnit(
        TemplateGenerationUnitEntity Unit,
        CanonicalTemplateGenerationDraft Draft,
        string DerivedFileReferenceId,
        string? AiRequestId);
}
