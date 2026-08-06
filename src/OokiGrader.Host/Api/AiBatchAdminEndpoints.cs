using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Security;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.Host.Api;

internal static class AiBatchAdminEndpoints
{
    private const string ListRoute = "/api/v1/admin/ai-batches";
    private static readonly Uri GeminiBaseAddress =
        new("https://generativelanguage.googleapis.com/");

    public static IEndpointRouteBuilder MapAiBatchAdminEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/admin")
            .WithTags("AI batch administration")
            .RequireAuthorization("administrator");
        group.MapGet("/ai-batches", ListAsync);
        group.MapGet("/ai-batches/{batchId}", GetAsync);
        group.MapPost("/ai-batches:flush", FlushAsync)
            .RequireIdempotency();
        group.MapPost("/ai-batches/{batchId}:reconcile", ReconcileAsync)
            .RequireIdempotency();
        group.MapPost("/ai-batches/{batchId}:cancel", CancelAsync)
            .RequireIdempotency();
        group.MapPost("/ai-batches/{batchId}:expedite", ExpediteAsync)
            .RequireIdempotency();
        return endpoints;
    }

    [SuppressMessage(
        "Globalization",
        "CA1309:Use ordinal string comparison",
        Justification =
            "EF Core translates this predicate to SQLite BINARY collation but cannot translate CompareOrdinal.")]
    private static async Task<IResult> ListAsync(
        HttpContext context,
        OokiGraderDbContext db,
        ProtectedCursorCodec cursorCodec,
        string? state,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(pageSize ?? 100, 1, 250);
        var normalizedState = CursorPagination.TrimToNull(state);
        if (normalizedState?.Length > 32)
        {
            return Results.BadRequest();
        }

        var query = db.AiBatches.AsNoTracking();
        if (normalizedState is not null)
        {
            query = query.Where(item => item.State == normalizedState);
        }

        var binding = CursorPagination.Bind(
            ("sort", "-createdAt,-id"),
            ("state", normalizedState));
        if (!CursorPagination.TryRead(
                context,
                cursorCodec,
                cursor,
                ListRoute,
                binding,
                out BatchCursor position,
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
        var stagedGroups = await db.AiBatchRequests
            .AsNoTracking()
            .Where(item =>
                item.State == "ready"
                && item.AiBatchId == null)
            .GroupBy(item => item.CompatibilityKey)
            .Select(group => new
            {
                compatibilityKey = group.Key,
                requestCount = group.Count(),
                oldestReadyAt = group.Min(item => item.CreatedAt),
                newestReadyAt = group.Max(item => item.CreatedAt),
            })
            .OrderBy(group => group.oldestReadyAt)
            .Take(250)
            .ToListAsync(cancellationToken);
        if (position is not null)
        {
            query = query.Where(item =>
                item.CreatedAt < position.CreatedAt
                || (item.CreatedAt == position.CreatedAt
                    && string.Compare(item.Id, position.Id) < 0));
        }

        var items = await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(take + 1)
            .Select(item => new
            {
                item.Id,
                item.Provider,
                item.ModelId,
                item.DisplayName,
                item.State,
                item.ProviderBatchName,
                item.RequestCount,
                item.SuccessfulRequestCount,
                item.FailedRequestCount,
                item.PendingRequestCount,
                item.PossibleDuplicate,
                item.CreateAttemptCount,
                item.ReconciliationAttemptCount,
                item.NextActionAt,
                item.LastPolledAt,
                item.RemoteCreatedAt,
                item.RemoteEndedAt,
                item.ErrorCode,
                item.CleanupState,
                item.CreatedAt,
                item.UpdatedAt,
                item.CompletedAt,
                item.Revision,
            })
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > take;
        if (hasMore)
        {
            items.RemoveAt(take);
        }

        var nextCursor = items.Count == 0
            ? null
            : CursorPagination.Next(
                cursorCodec,
                ListRoute,
                binding,
                hasMore,
                new BatchCursor(
                    items[^1].CreatedAt,
                    items[^1].Id));
        return Results.Ok(new
        {
            items,
            total,
            nextCursor,
            stagedGroups,
        });
    }

    private static async Task<IResult> GetAsync(
        string batchId,
        OokiGraderDbContext db,
        CancellationToken cancellationToken)
    {
        var batch = await db.AiBatches
            .AsNoTracking()
            .Where(item => item.Id == batchId)
            .Select(item => new
            {
                item.Id,
                item.Provider,
                item.ModelId,
                item.AiConnectionId,
                item.ConnectionRevision,
                item.AiTaskProfileId,
                item.TaskProfileRevision,
                item.CompatibilityKey,
                item.ManifestHash,
                item.DisplayName,
                item.State,
                item.SubmissionEpoch,
                item.CreateAttemptCount,
                item.CreateAttemptStartedAt,
                item.CreateAttemptCompletedAt,
                item.ProviderBatchName,
                item.ProviderInputFileName,
                item.ProviderOutputFileName,
                item.ProviderInputFileExpiresAt,
                item.InputJsonLinesSha256,
                item.InputJsonLinesBytes,
                item.RequestCount,
                item.SuccessfulRequestCount,
                item.FailedRequestCount,
                item.PendingRequestCount,
                item.PossibleDuplicate,
                item.ReconciliationAttemptCount,
                item.ReconciliationDeadlineAt,
                item.LastPolledAt,
                item.NextActionAt,
                item.RemoteCreatedAt,
                item.RemoteUpdatedAt,
                item.RemoteEndedAt,
                item.ErrorCode,
                item.SafeErrorDetail,
                item.CleanupState,
                item.CreatedAt,
                item.UpdatedAt,
                item.CompletedAt,
                item.Revision,
                requests = item.Requests
                    .OrderBy(request => request.Ordinal)
                    .Select(request => new
                    {
                        request.Id,
                        request.AiRequestId,
                        request.RequestKey,
                        request.Ordinal,
                        request.State,
                        request.ProviderResponseId,
                        request.ResponseHash,
                        request.ErrorCode,
                        request.CreatedAt,
                        request.UpdatedAt,
                        request.CompletedAt,
                        request.Revision,
                    }),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return batch is null ? Results.NotFound() : Results.Ok(batch);
    }

    private static async Task<IResult> ReconcileAsync(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        return await writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleOrDefaultAsync(
                item => item.Id == batchId,
                token);
            if (batch is null)
            {
                return Results.NotFound();
            }

            if (batch.State == "prepared"
                || batch.State == "uploading")
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_NOT_SUBMITTED",
                    "この一括処理はまだ送信されていません",
                    "送信前の一括処理にリモート照合は必要ありません。");
            }

            var now = timeProvider.GetUtcNow();
            var jobType = batch.ProviderBatchName is null
                ? AiBatchJobWorker.ReconcileJobType
                : AiBatchJobWorker.PollJobType;
            var deduplicationKey =
                $"ai-batch:{batch.Id}:admin-reconcile:{batch.Revision}";
            var existing = await db.BackgroundJobs.SingleOrDefaultAsync(
                item => item.DeduplicationKey == deduplicationKey,
                token);
            if (existing is not null)
            {
                return Results.Accepted(
                    $"/api/v1/admin/ai-batches/{batch.Id}",
                    new
                    {
                        batchId = batch.Id,
                        jobId = existing.Id,
                        state = existing.State,
                    });
            }

            var job = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = jobType,
                SchemaVersion = AiBatchJobWorker.JobSchemaVersion,
                DeduplicationKey = deduplicationKey,
                Priority = 100,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    batchId = batch.Id,
                }),
                State = "queued",
                AttemptCount = 0,
                MaxAttempts = 100,
                NextAttemptAt = now,
                CorrelationId = context.TraceIdentifier,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(job);
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                ActorStaffUserId = ApiHelpers.StaffId(principal),
                EventType = "ai.batch_reconciliation_requested",
                ObjectType = "aiBatch",
                ObjectId = batch.Id,
                Outcome = "accepted",
                CorrelationId = context.TraceIdentifier,
                SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                    context.Connection.RemoteIpAddress),
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    batch.State,
                    hasProviderBatchName =
                        batch.ProviderBatchName is not null,
                    jobId = job.Id,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return Results.Accepted(
                $"/api/v1/admin/ai-batches/{batch.Id}",
                new
                {
                    batchId = batch.Id,
                    jobId = job.Id,
                    state = "queued",
                });
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> FlushAsync(
        FlushBatchRequest request,
        HttpContext context,
        ClaimsPrincipal principal,
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (request.CompatibilityKey is not { Length: 64 }
            || request.CompatibilityKey.Any(character =>
                character is not (>= '0' and <= '9'
                    or >= 'a' and <= 'f')))
        {
            return Results.BadRequest();
        }

        return await writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var staged = await db.AiBatchRequests
                .Where(item =>
                    item.CompatibilityKey == request.CompatibilityKey
                    && item.State == "ready"
                    && item.AiBatchId == null)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(
                    token)
                .ConfigureAwait(false);
            if (staged is null)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_REQUEST_NOT_STAGED",
                    "このリクエストは送信待ちではありません",
                    "送信待ちの Gemini Batch リクエストだけを即時送信できます。");
            }

            var deduplicationKey =
                AiBatchRequestStager.PrepareDeduplicationKey(
                    staged.CompatibilityKey,
                    staged.Id);
            var now = timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                    item => item.DeduplicationKey == deduplicationKey,
                    token)
                .ConfigureAwait(false);
            if (job is null)
            {
                job = new BackgroundJobEntity
                {
                    Id = UlidId.New(now),
                    Type = AiBatchJobWorker.PrepareJobType,
                    SchemaVersion = AiBatchJobWorker.JobSchemaVersion,
                    DeduplicationKey = deduplicationKey,
                    Priority = 100,
                    State = "queued",
                    AttemptCount = 0,
                    MaxAttempts = 100,
                    NextAttemptAt = now,
                    CorrelationId = context.TraceIdentifier,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.BackgroundJobs.Add(job);
            }
            else
            {
                job.Priority = Math.Max(job.Priority, 100);
                job.State = "queued";
                job.NextAttemptAt = now;
                job.LeaseOwner = null;
                job.LeaseExpiresAt = null;
                job.CompletedAt = null;
                job.ErrorCode = null;
                job.SafeErrorDetail = null;
                job.MaxAttempts = Math.Max(
                    job.MaxAttempts,
                    checked(job.AttemptCount + 1));
                job.UpdatedAt = now;
            }

            job.PayloadJson = JsonSerializer.Serialize(new
            {
                compatibilityKey = staged.CompatibilityKey,
                submitNow = true,
            });
            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                ActorStaffUserId = ApiHelpers.StaffId(principal),
                EventType = "ai.batch_flush_requested",
                ObjectType = "aiBatchGroup",
                ObjectId = staged.CompatibilityKey,
                Outcome = "accepted",
                CorrelationId = context.TraceIdentifier,
                SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                    context.Connection.RemoteIpAddress),
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    batchRequestId = staged.Id,
                    jobId = job.Id,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Results.Accepted(
                "/api/v1/admin/ai-batches",
                new
                {
                    compatibilityKey = staged.CompatibilityKey,
                    jobId = job.Id,
                    state = "queued",
                });
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> CancelAsync(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        IAiBatchProviderClient batchProvider,
        IAiSecretStore secretStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var readDb = await dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var claim = await readDb.AiBatches
            .AsNoTracking()
            .Include(item => item.AiConnection)
            .SingleOrDefaultAsync(
                item => item.Id == batchId,
                cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
        {
            return Results.NotFound();
        }

        if (claim.State is not (
                "submitted" or "pending" or "running" or "delayed")
            || claim.ProviderBatchName is null)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_BATCH_NOT_CANCELLABLE",
                "この一括処理はキャンセルできません",
                "送信済みで処理中の Gemini Batch だけをキャンセルできます。");
        }

        if (claim.AiConnection.CredentialRevision
                != claim.ConnectionRevision
            || claim.AiConnection.State != "active")
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status409Conflict,
                "AI_BATCH_CREDENTIAL_STALE",
                "この一括処理の認証情報が変更されています",
                "リモート照合を行い、認証情報の状態を確認してください。");
        }

        try
        {
            using var secret = await secretStore.ReadAsync(
                    new AiSecretReference(
                        claim.AiConnection.SecretReference),
                    cancellationToken)
                .ConfigureAwait(false);
            await batchProvider.CancelAsync(
                    new AiConnectionSettings(
                        claim.AiConnection.Id,
                        claim.AiConnection.Provider,
                        GeminiBaseAddress,
                        claim.AiConnection.ModelId,
                        TimeSpan.FromSeconds(
                            claim.AiConnection.TimeoutSeconds)),
                    secret.Utf8Bytes,
                    claim.ProviderBatchName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AiProviderException)
        {
            return ApiHelpers.Problem(
                context,
                StatusCodes.Status502BadGateway,
                "AI_BATCH_CANCEL_FAILED",
                "Gemini Batch のキャンセルを確認できませんでした",
                "状態を再照合してから、もう一度お試しください。");
        }

        return await writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches.SingleAsync(
                item => item.Id == batchId,
                token);
            var now = timeProvider.GetUtcNow();
            if (batch.State is "succeeded" or "failed"
                or "cancelled" or "expired")
            {
                return Results.Ok(new
                {
                    batchId = batch.Id,
                    state = batch.State,
                });
            }

            batch.ErrorCode = "gemini_batch_cancel_requested";
            batch.SafeErrorDetail = null;
            batch.NextActionAt = now;
            batch.UpdatedAt = now;
            var deduplicationKey =
                $"ai-batch:{batch.Id}:admin-cancel-poll:{batch.Revision}";
            var job = await db.BackgroundJobs.SingleOrDefaultAsync(
                    item => item.DeduplicationKey == deduplicationKey,
                    token)
                .ConfigureAwait(false);
            if (job is null)
            {
                job = new BackgroundJobEntity
                {
                    Id = UlidId.New(now),
                    Type = AiBatchJobWorker.PollJobType,
                    SchemaVersion = AiBatchJobWorker.JobSchemaVersion,
                    DeduplicationKey = deduplicationKey,
                    Priority = 100,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        batchId = batch.Id,
                    }),
                    State = "queued",
                    AttemptCount = 0,
                    MaxAttempts = 100,
                    NextAttemptAt = now,
                    CorrelationId = context.TraceIdentifier,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.BackgroundJobs.Add(job);
            }

            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                ActorStaffUserId = ApiHelpers.StaffId(principal),
                EventType = "ai.batch_cancel_requested",
                ObjectType = "aiBatch",
                ObjectId = batch.Id,
                Outcome = "accepted",
                CorrelationId = context.TraceIdentifier,
                SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                    context.Connection.RemoteIpAddress),
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    batchId = batch.Id,
                    jobId = job.Id,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return Results.Accepted(
                $"/api/v1/admin/ai-batches/{batch.Id}",
                new
                {
                    batchId = batch.Id,
                    jobId = job.Id,
                    state = "cancel_requested",
                });
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<IResult> ExpediteAsync(
        string batchId,
        HttpContext context,
        ClaimsPrincipal principal,
        IDbContextFactory<OokiGraderDbContext> dbContextFactory,
        IWriteCoordinator writeCoordinator,
        TimeProvider timeProvider,
        Microsoft.Extensions.Options.IOptions<AiBatchJobWorkerOptions>
            batchOptions,
        CancellationToken cancellationToken)
    {
        return writeCoordinator.ExecuteAsync<IResult>(async token =>
        {
            await using var db = await dbContextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);
            await using var transaction = await db.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);
            var batch = await db.AiBatches
                .Include(item => item.AiTaskProfile)
                .Include(item => item.Requests)
                    .ThenInclude(item => item.AiRequest)
                .SingleOrDefaultAsync(
                    item => item.Id == batchId,
                    token)
                .ConfigureAwait(false);
            if (batch is null)
            {
                return Results.NotFound();
            }

            if (batch.State is not ("cancelled" or "failed" or "expired")
                || batch.PossibleDuplicate)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_EXPEDITE_UNSAFE",
                    "この一括処理は安全に優先処理へ切り替えられません",
                    "まずキャンセルまたは失敗をリモート照合で確定してください。結果が不明な処理は再送信されません。");
            }

            if (!batch.AiTaskProfile.Active
                || batch.AiTaskProfile.ProcessingStrategy != "gemini_batch")
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_PROFILE_STALE",
                    "採点プロファイルが変更されています",
                    "有効な採点プロファイルを確認してから、再度お試しください。");
            }

            var candidates = batch.Requests
                .Where(item =>
                    item.State is "failed" or "missing" or "cancelled"
                    && item.ResponseJson is null
                    && item.AiRequest.Purpose == AiTaskTypes.InitialGrading
                    && item.AiRequest.EntityType == "submission"
                    && item.AiRequest.AttemptNumber
                        < batchOptions.Value.MaximumRequestAttempts)
                .OrderBy(item => item.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            var candidateRequestIds = candidates
                .Select(item => item.AiRequestId)
                .ToArray();
            var alreadyRetried = await db.AiRequests
                .AsNoTracking()
                .Where(item =>
                    item.RetryOfAiRequestId != null
                    && candidateRequestIds.Contains(
                        item.RetryOfAiRequestId))
                .Select(item => item.RetryOfAiRequestId!)
                .ToHashSetAsync(token)
                .ConfigureAwait(false);
            candidates = candidates
                .Where(item => !alreadyRetried.Contains(item.AiRequestId))
                .ToArray();
            if (candidates.Length == 0)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_EXPEDITE_NOTHING_PENDING",
                    "優先処理へ切り替えられる未完了リクエストがありません",
                    "完了済みまたは既に再試行中のリクエストは重複送信されません。");
            }

            var submissionIds = candidates
                .Select(item => item.AiRequest.EntityId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var submissions = await db.Submissions
                .AsNoTracking()
                .Include(item => item.TestSession)
                .Where(item => submissionIds.Contains(item.Id))
                .ToDictionaryAsync(
                    item => item.Id,
                    StringComparer.Ordinal,
                    token)
                .ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var enqueued = new List<string>(candidates.Length);
            foreach (var candidate in candidates)
            {
                var source = candidate.AiRequest;
                if (!submissions.TryGetValue(
                        source.EntityId,
                        out var submission)
                    || submission.PreprocessingManifestHash
                        is not { Length: 64 })
                {
                    continue;
                }

                var retryId = UlidId.New(now);
                var retry = new AiRequestEntity
                {
                    Id = retryId,
                    RequestKey = $"grade_{retryId}",
                    AiTaskProfileId = source.AiTaskProfileId,
                    TaskProfileRevision = source.TaskProfileRevision,
                    Purpose = source.Purpose,
                    EntityType = source.EntityType,
                    EntityId = source.EntityId,
                    EntityRevision = source.EntityRevision,
                    InputManifestHash = source.InputManifestHash,
                    AttemptNumber = checked(source.AttemptNumber + 1),
                    RetryOfAiRequestId = source.Id,
                    State = "prepared",
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.AiRequests.Add(retry);
                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = UlidId.New(now),
                    Type = AiInitialGradingJobWorker.JobType,
                    SchemaVersion =
                        AiInitialGradingJobWorker.JobSchemaVersion,
                    DeduplicationKey =
                        $"ai-request:{retry.Id}:admin-expedite",
                    Priority = 100,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        submissionId = submission.Id,
                        templateVersionId =
                            submission.TestSession.TemplateVersionId,
                        manifestHash =
                            submission.PreprocessingManifestHash,
                        aiRequestId = retry.Id,
                        forceExpedite = true,
                    }),
                    State = "queued",
                    AttemptCount = 0,
                    MaxAttempts = 8,
                    NextAttemptAt = now,
                    CorrelationId = context.TraceIdentifier,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.OutboxEvents.Add(new OutboxEventEntity
                {
                    Id = UlidId.New(now),
                    AggregateType = "aiRequest",
                    AggregateId = retry.Id,
                    EventType = "ai.request.expedite_scheduled",
                    SchemaVersion = 1,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        aiRequestId = retry.Id,
                        retryOfAiRequestId = source.Id,
                        batchId = batch.Id,
                        retry.AttemptNumber,
                    }),
                    CorrelationId = context.TraceIdentifier,
                    OccurredAt = now,
                });
                enqueued.Add(retry.Id);
            }

            if (enqueued.Count == 0)
            {
                return ApiHelpers.Problem(
                    context,
                    StatusCodes.Status409Conflict,
                    "AI_BATCH_EXPEDITE_INPUT_STALE",
                    "優先処理に必要な入力がありません",
                    "前処理の状態を確認してから、採点を再実行してください。");
            }

            db.AuditEvents.Add(new AuditEventEntity
            {
                Id = UlidId.New(now),
                OccurredAt = now,
                ActorStaffUserId = ApiHelpers.StaffId(principal),
                EventType = "ai.batch_expedite_requested",
                ObjectType = "aiBatch",
                ObjectId = batch.Id,
                Outcome = "accepted",
                CorrelationId = context.TraceIdentifier,
                SourceIpPrefix = StaffAuthenticationService.ToIpPrefix(
                    context.Connection.RemoteIpAddress),
                SafeMetadataJson = JsonSerializer.Serialize(new
                {
                    batchId = batch.Id,
                    requestCount = enqueued.Count,
                }),
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return Results.Accepted(
                $"/api/v1/admin/ai-batches/{batch.Id}",
                new
                {
                    batchId = batch.Id,
                    state = "expedite_queued",
                    aiRequestIds = enqueued,
                });
        }, cancellationToken);
    }

    private sealed record FlushBatchRequest(string CompatibilityKey);

    private sealed record BatchCursor(DateTimeOffset CreatedAt, string Id);
}
