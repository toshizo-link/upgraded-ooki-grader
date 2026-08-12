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
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Observability;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Services;

public sealed record TemplateGenerationBatchOptions
{
    public int MaximumUnitsPerBatch { get; init; } = 200;
    public int MaximumSourcePages { get; init; } = 1_000;

    internal void Validate()
    {
        if (MaximumUnitsPerBatch is < 1 or > 1_000
            || MaximumSourcePages is < 1 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TemplateGenerationBatchOptions),
                "Template-generation limits are invalid.");
        }
    }
}

public sealed record CreateTemplateGenerationBatchCommand(
    string SourceId,
    TestType TestType,
    string Subject,
    AnswerStyle? AnswerStyle,
    long ExpectedSourceRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record GenerateTemplateGenerationBatchCommand(
    string BatchId,
    long ExpectedRowVersion,
    string StaffUserId,
    bool IsAdministrator,
    string OperationId,
    string CorrelationId);

public sealed record TemplateGenerationUnitSnapshot(
    string Id,
    int Sequence,
    TemplateGenerationUnitStatus Status,
    int FirstPage,
    int LastPage,
    int? StepSetIndex,
    int? StepVariationIndex,
    string? Suffix,
    int OrientationAttemptCount,
    JsonElement AppliedRotations,
    string? PrintedTestName,
    string? UserConfirmedBaseName,
    string? FinalTemplateName,
    GradeLevel FilenameGrade,
    GradeLevel PaperGrade,
    GradeLevel ResolvedGrade,
    GradeEvidence GradeEvidence,
    bool GradeConfirmedByUser,
    JsonElement Warnings,
    string? CreatedTemplateId,
    string? CreatedTemplateVersionId,
    string? ExtractionJobId,
    int QuestionCount,
    long RowVersion);

public sealed record CreatedTemplateSnapshot(
    string TemplateId,
    string VersionId,
    string Title);

public sealed record TemplateGenerationBatchSnapshot(
    string Id,
    TemplateGenerationBatchStatus Status,
    TestType TestType,
    string Subject,
    AnswerStyle? AnswerStyle,
    TemplatePromptSystem PromptSystem,
    string SourceId,
    int SourcePageCount,
    int ExpectedUnitCount,
    int CompletedUnitCount,
    int FailedUnitCount,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    long RowVersion,
    IReadOnlyList<TemplateGenerationUnitSnapshot> Units,
    string? SourceDisplayName,
    IReadOnlyList<CreatedTemplateSnapshot> CreatedTemplates)
{
    public bool FinalCheckReady =>
        Status == TemplateGenerationBatchStatus.NeedsFinalCheck
        && Units.Count == ExpectedUnitCount
        && Units.All(unit => unit.Status == TemplateGenerationUnitStatus.Extracted);
}

public sealed record ResumableTemplateGenerationBatchSnapshot(
    string Id,
    TemplateGenerationBatchStatus Status,
    TestType TestType,
    string Subject,
    AnswerStyle? AnswerStyle,
    int SourcePageCount,
    int ExpectedUnitCount,
    int CompletedUnitCount,
    int FailedUnitCount,
    string? LastErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    long RowVersion,
    string DetailUrl);

public sealed record ResumableTemplateGenerationBatchListSnapshot(
    IReadOnlyList<ResumableTemplateGenerationBatchSnapshot> Items,
    int Limit);

public sealed class TemplateGenerationBatchServiceException(
    int statusCode,
    string code,
    string title,
    string detail,
    long? currentRowVersion = null) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public long? CurrentRowVersion { get; } = currentRowVersion;
}

/// <summary>
/// Owns durable deterministic planning and job fan-out. It performs no AI
/// classification and never derives page ranges from document contents.
/// </summary>
public sealed class TemplateGenerationBatchService
{
    public const int DefaultResumableBatchLimit = 20;
    public const int MaximumResumableBatchLimit = 50;
    public const string UnitJobType = "gemini_template_generation_unit";
    public const int UnitJobSchemaVersion = 1;
    public const string ExtractionPromptVersion = "template-extract-v2.0.0";
    public const string ExtractionSchemaVersion = "template_extract_v5";

    private static readonly HashSet<string> SupportedSubjects =
        ["算数", "国語", "理科", "社会"];
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OokiGraderDbContext _db;
    private readonly IContentStore _contentStore;
    private readonly IPdfPageCountReader _pageCountReader;
    private readonly ITemplateUnitPlanner _planner;
    private readonly IUlidGenerator _ids;
    private readonly TimeProvider _timeProvider;
    private readonly TemplateGenerationBatchOptions _options;
    private readonly IAiPromptBundleCatalog _promptCatalog;
    private readonly IAiProviderFeaturePolicy _providerFeaturePolicy;

    public TemplateGenerationBatchService(
        OokiGraderDbContext db,
        IContentStore contentStore,
        IPdfPageCountReader pageCountReader,
        ITemplateUnitPlanner planner,
        IUlidGenerator ids,
        TimeProvider timeProvider,
        IOptions<TemplateGenerationBatchOptions> options,
        IAiPromptBundleCatalog promptCatalog,
        IAiProviderFeaturePolicy providerFeaturePolicy)
    {
        _db = db;
        _contentStore = contentStore;
        _pageCountReader = pageCountReader;
        _planner = planner;
        _ids = ids;
        _timeProvider = timeProvider;
        _options = options.Value;
        _promptCatalog = promptCatalog;
        _providerFeaturePolicy = providerFeaturePolicy;
        _options.Validate();
    }

    public async Task<TemplateGenerationBatchSnapshot> CreateAsync(
        CreateTemplateGenerationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.SourceId)
            || command.ExpectedSourceRowVersion <= 0)
        {
            throw Invalid(
                "SOURCE_INVALID",
                "アップロード元を確認できません",
                "アップロード済みPDFと現在の行バージョンを指定してください。");
        }

        if (!SupportedSubjects.Contains(command.Subject))
        {
            throw Invalid(
                "SUBJECT_INVALID",
                "教科を確認してください",
                "算数、国語、理科、社会のいずれかを選択してください。");
        }

        TemplatePromptSystem promptSystem;
        try
        {
            promptSystem = TemplatePromptRouter.Resolve(
                command.TestType,
                command.AnswerStyle);
        }
        catch (DomainValidationException exception)
        {
            throw FromDomainValidation(exception);
        }

        var source = await LoadSourceAsync(command.SourceId, cancellationToken)
            .ConfigureAwait(false);
        ValidateSourceAccess(source, command.StaffUserId, command.IsAdministrator);
        ValidateSource(source, command.ExpectedSourceRowVersion);

        int pageCount;
        try
        {
            await using var sourceStream = await _contentStore.OpenReadAsync(
                    new ContentObjectLocator(
                        ContentStorageClass.TemplateSource,
                        source.ContentSha256,
                        source.ContentBytes,
                        source.ContentExtension),
                    cancellationToken)
                .ConfigureAwait(false);
            pageCount = await _pageCountReader.GetPageCountAsync(
                    sourceStream,
                    _options.MaximumSourcePages,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PdfPageCountException exception)
        {
            throw Invalid(
                exception.Code == "PDF_PAGE_COUNT_INVALID"
                    ? "PDF_PAGE_COUNT_INVALID"
                    : "SOURCE_INVALID",
                "PDFを確認できません",
                "暗号化されていない有効なPDFを選択してください。");
        }
        catch (FileNotFoundException)
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "SOURCE_MISSING",
                "元PDFが見つかりません",
                "PDFを再アップロードしてください。");
        }

        IReadOnlyList<TemplateUnitPlan> plan;
        try
        {
            plan = _planner.Plan(command.TestType, pageCount);
        }
        catch (DomainValidationException exception)
        {
            if (command.TestType == TestType.Step
                && exception.Errors.Any(error =>
                    error.Code == "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX"))
            {
                TemplateGenerationMetrics.StepPageCountRejected(
                    promptSystem,
                    TemplateGenerationProfile.CurrentProfileVersion);
            }

            throw FromDomainValidation(exception);
        }

        if (plan.Count > _options.MaximumUnitsPerBatch)
        {
            throw Invalid(
                "BATCH_UNIT_LIMIT_EXCEEDED",
                "一度に生成できる件数を超えています",
                $"1つのPDFから作成できるテンプレートは最大{_options.MaximumUnitsPerBatch}件です。");
        }

        var filenameGrade = GradeFromFileNameParser.Parse(source.OriginalFileName);
        var now = _timeProvider.GetUtcNow();
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentSource = await LoadTrackedSourceAsync(
                command.SourceId,
                cancellationToken)
            .ConfigureAwait(false);
        ValidateSourceAccess(
            currentSource,
            command.StaffUserId,
            command.IsAdministrator);
        ValidateSource(currentSource, command.ExpectedSourceRowVersion);
        if (!string.Equals(
                currentSource.ContentSha256,
                source.ContentSha256,
                StringComparison.Ordinal))
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "SOURCE_CHANGED",
                "元PDFが変更されました",
                "ページ構成を再確認してからやり直してください。",
                currentSource.Revision);
        }

        var batchId = _ids.NewId();
        var planHash = ComputePlanHash(
            currentSource,
            pageCount,
            command.TestType,
            command.Subject,
            command.AnswerStyle,
            promptSystem);
        var batch = new TemplateGenerationBatchEntity
        {
            Id = batchId,
            Status = TemplateGenerationBatchStatus.Draft,
            TestType = command.TestType,
            Subject = command.Subject,
            AnswerStyle = command.AnswerStyle,
            PromptSystem = promptSystem,
            SourceId = command.SourceId,
            SourcePageCount = pageCount,
            ExpectedUnitCount = plan.Count,
            CompletedUnitCount = 0,
            FailedUnitCount = 0,
            CurrentOperationId = command.OperationId,
            PlanHash = planHash,
            CreatedByUserId = command.StaffUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.TemplateGenerationBatches.Add(batch);

        var warningJson = filenameGrade.ErrorCode is null
            ? "[]"
            : JsonSerializer.Serialize(
                new[]
                {
                    new GenerationWarning(
                        filenameGrade.ErrorCode,
                        GenerationWarningSeverity.Blocking,
                        "ファイル名に複数の学年表記があります。"),
                },
                CanonicalJsonOptions);
        foreach (var unitPlan in plan)
        {
            var profile = new TemplateGenerationProfile(
                TemplateGenerationProfile.CurrentProfileVersion,
                command.TestType,
                command.Subject,
                command.AnswerStyle,
                promptSystem,
                pageCount,
                unitPlan.Sequence,
                unitPlan.FirstPage,
                unitPlan.LastPage,
                unitPlan.StepSetIndex,
                unitPlan.StepVariationIndex,
                unitPlan.DeterministicSuffix,
                TemplateGenerationProfile.CurrentSplitPolicyVersion,
                TemplateGenerationProfile.CurrentNamingPolicyVersion,
                ExtractionPromptVersion,
                ExtractionSchemaVersion);
            var resolvedFilenameGrade = filenameGrade.IsUnambiguous
                ? filenameGrade.Grade
                : GradeLevel.Unknown;
            _db.TemplateGenerationUnits.Add(new TemplateGenerationUnitEntity
            {
                Id = _ids.NewId(),
                BatchId = batchId,
                Sequence = unitPlan.Sequence,
                Status = TemplateGenerationUnitStatus.Pending,
                TestType = command.TestType,
                AnswerStyle = command.AnswerStyle,
                FirstPage = unitPlan.FirstPage,
                LastPage = unitPlan.LastPage,
                StepSetIndex = unitPlan.StepSetIndex,
                StepVariationIndex = unitPlan.StepVariationIndex,
                DeterministicSuffix = unitPlan.DeterministicSuffix,
                PromptSystem = promptSystem,
                GenerationProfileJson = JsonSerializer.Serialize(
                    profile,
                    CanonicalJsonOptions),
                GenerationProfileHash = profile.ComputeHash(),
                OrientationAttemptCount = 0,
                AppliedRotationsJson = "[]",
                FilenameGrade = resolvedFilenameGrade,
                PaperGrade = GradeLevel.Unknown,
                ResolvedGrade = resolvedFilenameGrade,
                GradeEvidence = resolvedFilenameGrade == GradeLevel.Unknown
                    ? GradeEvidence.None
                    : GradeEvidence.FileName,
                GradeConfirmedByUser = false,
                WarningsJson = warningJson,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        AddAudit(
            command.StaffUserId,
            "TemplateGenerationBatchCreated",
            "template_generation_batch",
            batchId,
            command.CorrelationId,
            new
            {
                testType = command.TestType,
                promptSystem,
                sourcePageCount = pageCount,
                expectedUnitCount = plan.Count,
                planHash,
                sourceRowVersion = command.ExpectedSourceRowVersion,
                batchRowVersion = batch.Revision,
                initialUnitRowVersion = 1,
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = ExtractionPromptVersion,
                schemaVersion = ExtractionSchemaVersion,
            });
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationPlanValidated",
            "template_generation_batch",
            batchId,
            command.CorrelationId,
            new
            {
                splitPolicyVersion =
                    TemplateGenerationProfile.CurrentSplitPolicyVersion,
                pageCount,
                unitCount = plan.Count,
                batchRowVersion = batch.Revision,
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = ExtractionPromptVersion,
                schemaVersion = ExtractionSchemaVersion,
            });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        TemplateGenerationMetrics.BatchCreated(
            command.TestType,
            promptSystem,
            TemplateGenerationProfile.CurrentProfileVersion,
            plan.Count);
        return await GetRequiredAsync(
                batchId,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TemplateGenerationBatchSnapshot> GenerateAsync(
        GenerateTemplateGenerationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = await _db.TemplateGenerationBatches
            .Include(entity => entity.Units)
            .SingleOrDefaultAsync(entity => entity.Id == command.BatchId, cancellationToken)
            .ConfigureAwait(false);
        if (batch is null)
        {
            throw NotFound();
        }

        ValidateBatchAccess(batch, command.StaffUserId, command.IsAdministrator);
        if (batch.Revision != command.ExpectedRowVersion)
        {
            throw Stale(batch.Revision);
        }

        if (batch.Status == TemplateGenerationBatchStatus.Generating
            && batch.Units.All(unit => unit.ExtractionJobId is not null))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return await GetRequiredAsync(
                    batch.Id,
                    command.StaffUserId,
                    command.IsAdministrator,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (batch.Status != TemplateGenerationBatchStatus.Draft
            || batch.Units.Count != batch.ExpectedUnitCount
            || batch.Units.Any(unit =>
                unit.Status != TemplateGenerationUnitStatus.Pending
                || unit.ExtractionJobId is not null))
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "BATCH_STATE_INVALID",
                "生成を開始できません",
                "バッチの状態を更新してからもう一度お試しください。",
                batch.Revision);
        }

        // This must remain before job creation and before unit/batch state
        // changes. A missing or stale AI profile is an operator-actionable
        // precondition failure, not 1 failed background job per planned unit.
        var profileSelection = await TemplateExtractionAiProfilePolicy
            .FindCurrentUsableAsync(
                _db,
                _promptCatalog,
                _providerFeaturePolicy,
                cancellationToken)
            .ConfigureAwait(false);
        if (profileSelection is null)
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "TEMPLATE_EXTRACTION_PROFILE_UNAVAILABLE",
                "テストひな形を生成するAI設定が利用できません",
                "管理のAI設定でAPIキーの接続確認を行い、ひな形作成用の設定を有効にしてから、もう一度お試しください。",
                batch.Revision);
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var unit in batch.Units.OrderBy(entity => entity.Sequence))
        {
            var jobId = _ids.NewId();
            _db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = jobId,
                Type = UnitJobType,
                SchemaVersion = UnitJobSchemaVersion,
                DeduplicationKey =
                    $"template-generation-unit:{unit.Id}:{unit.GenerationProfileHash}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(
                    new
                    {
                        unitId = unit.Id,
                        batchId = batch.Id,
                        generationProfileHash = unit.GenerationProfileHash,
                    },
                    CanonicalJsonOptions),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now,
                CorrelationId = command.CorrelationId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            unit.ExtractionJobId = jobId;
            unit.Status = TemplateGenerationUnitStatus.Queued;
        }

        batch.Status = TemplateGenerationBatchStatus.Generating;
        batch.CurrentOperationId = command.OperationId;
        AddAudit(
            command.StaffUserId,
            "TemplateGenerationStarted",
            "template_generation_batch",
            batch.Id,
            command.CorrelationId,
            new
            {
                unitCount = batch.ExpectedUnitCount,
                jobType = UnitJobType,
                jobSchemaVersion = UnitJobSchemaVersion,
                expectedBatchRowVersion = command.ExpectedRowVersion,
                nextBatchRowVersion = checked(batch.Revision + 1),
                profileVersion = TemplateGenerationProfile.CurrentProfileVersion,
                promptVersion = ExtractionPromptVersion,
                schemaVersion = ExtractionSchemaVersion,
            });
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Stale(batch.Revision);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRequiredAsync(
                batch.Id,
                command.StaffUserId,
                command.IsAdministrator,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TemplateGenerationBatchSnapshot?> GetAsync(
        string batchId,
        string staffUserId,
        bool isAdministrator,
        CancellationToken cancellationToken)
    {
        var batch = await _db.TemplateGenerationBatches
            .AsNoTracking()
            .Include(entity => entity.Source)
            .Include(entity => entity.Units)
            .SingleOrDefaultAsync(entity => entity.Id == batchId, cancellationToken)
            .ConfigureAwait(false);
        if (batch is null)
        {
            return null;
        }

        ValidateBatchAccess(batch, staffUserId, isAdministrator);
        return ToSnapshot(batch);
    }

    public async Task<ResumableTemplateGenerationBatchListSnapshot>
        ListResumableAsync(
            string staffUserId,
            bool isAdministrator,
            int requestedLimit,
            CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(
            requestedLimit,
            1,
            MaximumResumableBatchLimit);
        var query = _db.TemplateGenerationBatches
            .AsNoTracking()
            .Where(batch =>
                batch.Status == TemplateGenerationBatchStatus.Draft
                || batch.Status == TemplateGenerationBatchStatus.Validating
                || batch.Status == TemplateGenerationBatchStatus.Generating
                || batch.Status == TemplateGenerationBatchStatus.NeedsFinalCheck
                || batch.Status == TemplateGenerationBatchStatus.Confirming
                || batch.Status == TemplateGenerationBatchStatus.Failed);

        if (!isAdministrator)
        {
            query = query.Where(batch =>
                batch.CreatedByUserId == staffUserId);
        }

        var batches = await query
            .OrderByDescending(batch => batch.CreatedAt)
            .ThenByDescending(batch => batch.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ResumableTemplateGenerationBatchListSnapshot(
            batches.Select(ToResumableSnapshot).ToArray(),
            limit);
    }

    private async Task<TemplateGenerationBatchSnapshot> GetRequiredAsync(
        string batchId,
        string staffUserId,
        bool isAdministrator,
        CancellationToken cancellationToken) =>
        await GetAsync(batchId, staffUserId, isAdministrator, cancellationToken)
            .ConfigureAwait(false)
        ?? throw NotFound();

    private async Task<SourceSnapshot> LoadSourceAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        var source = await (
                from upload in _db.UploadSessions.AsNoTracking()
                join reference in _db.FileReferences.AsNoTracking()
                    on upload.Id equals reference.OwnerId
                join fileObject in _db.FileObjects.AsNoTracking()
                    on reference.FileObjectId equals fileObject.Id
                where upload.Id == sourceId
                    && reference.OwnerType == "upload_session"
                    && reference.Purpose == "template_source"
                orderby reference.CreatedAt, reference.Id
                select new SourceSnapshot(
                    upload.Id,
                    upload.CreatedByStaffUserId,
                    upload.OriginalFileName,
                    upload.State,
                    upload.Purpose,
                    upload.DestinationType,
                    upload.DeclaredMimeType,
                    upload.FinalSha256,
                    upload.Revision,
                    fileObject.Sha256,
                    fileObject.Bytes,
                    fileObject.VerifiedMime,
                    fileObject.Extension,
                    fileObject.StorageClass,
                    fileObject.State))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return source ?? throw NotFound("SOURCE_NOT_FOUND");
    }

    private async Task<SourceSnapshot> LoadTrackedSourceAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        // Loading the upload with tracking makes its concurrency revision part
        // of the validation transaction; file objects are content-addressed.
        var upload = await _db.UploadSessions
            .SingleOrDefaultAsync(entity => entity.Id == sourceId, cancellationToken)
            .ConfigureAwait(false);
        if (upload is null)
        {
            throw NotFound("SOURCE_NOT_FOUND");
        }

        var source = await LoadSourceAsync(sourceId, cancellationToken)
            .ConfigureAwait(false);
        return source with { Revision = upload.Revision };
    }

    private static void ValidateSourceAccess(
        SourceSnapshot source,
        string staffUserId,
        bool isAdministrator)
    {
        if (!isAdministrator
            && !string.Equals(
                source.CreatedByStaffUserId,
                staffUserId,
                StringComparison.Ordinal))
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status403Forbidden,
                "SOURCE_FORBIDDEN",
                "このPDFを使用できません",
                "自分がアップロードしたPDFを選択してください。");
        }
    }

    private static void ValidateSource(SourceSnapshot source, long expectedRevision)
    {
        if (source.Revision != expectedRevision)
        {
            throw new TemplateGenerationBatchServiceException(
                StatusCodes.Status409Conflict,
                "STALE_ROW_VERSION",
                "PDFの状態が更新されています",
                "最新の状態を読み込んでからやり直してください。",
                source.Revision);
        }

        if (source.State != "completed"
            || source.Purpose != "template_source"
            || source.DestinationType != "template_source"
            || source.VerifiedMime != "application/pdf"
            || source.ContentExtension != "pdf"
            || source.ContentStorageClass != ContentStorageClass.TemplateSource.ToString()
            || source.ContentState != "available"
            || source.FinalSha256 is null
            || !string.Equals(
                source.FinalSha256,
                source.ContentSha256,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "SOURCE_INVALID",
                "PDFを使用できません",
                "アップロードを完了した有効なPDFを選択してください。");
        }
    }

    private static void ValidateBatchAccess(
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
                "この生成バッチを表示できません",
                "作成者または管理者として操作してください。");
        }
    }

    private void AddAudit(
        string actorStaffUserId,
        string eventType,
        string objectType,
        string objectId,
        string correlationId,
        object metadata)
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
            SafeMetadataJson = JsonSerializer.Serialize(
                metadata,
                CanonicalJsonOptions),
        });
    }

    private static TemplateGenerationBatchSnapshot ToSnapshot(
        TemplateGenerationBatchEntity batch) =>
        new(
            batch.Id,
            batch.Status,
            batch.TestType,
            batch.Subject,
            batch.AnswerStyle,
            batch.PromptSystem,
            batch.SourceId,
            batch.SourcePageCount,
            batch.ExpectedUnitCount,
            batch.CompletedUnitCount,
            batch.FailedUnitCount,
            batch.LastErrorCode,
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.CompletedAt,
            batch.Revision,
            batch.Units
                .OrderBy(unit => unit.Sequence)
                .Select(unit => new TemplateGenerationUnitSnapshot(
                    unit.Id,
                    unit.Sequence,
                    unit.Status,
                    unit.FirstPage,
                    unit.LastPage,
                    unit.StepSetIndex,
                    unit.StepVariationIndex,
                    unit.DeterministicSuffix,
                    unit.OrientationAttemptCount,
                    ParseStoredJson(unit.AppliedRotationsJson, JsonValueKind.Array),
                    unit.PrintedTestName,
                    unit.UserConfirmedBaseName,
                    unit.FinalTemplateName,
                    unit.FilenameGrade,
                    unit.PaperGrade,
                    unit.ResolvedGrade,
                    unit.GradeEvidence,
                    unit.GradeConfirmedByUser,
                    ParseStoredJson(unit.WarningsJson, JsonValueKind.Array),
                    unit.CreatedTemplateId,
                    unit.CreatedTemplateVersionId,
                    unit.ExtractionJobId,
                    CountDraftQuestions(unit.ExtractionDraftJson),
                    unit.Revision))
                .ToArray(),
            batch.Source?.OriginalFileName,
            batch.Units
                .Where(unit => unit.CreatedTemplateId is not null
                    && unit.CreatedTemplateVersionId is not null)
                .OrderBy(unit => unit.Sequence)
                .Select(unit => new CreatedTemplateSnapshot(
                    unit.CreatedTemplateId!,
                    unit.CreatedTemplateVersionId!,
                    unit.FinalTemplateName ?? $"テンプレート {unit.Sequence}"))
                .ToArray());

    private static ResumableTemplateGenerationBatchSnapshot
        ToResumableSnapshot(TemplateGenerationBatchEntity batch) =>
        new(
            batch.Id,
            batch.Status,
            batch.TestType,
            batch.Subject,
            batch.AnswerStyle,
            batch.SourcePageCount,
            batch.ExpectedUnitCount,
            batch.CompletedUnitCount,
            batch.FailedUnitCount,
            batch.LastErrorCode,
            batch.CreatedAt,
            batch.UpdatedAt,
            batch.CompletedAt,
            batch.Revision,
            $"/api/v1/template-generation-batches/{batch.Id}");

    private static int CountDraftQuestions(string? draftJson)
    {
        if (string.IsNullOrWhiteSpace(draftJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(draftJson);
            if (!document.RootElement.TryGetProperty("pages", out var pages)
                || pages.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            return pages.EnumerateArray().Sum(page =>
                page.TryGetProperty("questions", out var questions)
                && questions.ValueKind == JsonValueKind.Array
                    ? questions.GetArrayLength()
                    : 0);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static JsonElement ParseStoredJson(
        string json,
        JsonValueKind requiredKind)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == requiredKind
                ? document.RootElement.Clone()
                : EmptyArray();
        }
        catch (JsonException)
        {
            return EmptyArray();
        }
    }

    private static JsonElement EmptyArray()
    {
        using var document = JsonDocument.Parse("[]");
        return document.RootElement.Clone();
    }

    private static string ComputePlanHash(
        SourceSnapshot source,
        int pageCount,
        TestType testType,
        string subject,
        AnswerStyle? answerStyle,
        TemplatePromptSystem promptSystem)
    {
        var canonical = JsonSerializer.Serialize(
            new
            {
                sourceId = source.Id,
                sourceSha256 = source.ContentSha256,
                sourceRowVersion = source.Revision,
                pageCount,
                testType,
                subject,
                answerStyle,
                promptSystem,
                splitPolicyVersion =
                    TemplateGenerationProfile.CurrentSplitPolicyVersion,
            },
            CanonicalJsonOptions);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static TemplateGenerationBatchServiceException FromDomainValidation(
        DomainValidationException exception)
    {
        var error = exception.Errors[0];
        return Invalid(
            error.Code,
            error.Code == "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX"
                ? "STEPのページ数を確認してください"
                : "入力内容を確認してください",
            error.Code == "STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX"
                ? "STEPのPDFは6ページ単位でアップロードしてください。"
                : error.Message);
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

    private static TemplateGenerationBatchServiceException Stale(
        long currentRowVersion) =>
        new(
            StatusCodes.Status409Conflict,
            "STALE_ROW_VERSION",
            "他の操作によって更新されています",
            "最新の状態を読み込んでからやり直してください。",
            currentRowVersion);

    private sealed record SourceSnapshot(
        string Id,
        string CreatedByStaffUserId,
        string OriginalFileName,
        string State,
        string Purpose,
        string? DestinationType,
        string DeclaredMimeType,
        string? FinalSha256,
        long Revision,
        string ContentSha256,
        long ContentBytes,
        string VerifiedMime,
        string ContentExtension,
        string ContentStorageClass,
        string ContentState);
}
