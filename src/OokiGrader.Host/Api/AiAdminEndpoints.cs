using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

public static class AiAdminEndpoints
{
    private const string PricingSnapshotListRoute =
        "GET:/api/v1/admin/pricing-snapshots";
    private const string EvaluationListRoute =
        "GET:/api/v1/admin/ai-task-profiles/{profileId}/evaluations";
    private const string SelectedModel = AiProviderRuntime.GeminiModel;
    private const int DefaultMetricsDays = 30;
    private const int MaximumMetricsDays = 90;
    private const int MaximumLatencySamples = 50_000;

    public static IEndpointRouteBuilder MapAiAdminEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .WithTags("AI administration")
            .RequireAuthorization("administrator");

        group.MapGet("/ai-connections", ListConnectionsAsync);
        group.MapPost("/ai-connections", CreateConnectionAsync);
        group.MapPut("/ai-connections/{connectionId}", ReplaceConnectionAsync);
        group.MapPost("/ai-connections/{connectionId}:test", ProbeConnectionAsync);
        group.MapGet("/ai-task-profiles", ListProfilesAsync);
        group.MapPost("/ai-task-profiles", CreateProfileAsync);
        group.MapPatch("/ai-task-profiles/{profileId}", UpdateProfileAsync);
        group.MapGet(
            "/ai-task-profiles/{profileId}/evaluations",
            ListEvaluationRecordsAsync);
        group.MapPost(
                "/ai-task-profiles/{profileId}/evaluations",
                CreateEvaluationRecordAsync)
            .RequireIdempotency();
        group.MapPost("/ai-task-profiles/{profileId}:validate", ValidateProfileAsync);
        group.MapPost("/ai-task-profiles/{profileId}:activate", ActivateProfileAsync);
        group.MapGet("/settings/budgets", GetBudgetAsync);
        group.MapPost("/settings/budgets", SetBudgetAsync);
        group.MapGet("/pricing-snapshots", ListPricingSnapshotsAsync);
        group.MapPost("/pricing-snapshots", CreatePricingSnapshotAsync);
        group.MapGet("/usage", GetUsageAsync);
        group.MapGet("/ai-metrics", GetMetricsAsync);

        return endpoints;
    }

    private static async Task<IResult> ListConnectionsAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var connections = await db.AiConnections
            .AsNoTracking()
            .OrderBy(connection => connection.Provider)
            .ThenBy(connection => connection.CreatedAt)
            .ToArrayAsync(cancellationToken);
        var items = new object[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            var connection = connections[index];
            var latestProbe = await db.AiCapabilityProbes
                .AsNoTracking()
                .Where(probe =>
                    probe.AiConnectionId == connection.Id
                    && probe.ConnectionRevision
                        == connection.CredentialRevision)
                .OrderByDescending(probe => probe.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            items[index] = ToConnectionResponse(connection, latestProbe);
        }

        return Results.Ok(Page(items));
    }

    private static async Task<IResult> CreateConnectionAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveAiConnectionRequest request,
        OokiGraderDbContext db,
        IAiSecretStore secretStore,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validation = ValidateConnectionRequest(request);
        if (validation is not null)
        {
            return validation(context);
        }

        _ = TryResolveConnectionSelection(
            request,
            out var provider,
            out var modelId);

        if (!providerFeaturePolicy.IsEnabled(provider))
        {
            return ProviderFeatureDisabled(context);
        }

        if (await db.AiConnections.AnyAsync(
                connection => connection.Provider == provider
                    && connection.State != "disabled",
                cancellationToken))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_CONNECTION_EXISTS",
                "AI接続は既に構成されています",
                "既存の接続でキーを交換するか、無効化してから追加してください。");
        }

        var now = timeProvider.GetUtcNow();
        var connection = new AiConnectionEntity
        {
            Id = UlidId.New(now),
            Provider = provider,
            EndpointProfile = AiProviderCatalog.GetEndpointProfile(provider),
            ModelId = modelId,
            TimeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 75, 5, 300),
            ConcurrencyLimit = Math.Clamp(request.ConcurrencyLimit ?? 2, 1, 16),
            State = "pending_probe",
            CreatedByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
            UpdatedAt = now,
        };
        connection.KeyFingerprint = Fingerprint(request.ApiKey);
        var secretReference = await secretStore.WriteAsync(
            connection.Id,
            connection.CredentialRevision,
            request.ApiKey.AsMemory(),
            cancellationToken);
        connection.SecretReference = secretReference.Value;
        db.AiConnections.Add(connection);
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.connection_created",
            connection.Id,
            new
            {
                connection.Provider,
                connection.ModelId,
                connection.KeyFingerprint,
            });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException
            {
                SqliteErrorCode: 19,
            })
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_CONNECTION_EXISTS",
                "AI接続は既に構成されています",
                "既存の接続でキーを交換するか、無効化してから追加してください。");
        }
        catch
        {
            await secretStore.DeleteAsync(secretReference, cancellationToken);
            throw;
        }

        ApiHelpers.SetRevisionEtag(context.Response, connection.Revision);
        return Results.Created(
            $"/api/v1/admin/ai-connections/{connection.Id}",
            ToConnectionResponse(connection));
    }

    private static async Task<IResult> ReplaceConnectionAsync(
        string connectionId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveAiConnectionRequest request,
        OokiGraderDbContext db,
        IAiSecretStore secretStore,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validation = ValidateConnectionRequest(request);
        if (validation is not null)
        {
            return validation(context);
        }


        _ = TryResolveConnectionSelection(
            request,
            out var provider,
            out var modelId);

        var connection = await db.AiConnections
            .Include(item => item.TaskProfiles)
            .SingleOrDefaultAsync(
                item => item.Id == connectionId,
                cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }


        if (connection.Provider != provider)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_CONNECTION_PROVIDER_IMMUTABLE",
                "接続の種類を変更できません",
                "Gemini と OpenRouter は別々の接続として追加してください。");
        }

        if (!providerFeaturePolicy.IsEnabled(connection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        if (!ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.Revision,
                out var expectedRevision)
            || expectedRevision != connection.Revision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_MISMATCH",
                "接続設定が更新されています",
                "最新の設定を読み込み直してから、もう一度保存してください。");
        }

        var remoteBatchInProgress = await db.AiBatches
            .AsNoTracking()
            .AnyAsync(
                batch =>
                    batch.AiConnectionId == connection.Id
                    && batch.ConnectionRevision
                        == connection.CredentialRevision
                    && (batch.State == "uploading"
                        || batch.State == "submitting"
                        || batch.State == "submitted"
                        || batch.State == "reconcile_required"
                        || batch.State == "pending"
                        || batch.State == "running"
                        || batch.State == "delayed"
                        || batch.State == "manual_review"
                        || batch.CleanupState == "pending"),
                cancellationToken);
        if (remoteBatchInProgress)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_KEY_ROTATION_BATCH_IN_PROGRESS",
                "AI APIキーを交換できません",
                "送信済みの Gemini Batch を照合し、リモートファイルの消去が完了してから交換してください。");
        }

        var previousReference = new AiSecretReference(connection.SecretReference);
        var nextCredentialRevision = checked(connection.CredentialRevision + 1);
        var nextReference = await secretStore.WriteAsync(
            connection.Id,
            nextCredentialRevision,
            request.ApiKey.AsMemory(),
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        connection.SecretReference = nextReference.Value;
        connection.KeyFingerprint = Fingerprint(request.ApiKey);
        connection.CredentialRevision = nextCredentialRevision;
        connection.TimeoutSeconds = Math.Clamp(request.TimeoutSeconds ?? 75, 5, 300);
        connection.ConcurrencyLimit = Math.Clamp(request.ConcurrencyLimit ?? 2, 1, 16);
        connection.EndpointProfile = AiProviderCatalog.GetEndpointProfile(provider);
        connection.ModelId = modelId;
        connection.State = "pending_probe";
        connection.LastCapabilityProbeState = null;
        connection.LastCapabilityProbeErrorCode = null;
        connection.LastCapabilityProbeAt = null;
        connection.LastBatchCapabilityProbeState = null;
        connection.LastBatchCapabilityProbeErrorCode = null;
        connection.LastBatchCapabilityProbeAt = null;
        connection.LastBatchCapabilityProbeCredentialRevision = null;
        connection.UpdatedAt = now;
        var deactivatedProfileCount = 0;
        foreach (var profile in connection.TaskProfiles.Where(
                     profile => profile.Active))
        {
            profile.Active = false;
            profile.UpdatedAt = now;
            deactivatedProfileCount++;
        }

        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.connection_key_replaced",
            connection.Id,
            new
            {
                connection.CredentialRevision,
                connection.KeyFingerprint,
                deactivatedProfileCount,
            });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await secretStore.DeleteAsync(nextReference, cancellationToken);
            throw;
        }

        _ = await secretStore.DeleteAsync(previousReference, cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, connection.Revision);
        return Results.Ok(ToConnectionResponse(connection));
    }

    private static async Task<IResult> ProbeConnectionAsync(
        string connectionId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        IAiSecretStore secretStore,
        IAiProviderClientResolver providerResolver,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        IAiPromptBundleCatalog promptCatalog,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var connection = await db.AiConnections.SingleOrDefaultAsync(
            item => item.Id == connectionId,
            cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(connection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        var settings = ToSettings(connection);
        AiCapabilityProbeResult result;
        using (var secret = await secretStore.ReadAsync(
                   new AiSecretReference(connection.SecretReference),
                   cancellationToken))
        {
            result = await providerResolver
                .GetRequired(connection.Provider)
                .ProbeAsync(
                settings,
                secret.Utf8Bytes,
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var probe = new AiCapabilityProbeEntity
        {
            Id = UlidId.New(now),
            AiConnectionId = connection.Id,
            ConnectionRevision = connection.CredentialRevision,
            State = result.State,
            Authentication = result.Authentication,
            ModelAvailable = result.ModelAvailable,
            ImageInput = result.ImageInput,
            StructuredOutput = result.StructuredOutput,
            UsageMetadata = result.UsageMetadata,
            BatchState = "not_run",
            BatchAvailable = false,
            BatchCleanupSucceeded = true,
            SafeErrorCode = result.SafeErrorCode,
            LatencyMilliseconds = result.Latency is null
                ? null
                : checked((long)Math.Round(result.Latency.Value.TotalMilliseconds)),
            CreatedAt = now,
            CompletedAt = now,
        };
        db.AiCapabilityProbes.Add(probe);
        connection.LastCapabilityProbeState = result.State;
        connection.LastCapabilityProbeErrorCode = result.SafeErrorCode;
        connection.LastCapabilityProbeAt = now;
        connection.LastBatchCapabilityProbeState = "not_run";
        connection.LastBatchCapabilityProbeErrorCode = null;
        connection.LastBatchCapabilityProbeAt = null;
        connection.LastBatchCapabilityProbeCredentialRevision = null;
        var imageTasksReady = result.State == "passed"
            && result.ImageInput
            && result.StructuredOutput
            && result.UsageMetadata
            && AiProviderCatalog.SupportsImageTasks(
                connection.Provider,
                connection.ModelId);
        connection.State = imageTasksReady ? "active" : "blocked";
        connection.UpdatedAt = now;
        if (imageTasksReady)
        {
            await EnsureDefaultProfilesAsync(
                db,
                connection,
                ApiHelpers.StaffId(principal),
                promptCatalog,
                now,
                cancellationToken);
        }

        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.connection_probed",
            connection.Id,
            new
            {
                result.State,
                result.Authentication,
                result.ModelAvailable,
                result.ImageInput,
                result.StructuredOutput,
                result.UsageMetadata,
                result.SafeErrorCode,
                processingMode = "standard_api",
            });
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, connection.Revision);
        return Results.Ok(new
        {
            probe.Id,
            result.State,
            result.Authentication,
            result.ModelAvailable,
            result.ImageInput,
            result.StructuredOutput,
            result.UsageMetadata,
            result.SafeErrorCode,
            latencyMilliseconds = probe.LatencyMilliseconds,
            processingMode = "standard_api",
            checkedAt = now,
        });
    }

    private static async Task<IResult> ListProfilesAsync(
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        CancellationToken cancellationToken)
    {
        var entities = await db.AiTaskProfiles
            .AsNoTracking()
            .Include(profile => profile.AiConnection)
            .OrderBy(profile => profile.TaskType)
            .ThenByDescending(profile => profile.Active)
            .ThenByDescending(profile => profile.CreatedAt)
            .ToArrayAsync(cancellationToken);
        var profiles = entities
            .Select(profile => ToProfileResponse(profile, promptCatalog))
            .ToArray();
        return Results.Ok(Page(profiles));
    }

    private static async Task<IResult> CreateProfileAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveAiTaskProfileRequest request,
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!IsTaskType(request.TaskType)
            || !IsProcessingStrategyValid(
                request.TaskType,
                request.ProcessingStrategy))
        {
            return Results.UnprocessableEntity();
        }

        var connection = await db.AiConnections.SingleOrDefaultAsync(
            item => item.Id == request.ConnectionId,
            cancellationToken);
        if (connection is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(connection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }


        if (request.ModelId is { } requestedModel
            && requestedModel.Trim() != connection.ModelId)
        {
            return Results.UnprocessableEntity();
        }

        if (!IsImageTaskConnectionReady(connection))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_IMAGE_CAPABILITY_REQUIRED",
                "画像AIプロファイルを作成できません",
                "画像入力と構造化出力の接続テストに合格したモデルを選んでください。");
        }

        var bundle = promptCatalog.GetRequired(request.TaskType);
        var now = timeProvider.GetUtcNow();
        var profile = BuildProfile(
            connection,
            request.TaskType,
            string.IsNullOrWhiteSpace(request.Name)
                ? DefaultProfileName(
                    connection.Provider,
                    request.TaskType)
                : request.Name.Trim(),
            request.ProcessingStrategy
                ?? DefaultProcessingStrategy(request.TaskType),
            request.MaxOutputTokens ?? 8_192,
            request.ConcurrencyLimit ?? connection.ConcurrencyLimit,
            ApiHelpers.StaffId(principal),
            bundle,
            now);
        db.AiTaskProfiles.Add(profile);
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.profile_created",
            profile.Id,
            new
            {
                profile.TaskType,
                profile.ModelId,
                profile.ProcessingStrategy,
                profile.PromptVersion,
                profile.SchemaVersion,
            });
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, profile.Revision);
        return Results.Created(
            $"/api/v1/admin/ai-task-profiles/{profile.Id}",
            ToProfileResponse(profile));
    }

    private static async Task<IResult> UpdateProfileAsync(
        string profileId,
        HttpContext context,
        [FromBody] SaveAiTaskProfileRequest request,
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await db.AiTaskProfiles
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.Id == profileId,
                cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(
                profile.AiConnection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        if (profile.Active
            || !ApiHelpers.TryReadExpectedRevision(
                context.Request,
                request.Revision,
                out var expectedRevision)
            || expectedRevision != profile.Revision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "AI_PROFILE_REVISION_MISMATCH",
                "プロファイルを更新できません",
                "有効なプロファイルは変更できません。複製するか、最新状態を読み込んでください。");
        }

        if ((request.ModelId is { } requestedModel
                && requestedModel.Trim() != profile.AiConnection.ModelId)
            || !IsProcessingStrategyValid(
                profile.TaskType,
                request.ProcessingStrategy))
        {
            return Results.UnprocessableEntity();
        }

        profile.Name = string.IsNullOrWhiteSpace(request.Name)
            ? profile.Name
            : request.Name.Trim();
        profile.ProcessingStrategy = request.ProcessingStrategy
            ?? profile.ProcessingStrategy;
        profile.MaxOutputTokens = Math.Clamp(
            request.MaxOutputTokens ?? profile.MaxOutputTokens,
            64,
            65_536);
        profile.ConcurrencyLimit = Math.Clamp(
            request.ConcurrencyLimit ?? profile.ConcurrencyLimit,
            1,
            16);
        profile.ApprovalState = "draft";
        profile.AccuracyEvaluationId = null;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, profile.Revision);
        return Results.Ok(ToProfileResponse(profile, promptCatalog));
    }

    private static async Task<IResult> ListEvaluationRecordsAsync(
        string profileId,
        HttpContext context,
        OokiGraderDbContext db,
        string? cursor,
        int? pageSize,
        [FromServices] ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        if (!await db.AiTaskProfiles
                .AsNoTracking()
                .AnyAsync(item => item.Id == profileId, cancellationToken))
        {
            return Results.NotFound();
        }

        var take = Math.Clamp(pageSize ?? 50, 1, 200);
        var binding = CursorPagination.Bind(
            ("profileId", profileId),
            ("sort", "-createdAt,-id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                EvaluationListRoute,
                binding,
                out EvaluationCursor position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrWhiteSpace(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var query = db.AiEvaluationRecords
            .AsNoTracking()
            .Where(item => item.AiTaskProfileId == profileId);
        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(item =>
                item.CreatedAt < position.CreatedAt
                || (item.CreatedAt == position.CreatedAt
                    && string.Compare(
                        item.Id,
                        position.Id,
                        StringComparison.Ordinal) < 0));
        }

        var records = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = records.Count > take;
        if (hasMore)
        {
            records.RemoveAt(take);
        }

        var nextCursor = records.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                EvaluationListRoute,
                binding,
                hasMore,
                new EvaluationCursor(
                    records[^1].CreatedAt,
                    records[^1].Id));
        return Results.Ok(new
        {
            items = records.Select(ToEvaluationResponse).ToArray(),
            nextCursor,
            totalApproximate = total,
        });
    }

    private static async Task<IResult> CreateEvaluationRecordAsync(
        string profileId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveAiEvaluationRecordRequest request,
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await db.AiTaskProfiles
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.Id == profileId,
                cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(
                profile.AiConnection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        var bundle = promptCatalog.GetRequired(profile.TaskType);
        var datasetVersion = request.DatasetVersion?.Trim();
        var datasetSha256 = request.DatasetSha256?.Trim().ToLowerInvariant();
        var evidenceSha256 = request.EvidenceSha256?.Trim().ToLowerInvariant();
        if (profile.Active
            || profile.ConnectionRevision
                != profile.AiConnection.CredentialRevision
            || !IsImageTaskConnectionReady(profile.AiConnection)
            || !HasCurrentBatchCapability(profile)
            || !MatchesBundle(profile, bundle)
            || string.IsNullOrWhiteSpace(datasetVersion)
            || datasetVersion.Length > 200
            || !IsSha256(datasetSha256)
            || !IsSha256(evidenceSha256)
            || request.SampleCount is < 1 or > 1_000_000
            || request.AgreementBasisPoints is < 0 or > 10_000
            || request.LowerConfidenceBoundBasisPoints is < 0 or > 10_000
            || request.LowerConfidenceBoundBasisPoints
                > request.AgreementBasisPoints
            || request.CriticalFailureCount is < 0
            || request.CriticalFailureCount > request.SampleCount
            || !request.TeacherReviewOnlyAcknowledged)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_EVALUATION_INVALID",
                "評価記録を保存できません",
                "現行プロファイルと一致する再現可能な評価証跡、および先生確認必須の同意が必要です。");
        }

        var existing = await db.AiEvaluationRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.AiTaskProfileId == profile.Id
                    && item.TaskProfileRevision == profile.Revision
                    && item.EvidenceSha256 == evidenceSha256,
                cancellationToken);
        if (existing is not null)
        {
            return Results.Ok(ToEvaluationResponse(existing));
        }

        var now = timeProvider.GetUtcNow();
        var record = new AiEvaluationRecordEntity
        {
            Id = UlidId.New(now),
            AiTaskProfileId = profile.Id,
            TaskProfileRevision = profile.Revision,
            Provider = profile.AiConnection.Provider,
            ModelId = profile.ModelId,
            ConnectionRevision = profile.ConnectionRevision,
            TaskType = profile.TaskType,
            ProcessingStrategy = profile.ProcessingStrategy,
            PromptVersion = profile.PromptVersion,
            SchemaVersion = profile.SchemaVersion,
            PromptContentHash = profile.PromptContentHash,
            DatasetVersion = datasetVersion,
            DatasetSha256 = datasetSha256!,
            EvidenceSha256 = evidenceSha256!,
            SampleCount = request.SampleCount,
            AgreementBasisPoints = request.AgreementBasisPoints,
            LowerConfidenceBoundBasisPoints =
                request.LowerConfidenceBoundBasisPoints,
            CriticalFailureCount = request.CriticalFailureCount,
            TeacherReviewOnly = true,
            SignedOffByStaffUserId = ApiHelpers.StaffId(principal),
            CreatedAt = now,
        };
        db.AiEvaluationRecords.Add(record);
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.evaluation_attested",
            record.Id,
            new
            {
                profileId = profile.Id,
                profileRevision = profile.Revision,
                record.DatasetVersion,
                record.DatasetSha256,
                record.EvidenceSha256,
                record.SampleCount,
                record.AgreementBasisPoints,
                record.LowerConfidenceBoundBasisPoints,
                record.CriticalFailureCount,
                teacherReviewOnly = true,
            });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created(
            $"/api/v1/admin/ai-task-profiles/{profile.Id}/evaluations",
            ToEvaluationResponse(record));
    }

    private sealed record EvaluationCursor(
        DateTimeOffset CreatedAt,
        string Id);

    private static async Task<IResult> ValidateProfileAsync(
        string profileId,
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] ValidateAiTaskProfileRequest request,
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await db.AiTaskProfiles
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(
                profile.AiConnection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        var bundle = promptCatalog.GetRequired(profile.TaskType);
        var evaluationId = request.EvaluationId?.Trim();
        var evaluation = string.IsNullOrWhiteSpace(evaluationId)
            ? null
            : await db.AiEvaluationRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == evaluationId
                        && item.AiTaskProfileId == profile.Id,
                    cancellationToken);
        if (profile.Active
            || profile.ConnectionRevision
                != profile.AiConnection.CredentialRevision
            || !IsImageTaskConnectionReady(profile.AiConnection)
            || !HasCurrentBatchCapability(profile)
            || !MatchesBundle(profile, bundle)
            || evaluation is null
            || evaluation.TaskProfileRevision != profile.Revision
            || !EvaluationMatchesProfile(evaluation, profile)
            || evaluation.CriticalFailureCount != 0
            || !evaluation.TeacherReviewOnly
            || !request.TeacherReviewOnlyAcknowledged)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_PROFILE_VALIDATION_REQUIRED",
                "プロファイルを承認できません",
                "接続確認、評価記録、および先生による確認運用への同意が必要です。");
        }

        var now = timeProvider.GetUtcNow();
        profile.AccuracyEvaluationId = evaluationId;
        profile.ApprovalState = "pilot_approved";
        profile.UpdatedAt = now;
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.profile_validated",
            profile.Id,
            new
            {
                profile.TaskType,
                profile.AccuracyEvaluationId,
                teacherReviewOnly = true,
            });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToProfileResponse(profile, promptCatalog));
    }

    private static async Task<IResult> ActivateProfileAsync(
        string profileId,
        HttpContext context,
        ClaimsPrincipal principal,
        OokiGraderDbContext db,
        IAiPromptBundleCatalog promptCatalog,
        [FromServices] IAiProviderFeaturePolicy providerFeaturePolicy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await db.AiTaskProfiles
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(item => item.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return Results.NotFound();
        }

        if (!providerFeaturePolicy.IsEnabled(
                profile.AiConnection.Provider))
        {
            return ProviderFeatureDisabled(context);
        }

        var bundle = promptCatalog.GetRequired(profile.TaskType);
        var evaluation = string.IsNullOrWhiteSpace(
            profile.AccuracyEvaluationId)
            ? null
            : await db.AiEvaluationRecords
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == profile.AccuracyEvaluationId
                        && item.AiTaskProfileId == profile.Id,
                    cancellationToken);
        if (profile.ApprovalState is not ("pilot_approved" or "production_approved")
            || profile.ConnectionRevision
                != profile.AiConnection.CredentialRevision
            || !IsImageTaskConnectionReady(profile.AiConnection)
            || !HasCurrentBatchCapability(profile)
            || !MatchesBundle(profile, bundle)
            || evaluation is null
            || !EvaluationMatchesProfile(evaluation, profile)
            || evaluation.CriticalFailureCount != 0
            || !evaluation.TeacherReviewOnly)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_PROFILE_NOT_APPROVED",
                "プロファイルを有効にできません",
                "能力確認と精度評価を完了してから有効にしてください。");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(
            cancellationToken);
        var currentProfiles = await db.AiTaskProfiles
            .Where(item => item.TaskType == profile.TaskType
                && item.Active
                && item.Id != profile.Id)
            .ToListAsync(cancellationToken);
        foreach (var current in currentProfiles)
        {
            current.Active = false;
            current.UpdatedAt = now;
        }

        // SQLite enforces the single-active-profile invariant immediately for
        // each UPDATE. Persist deactivation before activation so EF's statement
        // ordering cannot momentarily violate the unique filtered index. The
        // surrounding transaction keeps the switch atomic if activation fails.
        if (currentProfiles.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        profile.Active = true;
        profile.ActivatedAt = now;
        profile.ActivatedByStaffUserId = ApiHelpers.StaffId(principal);
        profile.UpdatedAt = now;
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.profile_activated",
            profile.Id,
            new
            {
                profile.TaskType,
                profile.ModelId,
                profile.PromptVersion,
                profile.SchemaVersion,
            });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(ToProfileResponse(profile, promptCatalog));
    }

    private static async Task<IResult> GetBudgetAsync(
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var policy = await db.AiBudgetPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == "default", cancellationToken);
        return Results.Ok(policy is null ? DefaultBudgetResponse() : ToBudgetResponse(policy));
    }

    private static async Task<IResult> SetBudgetAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SaveAiBudgetRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.DailyWarningUsdMicros < 0
            || request.DailyHardUsdMicros < request.DailyWarningUsdMicros
            || request.MonthlyWarningUsdMicros < 0
            || request.MonthlyHardUsdMicros < request.MonthlyWarningUsdMicros
            || request.UsdToJpyMicros <= 0)
        {
            return Results.UnprocessableEntity();
        }

        var policy = await db.AiBudgetPolicies.SingleOrDefaultAsync(
            item => item.Id == "default",
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (policy is null)
        {
            policy = new AiBudgetPolicyEntity
            {
                Id = "default",
                CreatedAt = now,
            };
            db.AiBudgetPolicies.Add(policy);
        }
        else if (!ApiHelpers.TryReadExpectedRevision(
                     context.Request,
                     request.Revision,
                     out var expectedRevision)
                 || expectedRevision != policy.Revision)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status412PreconditionFailed,
                "REVISION_MISMATCH",
                "予算設定が更新されています",
                "最新の設定を読み込み直してください。");
        }

        policy.DailyWarningUsdMicros = request.DailyWarningUsdMicros;
        policy.DailyHardUsdMicros = request.DailyHardUsdMicros;
        policy.MonthlyWarningUsdMicros = request.MonthlyWarningUsdMicros;
        policy.MonthlyHardUsdMicros = request.MonthlyHardUsdMicros;
        policy.UsdToJpyMicros = request.UsdToJpyMicros;
        policy.Active = request.Active;
        policy.UpdatedAt = now;
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.budget_updated",
            policy.Id,
            new
            {
                policy.DailyWarningUsdMicros,
                policy.DailyHardUsdMicros,
                policy.MonthlyWarningUsdMicros,
                policy.MonthlyHardUsdMicros,
                policy.Active,
            });
        await db.SaveChangesAsync(cancellationToken);
        ApiHelpers.SetRevisionEtag(context.Response, policy.Revision);
        return Results.Ok(ToBudgetResponse(policy));
    }

    private static async Task<IResult> GetUsageAsync(
        DateOnly? from,
        DateOnly? to,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var fromInstant = from?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            ?? DateTime.UtcNow.AddDays(-30);
        var toInstant = (to?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            ?? DateTime.UtcNow.AddDays(1);
        var query = db.AiUsage
            .AsNoTracking()
            .Where(item => item.MeasuredAt >= fromInstant && item.MeasuredAt < toInstant);
        var totalUsdMicros = await query.SumAsync(
            item => (long?)item.EstimatedUsdMicros,
            cancellationToken) ?? 0;
        var totalJpyMicros = await query.SumAsync(
            item => (long?)item.EstimatedJpyMicros,
            cancellationToken) ?? 0;
        var requestCount = await query.CountAsync(cancellationToken);
        var byModel = await query
            .GroupBy(item => new
            {
                item.RequestedProvider,
                item.RequestedModel,
            })
            .Select(group => new
            {
                provider = group.Key.RequestedProvider,
                model = group.Key.RequestedModel,
                requestCount = group.Count(),
                estimatedUsdMicros = group.Sum(item => item.EstimatedUsdMicros),
                estimatedJpyMicros = group.Sum(item => item.EstimatedJpyMicros),
                totalTokens = group.Sum(item => item.TotalTokens ?? 0),
            })
            .ToArrayAsync(cancellationToken);
        return Results.Ok(new
        {
            from = DateOnly.FromDateTime(fromInstant),
            to = DateOnly.FromDateTime(toInstant.AddDays(-1)),
            requestCount,
            estimatedUsdMicros = totalUsdMicros,
            estimatedJpyMicros = totalJpyMicros,
            byModel,
        });
    }

    private static async Task<IResult> GetMetricsAsync(
        HttpContext context,
        int? days,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var effectiveDays = days ?? DefaultMetricsDays;
        if (effectiveDays is < 1 or > MaximumMetricsDays)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_METRICS_WINDOW_INVALID",
                "AIメトリクスの期間を指定できません",
                $"集計期間は1日から{MaximumMetricsDays}日までです。");
        }

        var to = timeProvider.GetUtcNow();
        var from = to.AddDays(-effectiveDays);
        var requests = db.AiRequests
            .AsNoTracking()
            .Where(request =>
                request.CreatedAt >= from
                && request.CreatedAt < to);

        var requestSummaries = await requests
            .GroupBy(request => new
            {
                ProfileId = request.AiTaskProfileId,
                ProfileRevision = request.TaskProfileRevision,
                request.AiTaskProfile.Name,
                request.AiTaskProfile.TaskType,
                request.AiTaskProfile.ModelId,
                request.AiTaskProfile.Active,
            })
            .Select(group => new
            {
                group.Key.ProfileId,
                group.Key.ProfileRevision,
                ProfileName = group.Key.Name,
                group.Key.TaskType,
                group.Key.ModelId,
                ProfileActive = group.Key.Active,
                RequestCount = group.Count(),
                SuccessCount = group.Count(request =>
                    request.State == "succeeded"),
                FailureCount = group.Count(request =>
                    request.State == "failed"
                    || request.State == "invalid_output"
                    || request.State == "safety_blocked"),
                AmbiguousCount = group.Count(request =>
                    request.PossibleDuplicate
                    || request.ErrorCode == "ai_dispatch_outcome_unknown"),
                DispatchAttemptCount = group.Sum(request =>
                    (long)request.DispatchAttempt),
                RetriedRequestCount = group.Count(request =>
                    request.DispatchAttempt > 1),
                RetryAttemptCount = group.Sum(request =>
                    request.DispatchAttempt > 1
                        ? (long)request.DispatchAttempt - 1L
                        : 0L),
                RateLimitedCount = group.Count(request =>
                    request.ErrorCode == "gemini_rate_limited"
                    || request.ErrorCode == "openrouter_rate_limited"),
                Provider5XxCount = group.Count(request =>
                    request.ErrorCode == "gemini_provider_unavailable"
                    || request.ErrorCode
                        == "openrouter_provider_unavailable"),
                SchemaFailureCount = group.Count(request =>
                    request.State == "invalid_output"),
            })
            .OrderBy(item => item.TaskType)
            .ThenBy(item => item.ProfileName)
            .ThenBy(item => item.ProfileId)
            .ThenBy(item => item.ProfileRevision)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var stateCounts = await requests
            .GroupBy(request => request.State)
            .Select(group => new
            {
                state = group.Key,
                count = group.Count(),
            })
            .OrderBy(item => item.state)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var usageSummaries = await (
                from usage in db.AiUsage.AsNoTracking()
                join request in requests
                    on usage.AiRequestId equals request.Id
                group usage by new
                {
                    ProfileId = request.AiTaskProfileId,
                    ProfileRevision = request.TaskProfileRevision,
                }
                into grouped
                select new
                {
                    grouped.Key.ProfileId,
                    grouped.Key.ProfileRevision,
                    UsageRecordCount = grouped.Count(),
                    InputTokens = grouped.Sum(item => item.InputTokens ?? 0),
                    CachedTokens = grouped.Sum(item => item.CachedTokens ?? 0),
                    OutputTokens = grouped.Sum(item => item.OutputTokens ?? 0),
                    ThinkingTokens = grouped.Sum(item =>
                        item.ThinkingTokens ?? 0),
                    TotalTokens = grouped.Sum(item => item.TotalTokens ?? 0),
                    EstimatedUsdMicros = grouped.Sum(item =>
                        item.EstimatedUsdMicros),
                    EstimatedJpyMicros = grouped.Sum(item =>
                        item.EstimatedJpyMicros),
                })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var timingRows = await requests
            .Where(request => request.DispatchedAt != null)
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .Take(MaximumLatencySamples + 1)
            .Select(request => new
            {
                ProfileId = request.AiTaskProfileId,
                ProfileRevision = request.TaskProfileRevision,
                request.CreatedAt,
                request.DispatchedAt,
                request.CompletedAt,
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var timingSamplesTruncated =
            timingRows.Length > MaximumLatencySamples;
        if (timingSamplesTruncated)
        {
            timingRows = timingRows[..MaximumLatencySamples];
        }

        var queueWaitSamples = timingRows
            .Select(row => DurationMilliseconds(
                row.CreatedAt,
                row.DispatchedAt!.Value))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var providerLatencySamples = timingRows
            .Where(row => row.CompletedAt is not null)
            .Select(row => DurationMilliseconds(
                row.DispatchedAt!.Value,
                row.CompletedAt!.Value))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        var timingByProfile = timingRows
            .GroupBy(
                row => (row.ProfileId, row.ProfileRevision),
                row => row)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    QueueWait = LatencySummary(group
                        .Select(row => DurationMilliseconds(
                            row.CreatedAt,
                            row.DispatchedAt!.Value))
                        .Where(value => value is not null)
                        .Select(value => value!.Value)),
                    ProviderLatency = LatencySummary(group
                        .Where(row => row.CompletedAt is not null)
                        .Select(row => DurationMilliseconds(
                            row.DispatchedAt!.Value,
                            row.CompletedAt!.Value))
                        .Where(value => value is not null)
                        .Select(value => value!.Value)),
                });

        var correctionSummaries = await (
                from request in requests
                join profile in db.AiTaskProfiles.AsNoTracking()
                    on request.AiTaskProfileId equals profile.Id
                join run in db.GradingRuns.AsNoTracking()
                    on request.EntityId equals run.SubmissionId
                join result in db.QuestionResults.AsNoTracking()
                    on run.Id equals result.GradingRunId
                join initialRevision in db.ResultRevisions.AsNoTracking()
                    on new
                    {
                        QuestionResultId = result.Id,
                        RevisionNumber = 1,
                    }
                    equals new
                    {
                        initialRevision.QuestionResultId,
                        initialRevision.RevisionNumber,
                    }
                join currentRevision in db.ResultRevisions.AsNoTracking()
                    on result.CurrentRevisionId equals currentRevision.Id
                where request.Purpose == AiTaskTypes.InitialGrading
                    && request.State == "succeeded"
                    && request.CompletedAt != null
                    && (DateTimeOffset?)run.CreatedAt
                        == request.CompletedAt
                    && result.Method == "ai_pilot"
                    && currentRevision.Source == "teacher_override"
                    && profile.Active
                group new
                {
                    Initial = initialRevision,
                    Current = currentRevision,
                }
                by new
                {
                    ProfileId = request.AiTaskProfileId,
                    ProfileRevision = request.TaskProfileRevision,
                }
                into grouped
                select new
                {
                    grouped.Key.ProfileId,
                    grouped.Key.ProfileRevision,
                    ReviewedQuestionCount = grouped.Count(),
                    CorrectedQuestionCount = grouped.Count(item =>
                        item.Initial.AwardedPointsMilli
                            != item.Current.AwardedPointsMilli
                        || item.Initial.Outcome != item.Current.Outcome
                        || item.Initial.AnswerTextCorrection
                            != item.Current.AnswerTextCorrection),
                })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var usageByProfile = usageSummaries.ToDictionary(
            item => (item.ProfileId, item.ProfileRevision));
        var correctionsByProfile = correctionSummaries.ToDictionary(
            item => (item.ProfileId, item.ProfileRevision));
        var byProfile = requestSummaries.Select(summary =>
        {
            var key = (summary.ProfileId, summary.ProfileRevision);
            usageByProfile.TryGetValue(key, out var usage);
            correctionsByProfile.TryGetValue(key, out var correction);
            timingByProfile.TryGetValue(key, out var timing);
            var reviewed = correction?.ReviewedQuestionCount ?? 0;
            var corrected = correction?.CorrectedQuestionCount ?? 0;
            return new
            {
                profileId = summary.ProfileId,
                profileRevision = summary.ProfileRevision,
                profileName = summary.ProfileName,
                summary.TaskType,
                summary.ModelId,
                profileActive = summary.ProfileActive,
                summary.RequestCount,
                summary.SuccessCount,
                summary.FailureCount,
                summary.AmbiguousCount,
                summary.DispatchAttemptCount,
                summary.RetriedRequestCount,
                summary.RetryAttemptCount,
                errors = new
                {
                    rateLimited429 = summary.RateLimitedCount,
                    provider5Xx = summary.Provider5XxCount,
                    schemaOrOutputValidation =
                        summary.SchemaFailureCount,
                },
                tokens = new
                {
                    usageRecordCount = usage?.UsageRecordCount ?? 0,
                    input = usage?.InputTokens ?? 0,
                    cached = usage?.CachedTokens ?? 0,
                    output = usage?.OutputTokens ?? 0,
                    thinking = usage?.ThinkingTokens ?? 0,
                    total = usage?.TotalTokens ?? 0,
                },
                cost = new
                {
                    estimatedUsdMicros =
                        usage?.EstimatedUsdMicros ?? 0L,
                    estimatedJpyMicros =
                        usage?.EstimatedJpyMicros ?? 0L,
                },
                queueWait = timing?.QueueWait
                    ?? EmptyLatencySummary(),
                providerLatency = timing?.ProviderLatency
                    ?? EmptyLatencySummary(),
                teacherCorrection = new
                {
                    available = summary.ProfileActive
                        && summary.TaskType
                            == AiTaskTypes.InitialGrading
                        && reviewed > 0,
                    reviewedQuestionCount = reviewed,
                    correctedQuestionCount = corrected,
                    rateBasisPoints = reviewed == 0
                        ? (int?)null
                        : checked((int)Math.Round(
                            corrected * 10_000d / reviewed)),
                },
            };
        }).ToArray();

        var totalReviewed = correctionSummaries.Sum(item =>
            item.ReviewedQuestionCount);
        var totalCorrected = correctionSummaries.Sum(item =>
            item.CorrectedQuestionCount);
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.Ok(new
        {
            window = new
            {
                from,
                to,
                days = effectiveDays,
                maximumDays = MaximumMetricsDays,
            },
            totals = new
            {
                requestCount = requestSummaries.Sum(item =>
                    item.RequestCount),
                successCount = requestSummaries.Sum(item =>
                    item.SuccessCount),
                failureCount = requestSummaries.Sum(item =>
                    item.FailureCount),
                ambiguousCount = requestSummaries.Sum(item =>
                    item.AmbiguousCount),
                dispatchAttemptCount = requestSummaries.Sum(item =>
                    item.DispatchAttemptCount),
                retriedRequestCount = requestSummaries.Sum(item =>
                    item.RetriedRequestCount),
                retryAttemptCount = requestSummaries.Sum(item =>
                    item.RetryAttemptCount),
                errors = new
                {
                    rateLimited429 = requestSummaries.Sum(item =>
                        item.RateLimitedCount),
                    provider5Xx = requestSummaries.Sum(item =>
                        item.Provider5XxCount),
                    schemaOrOutputValidation = requestSummaries.Sum(item =>
                        item.SchemaFailureCount),
                },
                tokens = new
                {
                    usageRecordCount = usageSummaries.Sum(item =>
                        item.UsageRecordCount),
                    input = usageSummaries.Sum(item => item.InputTokens),
                    cached = usageSummaries.Sum(item => item.CachedTokens),
                    output = usageSummaries.Sum(item => item.OutputTokens),
                    thinking = usageSummaries.Sum(item =>
                        item.ThinkingTokens),
                    total = usageSummaries.Sum(item => item.TotalTokens),
                },
                cost = new
                {
                    estimatedUsdMicros = usageSummaries.Sum(item =>
                        item.EstimatedUsdMicros),
                    estimatedJpyMicros = usageSummaries.Sum(item =>
                        item.EstimatedJpyMicros),
                },
                queueWait = LatencySummary(queueWaitSamples),
                providerLatency =
                    LatencySummary(providerLatencySamples),
                teacherCorrection = new
                {
                    available = totalReviewed > 0,
                    reviewedQuestionCount = totalReviewed,
                    correctedQuestionCount = totalCorrected,
                    rateBasisPoints = totalReviewed == 0
                        ? (int?)null
                        : checked((int)Math.Round(
                            totalCorrected * 10_000d / totalReviewed)),
                },
            },
            stateCounts,
            byProfile,
            sampling = new
            {
                latencySampleLimit = MaximumLatencySamples,
                latencySamplesTruncated = timingSamplesTruncated,
            },
            derivation = new
            {
                errorCategories =
                    "latest_recorded_safe_error_or_output_state",
                queueWait =
                    "request_created_at_to_latest_dispatched_at",
                providerLatency =
                    "latest_dispatched_at_to_completed_at",
                teacherCorrection =
                    "active_initial_grading_profile_exact_request_run_match",
            },
            privacy = new
            {
                aggregateOnly = true,
                includesPrompts = false,
                includesResponses = false,
                includesStudentData = false,
            },
        });
    }

    private static async Task<IResult> ListPricingSnapshotsAsync(
        HttpContext context,
        OokiGraderDbContext db,
        string? cursor,
        int? pageSize,
        [FromServices] ProtectedCursorCodec cursorCodec,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var query = db.PricingSnapshots
            .AsNoTracking();
        var binding = CursorPagination.Bind(
            ("sort", "-effectiveAt,-capturedAt,-id"));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                PricingSnapshotListRoute,
                binding,
                out PricingSnapshotCursor position,
                out var cursorError))
        {
            return cursorError!;
        }

        if (position is not null
            && (string.IsNullOrWhiteSpace(position.Id)
                || position.Id.Length > 128))
        {
            return CursorPagination.Invalid(context);
        }

        var total = await query.CountAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(item =>
                item.EffectiveAt < position.EffectiveAt
                || (item.EffectiveAt == position.EffectiveAt
                    && (item.CapturedAt < position.CapturedAt
                        || (item.CapturedAt == position.CapturedAt
                            && string.Compare(
                                item.Id,
                                position.Id,
                                StringComparison.Ordinal) < 0))));
        }

        var snapshots = await query
            .OrderByDescending(item => item.EffectiveAt)
            .ThenByDescending(item => item.CapturedAt)
            .ThenByDescending(item => item.Id)
            .Take(take + 1)
            .Select(item => new
            {
                id = item.Id,
                provider = item.Provider,
                modelId = item.ModelId,
                inputUsdMicrosPerMillionTokens =
                    item.InputUsdMicrosPerMillionTokens,
                outputUsdMicrosPerMillionTokens =
                    item.OutputUsdMicrosPerMillionTokens,
                thinkingUsdMicrosPerMillionTokens =
                    item.ThinkingUsdMicrosPerMillionTokens,
                sourceUrl = item.SourceUrl,
                effectiveAt = item.EffectiveAt,
                capturedAt = item.CapturedAt,
            })
            .ToListAsync(cancellationToken);
        var hasMore = snapshots.Count > take;
        if (hasMore)
        {
            snapshots.RemoveAt(take);
        }

        var nextCursor = snapshots.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                PricingSnapshotListRoute,
                binding,
                hasMore,
                new PricingSnapshotCursor(
                    snapshots[^1].effectiveAt,
                    snapshots[^1].capturedAt,
                    snapshots[^1].id));
        return Results.Ok(new
        {
            items = snapshots,
            nextCursor,
            totalApproximate = total,
        });
    }

    private sealed record PricingSnapshotCursor(
        DateTimeOffset EffectiveAt,
        DateTimeOffset CapturedAt,
        string Id);

    private static async Task<IResult> CreatePricingSnapshotAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        [FromBody] SavePricingSnapshotRequest request,
        OokiGraderDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var provider = AiProviderRuntime.NormalizeProvider(request.Provider);
        var modelId = string.IsNullOrWhiteSpace(request.ModelId)
            && AiProviderCatalog.IsSupportedProvider(provider)
                ? AiProviderRuntime.DefaultModel(provider)
                : request.ModelId?.Trim() ?? string.Empty;
        var expectedSourceHost = provider switch
        {
            AiProviders.GeminiDirect => "ai.google.dev",
            AiProviders.OpenRouter => "openrouter.ai",
            _ => string.Empty,
        };
        if (!AiProviderCatalog.IsSupportedProvider(provider)
            || !AiProviderCatalog.IsModelIdValid(provider, modelId)
            || request.InputUsdMicrosPerMillionTokens < 0
            || request.OutputUsdMicrosPerMillionTokens < 0
            || request.ThinkingUsdMicrosPerMillionTokens < 0
            || (request.InputUsdMicrosPerMillionTokens == 0
                && request.OutputUsdMicrosPerMillionTokens == 0
                && request.ThinkingUsdMicrosPerMillionTokens == 0)
            || !Uri.TryCreate(
                request.SourceUrl,
                UriKind.Absolute,
                out var sourceUri)
            || sourceUri.Scheme != Uri.UriSchemeHttps
            || !sourceUri.IsDefaultPort
            || sourceUri.UserInfo.Length != 0
            || !string.Equals(
                sourceUri.Host,
                expectedSourceHost,
                StringComparison.OrdinalIgnoreCase))
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_PRICING_SNAPSHOT_INVALID",
                "価格情報を保存できません",
                "選択した AI 接続、公式価格ページ、非負のトークン単価を確認してください。");
        }

        var connectionExists = await db.AiConnections
            .AsNoTracking()
            .AnyAsync(
                connection =>
                    connection.Provider == provider
                    && connection.ModelId == modelId
                    && connection.State != "disabled",
                cancellationToken);
        if (!connectionExists)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_PRICING_CONNECTION_NOT_FOUND",
                "価格情報を保存できません",
                "価格を登録するプロバイダーとモデルの AI 接続を先に保存してください。");
        }

        var now = timeProvider.GetUtcNow();
        var effectiveAt = request.EffectiveAt ?? now;
        if (effectiveAt > now.AddDays(1)
            || effectiveAt < new DateTimeOffset(
                2020,
                1,
                1,
                0,
                0,
                0,
                TimeSpan.Zero))
        {
            return Results.UnprocessableEntity();
        }

        var snapshot = new PricingSnapshotEntity
        {
            Id = UlidId.New(now),
            Provider = provider,
            ModelId = modelId,
            InputUsdMicrosPerMillionTokens =
                request.InputUsdMicrosPerMillionTokens,
            OutputUsdMicrosPerMillionTokens =
                request.OutputUsdMicrosPerMillionTokens,
            ThinkingUsdMicrosPerMillionTokens =
                request.ThinkingUsdMicrosPerMillionTokens,
            SourceUrl = sourceUri.AbsoluteUri,
            EffectiveAt = effectiveAt,
            CapturedAt = now,
        };
        db.PricingSnapshots.Add(snapshot);
        AddAudit(
            db,
            context,
            principal,
            now,
            "ai.pricing_snapshot_created",
            snapshot.Id,
            new
            {
                snapshot.Provider,
                snapshot.ModelId,
                snapshot.InputUsdMicrosPerMillionTokens,
                snapshot.OutputUsdMicrosPerMillionTokens,
                snapshot.ThinkingUsdMicrosPerMillionTokens,
                snapshot.SourceUrl,
                snapshot.EffectiveAt,
            });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created(
            $"/api/v1/admin/pricing-snapshots/{snapshot.Id}",
            new
            {
                snapshot.Id,
                snapshot.Provider,
                snapshot.ModelId,
                snapshot.InputUsdMicrosPerMillionTokens,
                snapshot.OutputUsdMicrosPerMillionTokens,
                snapshot.ThinkingUsdMicrosPerMillionTokens,
                snapshot.SourceUrl,
                snapshot.EffectiveAt,
                snapshot.CapturedAt,
            });
    }

    internal static async Task<int> EnsureCurrentProfilesAsync(
        OokiGraderDbContext db,
        IAiPromptBundleCatalog catalog,
        TimeProvider timeProvider,
        IAiProviderFeaturePolicy providerFeaturePolicy,
        CancellationToken cancellationToken = default)
    {
        var connections = await db.AiConnections
            .Where(connection =>
                connection.State == "active"
                && connection.LastCapabilityProbeState == "passed")
            .ToArrayAsync(cancellationToken);
        var before = db.ChangeTracker
            .Entries<AiTaskProfileEntity>()
            .Count(entry => entry.State == EntityState.Added);
        var now = timeProvider.GetUtcNow();
        foreach (var connection in connections)
        {
            if (!providerFeaturePolicy.IsEnabled(connection.Provider)
                || !IsImageTaskConnectionReady(connection))
            {
                continue;
            }

            await EnsureDefaultProfilesAsync(
                db,
                connection,
                connection.CreatedByStaffUserId,
                catalog,
                now,
                cancellationToken);
        }

        var added = db.ChangeTracker
            .Entries<AiTaskProfileEntity>()
            .Count(entry => entry.State == EntityState.Added)
            - before;
        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return added;
    }

    private static async Task EnsureDefaultProfilesAsync(
        OokiGraderDbContext db,
        AiConnectionEntity connection,
        string staffId,
        IAiPromptBundleCatalog catalog,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var taskType in new[]
                 {
                     AiTaskTypes.TemplateExtraction,
                     AiTaskTypes.NameTranscription,
                     AiTaskTypes.InitialGrading,
                     AiTaskTypes.Adjudication,
                 })
        {
            var bundle = catalog.GetRequired(taskType);
            var processingStrategy = DefaultProcessingStrategy(taskType);
            if (await db.AiTaskProfiles.AnyAsync(
                    profile => profile.AiConnectionId == connection.Id
                        && profile.TaskType == taskType
                        && profile.ProcessingStrategy == processingStrategy
                        && profile.ConnectionRevision
                            == connection.CredentialRevision
                        && profile.PromptVersion == bundle.PromptVersion
                        && profile.SchemaVersion == bundle.SchemaVersion
                        && profile.PromptContentHash == bundle.ContentHash,
                    cancellationToken))
            {
                continue;
            }

            db.AiTaskProfiles.Add(BuildProfile(
                connection,
                taskType,
                DefaultProfileName(connection.Provider, taskType),
                processingStrategy,
                taskType == AiTaskTypes.TemplateExtraction ? 16_384 : 8_192,
                connection.ConcurrencyLimit,
                staffId,
                bundle,
                now));
        }
    }

    private static AiTaskProfileEntity BuildProfile(
        AiConnectionEntity connection,
        string taskType,
        string name,
        string processingStrategy,
        int maxOutputTokens,
        int concurrencyLimit,
        string staffId,
        AiPromptBundle bundle,
        DateTimeOffset now) =>
        new()
        {
            Id = UlidId.New(now),
            Name = name,
            TaskType = taskType,
            AiConnectionId = connection.Id,
            ConnectionRevision = connection.CredentialRevision,
            ModelId = connection.ModelId,
            ProcessingStrategy = processingStrategy,
            PromptVersion = bundle.PromptVersion,
            SchemaVersion = bundle.SchemaVersion,
            PromptContentHash = bundle.ContentHash,
            ThinkingLevel = taskType == AiTaskTypes.TemplateExtraction
                ? "medium"
                : "minimal",
            MediaResolution = "high",
            MaxOutputTokens = Math.Clamp(maxOutputTokens, 64, 65_536),
            ConcurrencyLimit = Math.Clamp(concurrencyLimit, 1, 16),
            ApprovalState = "capability_passed",
            Active = false,
            CreatedByStaffUserId = staffId,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static object ToConnectionResponse(
        AiConnectionEntity connection,
        AiCapabilityProbeEntity? latestProbe = null)
    {
        return new
        {
            connection.Id,
            connection.Provider,
            configured = !string.IsNullOrWhiteSpace(connection.SecretReference),
            connection.KeyFingerprint,
            connection.ModelId,
            connection.State,
            connection.TimeoutSeconds,
            connection.ConcurrencyLimit,
            lastCapabilityProbe = connection.LastCapabilityProbeState is null
                ? null
                : new
                {
                    state = connection.LastCapabilityProbeState,
                    checkedAt = connection.LastCapabilityProbeAt,
                    safeErrorCode = connection.LastCapabilityProbeErrorCode,
                    imageInput = latestProbe?.ImageInput,
                    structuredOutput = latestProbe?.StructuredOutput,
                },
            lastBatchCapabilityProbe =
                connection.LastBatchCapabilityProbeState is null
                    ? null
                    : new
                    {
                        state = connection.LastBatchCapabilityProbeState,
                        checkedAt = connection.LastBatchCapabilityProbeAt,
                        credentialRevision =
                            connection
                                .LastBatchCapabilityProbeCredentialRevision,
                        safeErrorCode =
                            connection.LastBatchCapabilityProbeErrorCode,
                    },
            connection.CreatedAt,
            connection.UpdatedAt,
            connection.Revision,
        };
    }

    private static object ToProfileResponse(
        AiTaskProfileEntity profile,
        IAiPromptBundleCatalog? promptCatalog = null)
    {
        var bundle = promptCatalog?.GetRequired(profile.TaskType);
        var stale = profile.AiConnection is not null
            && (profile.ConnectionRevision
                    != profile.AiConnection.CredentialRevision
                || !HasCurrentBatchCapability(profile)
                || (bundle is not null
                    && !MatchesBundle(profile, bundle)));
        return new
        {
            profile.Id,
            profile.Name,
            profile.TaskType,
            connectionId = profile.AiConnectionId,
            profile.ModelId,
            profile.ProcessingStrategy,
            profile.PromptVersion,
            profile.SchemaVersion,
            profile.ThinkingLevel,
            profile.MediaResolution,
            profile.MaxOutputTokens,
            profile.ConcurrencyLimit,
            profile.ApprovalState,
            profile.AccuracyEvaluationId,
            profile.Active,
            connectionRevision = profile.ConnectionRevision,
            connectionCredentialRevision = profile.AiConnection?.CredentialRevision,
            stale,
            profile.ActivatedAt,
            profile.CreatedAt,
            profile.UpdatedAt,
            profile.Revision,
        };
    }

    private static bool MatchesBundle(
        AiTaskProfileEntity profile,
        AiPromptBundle bundle) =>
        profile.PromptVersion == bundle.PromptVersion
        && profile.SchemaVersion == bundle.SchemaVersion
        && profile.PromptContentHash == bundle.ContentHash;

    private static bool HasCurrentBatchCapability(
        AiTaskProfileEntity profile) =>
        profile.ProcessingStrategy != "gemini_batch"
        || (profile.AiConnection.LastBatchCapabilityProbeState == "passed"
            && profile.AiConnection
                    .LastBatchCapabilityProbeCredentialRevision
                == profile.AiConnection.CredentialRevision);

    private static bool IsImageTaskConnectionReady(
        AiConnectionEntity connection) =>
        connection.State == "active"
        && connection.LastCapabilityProbeState == "passed"
        && AiProviderCatalog.IsConnectionShapeValid(
            connection.Provider,
            connection.EndpointProfile,
            connection.ModelId)
        && AiProviderCatalog.SupportsImageTasks(
            connection.Provider,
            connection.ModelId);

    private static bool EvaluationMatchesProfile(
        AiEvaluationRecordEntity evaluation,
        AiTaskProfileEntity profile) =>
        evaluation.AiTaskProfileId == profile.Id
        && evaluation.Provider == profile.AiConnection.Provider
        && evaluation.ModelId == profile.ModelId
        && evaluation.ConnectionRevision == profile.ConnectionRevision
        && evaluation.TaskType == profile.TaskType
        && evaluation.ProcessingStrategy == profile.ProcessingStrategy
        && evaluation.PromptVersion == profile.PromptVersion
        && evaluation.SchemaVersion == profile.SchemaVersion
        && evaluation.PromptContentHash == profile.PromptContentHash;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static object ToEvaluationResponse(
        AiEvaluationRecordEntity evaluation) => new
        {
            evaluation.Id,
            profileId = evaluation.AiTaskProfileId,
            profileRevision = evaluation.TaskProfileRevision,
            evaluation.Provider,
            evaluation.ModelId,
            credentialRevision = evaluation.ConnectionRevision,
            evaluation.TaskType,
            evaluation.ProcessingStrategy,
            evaluation.PromptVersion,
            evaluation.SchemaVersion,
            evaluation.PromptContentHash,
            evaluation.DatasetVersion,
            evaluation.DatasetSha256,
            evaluation.EvidenceSha256,
            evaluation.SampleCount,
            evaluation.AgreementBasisPoints,
            evaluation.LowerConfidenceBoundBasisPoints,
            evaluation.CriticalFailureCount,
            evaluation.TeacherReviewOnly,
            signedOffByStaffUserId = evaluation.SignedOffByStaffUserId,
            evaluation.CreatedAt,
            eligibleForPilotApproval =
                evaluation.TeacherReviewOnly
                && evaluation.CriticalFailureCount == 0,
            eligibleForAutomaticFinalization = false,
        };

    private static object ToBudgetResponse(AiBudgetPolicyEntity policy) => new
    {
        policy.Id,
        policy.DailyWarningUsdMicros,
        policy.DailyHardUsdMicros,
        policy.MonthlyWarningUsdMicros,
        policy.MonthlyHardUsdMicros,
        policy.UsdToJpyMicros,
        policy.Active,
        policy.UpdatedAt,
        policy.Revision,
    };

    private static object DefaultBudgetResponse() => new
    {
        id = "default",
        dailyWarningUsdMicros = 0L,
        dailyHardUsdMicros = 0L,
        monthlyWarningUsdMicros = 0L,
        monthlyHardUsdMicros = 0L,
        usdToJpyMicros = 150_000_000L,
        active = false,
        revision = 0L,
    };

    private static long? DurationMilliseconds(
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        var duration = completedAt - startedAt;
        if (duration < TimeSpan.Zero)
        {
            return null;
        }

        return checked((long)Math.Round(
            duration.TotalMilliseconds,
            MidpointRounding.AwayFromZero));
    }

    private static object LatencySummary(IEnumerable<long> values)
    {
        var samples = values.Order().ToArray();
        if (samples.Length == 0)
        {
            return EmptyLatencySummary();
        }

        var percentileIndex = Math.Clamp(
            checked((int)Math.Ceiling(samples.Length * 0.95d) - 1),
            0,
            samples.Length - 1);
        return new
        {
            sampleCount = samples.Length,
            averageMilliseconds = checked((long)Math.Round(
                samples.Average(value => (double)value),
                MidpointRounding.AwayFromZero)),
            p95Milliseconds = samples[percentileIndex],
        };
    }

    private static object EmptyLatencySummary() => new
    {
        sampleCount = 0,
        averageMilliseconds = (long?)null,
        p95Milliseconds = (long?)null,
    };

    private static object Page(Array items) => new
    {
        items,
        nextCursor = (string?)null,
        totalApproximate = items.Length,
    };

    private static IResult ProviderFeatureDisabled(HttpContext context) =>
        ApiHelpers.Problem(
            context,
            StatusCodes.Status409Conflict,
            "AI_PROVIDER_FEATURE_DISABLED",
            "AI プロバイダーが無効です",
            "機能設定でこのプロバイダーを有効にしてから、もう一度実行してください。");

    private static Func<HttpContext, IResult>? ValidateConnectionRequest(
        SaveAiConnectionRequest request)
    {
        if (!TryResolveConnectionSelection(request, out _, out _)
            || string.IsNullOrWhiteSpace(request.ApiKey)
            || request.ApiKey.Length is < 20 or > 512
            || request.ApiKey.Any(char.IsControl)
            || request.TimeoutSeconds is < 5 or > 300
            || request.ConcurrencyLimit is < 1 or > 16)
        {
            return context => ApiHelpers.Problem(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "AI_CONNECTION_INVALID",
                "AI接続を保存できません",
                "API キーと接続設定を確認してください。");
        }

        return null;
    }

    private static bool TryResolveConnectionSelection(
        SaveAiConnectionRequest request,
        out string provider,
        out string modelId)
    {
        provider = AiProviderRuntime.NormalizeProvider(request.Provider);
        modelId = string.IsNullOrWhiteSpace(request.ModelId)
            && AiProviderCatalog.IsSupportedProvider(provider)
                ? AiProviderRuntime.DefaultModel(provider)
                : request.ModelId?.Trim() ?? string.Empty;
        return AiProviderCatalog.IsSupportedProvider(provider)
            && AiProviderCatalog.IsModelIdValid(provider, modelId);
    }

    private static string Fingerprint(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            return $"sha256:{hash[..8]}…{hash[^4..]}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static AiConnectionSettings ToSettings(AiConnectionEntity connection) =>
        new(
            connection.Id,
            connection.Provider,
            AiProviderCatalog.GetBaseAddress(connection.Provider),
            connection.ModelId,
            TimeSpan.FromSeconds(connection.TimeoutSeconds));

    private static bool IsTaskType(string value) =>
        value is AiTaskTypes.TemplateExtraction
            or AiTaskTypes.NameTranscription
            or AiTaskTypes.InitialGrading
            or AiTaskTypes.Adjudication;

    internal static bool IsProcessingStrategyValid(
        string taskType,
        string? processingStrategy) =>
        processingStrategy is null
            or "expedite_standard"
            or "queued_standard";

    internal static string DefaultProcessingStrategy(string taskType) =>
        taskType == AiTaskTypes.InitialGrading
            ? "queued_standard"
            : "expedite_standard";

    private static string DefaultProfileName(
        string provider,
        string taskType) => taskType switch
        {
            AiTaskTypes.TemplateExtraction =>
                $"{AiProviderRuntime.DisplayName(provider)} ひな形抽出",
            AiTaskTypes.NameTranscription =>
                $"{AiProviderRuntime.DisplayName(provider)} 氏名読み取り",
            AiTaskTypes.InitialGrading =>
                $"{AiProviderRuntime.DisplayName(provider)} 初回採点",
            AiTaskTypes.Adjudication =>
                $"{AiProviderRuntime.DisplayName(provider)} 再確認",
            _ => throw new ArgumentOutOfRangeException(nameof(taskType)),
        };

    private static void AddAudit(
        OokiGraderDbContext db,
        HttpContext context,
        ClaimsPrincipal principal,
        DateTimeOffset now,
        string eventType,
        string objectId,
        object metadata)
    {
        db.AuditEvents.Add(new AuditEventEntity
        {
            Id = UlidId.New(now),
            OccurredAt = now,
            ActorStaffUserId = ApiHelpers.StaffId(principal),
            EventType = eventType,
            ObjectType = "ai_configuration",
            ObjectId = objectId,
            Outcome = "succeeded",
            CorrelationId = context.TraceIdentifier,
            SafeMetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata),
        });
    }

    private sealed record SaveAiConnectionRequest(
        string ApiKey,
        string? Provider,
        string? ModelId,
        int? TimeoutSeconds,
        int? ConcurrencyLimit,
        long? Revision);

    private sealed record SaveAiTaskProfileRequest(
        string ConnectionId,
        string TaskType,
        string? Name,
        string? ModelId,
        string? ProcessingStrategy,
        int? MaxOutputTokens,
        int? ConcurrencyLimit,
        long? Revision);

    private sealed record ValidateAiTaskProfileRequest(
        string EvaluationId,
        bool TeacherReviewOnlyAcknowledged);

    private sealed record SaveAiEvaluationRecordRequest(
        string DatasetVersion,
        string DatasetSha256,
        string EvidenceSha256,
        int SampleCount,
        int AgreementBasisPoints,
        int LowerConfidenceBoundBasisPoints,
        int CriticalFailureCount,
        bool TeacherReviewOnlyAcknowledged);

    private sealed record SaveAiBudgetRequest(
        long DailyWarningUsdMicros,
        long DailyHardUsdMicros,
        long MonthlyWarningUsdMicros,
        long MonthlyHardUsdMicros,
        long UsdToJpyMicros,
        bool Active,
        long? Revision);

    private sealed record SavePricingSnapshotRequest(
        string? Provider,
        string? ModelId,
        long InputUsdMicrosPerMillionTokens,
        long OutputUsdMicrosPerMillionTokens,
        long ThinkingUsdMicrosPerMillionTokens,
        string SourceUrl,
        DateTimeOffset? EffectiveAt);
}
