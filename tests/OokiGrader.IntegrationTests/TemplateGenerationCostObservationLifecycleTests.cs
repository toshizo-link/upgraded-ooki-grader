using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Observability;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TemplateGenerationCostObservationSerialScope
{
    public const string Name = "Template generation cost observations";
}

[Collection(TemplateGenerationCostObservationSerialScope.Name)]
public sealed class TemplateGenerationCostObservationLifecycleTests
{
    [Fact]
    public async Task FailedRunThenSuccessfulRetryObservesOnlyIncrementalBatchCosts()
    {
        await using var fixture = await CostFixture.CreateAsync();
        const long failedRunCost = 17;
        const long retryRunCost = 29;

        var failedRequestId = await fixture.AddSettledRunAsync(
            "failed-run",
            failedRunCost,
            state: "failed");
        var failed = await fixture.PrepareBatchObservationAsync(
            "failed-operation",
            "failed");

        Assert.NotNull(failed);
        Assert.Equal(failedRunCost, failed.TotalActualUsdMicros);
        Assert.Equal(0, failed.PreviousActualUsdMicros);
        Assert.Equal(failedRunCost, failed.DeltaActualUsdMicros);

        await fixture.AddSettledRunAsync(
            "retry-success-run",
            retryRunCost,
            state: "succeeded",
            retryOfRequestId: failedRequestId);
        var succeeded = await fixture.PrepareBatchObservationAsync(
            "success-operation",
            "succeeded");

        Assert.NotNull(succeeded);
        Assert.Equal(failedRunCost + retryRunCost, succeeded.TotalActualUsdMicros);
        Assert.Equal(failedRunCost, succeeded.PreviousActualUsdMicros);
        Assert.Equal(retryRunCost, succeeded.DeltaActualUsdMicros);
        Assert.NotEqual(
            failedRunCost + retryRunCost,
            succeeded.DeltaActualUsdMicros);

        var observations = await fixture.ReadBatchObservationsAsync();
        Assert.Equal(2, observations.Length);
        Assert.Equal(
            failedRunCost,
            ReadDelta(Assert.Single(
                observations,
                item => item.ReasonCode == "failed")));
        Assert.Equal(
            retryRunCost,
            ReadDelta(Assert.Single(
                observations,
                item => item.ReasonCode == "succeeded")));
    }

    [Fact]
    public async Task DispatchingCancellationSettlesAndObservesExactlyOnce()
    {
        await using var fixture = await CostFixture.CreateAsync();
        using var metrics = new CostMetricCollector();
        const long reservedCost = 41;
        await fixture.AddDispatchingRunAsync("cancelled-run", reservedCost);

        var cancelled = await fixture.CancelAsync(
            operationId: "cancel-operation",
            correlationId: "cancel-request");

        Assert.Equal(TemplateGenerationBatchStatus.Cancelled, cancelled.Status);
        var dispatch = await fixture.ReadDispatchSettlementAsync();
        Assert.Equal("cancelled", dispatch.RequestState);
        Assert.True(dispatch.PossibleDuplicate);
        Assert.Equal("TEMPLATE_GENERATION_CANCELLED", dispatch.ErrorCode);
        Assert.Equal("settled", dispatch.ReservationState);
        Assert.Equal(reservedCost, dispatch.ReservedUsdMicros);
        Assert.Equal(reservedCost, dispatch.ActualUsdMicros);
        Assert.NotNull(dispatch.SettledAt);

        var unitObservations = await fixture.ReadUnitObservationsAsync();
        var batchObservations = await fixture.ReadBatchObservationsAsync();
        var unitObservation = Assert.Single(unitObservations);
        var batchObservation = Assert.Single(batchObservations);
        Assert.Equal("cancelled", unitObservation.ReasonCode);
        Assert.Equal("cancelled", batchObservation.ReasonCode);
        Assert.Equal(reservedCost, ReadActual(unitObservation));
        Assert.Equal(reservedCost, ReadDelta(batchObservation));

        var unitMetric = Assert.Single(
            metrics.CostMeasurements,
            item => item.InstrumentName
                == "ookigrader.template_generation.ai_cost_per_unit");
        var batchMetric = Assert.Single(
            metrics.CostMeasurements,
            item => item.InstrumentName
                == "ookigrader.template_generation.ai_cost_per_batch");
        Assert.Equal(reservedCost, unitMetric.Value);
        Assert.Equal(reservedCost, batchMetric.Value);
        Assert.Equal("cancelled", unitMetric.Tags["outcome"]);
        Assert.Equal("cancelled", batchMetric.Tags["outcome"]);

        await fixture.CancelAsync(
            operationId: "cancel-replay-again",
            correlationId: "cancel-replay-again");

        Assert.Single(await fixture.ReadUnitObservationsAsync());
        Assert.Single(await fixture.ReadBatchObservationsAsync());
        Assert.Single(
            metrics.CostMeasurements,
            item => item.InstrumentName
                == "ookigrader.template_generation.ai_cost_per_unit");
        Assert.Single(
            metrics.CostMeasurements,
            item => item.InstrumentName
                == "ookigrader.template_generation.ai_cost_per_batch");
    }

    private static long ReadDelta(AuditEventEntity observation) =>
        ReadInt64(observation, "deltaActualUsdMicros");

    private static long ReadActual(AuditEventEntity observation) =>
        ReadInt64(observation, "actualUsdMicros");

    private static long ReadInt64(
        AuditEventEntity observation,
        string propertyName)
    {
        using var metadata = JsonDocument.Parse(observation.SafeMetadataJson!);
        return metadata.RootElement.GetProperty(propertyName).GetInt64();
    }

    private sealed class CostFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<OokiGraderDbContext> _dbOptions;
        private readonly IUlidGenerator _ids;
        private readonly TimeProvider _timeProvider;
        private readonly TemplateGenerationProfile _profile;
        private string? _dispatchingRequestId;
        private string? _dispatchingReservationId;

        private CostFixture(
            SqliteConnection connection,
            DbContextOptions<OokiGraderDbContext> dbOptions,
            IUlidGenerator ids,
            TimeProvider timeProvider,
            TemplateGenerationProfile profile,
            string staffId,
            string batchId,
            string unitId,
            string taskProfileId)
        {
            _connection = connection;
            _dbOptions = dbOptions;
            _ids = ids;
            _timeProvider = timeProvider;
            _profile = profile;
            StaffId = staffId;
            BatchId = batchId;
            UnitId = unitId;
            TaskProfileId = taskProfileId;
        }

        public string StaffId { get; }
        public string BatchId { get; }
        public string UnitId { get; }
        public string TaskProfileId { get; }

        public static async Task<CostFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var dbOptions = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connection)
                .Options;
            var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var ids = new UlidGenerator(timeProvider);
            var profile = new TemplateGenerationProfile(
                TemplateGenerationProfile.CurrentProfileVersion,
                TestType.Hop,
                "算数",
                AnswerStyle: null,
                TemplatePromptSystem.Standard,
                SourcePageCount: 1,
                UnitSequence: 1,
                FirstPage: 1,
                LastPage: 1,
                StepSetIndex: null,
                StepVariationIndex: null,
                DeterministicSuffix: null,
                TemplateGenerationProfile.CurrentSplitPolicyVersion,
                TemplateGenerationProfile.CurrentNamingPolicyVersion,
                TemplateGenerationBatchService.ExtractionPromptVersion,
                TemplateGenerationBatchService.ExtractionSchemaVersion);
            await using var db = new OokiGraderDbContext(dbOptions);
            await db.Database.EnsureCreatedAsync();
            var staffId = ids.NewId();
            var sourceId = ids.NewId();
            var batchId = ids.NewId();
            var unitId = ids.NewId();
            var connectionId = ids.NewId();
            var taskProfileId = ids.NewId();
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "cost.observer",
                UsernameNormalized = "cost.observer",
                DisplayName = "Cost Observer",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.UploadSessions.Add(new UploadSessionEntity
            {
                Id = sourceId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "cost-source.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 1,
                CurrentBytes = 1,
                FinalSha256 = new string('a', 64),
                IncomingRelativePath = "incoming/cost-source.part",
                State = "completed",
                ExpiresAt = now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateGenerationBatches.Add(new TemplateGenerationBatchEntity
            {
                Id = batchId,
                Status = TemplateGenerationBatchStatus.Generating,
                TestType = TestType.Hop,
                Subject = "算数",
                PromptSystem = TemplatePromptSystem.Standard,
                SourceId = sourceId,
                SourcePageCount = 1,
                ExpectedUnitCount = 1,
                CurrentOperationId = "generate-operation",
                PlanHash = new string('b', 64),
                CreatedByUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateGenerationUnits.Add(new TemplateGenerationUnitEntity
            {
                Id = unitId,
                BatchId = batchId,
                Sequence = 1,
                Status = TemplateGenerationUnitStatus.Queued,
                TestType = TestType.Hop,
                FirstPage = 1,
                LastPage = 1,
                PromptSystem = TemplatePromptSystem.Standard,
                GenerationProfileJson = JsonSerializer.Serialize(profile),
                GenerationProfileHash = profile.ComputeHash(),
                AppliedRotationsJson = "[]",
                WarningsJson = "[]",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = AiProviderCatalog.GeminiEndpointProfile,
                ModelId = "gemini-3.5-flash-lite",
                SecretReference = "fixture-secret",
                KeyFingerprint = new string('c', 64),
                CredentialRevision = 1,
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = taskProfileId,
                Name = "Cost observation fixture",
                TaskType = AiTaskTypes.TemplateExtraction,
                AiConnectionId = connectionId,
                ConnectionRevision = 1,
                ModelId = "gemini-3.5-flash-lite",
                ProcessingStrategy = "queued_standard",
                PromptVersion = TemplateGenerationBatchService.ExtractionPromptVersion,
                SchemaVersion = TemplateGenerationBatchService.ExtractionSchemaVersion,
                PromptContentHash = new string('d', 64),
                ApprovalState = "production_approved",
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            return new CostFixture(
                connection,
                dbOptions,
                ids,
                timeProvider,
                profile,
                staffId,
                batchId,
                unitId,
                taskProfileId);
        }

        public async Task<string> AddSettledRunAsync(
            string runName,
            long actualUsdMicros,
            string state,
            string? retryOfRequestId = null)
        {
            await using var db = CreateDb();
            var jobId = _ids.NewId();
            var requestId = _ids.NewId();
            var now = _timeProvider.GetUtcNow();
            db.BackgroundJobs.Add(Job(jobId, state == "succeeded" ? "succeeded" : "failed"));
            db.AiRequests.Add(Request(
                requestId,
                jobId,
                runName,
                state,
                retryOfRequestId));
            db.AiBudgetReservations.Add(Reservation(
                _ids.NewId(),
                requestId,
                "settled",
                actualUsdMicros));
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            unit.ExtractionJobId = jobId;
            unit.UpdatedAt = now;
            await db.SaveChangesAsync();
            return requestId;
        }

        public async Task AddDispatchingRunAsync(
            string runName,
            long reservedUsdMicros)
        {
            await using var db = CreateDb();
            var jobId = _ids.NewId();
            var requestId = _ids.NewId();
            var reservationId = _ids.NewId();
            db.BackgroundJobs.Add(Job(jobId, "leased"));
            db.AiRequests.Add(Request(
                requestId,
                jobId,
                runName,
                "dispatching",
                retryOfRequestId: null));
            db.AiBudgetReservations.Add(Reservation(
                reservationId,
                requestId,
                "reserved",
                reservedUsdMicros));
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            unit.ExtractionJobId = jobId;
            await db.SaveChangesAsync();
            _dispatchingRequestId = requestId;
            _dispatchingReservationId = reservationId;
        }

        public async Task<DispatchSettlement> ReadDispatchSettlementAsync()
        {
            await using var db = CreateDb();
            return await (
                    from request in db.AiRequests.AsNoTracking()
                    join reservation in db.AiBudgetReservations.AsNoTracking()
                        on request.Id equals reservation.AiRequestId
                    where request.Id == _dispatchingRequestId
                        && reservation.Id == _dispatchingReservationId
                    select new DispatchSettlement(
                        request.State,
                        request.PossibleDuplicate,
                        request.ErrorCode,
                        reservation.State,
                        reservation.ReservedUsdMicros,
                        reservation.ActualUsdMicros,
                        reservation.SettledAt))
                .SingleAsync();
        }

        public async Task<TemplateGenerationBatchCostObservation?>
            PrepareBatchObservationAsync(string operationId, string outcome)
        {
            await using var db = CreateDb();
            var observation = await TemplateGenerationCostObservationLedger
                .PrepareBatchObservationAsync(
                    db,
                    BatchId,
                    operationId,
                    outcome,
                    StaffId,
                    _ids,
                    _timeProvider.GetUtcNow(),
                    CancellationToken.None);
            await db.SaveChangesAsync();
            return observation;
        }

        public async Task<TemplateGenerationBatchSnapshot> CancelAsync(
            string operationId,
            string correlationId)
        {
            await using var db = CreateDb();
            var batch = await db.TemplateGenerationBatches
                .AsNoTracking()
                .SingleAsync();
            var batchService = new TemplateGenerationBatchService(
                db,
                UnusedContentStore.Instance,
                OnePageCountReader.Instance,
                new TemplateUnitPlanner(),
                _ids,
                _timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                new ApprovedPromptBundleCatalog(),
                AiProviderFeaturePolicy.AllowAll);
            var finalization = new TemplateGenerationFinalizationService(
                db,
                _ids,
                _timeProvider,
                batchService);
            return await finalization.CancelAsync(
                new CancelTemplateGenerationBatchCommand(
                    BatchId,
                    batch.Revision,
                    StaffId,
                    IsAdministrator: false,
                    operationId,
                    correlationId),
                CancellationToken.None);
        }

        public Task<AuditEventEntity[]> ReadBatchObservationsAsync() =>
            ReadObservationsAsync(
                TemplateGenerationCostObservationLedger.BatchEventType);

        public Task<AuditEventEntity[]> ReadUnitObservationsAsync() =>
            ReadObservationsAsync(
                TemplateGenerationCostObservationLedger.UnitEventType);

        public async ValueTask DisposeAsync() =>
            await _connection.DisposeAsync();

        private async Task<AuditEventEntity[]> ReadObservationsAsync(
            string eventType)
        {
            await using var db = CreateDb();
            return await db.AuditEvents
                .AsNoTracking()
                .Where(item => item.EventType == eventType)
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.Id)
                .ToArrayAsync();
        }

        private OokiGraderDbContext CreateDb() => new(_dbOptions);

        private BackgroundJobEntity Job(string jobId, string state)
        {
            var now = _timeProvider.GetUtcNow();
            return new BackgroundJobEntity
            {
                Id = jobId,
                Type = TemplateGenerationBatchService.UnitJobType,
                SchemaVersion = TemplateGenerationBatchService.UnitJobSchemaVersion,
                DeduplicationKey = $"cost-observation:{jobId}",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    unitId = UnitId,
                    batchId = BatchId,
                    generationProfileHash = _profile.ComputeHash(),
                }),
                State = state,
                MaxAttempts = 8,
                NextAttemptAt = now,
                LeaseOwner = state == "leased" ? "cost-worker" : null,
                LeaseExpiresAt = state == "leased" ? now.AddMinutes(10) : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        private AiRequestEntity Request(
            string requestId,
            string jobId,
            string runName,
            string state,
            string? retryOfRequestId)
        {
            var now = _timeProvider.GetUtcNow();
            return new AiRequestEntity
            {
                Id = requestId,
                RequestKey = $"template_unit_run_{jobId}_{runName}",
                AiTaskProfileId = TaskProfileId,
                TaskProfileRevision = 1,
                Purpose = AiTaskTypes.TemplateExtraction,
                EntityType = "template_generation_unit",
                EntityId = UnitId,
                EntityRevision = 1,
                InputManifestHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(runName)))
                    .ToLowerInvariant(),
                AttemptNumber = 1,
                RetryOfAiRequestId = retryOfRequestId,
                State = state,
                DispatchAttempt = 1,
                CreatedAt = now,
                UpdatedAt = now,
                DispatchedAt = now,
                CompletedAt = state == "dispatching" ? null : now,
            };
        }

        private AiBudgetReservationEntity Reservation(
            string reservationId,
            string requestId,
            string state,
            long actualUsdMicros)
        {
            var now = _timeProvider.GetUtcNow();
            return new AiBudgetReservationEntity
            {
                Id = reservationId,
                AiRequestId = requestId,
                UsageDay = DateOnly.FromDateTime(now.UtcDateTime),
                UsageMonth = now.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                ReservedUsdMicros = actualUsdMicros,
                ActualUsdMicros = state == "settled" ? actualUsdMicros : 0,
                State = state,
                CreatedAt = now,
                SettledAt = state == "settled" ? now : null,
            };
        }

        public sealed record DispatchSettlement(
            string RequestState,
            bool PossibleDuplicate,
            string? ErrorCode,
            string ReservationState,
            long ReservedUsdMicros,
            long ActualUsdMicros,
            DateTimeOffset? SettledAt);
    }

    private sealed class CostMetricCollector : IDisposable
    {
        private readonly object _gate = new();
        private readonly MeterListener _listener = new();
        private readonly List<CostMeasurement> _measurements = [];

        public CostMetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TemplateGenerationMetrics.MeterName
                    && instrument.Name is
                        "ookigrader.template_generation.ai_cost_per_unit"
                        or "ookigrader.template_generation.ai_cost_per_batch")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) =>
                {
                    var copiedTags = tags.ToArray().ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.Ordinal);
                    lock (_gate)
                    {
                        _measurements.Add(new CostMeasurement(
                            instrument.Name,
                            value,
                            copiedTags));
                    }
                });
            _listener.Start();
        }

        public IReadOnlyList<CostMeasurement> CostMeasurements
        {
            get
            {
                lock (_gate)
                {
                    return _measurements.ToArray();
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record CostMeasurement(
        string InstrumentName,
        long Value,
        IReadOnlyDictionary<string, object?> Tags);

    private sealed class UnusedContentStore : IContentStore
    {
        public static UnusedContentStore Instance { get; } = new();

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class OnePageCountReader : IPdfPageCountReader
    {
        public static OnePageCountReader Instance { get; } = new();

        public Task<int> GetPageCountAsync(
            Stream source,
            int maximumPages,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
