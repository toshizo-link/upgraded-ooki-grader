using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.IntegrationTests;

public sealed class AiBatchJobWorkerTests
{
    [Fact]
    public async Task CompleteLifecycleStoresResponseReadyUsageAtBatchRate()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        var staged = await fixture.StageAsync();

        Assert.True(staged.Created);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var batch = await db.AiBatches.SingleAsync();
        var mapping = await db.AiBatchRequests.SingleAsync();
        var request = await db.AiRequests.SingleAsync();
        var usage = await db.AiUsage.SingleAsync();
        Assert.Equal("succeeded", batch.State);
        Assert.Equal(1, batch.ConnectionRevision);
        Assert.Equal("completed", batch.CleanupState);
        Assert.Equal("response_ready", mapping.State);
        Assert.Null(mapping.ProviderRequestJson);
        Assert.Equal(0, mapping.ProviderRequestBytes);
        Assert.Equal("response_ready", request.State);
        Assert.Equal("""{"ok":true}""", request.ValidatedResponseJson);
        Assert.Equal(6, usage.EstimatedUsdMicros);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Equal(1, fixture.Provider.UploadCalls);
        Assert.Equal(2, fixture.Provider.DeleteCalls);
        Assert.False(fixture.Provider.ObservedInsideWriteCoordinator);
        Assert.DoesNotContain(
            db.BackgroundJobs,
            item => item.Type == AiInitialGradingJobWorker.JobType);
    }

    [Fact]
    public async Task AmbiguousCreateNeverResubmitsAndAdoptsSingleMatch()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.CreateFailure = new AiBatchCreateException(
            AiBatchCreateFailureKind.AmbiguousAfterSend,
            "synthetic_network_unknown",
            isTransient: true);
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = fixture.CreateDbContext())
        {
            var batch = await db.AiBatches.SingleAsync();
            Assert.Equal("reconcile_required", batch.State);
            Assert.True(batch.PossibleDuplicate);
            fixture.Provider.ListedBatches =
            [
                FakeBatchProvider.Status(
                    batch.DisplayName,
                    AiBatchRemoteState.Pending,
                    "batches/adopted",
                    fixture.Time.GetUtcNow()),
            ];
        }

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var verified = fixture.CreateDbContext();
        var adopted = await verified.AiBatches.SingleAsync();
        Assert.Equal("submitted", adopted.State);
        Assert.Equal("batches/adopted", adopted.ProviderBatchName);
        Assert.False(adopted.PossibleDuplicate);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Contains(
            verified.BackgroundJobs,
            item => item.Type == AiBatchJobWorker.PollJobType
                && item.State == "queued");
    }

    [Fact]
    public async Task ReconciliationWithMultipleMatchesStopsForManualReview()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.CreateFailure = new AiBatchCreateException(
            AiBatchCreateFailureKind.AmbiguousAfterSend,
            "synthetic_network_unknown",
            isTransient: true);
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = fixture.CreateDbContext())
        {
            var batch = await db.AiBatches.SingleAsync();
            fixture.Provider.ListedBatches =
            [
                FakeBatchProvider.Status(
                    batch.DisplayName,
                    AiBatchRemoteState.Pending,
                    "batches/duplicate-1",
                    fixture.Time.GetUtcNow()),
                FakeBatchProvider.Status(
                    batch.DisplayName,
                    AiBatchRemoteState.Running,
                    "batches/duplicate-2",
                    fixture.Time.GetUtcNow()),
            ];
        }

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var verified = fixture.CreateDbContext();
        var stopped = await verified.AiBatches.SingleAsync();
        Assert.Equal("manual_review", stopped.State);
        Assert.True(stopped.PossibleDuplicate);
        Assert.Equal(
            "gemini_batch_multiple_remote_matches",
            stopped.ErrorCode);
        Assert.Null(stopped.ProviderBatchName);
        Assert.Equal(1, fixture.Provider.CreateCalls);
    }

    [Fact]
    public async Task RecoveredSubmittingStateReconcilesWithoutCallingCreate()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = fixture.CreateDbContext())
        {
            var batch = await db.AiBatches.SingleAsync();
            batch.State = "submitting";
            batch.CreateAttemptCount = 1;
            batch.CreateAttemptStartedAt = fixture.Time.GetUtcNow();
            await db.SaveChangesAsync();
        }

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var verified = fixture.CreateDbContext();
        var recovered = await verified.AiBatches.SingleAsync();
        Assert.Equal("reconcile_required", recovered.State);
        Assert.True(recovered.PossibleDuplicate);
        Assert.Equal(0, fixture.Provider.CreateCalls);
        Assert.Contains(
            verified.BackgroundJobs,
            item => item.Type == AiBatchJobWorker.ReconcileJobType
                && item.State == "queued");
    }

    [Fact]
    public async Task StagingIsIdempotentButRejectsDifferentImmutablePayload()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        var first = await fixture.StageAsync();
        var duplicate = await fixture.StageAsync();

        Assert.False(duplicate.Created);
        Assert.Equal(first.BatchRequestId, duplicate.BatchRequestId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.StageAsync(userInstruction: "different"));

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.AiBatchRequests.CountAsync());
        Assert.Equal(1, await db.BackgroundJobs.CountAsync(
            item => item.Type == AiBatchJobWorker.PrepareJobType));
    }

    [Fact]
    public async Task StagingRequiresCurrentCredentialBoundBatchProbe()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var connection = await db.AiConnections.SingleAsync();
            connection.LastBatchCapabilityProbeState = "failed";
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.StageAsync());
        await using var verified = fixture.CreateDbContext();
        Assert.Empty(verified.AiBatchRequests);
    }

    [Fact]
    public async Task SubmitNowPayloadBypassesAggregationWait()
    {
        await using var fixture = await BatchFixture.CreateAsync(
            aggregationRequestCount: 20,
            maximumAggregationWait: TimeSpan.FromMinutes(5));
        var staged = await fixture.StageAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Empty(db.AiBatches);
            var mapping = await db.AiBatchRequests.SingleAsync();
            var job = await db.BackgroundJobs.SingleAsync(
                item => item.Id == staged.PreparationJobId);
            job.PayloadJson = JsonSerializer.Serialize(new
            {
                compatibilityKey = mapping.CompatibilityKey,
                submitNow = true,
            });
            job.State = "queued";
            job.NextAttemptAt = fixture.Time.GetUtcNow();
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            await db.SaveChangesAsync();
        }

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using var verified = fixture.CreateDbContext();
        Assert.Single(verified.AiBatches);
        Assert.Equal(
            "prepared",
            (await verified.AiBatchRequests.SingleAsync()).State);
    }

    [Fact]
    public async Task PollDelayJitterIsDeterministicAndBounded()
    {
        await using var fixture = await BatchFixture.CreateAsync();

        var first = fixture.Worker.RemotePollDelay(
            TimeSpan.Zero,
            "01K14P4A2GBB7W4K1M1M1M1M1M",
            1);
        var repeated = fixture.Worker.RemotePollDelay(
            TimeSpan.Zero,
            "01K14P4A2GBB7W4K1M1M1M1M1M",
            1);
        var nextEpoch = fixture.Worker.RemotePollDelay(
            TimeSpan.Zero,
            "01K14P4A2GBB7W4K1M1M1M1M1M",
            2);
        var hourTier = fixture.Worker.RemotePollDelay(
            TimeSpan.FromMinutes(10),
            "01K14P4A2GBB7W4K1M1M1M1M1M",
            1);
        var longTier = fixture.Worker.RemotePollDelay(
            TimeSpan.FromHours(2),
            "01K14P4A2GBB7W4K1M1M1M1M1M",
            1);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, nextEpoch);
        Assert.InRange(
            first,
            TimeSpan.FromMilliseconds(900),
            TimeSpan.FromMilliseconds(1_100));
        Assert.InRange(
            hourTier,
            TimeSpan.FromMilliseconds(1_800),
            TimeSpan.FromMilliseconds(2_200));
        Assert.InRange(
            longTier,
            TimeSpan.FromSeconds(9),
            TimeSpan.FromSeconds(11));
    }

    [Fact]
    public async Task RemoteFailureMarksRequestsRetryableWithoutUsage()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.RemoteStatus = FakeBatchProvider.Status(
            BatchFixture.DisplayNamePlaceholder,
            AiBatchRemoteState.Expired,
            "batches/batch-1",
            fixture.Time.GetUtcNow());
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        Assert.Equal("expired", (await db.AiBatches.SingleAsync()).State);
        var requests = await db.AiRequests
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.Equal("failed", requests[0].State);
        Assert.Equal(1, requests[0].AttemptNumber);
        Assert.Equal("prepared", requests[1].State);
        Assert.Equal(2, requests[1].AttemptNumber);
        Assert.Equal(requests[0].Id, requests[1].RetryOfAiRequestId);
        Assert.NotEqual(requests[0].RequestKey, requests[1].RequestKey);

        var mappings = await db.AiBatchRequests
            .Include(item => item.AiRequest)
            .OrderBy(item => item.AiRequest.AttemptNumber)
            .ToListAsync();
        Assert.Null(mappings[0].ProviderRequestJson);
        Assert.Equal(0, mappings[0].ProviderRequestBytes);
        Assert.Equal("ready", mappings[1].State);
        Assert.Contains(
            requests[1].RequestKey,
            mappings[1].ProviderRequestJson,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            requests[0].RequestKey,
            mappings[1].ProviderRequestJson,
            StringComparison.Ordinal);

        var reservations = await db.AiBudgetReservations
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, reservations.Count);
        Assert.Equal("settled", reservations[0].State);
        Assert.Equal(
            reservations[0].ReservedUsdMicros,
            reservations[0].ActualUsdMicros);
        Assert.Equal("reserved", reservations[1].State);
        Assert.Contains(
            db.BackgroundJobs,
            item => item.Type == AiBatchJobWorker.PrepareJobType
                && item.State == "queued"
                && item.NextAttemptAt
                    == fixture.Time.GetUtcNow().AddSeconds(30));
        Assert.Empty(db.AiUsage);
    }

    [Fact]
    public async Task MissingResultCreatesOnlyOneFreshImmutableAttempt()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.ReturnNoResults = true;
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var attempts = await db.AiRequests
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("gemini_batch_result_missing", attempts[0].ErrorCode);
        Assert.Equal(attempts[0].Id, attempts[1].RetryOfAiRequestId);
        Assert.Equal(2, attempts[1].AttemptNumber);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.Empty(db.AiUsage);
    }

    [Fact]
    public async Task InvalidOversizedOutputIsNotRetried()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.ResponseJson =
            JsonSerializer.Serialize(new
            {
                value = new string('a', 1_000_001),
            });
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var request = await db.AiRequests.SingleAsync();
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            "gemini_batch_response_too_large",
            request.ErrorCode);
        Assert.Null(request.RetryOfAiRequestId);
        Assert.Single(db.AiBatchRequests);
        Assert.Single(db.AiBudgetReservations);
        Assert.Equal(
            "settled",
            (await db.AiBudgetReservations.SingleAsync()).State);
        Assert.Empty(db.AiUsage);
    }

    [Fact]
    public async Task InvalidSchemaItemIsNotRetried()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.ResultErrorCode = "gemini_json_invalid";
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var request = await db.AiRequests.SingleAsync();
        Assert.Equal("invalid_output", request.State);
        Assert.Equal("gemini_json_invalid", request.ErrorCode);
        Assert.Single(db.AiBatchRequests);
        Assert.Single(db.AiBudgetReservations);
        Assert.Empty(db.AiUsage);
    }

    [Fact]
    public async Task RetryLimitStopsWithoutCreatingAnotherAttempt()
    {
        await using var fixture = await BatchFixture.CreateAsync(
            maximumRequestAttempts: 1);
        fixture.Provider.RemoteStatus = FakeBatchProvider.Status(
            BatchFixture.DisplayNamePlaceholder,
            AiBatchRemoteState.Expired,
            "batches/batch-1",
            fixture.Time.GetUtcNow());
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var request = await db.AiRequests.SingleAsync();
        Assert.Equal("failed", request.State);
        Assert.Equal("gemini_batch_retry_exhausted", request.ErrorCode);
        Assert.Single(db.AiBatchRequests);
        Assert.Single(db.AiBudgetReservations);
        Assert.Equal(
            "settled",
            (await db.AiBudgetReservations.SingleAsync()).State);
    }

    [Fact]
    public async Task RetryReservesAgainOnlyWhenBudgetCanCoverBothAttempts()
    {
        await using var fixture = await BatchFixture.CreateAsync(
            dailyHardLimit: 150);
        fixture.Provider.RemoteStatus = FakeBatchProvider.Status(
            BatchFixture.DisplayNamePlaceholder,
            AiBatchRemoteState.Expired,
            "batches/batch-1",
            fixture.Time.GetUtcNow());
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var request = await db.AiRequests.SingleAsync();
        Assert.Equal("budget_blocked", request.State);
        Assert.Equal(
            "gemini_batch_retry_budget_blocked",
            request.ErrorCode);
        Assert.Single(db.AiBatchRequests);
        Assert.Single(db.AiBudgetReservations);
    }

    [Fact]
    public async Task DefinitePreSendCreateFailureReturnsToPreparedSafely()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.CreateFailure = new AiBatchCreateException(
            AiBatchCreateFailureKind.DefinitePreSend,
            "synthetic_pre_send_rejection",
            isTransient: false);
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        var batch = await db.AiBatches.SingleAsync();
        var request = await db.AiRequests.SingleAsync();
        var mapping = await db.AiBatchRequests.SingleAsync();
        Assert.Equal("prepared", batch.State);
        Assert.Equal(0, batch.CreateAttemptCount);
        Assert.NotNull(batch.ProviderInputFileName);
        Assert.Null(batch.CompletedAt);
        Assert.Equal("prepared", request.State);
        Assert.Equal("prepared", mapping.State);
        Assert.Equal(1, fixture.Provider.CreateCalls);
        Assert.DoesNotContain(
            db.BackgroundJobs,
            item => item.Type == AiBatchJobWorker.ReconcileJobType);
    }

    [Fact]
    public async Task DefiniteTransientCreateRejectionUsesFreshAttempt()
    {
        await using var fixture = await BatchFixture.CreateAsync();
        fixture.Provider.CreateFailure = new AiBatchCreateException(
            AiBatchCreateFailureKind.DefiniteRemoteRejection,
            "gemini_rate_limited",
            isTransient: true);
        await fixture.StageAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = fixture.CreateDbContext();
        Assert.Equal("failed", (await db.AiBatches.SingleAsync()).State);
        var attempts = await db.AiRequests
            .OrderBy(item => item.AttemptNumber)
            .ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(attempts[0].Id, attempts[1].RetryOfAiRequestId);
        var reservations = await db.AiBudgetReservations
            .ToListAsync();
        Assert.Equal(
            "released",
            Assert.Single(
                reservations,
                item => item.AiRequestId == attempts[0].Id).State);
        Assert.Equal(
            "reserved",
            Assert.Single(
                reservations,
                item => item.AiRequestId == attempts[1].Id).State);
        Assert.Equal(1, fixture.Provider.DeleteCalls);
        Assert.Equal("completed", (await db.AiBatches.SingleAsync()).CleanupState);
        Assert.Equal(1, fixture.Provider.CreateCalls);
    }

    private sealed class BatchFixture : IAsyncDisposable
    {
        public const string DisplayNamePlaceholder =
            "ooki-01K14P4A2GBB7W4K1M1M1M1M1M-0123456789ab";
        private readonly SqliteConnection _connection;
        private readonly DbContextFactory _factory;
        private readonly InMemoryAiSecretStore _secretStore;
        private readonly string _aiRequestId;
        private readonly string _requestKey;

        private BatchFixture(
            SqliteConnection connection,
            DbContextFactory factory,
            InMemoryAiSecretStore secretStore,
            string aiRequestId,
            string requestKey,
            MutableTimeProvider time,
            BoundaryWriteCoordinator writeCoordinator,
            FakeBatchProvider provider,
            AiBatchRequestStager stager,
            AiBatchJobWorker worker)
        {
            _connection = connection;
            _factory = factory;
            _secretStore = secretStore;
            _aiRequestId = aiRequestId;
            _requestKey = requestKey;
            Time = time;
            WriteCoordinator = writeCoordinator;
            Provider = provider;
            Stager = stager;
            Worker = worker;
        }

        public MutableTimeProvider Time { get; }
        public BoundaryWriteCoordinator WriteCoordinator { get; }
        public FakeBatchProvider Provider { get; }
        public AiBatchRequestStager Stager { get; }
        public AiBatchJobWorker Worker { get; }

        public static async Task<BatchFixture> CreateAsync(
            int maximumRequestAttempts = 3,
            long? dailyHardLimit = null,
            int aggregationRequestCount = 1,
            TimeSpan? maximumAggregationWait = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connection)
                .Options;
            var time = new MutableTimeProvider(new DateTimeOffset(
                2026,
                7,
                27,
                3,
                0,
                0,
                TimeSpan.Zero));
            var factory = new DbContextFactory(options);
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var writeCoordinator = new BoundaryWriteCoordinator();
            var provider = new FakeBatchProvider(writeCoordinator, time);
            var secretStore = new InMemoryAiSecretStore();
            var connectionId = UlidId.New(time.GetUtcNow());
            var secretReference = await secretStore.WriteAsync(
                connectionId,
                1,
                "test-only-key".AsMemory());
            var profileId = UlidId.New(time.GetUtcNow());
            var aiRequestId = UlidId.New(time.GetUtcNow());
            var requestKey = $"grade_{aiRequestId}";
            await using (var db = factory.CreateDbContext())
            {
                db.AiConnections.Add(new AiConnectionEntity
                {
                    Id = connectionId,
                    Provider = AiProviders.GeminiDirect,
                    EndpointProfile = "googleGenerativeLanguage",
                    ModelId = GeminiBatchClient.SelectedModel,
                    SecretReference = secretReference.Value,
                    KeyFingerprint = "test",
                    CredentialRevision = 1,
                    TimeoutSeconds = 30,
                    ConcurrencyLimit = 1,
                    State = "active",
                    LastCapabilityProbeState = "passed",
                    LastCapabilityProbeAt = time.GetUtcNow(),
                    LastBatchCapabilityProbeState = "passed",
                    LastBatchCapabilityProbeAt = time.GetUtcNow(),
                    LastBatchCapabilityProbeCredentialRevision = 1,
                    CreatedByStaffUserId = UlidId.New(time.GetUtcNow()),
                    CreatedAt = time.GetUtcNow(),
                    UpdatedAt = time.GetUtcNow(),
                    Revision = 7,
                });
                db.AiTaskProfiles.Add(new AiTaskProfileEntity
                {
                    Id = profileId,
                    Name = "Batch initial grading",
                    TaskType = AiTaskTypes.InitialGrading,
                    AiConnectionId = connectionId,
                    ConnectionRevision = 1,
                    ModelId = GeminiBatchClient.SelectedModel,
                    ProcessingStrategy = "gemini_batch",
                    PromptVersion = "prompt-v1",
                    SchemaVersion = "schema-v1",
                    PromptContentHash = new string('a', 64),
                    ThinkingLevel = "minimal",
                    MediaResolution = "high",
                    MaxOutputTokens = 1_024,
                    ConcurrencyLimit = 1,
                    ApprovalState = "pilot_approved",
                    Active = true,
                    ActivatedAt = time.GetUtcNow(),
                    ActivatedByStaffUserId = UlidId.New(time.GetUtcNow()),
                    CreatedByStaffUserId = UlidId.New(time.GetUtcNow()),
                    CreatedAt = time.GetUtcNow(),
                    UpdatedAt = time.GetUtcNow(),
                });
                db.AiRequests.Add(new AiRequestEntity
                {
                    Id = aiRequestId,
                    RequestKey = requestKey,
                    AiTaskProfileId = profileId,
                    TaskProfileRevision = 1,
                    Purpose = AiTaskTypes.InitialGrading,
                    EntityType = "submission",
                    EntityId = UlidId.New(time.GetUtcNow()),
                    EntityRevision = 1,
                    InputManifestHash = new string('b', 64),
                    AttemptNumber = 1,
                    State = "prepared",
                    CreatedAt = time.GetUtcNow(),
                    UpdatedAt = time.GetUtcNow(),
                });
                db.AiBudgetReservations.Add(
                    new AiBudgetReservationEntity
                    {
                        Id = UlidId.New(time.GetUtcNow()),
                        AiRequestId = aiRequestId,
                        UsageDay = DateOnly.FromDateTime(
                            time.GetUtcNow().UtcDateTime),
                        UsageMonth = "2026-07",
                        ReservedUsdMicros = 100,
                        ActualUsdMicros = 0,
                        State = "reserved",
                        CreatedAt = time.GetUtcNow(),
                    });
                if (dailyHardLimit is not null)
                {
                    db.AiBudgetPolicies.Add(new AiBudgetPolicyEntity
                    {
                        Id = "default",
                        DailyWarningUsdMicros = 0,
                        DailyHardUsdMicros = dailyHardLimit.Value,
                        MonthlyWarningUsdMicros = 0,
                        MonthlyHardUsdMicros = 10_000,
                        UsdToJpyMicros = 150_000_000,
                        Active = true,
                        CreatedAt = time.GetUtcNow(),
                        UpdatedAt = time.GetUtcNow(),
                    });
                }
                db.PricingSnapshots.Add(new PricingSnapshotEntity
                {
                    Id = UlidId.New(time.GetUtcNow()),
                    Provider = AiProviders.GeminiDirect,
                    ModelId = GeminiBatchClient.SelectedModel,
                    InputUsdMicrosPerMillionTokens = 300_000,
                    OutputUsdMicrosPerMillionTokens = 2_500_000,
                    ThinkingUsdMicrosPerMillionTokens = 2_500_000,
                    SourceUrl =
                        "https://ai.google.dev/gemini-api/docs/pricing",
                    EffectiveAt = time.GetUtcNow().AddDays(-1),
                    CapturedAt = time.GetUtcNow(),
                });
                await db.SaveChangesAsync();
            }

            var stager = new AiBatchRequestStager(
                factory,
                writeCoordinator,
                provider,
                time);
            var worker = new AiBatchJobWorker(
                factory,
                writeCoordinator,
                provider,
                secretStore,
                time,
                Options.Create(new AiBatchJobWorkerOptions
                {
                    AggregationRequestCount = aggregationRequestCount,
                    MaximumRequestsPerBatch = Math.Max(
                        20,
                        aggregationRequestCount),
                    MaximumRequestAttempts = maximumRequestAttempts,
                    MaximumAggregationWait =
                        maximumAggregationWait ?? TimeSpan.Zero,
                    InitialRemotePollInterval = TimeSpan.FromSeconds(1),
                    HourRemotePollInterval = TimeSpan.FromSeconds(2),
                    LongRemotePollInterval = TimeSpan.FromSeconds(10),
                }),
                NullLogger<AiBatchJobWorker>.Instance);
            return new BatchFixture(
                connection,
                factory,
                secretStore,
                aiRequestId,
                requestKey,
                time,
                writeCoordinator,
                provider,
                stager,
                worker);
        }

        public Task<AiBatchStageResult> StageAsync(
            string userInstruction = "user")
        {
            using var schema = JsonDocument.Parse(
                """{"type":"object","additionalProperties":false,"properties":{"ok":{"type":"boolean"}},"required":["ok"]}""");
            var bytes = "answer-crop"u8.ToArray();
            return Stager.StageAsync(new AiBatchStageRequest(
                _aiRequestId,
                new string('c', 64),
                new AiProviderRequest(
                    _requestKey,
                    AiTaskTypes.InitialGrading,
                    "prompt-v1",
                    "schema-v1",
                    "system",
                    userInstruction,
                    schema.RootElement.Clone(),
                    [
                        new AiMediaPart(
                            "image/png",
                            bytes,
                            Convert.ToHexString(
                                    System.Security.Cryptography.SHA256.HashData(
                                        bytes))
                                .ToLowerInvariant()),
                    ])));
        }

        public OokiGraderDbContext CreateDbContext() =>
            _factory.CreateDbContext();

        public async ValueTask DisposeAsync()
        {
            _secretStore.Dispose();
            WriteCoordinator.Dispose();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeBatchProvider(
        BoundaryWriteCoordinator writeCoordinator,
        TimeProvider timeProvider) : IAiBatchProviderClient
    {
        private readonly GeminiBatchClient _serializer =
            new(new HttpClient());

        public string Provider => AiProviders.GeminiDirect;
        public int UploadCalls { get; private set; }
        public int CreateCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public bool ObservedInsideWriteCoordinator { get; private set; }
        public AiBatchCreateException? CreateFailure { get; set; }
        public IReadOnlyList<AiBatchStatus> ListedBatches { get; set; } = [];
        public AiBatchStatus? RemoteStatus { get; set; }
        public bool ReturnNoResults { get; set; }
        public string ResponseJson { get; set; } = """{"ok":true}""";
        public string? ResultErrorCode { get; set; }
        private string[] RequestKeys { get; set; } = [];

        public byte[] BuildJsonLines(
            IReadOnlyList<AiProviderRequest> requests)
        {
            RequestKeys = requests
                .Select(item => item.RequestKey)
                .ToArray();
            return _serializer.BuildJsonLines(requests);
        }

        public Task<AiBatchInputFile> UploadJsonLinesAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string displayName,
            ReadOnlyMemory<byte> jsonLines,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            UploadCalls++;
            return Task.FromResult(new AiBatchInputFile(
                "files/input-1",
                null,
                DateTimeOffset.UtcNow.AddHours(48),
                jsonLines.Length));
        }

        public Task<AiBatchCreateReceipt> CreateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            CreateCalls++;
            if (CreateFailure is not null)
            {
                throw CreateFailure;
            }

            RemoteStatus ??= Status(
                request.DisplayName,
                AiBatchRemoteState.Succeeded,
                "batches/batch-1",
                timeProvider.GetUtcNow());
            return Task.FromResult(new AiBatchCreateReceipt(
                "batches/batch-1",
                request.DisplayName,
                DateTimeOffset.UtcNow));
        }

        public Task<AiBatchStatus> GetAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            return Task.FromResult(
                RemoteStatus
                ?? Status(
                    BatchFixture.DisplayNamePlaceholder,
                    AiBatchRemoteState.Succeeded,
                    providerBatchName,
                    timeProvider.GetUtcNow()));
        }

        public Task CancelAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            return Task.CompletedTask;
        }

        public Task DeleteBatchAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            return Task.CompletedTask;
        }

        public Task<AiBatchListPage> ListAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string? pageToken = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            return Task.FromResult(new AiBatchListPage(
                ListedBatches,
                null));
        }

        public Task<IReadOnlyList<AiBatchItemResult>> ReadResultsAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchStatus completedBatch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            if (ReturnNoResults)
            {
                return Task.FromResult<
                    IReadOnlyList<AiBatchItemResult>>([]);
            }

            if (ResultErrorCode is not null)
            {
                IReadOnlyList<AiBatchItemResult> failed =
                [
                    new(
                        Assert.Single(RequestKeys),
                        null,
                        ResultErrorCode),
                ];
                return Task.FromResult(failed);
            }

            using var output = JsonDocument.Parse(ResponseJson);
            IReadOnlyList<AiBatchItemResult> results =
            [
                new(
                    Assert.Single(RequestKeys),
                    new AiProviderResponse(
                        AiProviders.GeminiDirect,
                        GeminiBatchClient.SelectedModel,
                        "gemini-3.5-flash-lite-001",
                        "response-1",
                        "STOP",
                        output.RootElement.Clone(),
                        new AiUsage(10, 0, 2, 1, 13),
                        TimeSpan.Zero),
                    null),
            ];
            return Task.FromResult(results);
        }

        public Task DeleteFileAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObserveBoundary();
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public static AiBatchStatus Status(
            string displayName,
            AiBatchRemoteState state,
            string name,
            DateTimeOffset? timestamp = null)
        {
            var occurredAt = timestamp ?? DateTimeOffset.UtcNow;
            using var raw = JsonDocument.Parse(
                JsonSerializer.Serialize(new
                {
                    name,
                    metadata = new
                    {
                        displayName,
                    },
                }));
            return new AiBatchStatus(
                name,
                displayName,
                state,
                occurredAt,
                occurredAt,
                state is AiBatchRemoteState.Succeeded
                    or AiBatchRemoteState.Failed
                    or AiBatchRemoteState.Cancelled
                    or AiBatchRemoteState.Expired
                        ? occurredAt
                        : null,
                new AiBatchStats(1, state == AiBatchRemoteState.Succeeded ? 1 : 0,
                    state is AiBatchRemoteState.Failed
                        or AiBatchRemoteState.Expired ? 1 : 0,
                    state is AiBatchRemoteState.Pending
                        or AiBatchRemoteState.Running ? 1 : 0),
                "files/output-1",
                null,
                raw.RootElement.Clone());
        }

        private void ObserveBoundary()
        {
            ObservedInsideWriteCoordinator |= writeCoordinator.IsInside;
        }
    }

    private sealed class BoundaryWriteCoordinator : IWriteCoordinator, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly AsyncLocal<int> _depth = new();

        public bool IsInside => _depth.Value > 0;

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _depth.Value++;
                await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _gate.Release();
            }
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                _depth.Value++;
                return await operation(cancellationToken);
            }
            finally
            {
                _depth.Value--;
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount)
        {
            now = now.Add(amount);
        }
    }

    private sealed class DbContextFactory(
        DbContextOptions<OokiGraderDbContext> options)
        : IDbContextFactory<OokiGraderDbContext>
    {
        public OokiGraderDbContext CreateDbContext() => new(options);

        public Task<OokiGraderDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
