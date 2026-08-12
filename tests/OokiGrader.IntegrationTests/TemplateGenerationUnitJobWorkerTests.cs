using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;
using OokiGrader.Preprocessing;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationUnitJobWorkerTests
{
    private static readonly JsonSerializerOptions WorkerJsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task ExpiredLeaseRecoversAmbiguousDispatchWithoutProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync();
        const long reservedUsdMicros = 37;
        await fixture.SeedExpiredDispatchingRunAsync(reservedUsdMicros);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Empty(fixture.Provider.Requests);

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var batch = await db.TemplateGenerationBatches.AsNoTracking().SingleAsync();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();

        Assert.Equal("failed", request.State);
        Assert.True(request.PossibleDuplicate);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", request.ErrorCode);
        Assert.Equal("recovered_dispatching_request", request.SafeErrorDetail);
        Assert.NotNull(request.CompletedAt);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(reservedUsdMicros, reservation.ReservedUsdMicros);
        Assert.Equal(reservedUsdMicros, reservation.ActualUsdMicros);
        Assert.NotNull(reservation.SettledAt);
        Assert.Equal(TemplateGenerationUnitStatus.Failed, unit.Status);
        Assert.Equal(TemplateGenerationBatchStatus.Failed, batch.Status);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", batch.LastErrorCode);
        Assert.Equal("failed", job.State);
        Assert.Equal("AI_PROVIDER_UNAVAILABLE", job.ErrorCode);
        Assert.Equal(job.MaxAttempts, job.AttemptCount);
        Assert.Null(job.LeaseOwner);
        Assert.Null(job.LeaseExpiresAt);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task UprightUnitMakesExactlyOneProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.PageExtractor.Calls);
        Assert.Empty(fixture.PageExtractor.Calls[0]);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Equal(0, unit.OrientationAttemptCount);
        Assert.Equal(1, request.AttemptNumber);
        Assert.Equal("succeeded", request.State);
    }

    [Fact]
    public async Task RotationThenExtractionMakesExactlyTwoProviderCalls()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Rotate,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(2, fixture.PageExtractor.Calls.Count);
        Assert.Empty(fixture.PageExtractor.Calls[0]);
        Assert.Equal(90, Assert.Single(fixture.PageExtractor.Calls[1]).Value);
        Assert.Contains(
            "\"host_applied_requested_rotations\":true",
            fixture.Provider.Requests[1].UserInstruction,
            StringComparison.Ordinal);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var requests = await db.AiRequests
            .AsNoTracking()
            .OrderBy(item => item.AttemptNumber)
            .ToArrayAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Extracted, unit.Status);
        Assert.Equal(1, unit.OrientationAttemptCount);
        Assert.Equal([1, 2], requests.Select(item => item.AttemptNumber));
        Assert.Equal(requests[0].Id, requests[1].RetryOfAiRequestId);
        Assert.All(requests, item => Assert.Equal("succeeded", item.State));
    }

    [Fact]
    public async Task SecondRotationRequestFailsWithoutThirdProviderCall()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Rotate,
            ProviderAction.Rotate);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(2, fixture.PageExtractor.Calls.Count);
        await using var db = await fixture.CreateDbContextAsync();
        var unit = await db.TemplateGenerationUnits.AsNoTracking().SingleAsync();
        var batch = await db.TemplateGenerationBatches.AsNoTracking().SingleAsync();
        var job = await db.BackgroundJobs.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateGenerationUnitStatus.Failed, unit.Status);
        Assert.Equal(TemplateGenerationBatchStatus.Failed, batch.Status);
        Assert.Equal("ORIENTATION_RETRY_EXHAUSTED", batch.LastErrorCode);
        Assert.Equal("failed", job.State);
        Assert.Equal("ORIENTATION_RETRY_EXHAUSTED", job.ErrorCode);
        Assert.Contains(
            "ORIENTATION_RETRY_EXHAUSTED",
            unit.WarningsJson,
            StringComparison.Ordinal);
        Assert.Equal(2, await db.AiRequests.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ManualRetryAfterFirstProviderFailureStartsFreshLinkedRun()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Fail,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        var retried = await fixture.RetryFailedAsync("retry-first-provider-failure");

        var queued = Assert.Single(retried.Units);
        Assert.Equal(TemplateGenerationUnitStatus.Queued, queued.Status);
        Assert.Equal(0, queued.OrientationAttemptCount);
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Equal(2, fixture.Provider.Requests.Count);

        await using var db = await fixture.CreateDbContextAsync();
        var jobs = await db.BackgroundJobs.AsNoTracking().ToArrayAsync();
        var requests = await db.AiRequests
            .AsNoTracking()
            .OrderBy(item => item.Id)
            .ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        Assert.Equal(2, requests.Length);
        var failed = Assert.Single(requests, item => item.State == "failed");
        var succeeded = Assert.Single(requests, item => item.State == "succeeded");
        Assert.Equal(failed.Id, succeeded.RetryOfAiRequestId);
        Assert.All(jobs, job => Assert.InRange(
            CountRequestsForRun(job.Id, requests),
            1,
            2));
        Assert.Equal(
            TemplateGenerationUnitStatus.Extracted,
            await db.TemplateGenerationUnits
                .AsNoTracking()
                .Select(item => item.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task ManualRetryAfterSecondProviderFailureGetsTwoNewCallsAtMost()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Rotate,
            ProviderAction.Fail,
            ProviderAction.Rotate,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await using (var failedDb = await fixture.CreateDbContextAsync())
        {
            var failedUnit = await failedDb.TemplateGenerationUnits
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(1, failedUnit.OrientationAttemptCount);
            Assert.NotEqual("[]", failedUnit.AppliedRotationsJson);
        }

        var retried = await fixture.RetryFailedAsync("retry-second-provider-failure");
        var queued = Assert.Single(retried.Units);
        Assert.Equal(0, queued.OrientationAttemptCount);
        Assert.Empty(queued.AppliedRotations.EnumerateArray());
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Equal(4, fixture.Provider.Requests.Count);

        await using var db = await fixture.CreateDbContextAsync();
        var jobs = await db.BackgroundJobs.AsNoTracking().ToArrayAsync();
        var requests = await db.AiRequests.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, jobs.Length);
        Assert.Equal(4, requests.Length);
        Assert.All(jobs, job => Assert.Equal(
            2,
            CountRequestsForRun(job.Id, requests)));

        var failedJob = Assert.Single(jobs, job => job.State == "cancelled");
        var succeededJob = Assert.Single(jobs, job => job.State == "succeeded");
        var failedRun = RequestsForRun(failedJob.Id, requests);
        var succeededRun = RequestsForRun(succeededJob.Id, requests);
        Assert.Equal([1, 2], failedRun.Select(item => item.AttemptNumber));
        Assert.Equal([1, 2], succeededRun.Select(item => item.AttemptNumber));
        Assert.Equal(failedRun[1].Id, succeededRun[0].RetryOfAiRequestId);
        Assert.Equal(succeededRun[0].Id, succeededRun[1].RetryOfAiRequestId);
        Assert.Equal(
            TemplateGenerationUnitStatus.Extracted,
            await db.TemplateGenerationUnits
                .AsNoTracking()
                .Select(item => item.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task AuditAndFinalCheckDoNotTriggerClassificationCalls()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(AiTaskTypes.TemplateExtraction, providerRequest.TaskType);
        Assert.Equal("template-extract-v2.0.0", providerRequest.PromptVersion);
        Assert.Equal("template_extract_v5", providerRequest.SchemaVersion);
        await using var db = await fixture.CreateDbContextAsync();
        Assert.Equal(
            TemplateGenerationBatchStatus.NeedsFinalCheck,
            await db.TemplateGenerationBatches
                .AsNoTracking()
                .Select(item => item.Status)
                .SingleAsync());
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        Assert.Equal("template_generation_unit", request.EntityType);
        Assert.Equal(AiTaskTypes.TemplateExtraction, request.Purpose);
        Assert.Contains(
            await db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateUnitExtracted");
    }

    [Fact]
    public async Task RecreatedBatchReusesDerivedObjectWithUnitScopedProvenance()
    {
        await using var fixture = await WorkerFixture.CreateAsync(
            ProviderAction.Extract,
            ProviderAction.Extract);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await fixture.QueueRecreatedBatchAsync();
        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.False(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var units = await db.TemplateGenerationUnits
            .AsNoTracking()
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .ToArrayAsync();
        var derivedSources = await db.TemplateGenerationDerivedSources
            .AsNoTracking()
            .OrderBy(item => item.UnitId)
            .ToArrayAsync();
        var derivedReferences = await db.FileReferences
            .AsNoTracking()
            .Where(item => item.OwnerType == "template_generation_unit"
                && item.Purpose == "derived_source")
            .OrderBy(item => item.OwnerId)
            .ToArrayAsync();
        var derivedObjects = await db.FileObjects
            .AsNoTracking()
            .Where(item => item.StorageClass
                == ContentStorageClass.TemplateDerived.ToString())
            .ToArrayAsync();

        Assert.Equal(2, units.Length);
        Assert.All(
            units,
            item => Assert.Equal(
                TemplateGenerationUnitStatus.Extracted,
                item.Status));
        Assert.Equal(
            units.Select(item => item.Id).Order(),
            derivedSources.Select(item => item.UnitId).Order());
        Assert.Equal(
            units.Select(item => item.Id).Order(),
            derivedReferences.Select(item => item.OwnerId).Order());
        Assert.Equal(2, derivedSources.Select(item => item.FileReferenceId).Distinct().Count());
        Assert.Single(derivedSources.Select(item => item.DerivedContentSha256).Distinct());
        Assert.Single(derivedObjects);
        Assert.Single(derivedReferences.Select(item => item.FileObjectId).Distinct());
        Assert.Equal(derivedObjects[0].Id, derivedReferences[0].FileObjectId);
        Assert.Equal(2, derivedObjects[0].ReferenceCountCache);
    }

    private enum ProviderAction
    {
        Extract,
        Rotate,
        Fail,
    }

    private static int CountRequestsForRun(
        string jobId,
        IEnumerable<AiRequestEntity> requests) =>
        RequestsForRun(jobId, requests).Length;

    private static AiRequestEntity[] RequestsForRun(
        string jobId,
        IEnumerable<AiRequestEntity> requests) =>
        requests
            .Where(item => item.RequestKey.StartsWith(
                $"template_unit_run_{jobId}_",
                StringComparison.Ordinal))
            .OrderBy(item => item.AttemptNumber)
            .ToArray();

    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly MemoryContentStore _contentStore;
        private readonly IUlidGenerator _ids;
        private readonly TimeProvider _timeProvider;

        private WorkerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            RecordingProvider provider,
            RecordingPageRangeExtractor pageExtractor,
            MemoryContentStore contentStore,
            IUlidGenerator ids,
            TimeProvider timeProvider)
        {
            _connection = connection;
            _services = services;
            _contentStore = contentStore;
            _ids = ids;
            _timeProvider = timeProvider;
            Provider = provider;
            PageExtractor = pageExtractor;
            Worker = services.GetRequiredService<TemplateGenerationUnitJobWorker>();
        }

        public TemplateGenerationUnitJobWorker Worker { get; }
        public RecordingProvider Provider { get; }
        public RecordingPageRangeExtractor PageExtractor { get; }
        public string StaffId { get; private set; } = string.Empty;

        public static async Task<WorkerFixture> CreateAsync(
            params ProviderAction[] actions)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var now = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
            var timeProvider = new FixedTimeProvider(now);
            var contentStore = new MemoryContentStore();
            var pageExtractor = new RecordingPageRangeExtractor();
            var provider = new RecordingProvider(actions);
            var secretStore = new InMemoryAiSecretStore();
            var ids = new UlidGenerator(timeProvider);
            var promptCatalog = new ApprovedPromptBundleCatalog();
            var writeCoordinator = new SemaphoreWriteCoordinator();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(timeProvider);
            services.AddSingleton<IUlidGenerator>(ids);
            services.AddSingleton<IWriteCoordinator>(writeCoordinator);
            services.AddSingleton<IContentStore>(contentStore);
            services.AddSingleton<IPdfPageRangeExtractor>(pageExtractor);
            services.AddSingleton<IAiProviderClient>(provider);
            services.AddSingleton<IAiProviderClientResolver>(
                new AiProviderClientResolver([provider]));
            services.AddSingleton<IAiProviderFeaturePolicy>(
                AiProviderFeaturePolicy.AllowAll);
            services.AddSingleton<IAiPromptBundleCatalog>(promptCatalog);
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton(
                Options.Create(new TemplateGenerationUnitJobWorkerOptions()));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<TemplateGenerationUnitJobWorker>();
            var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            try
            {
                var fixture = new WorkerFixture(
                    connection,
                    serviceProvider,
                    provider,
                    pageExtractor,
                    contentStore,
                    ids,
                    timeProvider);
                await fixture.SeedAsync(
                    contentStore,
                    secretStore,
                    ids,
                    timeProvider,
                    promptCatalog);
                return fixture;
            }
            catch
            {
                await serviceProvider.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync() =>
            _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();

        public async Task<TemplateGenerationBatchSnapshot> RetryFailedAsync(
            string operationId)
        {
            await using var db = await CreateDbContextAsync();
            var batch = await db.TemplateGenerationBatches
                .AsNoTracking()
                .SingleAsync();
            var batchService = new TemplateGenerationBatchService(
                db,
                _contentStore,
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                _ids,
                _timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                _services.GetRequiredService<IAiPromptBundleCatalog>(),
                _services.GetRequiredService<IAiProviderFeaturePolicy>());
            var service = new TemplateGenerationFinalizationService(
                db,
                _ids,
                _timeProvider,
                batchService);
            return await service.RetryAsync(
                new RetryTemplateGenerationBatchCommand(
                    batch.Id,
                    batch.Revision,
                    StaffId,
                    IsAdministrator: false,
                    operationId,
                    CorrelationId: operationId),
                CancellationToken.None);
        }

        public async Task SeedExpiredDispatchingRunAsync(
            long reservedUsdMicros)
        {
            await using var db = await CreateDbContextAsync();
            var now = _timeProvider.GetUtcNow();
            var job = await db.BackgroundJobs.SingleAsync();
            var unit = await db.TemplateGenerationUnits.SingleAsync();
            var batch = await db.TemplateGenerationBatches.SingleAsync();
            var taskProfile = await db.AiTaskProfiles
                .Include(item => item.AiConnection)
                .SingleAsync();
            var source = await db.FileReferences
                .AsNoTracking()
                .Include(item => item.FileObject)
                .SingleAsync(item => item.OwnerType == "upload_session"
                    && item.OwnerId == batch.SourceId
                    && item.Purpose == "template_source");
            var profile = JsonSerializer.Deserialize<TemplateGenerationProfile>(
                    unit.GenerationProfileJson,
                    WorkerJsonOptions)
                ?? throw new InvalidOperationException(
                    "The seeded unit generation profile is missing.");
            var requestId = _ids.NewId();
            var requestKey =
                $"template_unit_run_{job.Id}_1_{requestId}";
            var instruction = TemplateExtractionInstructionBuilder.Build(
                requestKey,
                unit.Id,
                profile,
                rotationsWereApplied: false);
            var derivedBytes = Encoding.UTF8.GetBytes(
                $"derived:{profile.FirstPage}-{profile.LastPage}:");
            var derivedSha256 = Sha256(derivedBytes);
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.TemplateExtraction);
            var canonical = JsonSerializer.Serialize(
                new
                {
                    batchPlanHash = batch.PlanHash,
                    unitId = unit.Id,
                    profileHash = profile.ComputeHash(),
                    profile.FirstPage,
                    profile.LastPage,
                    profile.StepSetIndex,
                    profile.StepVariationIndex,
                    sha256 = source.FileObject.Sha256,
                    derivedSha256,
                    rotations = Array.Empty<AppliedPageRotation>(),
                    instruction.Fingerprint,
                    promptBundleHash = bundle.ContentHash,
                    bundle.PromptVersion,
                    bundle.SchemaVersion,
                    provider = taskProfile.AiConnection.Provider,
                    model = taskProfile.AiConnection.ModelId,
                    preprocessingPipeline =
                        PreprocessingOptions.DefaultPipelineVersion,
                    attemptNumber = 1,
                },
                WorkerJsonOptions);

            job.State = "leased";
            job.LeaseOwner = "crashed-worker";
            job.LeaseExpiresAt = now.AddSeconds(-1);
            job.AttemptCount = job.MaxAttempts;
            job.StartedAt = now.AddMinutes(-1);
            job.UpdatedAt = now.AddMinutes(-1);
            unit.Status = TemplateGenerationUnitStatus.Generating;
            db.AiRequests.Add(new AiRequestEntity
            {
                Id = requestId,
                RequestKey = requestKey,
                AiTaskProfileId = taskProfile.Id,
                TaskProfileRevision = taskProfile.Revision,
                Purpose = AiTaskTypes.TemplateExtraction,
                EntityType = "template_generation_unit",
                EntityId = unit.Id,
                EntityRevision = unit.Revision,
                InputManifestHash = Sha256(Encoding.UTF8.GetBytes(canonical)),
                AttemptNumber = 1,
                State = "dispatching",
                DispatchAttempt = 1,
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now.AddMinutes(-1),
                DispatchedAt = now.AddMinutes(-1),
            });
            db.AiBudgetReservations.Add(new AiBudgetReservationEntity
            {
                Id = _ids.NewId(),
                AiRequestId = requestId,
                UsageDay = DateOnly.FromDateTime(now.UtcDateTime),
                UsageMonth = $"{now.Year:0000}-{now.Month:00}",
                ReservedUsdMicros = reservedUsdMicros,
                ActualUsdMicros = 0,
                State = "reserved",
                CreatedAt = now.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        public async Task QueueRecreatedBatchAsync()
        {
            await using var db = await CreateDbContextAsync();
            var source = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(item => item.Purpose == "template_source");
            var service = new TemplateGenerationBatchService(
                db,
                _services.GetRequiredService<IContentStore>(),
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                _services.GetRequiredService<IUlidGenerator>(),
                _services.GetRequiredService<TimeProvider>(),
                Options.Create(new TemplateGenerationBatchOptions()),
                _services.GetRequiredService<IAiPromptBundleCatalog>(),
                _services.GetRequiredService<IAiProviderFeaturePolicy>());
            var created = await service.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    source.Id,
                    TestType.Hop,
                    "算数",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: source.Revision,
                    source.CreatedByStaffUserId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-recreate"),
                CancellationToken.None);
            db.ChangeTracker.Clear();
            await service.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    created.Id,
                    created.RowVersion,
                    source.CreatedByStaffUserId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-recreate-generate"),
                CancellationToken.None);
        }

        private async Task SeedAsync(
            MemoryContentStore contentStore,
            InMemoryAiSecretStore secretStore,
            UlidGenerator ids,
            TimeProvider timeProvider,
            ApprovedPromptBundleCatalog promptCatalog)
        {
            await using var db = await CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            var now = timeProvider.GetUtcNow();
            var staffId = ids.NewId();
            StaffId = staffId;
            var uploadId = ids.NewId();
            var connectionId = ids.NewId();
            var sourceBytes = Encoding.UTF8.GetBytes("fixture-pdf-source");
            var sourceHash = Sha256(sourceBytes);
            contentStore.Add(sourceHash, sourceBytes);
            var secretReference = await secretStore.WriteAsync(
                connectionId,
                1,
                "fixture-provider-key".AsMemory());
            var bundle = promptCatalog.GetRequired(AiTaskTypes.TemplateExtraction);
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "unit.worker",
                UsernameNormalized = "unit.worker",
                DisplayName = "生成担当",
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
                Id = uploadId,
                CreatedByStaffUserId = staffId,
                Purpose = "template_source",
                DestinationType = "template_source",
                OriginalFileName = "HOP算数_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = sourceBytes.Length,
                CurrentBytes = sourceBytes.Length,
                FinalSha256 = sourceHash,
                IncomingRelativePath = "incoming/source.part",
                State = "completed",
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            var fileObjectId = ids.NewId();
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = sourceHash,
                Bytes = sourceBytes.Length,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = $"template/source/{sourceHash}.pdf",
                StorageClass = ContentStorageClass.TemplateSource.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = ids.NewId(),
                FileObjectId = fileObjectId,
                OwnerType = "upload_session",
                OwnerId = uploadId,
                Purpose = "template_source",
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = AiProviderCatalog.GeminiEndpointProfile,
                ModelId = "gemini-3.5-flash-lite",
                SecretReference = secretReference.Value,
                KeyFingerprint = new string('a', 64),
                CredentialRevision = 1,
                TimeoutSeconds = 75,
                ConcurrencyLimit = 2,
                State = "active",
                LastCapabilityProbeState = "passed",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = ids.NewId(),
                Name = "Template unit fixture",
                TaskType = AiTaskTypes.TemplateExtraction,
                AiConnectionId = connectionId,
                ConnectionRevision = 1,
                ModelId = "gemini-3.5-flash-lite",
                ProcessingStrategy = "queued_standard",
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "medium",
                MediaResolution = "high",
                MaxOutputTokens = 8_192,
                ConcurrencyLimit = 1,
                ApprovalState = "production_approved",
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var batchService = new TemplateGenerationBatchService(
                db,
                contentStore,
                new OnePageCountReader(),
                new TemplateUnitPlanner(),
                ids,
                timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                promptCatalog,
                AiProviderFeaturePolicy.AllowAll);
            var created = await batchService.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    uploadId,
                    TestType.Hop,
                    "算数",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: 1,
                    staffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-create"),
                CancellationToken.None);
            db.ChangeTracker.Clear();
            await batchService.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    created.Id,
                    created.RowVersion,
                    staffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "unit-worker-generate"),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingProvider : IAiProviderClient
    {
        private readonly Queue<ProviderAction> _actions;

        public RecordingProvider(IEnumerable<ProviderAction> actions)
        {
            _actions = new Queue<ProviderAction>(actions);
        }

        public string Provider => AiProviders.GeminiDirect;
        public List<AiProviderRequest> Requests { get; } = [];

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request with
            {
                Media = request.Media.Select(item => item with
                {
                    Bytes = item.Bytes.ToArray(),
                }).ToArray(),
            });
            var action = _actions.Count == 0
                ? throw new InvalidOperationException("Unexpected provider call.")
                : _actions.Dequeue();
            if (action == ProviderAction.Fail)
            {
                throw new AiProviderException(
                    AiFailureKind.TransientProvider,
                    "fixture_provider_unavailable",
                    isTransient: true);
            }

            return Task.FromResult(CreateResponse(connection, request, action));
        }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static AiProviderResponse CreateResponse(
            AiConnectionSettings connection,
            AiProviderRequest request,
            ProviderAction action)
        {
            var contractStart = request.UserInstruction.LastIndexOf(
                "{\"schema_version\"",
                StringComparison.Ordinal);
            using var contract = JsonDocument.Parse(
                request.UserInstruction[contractStart..]);
            var requestKey = contract.RootElement
                .GetProperty("request_key")
                .GetString()!;
            var source = contract.RootElement.GetProperty("sources")[0];
            var sourceId = source.GetProperty("source_id").GetString()!;
            var pageIds = source.GetProperty("pages")
                .EnumerateArray()
                .Select(item => item.GetProperty("page_id").GetString()!)
                .ToArray();
            var orientation = pageIds.Select(pageId => new
            {
                page_id = pageId,
                clockwise_degrees_to_upright =
                    action == ProviderAction.Rotate ? 90 : 0,
                confidence = 0.99,
            }).ToArray();
            object[] pages = action == ProviderAction.Rotate
                ? []
                :
                [
                    new
                    {
                        source_id = sourceId,
                        page_number = 1,
                        detected_answer_slot_count = 1,
                        questions = new[]
                        {
                            new
                            {
                                source_key = "page-1-slot-1",
                                display_label = "1",
                                question_text = "1 + 1 はいくつですか。",
                                answer_slot_ordinal = 1,
                                answer_slot_count = 1,
                                filled_answer_removed = true,
                                is_embedded_fill_blank = false,
                                question_type = "numeric",
                                expected_answer = "2",
                                answer_provenance = "ai_proposed",
                                answer_source = (object?)null,
                                accepted_variants = Array.Empty<string>(),
                                suggested_points_milli = 1_000,
                                allow_non_kanji_suggestion = false,
                                requires_complete_answer_suggestion = false,
                                answer_order_insensitive_suggestion = false,
                                requires_teacher_answer = false,
                                confidence = 0.99,
                                warnings = Array.Empty<string>(),
                            },
                        },
                    },
                ];
            var root = JsonSerializer.SerializeToElement(new
            {
                schema_version = "template_extract_v5",
                request_key = requestKey,
                action = action == ProviderAction.Rotate ? "rotate" : "extract",
                orientation = new { pages = orientation },
                metadata = action == ProviderAction.Rotate
                    ? null
                    : new
                    {
                        printed_test_name = "HOP算数 第1回",
                        printed_grade_label = "小学4年",
                        grade_confidence = 0.99,
                        warnings = Array.Empty<string>(),
                    },
                pages,
            });
            return new AiProviderResponse(
                AiProviders.GeminiDirect,
                connection.ModelId,
                connection.ModelId,
                $"fixture-response-{requestKey}",
                "STOP",
                root,
                new AiUsage(100, 0, 100, 0, 200),
                TimeSpan.FromMilliseconds(10));
        }
    }

    private sealed class RecordingPageRangeExtractor : IPdfPageRangeExtractor
    {
        public List<IReadOnlyDictionary<int, int>> Calls { get; } = [];

        public Task<DerivedPdfResult> ExtractAsync(
            Stream source,
            string sourceName,
            int firstPage,
            int lastPage,
            IReadOnlyDictionary<int, int> rotations,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new Dictionary<int, int>(rotations));
            var rotationText = string.Join(
                ",",
                rotations.OrderBy(item => item.Key)
                    .Select(item => $"{item.Key}:{item.Value}"));
            var bytes = Encoding.UTF8.GetBytes(
                $"derived:{firstPage}-{lastPage}:{rotationText}");
            var applied = rotations.OrderBy(item => item.Key)
                .Select(item => new AppliedPageRotation(
                    $"page-{item.Key}",
                    item.Key,
                    item.Value,
                    "fixture",
                    0.99))
                .ToArray();
            return Task.FromResult(new DerivedPdfResult(
                bytes,
                Sha256(bytes),
                lastPage - firstPage + 1,
                firstPage,
                lastPage,
                applied));
        }
    }

    private sealed class MemoryContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);

        public void Add(string sha256, byte[] content)
        {
            _content[sha256] = content.ToArray();
        }

        public async Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var bytes = buffer.ToArray();
            var hash = Sha256(bytes);
            var deduplicated = _content.ContainsKey(hash);
            _content[hash] = bytes;
            return new ContentWriteResult(
                new ContentObjectLocator(
                    storageClass,
                    hash,
                    bytes.LongLength,
                    verifiedExtension),
                $"{storageClass}/{hash}.{verifiedExtension}",
                deduplicated);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<Stream>(
                new MemoryStream(_content[locator.Sha256], writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_content.ContainsKey(locator.Sha256));

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            _content.Remove(locator.Sha256);
            return Task.CompletedTask;
        }
    }

    private sealed class OnePageCountReader : IPdfPageCountReader
    {
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

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
