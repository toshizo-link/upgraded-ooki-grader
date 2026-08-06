using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

public sealed class AiInitialGradingJobWorkerTests
{
    private static readonly string[] KanjiScript = ["kanji"];

    [Fact]
    public async Task RoutesAnOpenRouterProfileToTheOpenRouterClient()
    {
        const string model = "google/gemini-3.1-flash-lite";
        await using var fixture = await AiWorkerFixture.CreateAsync(
            responseFactory: request => CreateResponse(request) with
            {
                Provider = AiProviders.OpenRouter,
                RequestedModel = model,
                ActualModel = model,
                FinishReason = "stop",
                RoutedProvider = "Google",
                Usage = new AiUsage(
                    PromptTokens: 240,
                    CachedTokens: 0,
                    OutputTokens: 12,
                    ThinkingTokens: 4,
                    TotalTokens: 256,
                    ProviderCostUsdMicros: 321),
            },
            providerId: AiProviders.OpenRouter,
            modelId: model);
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var selectedConnection = Assert.Single(fixture.Provider.Connections);
        Assert.Equal(AiProviders.OpenRouter, selectedConnection.Provider);
        Assert.Equal(AiProviderCatalog.OpenRouterBaseAddress,
            selectedConnection.BaseAddress);
        Assert.Equal(model, selectedConnection.ModelId);
        Assert.Single(fixture.Provider.Requests);

        await using var db = await fixture.CreateDbContextAsync();
        var run = await db.GradingRuns
            .AsNoTracking()
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        var usage = await db.AiUsage.AsNoTracking().SingleAsync();
        Assert.Equal(AiProviders.OpenRouter, run.Provider);
        Assert.Equal(model, run.Model);
        Assert.Equal(AiProviders.OpenRouter, usage.RequestedProvider);
        Assert.Equal(model, usage.RequestedModel);
        Assert.Equal("Google", usage.ActualProvider);
        Assert.Equal(model, usage.ActualModel);
        Assert.Equal(12, usage.OutputTokens);
        Assert.Equal(4, usage.ThinkingTokens);
        Assert.Equal(321, usage.EstimatedUsdMicros);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(321, reservation.ActualUsdMicros);
    }

    [Fact]
    public async Task DisabledOpenRouterFeatureBlocksBeforeMediaOrDispatch()
    {
        const string model = "google/gemini-3.1-flash-lite";
        await using var fixture = await AiWorkerFixture.CreateAsync(
            providerId: AiProviders.OpenRouter,
            modelId: model,
            providerFeaturePolicy: new AiProviderFeaturePolicy(
                geminiDirectEnabled: true,
                openRouterEnabled: false));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.ContentStore.OpenedHashes);
        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("failed", request.State);
        Assert.Equal("ai_provider_feature_disabled", request.ErrorCode);
        Assert.Equal("blocked", job.State);
    }

    [Fact]
    public async Task OpenRouterProviderOutageIsRetriedByFailureKind()
    {
        const string model = "google/gemini-3.1-flash-lite";
        await using var fixture = await AiWorkerFixture.CreateAsync(
            _ => throw new AiProviderException(
                AiFailureKind.TransientProvider,
                "openrouter_provider_unavailable",
                isTransient: true,
                retryAfter: TimeSpan.FromSeconds(7)),
            providerId: AiProviders.OpenRouter,
            modelId: model);
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("retry_waiting", request.State);
        Assert.Equal("openrouter_provider_unavailable", request.ErrorCode);
        Assert.Equal("retry_waiting", job.State);
        Assert.Equal("openrouter_provider_unavailable", job.ErrorCode);
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task MissingUsageSettlesAtReservedCostInsteadOfZero()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            request => CreateResponse(request) with
            {
                Usage = new AiUsage(null, null, null, null, null),
            });
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var usage = await db.AiUsage
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        Assert.True(reservation.ReservedUsdMicros > 0);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            reservation.ActualUsdMicros);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            usage.EstimatedUsdMicros);
    }

    [Fact]
    public async Task OpenRouterMissingAuthoritativeCostKeepsReservation()
    {
        const string model = "google/gemini-3.1-flash-lite";
        await using var fixture = await AiWorkerFixture.CreateAsync(
            request => CreateResponse(request) with
            {
                Provider = AiProviders.OpenRouter,
                RequestedModel = model,
                ActualModel = model,
                FinishReason = "stop",
                Usage = new AiUsage(
                    PromptTokens: 240,
                    CachedTokens: 0,
                    OutputTokens: 12,
                    ThinkingTokens: 4,
                    TotalTokens: 256,
                    ProviderCostUsdMicros: null),
            },
            providerId: AiProviders.OpenRouter,
            modelId: model);
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var usage = await db.AiUsage
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        Assert.True(reservation.ReservedUsdMicros > 0);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            reservation.ActualUsdMicros);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            usage.EstimatedUsdMicros);
    }

    [Fact]
    public async Task SendsOnlyDisclosureApprovedAnswerCropsOutsideWriteLock()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(includePrivateNameCrop: true);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        Assert.Single(providerRequest.Media);
        Assert.Equal(
            seeded.AnswerCropSha256,
            providerRequest.Media[0].Sha256);
        Assert.DoesNotContain(
            seeded.PrivateNameCropSha256,
            fixture.ContentStore.OpenedHashes);
        Assert.False(fixture.Provider.ObservedInsideWriteCoordinator);
        Assert.False(fixture.ContentStore.ObservedInsideWriteCoordinator);
        Assert.DoesNotContain(
            "name_crop",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistsLocalTotalsAndRequiresTeacherReviewForEveryPilotItem()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var run = await db.GradingRuns
            .AsNoTracking()
            .Include(item => item.QuestionResults)
                .ThenInclude(item => item.Revisions)
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var usage = await db.AiUsage
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("needs_grade_review", submission.State);
        Assert.Equal(run.Id, submission.CurrentGradingRunId);
        Assert.Null(submission.FinalizedAt);
        Assert.Equal("needs_grade_review", run.State);
        Assert.Equal(AiProviders.GeminiDirect, run.Provider);
        Assert.Equal(AiInitialGradingJobWorker.ModelId, run.Model);
        Assert.Equal(AiInitialGradingJobWorker.PipelineVersion, run.PipelineVersion);
        Assert.Equal(1_000, run.EarnedPointsMilli);
        Assert.Equal(1_000, run.PossiblePointsMilli);
        Assert.Null(run.FinalizedAt);

        var result = Assert.Single(run.QuestionResults);
        Assert.Equal(seeded.QuestionId, result.QuestionId);
        Assert.Equal("東京", result.TranscribedAnswer);
        Assert.Equal(1_000, result.ProposedPointsMilli);
        Assert.True(result.ReviewRequired);
        Assert.Equal("pending", result.ReviewStatus);
        var revision = Assert.Single(result.Revisions);
        Assert.Equal(1_000, revision.AwardedPointsMilli);
        Assert.Equal(result.CurrentRevisionId, revision.Id);
        Assert.Equal(
            run.EarnedPointsMilli,
            run.QuestionResults.Sum(item =>
                item.Revisions.Single().AwardedPointsMilli));

        Assert.Equal("succeeded", request.State);
        Assert.False(request.PossibleDuplicate);
        Assert.Equal("response-1", request.ProviderResponseId);
        Assert.Equal(240, usage.InputTokens);
        Assert.Equal(16, usage.OutputTokens);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(usage.EstimatedUsdMicros, reservation.ActualUsdMicros);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(10_000, job.ProgressBasisPoints);
    }

    [Fact]
    public async Task RecomputesContradictoryDeterministicItemWithoutRejectingPaper()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            request => CreateResponse(
                request,
                proposedPointsMilli: 1_000,
                proposedOutcome: "incorrect"));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var run = await db.GradingRuns
            .AsNoTracking()
            .Include(item => item.QuestionResults)
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);

        Assert.Equal("succeeded", request.State);
        Assert.Null(request.ErrorCode);
        Assert.NotNull(request.ValidatedResponseJson);
        Assert.Equal("needs_grade_review", submission.State);
        Assert.Equal("succeeded", job.State);
        var result = Assert.Single(run.QuestionResults);
        Assert.Equal(1_000, result.ProposedPointsMilli);
        Assert.Equal("correct", result.Outcome);
        Assert.Equal("ai_deterministic_recomputed", result.ReasonCode);
        Assert.True(result.ReviewRequired);
        Assert.Single(
            await db.AiUsage
                .AsNoTracking()
                .Where(item => item.AiRequestId == request.Id)
                .ToListAsync());
    }

    [Fact]
    public async Task ActiveHardBudgetBlocksBeforeReadingCropsOrCallingProvider()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            activeBudget: true,
            dailyHardUsdMicros: 1,
            monthlyHardUsdMicros: 1);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("budget_blocked", request.State);
        Assert.Equal("ai_budget_hard_limit", request.ErrorCode);
        Assert.Equal("awaiting_grading", submission.State);
        Assert.Equal("blocked", job.State);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.ContentStore.OpenedHashes);
        Assert.Empty(await db.AiBudgetReservations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AmbiguousTimeoutIsNeverBlindlyRetried()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            _ => throw new AiProviderException(
                AiFailureKind.Timeout,
                "gemini_timeout",
                isTransient: true));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());
        await fixture.RequeueAsync(seeded.JobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);

        Assert.Single(fixture.Provider.Requests);
        Assert.True(request.PossibleDuplicate);
        Assert.Equal("failed", request.State);
        Assert.Equal("ai_dispatch_outcome_unknown", request.ErrorCode);
        Assert.Equal(1, request.DispatchAttempt);
        Assert.Equal("blocked", job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            reservation.ActualUsdMicros);
    }

    [Fact]
    public async Task BatchProfileStagesImmutableRequestWithoutStandardDispatch()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            enableBatch: true);
        var seeded = await fixture.SeedAsync(
            processingStrategy: "gemini_batch");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var mapping = await db.AiBatchRequests
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var originalJob = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("prepared", request.State);
        Assert.Equal("ready", mapping.State);
        Assert.Equal(64, mapping.CompatibilityKey.Length);
        Assert.Equal(64, mapping.ProviderRequestHash.Length);
        Assert.Equal("succeeded", originalJob.State);
        Assert.Contains(
            db.BackgroundJobs,
            item => item.Type == AiBatchJobWorker.PrepareJobType
                && item.State == "queued");
        Assert.Empty(fixture.Provider.Requests);
        Assert.Single(fixture.BatchProvider!.Requests);
    }

    [Fact]
    public async Task ExpediteSessionBypassesBatchEnabledProfile()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            enableBatch: true);
        _ = await fixture.SeedAsync(
            processingStrategy: "gemini_batch",
            sessionPriority: "expedite");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests.AsNoTracking().SingleAsync();
        Assert.Equal("succeeded", request.State);
        Assert.Empty(db.AiBatchRequests);
        Assert.Single(fixture.Provider.Requests);
        Assert.Empty(fixture.BatchProvider!.Requests);
    }

    [Fact]
    public async Task AdministrativeExpeditePayloadBypassesBatchProfile()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            enableBatch: true);
        _ = await fixture.SeedAsync(
            processingStrategy: "gemini_batch",
            sessionPriority: "economy",
            forceExpedite: true);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        Assert.Empty(db.AiBatchRequests);
        Assert.Single(fixture.Provider.Requests);
        Assert.Empty(fixture.BatchProvider!.Requests);
    }

    [Fact]
    public async Task StoredBatchResponseUsesNormalValidatorAndCreatesReviewRun()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            enableBatch: true);
        var seeded = await fixture.SeedAsync(
            processingStrategy: "gemini_batch");
        Assert.True(await fixture.Worker.ProcessNextAsync());
        await fixture.StoreBatchResponseAndQueueApplyAsync(
            seeded,
            actualModel: AiInitialGradingJobWorker.ModelId + "-001");

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var run = await db.GradingRuns
            .AsNoTracking()
            .Include(item => item.QuestionResults)
            .SingleAsync(item => item.SubmissionId == seeded.SubmissionId);
        Assert.Equal("succeeded", request.State);
        Assert.Equal(AiInitialGradingJobWorker.ModelId + "-001", run.Model);
        Assert.Equal("needs_grade_review", run.State);
        Assert.Single(run.QuestionResults);
        Assert.True(run.QuestionResults.Single().ReviewRequired);
        Assert.Single(
            await db.AiUsage
                .AsNoTracking()
                .Where(item => item.AiRequestId == request.Id)
                .ToListAsync());
        Assert.Empty(fixture.Provider.Requests);
    }

    [Fact]
    public async Task RejectsMismatchedProviderResponseMetadata()
    {
        await using var fixture = await AiWorkerFixture.CreateAsync(
            request => CreateResponse(request) with
            {
                RequestedModel = "unexpected-model",
            });
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            "ai_response_metadata_invalid",
            request.ErrorCode);
        Assert.Empty(await db.GradingRuns.AsNoTracking().ToListAsync());
    }

    private static AiProviderResponse CreateResponse(
        AiProviderRequest request,
        long proposedPointsMilli = 1_000,
        string proposedOutcome = "correct")
    {
        var questionId = ExtractQuestionId(request.UserInstruction);
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = request.RequestKey,
                results = new[]
                {
                    new
                    {
                        question_id = questionId,
                        transcription = "東京",
                        script_observed = KanjiScript,
                        legibility = "clear",
                        blank = false,
                        proposed_outcome = proposedOutcome,
                        proposed_points_milli = proposedPointsMilli,
                        kanji_observation = "not_applicable",
                        reason_code = "exact_match",
                        confidence = 0.99,
                        review_recommended = false,
                        bounded_explanation = "Matches the supplied answer.",
                    },
                },
                missing_question_ids = Array.Empty<string>(),
                unexpected_content = false,
            }));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            AiInitialGradingJobWorker.ModelId,
            AiInitialGradingJobWorker.ModelId,
            "response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(
                PromptTokens: 240,
                CachedTokens: 0,
                OutputTokens: 16,
                ThinkingTokens: 0,
                TotalTokens: 256),
            TimeSpan.FromMilliseconds(25));
    }

    private static string ExtractQuestionId(string instruction)
    {
        var start = instruction.IndexOf('{');
        using var document = JsonDocument.Parse(instruction[start..]);
        return document.RootElement
            .GetProperty("questions")[0]
            .GetProperty("question_id")
            .GetString()!;
    }

    private sealed class AiWorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly InMemoryAiSecretStore _secretStore;
        private readonly string _connectionId;
        private readonly string _secretReference;
        private readonly string _providerId;
        private readonly string _modelId;

        private AiWorkerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            InMemoryAiSecretStore secretStore,
            string connectionId,
            string secretReference,
            BoundaryWriteCoordinator writeCoordinator,
            FakeContentStore contentStore,
            FakeAiProvider provider,
            FakeBatchProvider? batchProvider,
            string providerId,
            string modelId)
        {
            _connection = connection;
            _services = services;
            _secretStore = secretStore;
            _connectionId = connectionId;
            _secretReference = secretReference;
            _providerId = providerId;
            _modelId = modelId;
            WriteCoordinator = writeCoordinator;
            ContentStore = contentStore;
            Provider = provider;
            BatchProvider = batchProvider;
            Worker = services.GetRequiredService<AiInitialGradingJobWorker>();
        }

        public AiInitialGradingJobWorker Worker { get; }
        public BoundaryWriteCoordinator WriteCoordinator { get; }
        public FakeContentStore ContentStore { get; }
        public FakeAiProvider Provider { get; }
        public FakeBatchProvider? BatchProvider { get; }

        public static async Task<AiWorkerFixture> CreateAsync(
            Func<AiProviderRequest, AiProviderResponse>? responseFactory = null,
            bool enableBatch = false,
            string providerId = AiProviders.GeminiDirect,
            string modelId = AiInitialGradingJobWorker.ModelId,
            IAiProviderFeaturePolicy? providerFeaturePolicy = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var boundary = new BoundaryProbe();
            var writeCoordinator = new BoundaryWriteCoordinator(boundary);
            var contentStore = new FakeContentStore(boundary);
            var provider = new FakeAiProvider(
                boundary,
                providerId,
                responseFactory ?? (request => CreateResponse(request)));
            var batchProvider = enableBatch ? new FakeBatchProvider() : null;
            var secretStore = new InMemoryAiSecretStore();
            var connectionId = UlidId.New(DateTimeOffset.UtcNow);
            var secretReference = (await secretStore.WriteAsync(
                connectionId,
                1,
                "test-only-provider-key".AsMemory())).Value;

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IClock>(SystemClock.Instance);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IWriteCoordinator>(writeCoordinator);
            services.AddSingleton<IContentStore>(contentStore);
            services.AddSingleton<IAiProviderClient>(provider);
            if (providerFeaturePolicy is not null)
            {
                services.AddSingleton(providerFeaturePolicy);
            }
            if (batchProvider is not null)
            {
                services.AddSingleton<IAiBatchProviderClient>(batchProvider);
                services.AddSingleton<AiBatchRequestStager>();
            }
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton<IAiPromptBundleCatalog>(
                new ApprovedPromptBundleCatalog());
            services.AddSingleton(
                Options.Create(new AiInitialGradingJobWorkerOptions()));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<AiInitialGradingJobWorker>();
            var serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });
            try
            {
                await using var db = await serviceProvider
                    .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                    .CreateDbContextAsync();
                await db.Database.EnsureCreatedAsync();
                return new AiWorkerFixture(
                    connection,
                    serviceProvider,
                    secretStore,
                    connectionId,
                    secretReference,
                    writeCoordinator,
                    contentStore,
                    provider,
                    batchProvider,
                    providerId,
                    modelId);
            }
            catch
            {
                await serviceProvider.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<OokiGraderDbContext> CreateDbContextAsync()
        {
            return _services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();
        }

        public async Task<SeededAiWorkflow> SeedAsync(
            bool includePrivateNameCrop = false,
            bool activeBudget = false,
            long dailyHardUsdMicros = 1_000_000,
            long monthlyHardUsdMicros = 10_000_000,
            string processingStrategy = "expedite_standard",
            string sessionPriority = "economy",
            bool forceExpedite = false)
        {
            var now = DateTimeOffset.UtcNow;
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.InitialGrading);
            var staffId = UlidId.New(now);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var questionId = UlidId.New(now);
            var answerId = UlidId.New(now);
            var sessionId = UlidId.New(now);
            var submissionId = UlidId.New(now);
            var pageId = UlidId.New(now);
            var answerArtifactId = UlidId.New(now);
            var answerReferenceId = UlidId.New(now);
            var pageReferenceId = UlidId.New(now);
            var thumbnailReferenceId = UlidId.New(now);
            var answerBytes = "answer-only-crop"u8.ToArray();
            var answerHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(answerBytes))
                .ToLowerInvariant();
            ContentStore.Add(answerHash, answerBytes);

            await using var db = await CreateDbContextAsync();
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "AI worker fixture",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = versionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                TargetTotalPointsMilli = 1_000,
                PipelineVersion = "template-v1",
                PublishedByStaffUserId = staffId,
                PublishedAt = now,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Questions.Add(new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = versionId,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = 0,
                DisplayLabel = "Q1",
                QuestionText = "日本の首都を書きなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "transcribe_then_rules",
                MaxPointsMilli = 1_000,
                PointIncrementMilli = 1_000,
                AllowNonKanji = false,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AcceptedAnswers.Add(new AcceptedAnswerEntity
            {
                Id = answerId,
                QuestionId = questionId,
                AnswerText = "東京",
                NormalizedText = "東京",
                VariantType = "canonical",
                TeacherVerified = true,
                AnswerProvenance = "teacher_entered",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                Priority = sessionPriority,
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "grading",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AssignmentEvidenceJson =
                    """{"disposition":"unidentified"}""",
                AttemptNumber = 1,
                CanonicalForSession = false,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "completed-test.png",
                UploadCompletedAt = now,
                PreprocessingPipelineVersion = "submission-normalize-v1",
                PreprocessingManifestHash = new string('c', 64),
                PreprocessingCompletedAt = now,
                PageCount = 1,
                QualitySummaryJson =
                    """{"pipeline":"submission-normalize-v1","status":"accepted"}""",
                CreatedAt = now,
                UpdatedAt = now,
            });
            var fileObject = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = answerHash,
                Bytes = answerBytes.Length,
                VerifiedMime = "image/png",
                Extension = ".png",
                RelativeObjectPath = $"scan/derived/{answerHash}.png",
                StorageClass =
                    ContentStorageClass.ManagedScanDerived.ToString(),
                RetentionClass = "submitted_scan_derived",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = includePrivateNameCrop ? 4 : 3,
            };
            db.FileObjects.Add(fileObject);
            db.FileReferences.AddRange(
                new FileReferenceEntity
                {
                    Id = pageReferenceId,
                    FileObjectId = fileObject.Id,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "normalized_page",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                },
                new FileReferenceEntity
                {
                    Id = thumbnailReferenceId,
                    FileObjectId = fileObject.Id,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "thumbnail",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                },
                new FileReferenceEntity
                {
                    Id = answerReferenceId,
                    FileObjectId = fileObject.Id,
                    OwnerType = "submission_artifact",
                    OwnerId = answerArtifactId,
                    Purpose = "answer_crop",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
            db.SubmissionPages.Add(new SubmissionPageEntity
            {
                Id = pageId,
                SubmissionId = submissionId,
                PageNumber = 1,
                NormalizedFileReferenceId = pageReferenceId,
                ThumbnailFileReferenceId = thumbnailReferenceId,
                WidthPixels = 1_000,
                HeightPixels = 1_400,
                RotationDegrees = 0,
                SourceSha256 = answerHash,
                NormalizedSha256 = answerHash,
                DifferenceHash = "0123456789abcdef",
                PerceptualHash = "0123456789abcdef",
                QualityState = "accepted",
                BlurBasisPoints = 5_000,
                ContrastBasisPoints = 5_000,
                BrightnessBasisPoints = 5_000,
                InkCoverageBasisPoints = 5_000,
                AlignmentState = "not_configured",
                CreatedAt = now,
            });
            db.SubmissionArtifacts.Add(new SubmissionArtifactEntity
            {
                Id = answerArtifactId,
                SubmissionId = submissionId,
                SubmissionPageId = pageId,
                QuestionId = questionId,
                FileReferenceId = answerReferenceId,
                ArtifactType = "answer_crop",
                Ordinal = 0,
                PanelLabel = "Q1",
                InputManifestHash = new string('c', 64),
                WidthPixels = 800,
                HeightPixels = 500,
                ProviderDisclosureAllowed = true,
                CreatedAt = now,
            });

            string? privateHash = null;
            if (includePrivateNameCrop)
            {
                var privateBytes = "private-name-crop"u8.ToArray();
                privateHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(privateBytes))
                    .ToLowerInvariant();
                ContentStore.Add(privateHash, privateBytes);
                var privateObjectId = UlidId.New(now);
                var privateArtifactId = UlidId.New(now);
                var privateReferenceId = UlidId.New(now);
                db.FileObjects.Add(new FileObjectEntity
                {
                    Id = privateObjectId,
                    Sha256 = privateHash,
                    Bytes = privateBytes.Length,
                    VerifiedMime = "image/png",
                    Extension = ".png",
                    RelativeObjectPath = $"scan/derived/{privateHash}.png",
                    StorageClass =
                        ContentStorageClass.ManagedScanDerived.ToString(),
                    RetentionClass = "submitted_scan_derived",
                    ManagedScanBytes = true,
                    State = "available",
                    CreatedAt = now,
                    VerifiedAt = now,
                    ReferenceCountCache = 1,
                });
                db.FileReferences.Add(new FileReferenceEntity
                {
                    Id = privateReferenceId,
                    FileObjectId = privateObjectId,
                    OwnerType = "submission_artifact",
                    OwnerId = privateArtifactId,
                    Purpose = "name_crop",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
                db.SubmissionArtifacts.Add(new SubmissionArtifactEntity
                {
                    Id = privateArtifactId,
                    SubmissionId = submissionId,
                    SubmissionPageId = pageId,
                    FileReferenceId = privateReferenceId,
                    ArtifactType = "name_crop",
                    Ordinal = 0,
                    PanelLabel = "NAME",
                    InputManifestHash = new string('c', 64),
                    WidthPixels = 400,
                    HeightPixels = 100,
                    ProviderDisclosureAllowed = false,
                    CreatedAt = now,
                });
            }

            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = _connectionId,
                Provider = _providerId,
                EndpointProfile = AiProviderCatalog.GetEndpointProfile(
                    _providerId),
                ModelId = _modelId,
                SecretReference = _secretReference,
                KeyFingerprint = "sha256:test",
                CredentialRevision = 1,
                TimeoutSeconds = 30,
                ConcurrencyLimit = 1,
                State = "active",
                LastCapabilityProbeState = "passed",
                LastCapabilityProbeAt = now,
                LastBatchCapabilityProbeState = "passed",
                LastBatchCapabilityProbeAt = now,
                LastBatchCapabilityProbeCredentialRevision = 1,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = UlidId.New(now),
                Name = "Initial grading pilot",
                TaskType = AiTaskTypes.InitialGrading,
                AiConnectionId = _connectionId,
                ConnectionRevision = 1,
                ModelId = _modelId,
                ProcessingStrategy = processingStrategy,
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "minimal",
                MediaResolution = "high",
                MaxOutputTokens = 1_024,
                ConcurrencyLimit = 1,
                ApprovalState = "pilot_approved",
                AccuracyEvaluationId = "fixture-evaluation",
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1,
            });
            db.PricingSnapshots.Add(new PricingSnapshotEntity
            {
                Id = UlidId.New(now),
                Provider = _providerId,
                ModelId = _modelId,
                InputUsdMicrosPerMillionTokens = 250_000,
                OutputUsdMicrosPerMillionTokens = 1_500_000,
                ThinkingUsdMicrosPerMillionTokens = 1_500_000,
                SourceUrl = _providerId == AiProviders.OpenRouter
                    ? "https://openrouter.ai/models"
                    : "https://ai.google.dev/gemini-api/docs/pricing",
                EffectiveAt = now.AddDays(-1),
                CapturedAt = now,
            });
            db.AiBudgetPolicies.Add(new AiBudgetPolicyEntity
            {
                Id = "default",
                DailyWarningUsdMicros = 0,
                DailyHardUsdMicros = dailyHardUsdMicros,
                MonthlyWarningUsdMicros = 0,
                MonthlyHardUsdMicros = monthlyHardUsdMicros,
                UsdToJpyMicros = 150_000_000,
                Active = activeBudget,
                CreatedAt = now,
                UpdatedAt = now,
            });
            var job = new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = AiInitialGradingJobWorker.JobType,
                SchemaVersion = 1,
                DeduplicationKey =
                    $"submission:{submissionId}:gemini-grade:{new string('c', 64)}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    submissionId,
                    templateVersionId = versionId,
                    manifestHash = new string('c', 64),
                    forceExpedite,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.BackgroundJobs.Add(job);
            await db.SaveChangesAsync();

            return new SeededAiWorkflow(
                submissionId,
                job.Id,
                versionId,
                questionId,
                answerHash,
                privateHash);
        }

        public async Task StoreBatchResponseAndQueueApplyAsync(
            SeededAiWorkflow seeded,
            string? actualModel = null)
        {
            var providerRequest = Assert.Single(BatchProvider!.Requests);
            var response = CreateResponse(providerRequest) with
            {
                ActualModel = actualModel
                    ?? AiInitialGradingJobWorker.ModelId,
            };
            var responseJson = response.StructuredOutput.GetRawText();
            var responseHash = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(responseJson)))
                .ToLowerInvariant();
            var now = DateTimeOffset.UtcNow;
            await using var db = await CreateDbContextAsync();
            var request = await db.AiRequests
                .Include(item => item.BatchRequest)
                .SingleAsync(item => item.EntityId == seeded.SubmissionId);
            request.State = "response_ready";
            request.ProviderResponseId = response.ProviderResponseId;
            request.ActualModel = response.ActualModel;
            request.FinishReason = response.FinishReason;
            request.AcceptedResponseHash = responseHash;
            request.ValidatedResponseJson = responseJson;
            request.CompletedAt = now;
            request.UpdatedAt = now;
            request.BatchRequest!.State = "response_ready";
            request.BatchRequest.ProviderResponseId =
                response.ProviderResponseId;
            request.BatchRequest.ResponseJson = responseJson;
            request.BatchRequest.ResponseHash = responseHash;
            request.BatchRequest.ProviderRequestJson = null;
            request.BatchRequest.ProviderRequestBytes = 0;
            request.BatchRequest.CompletedAt = now;
            request.BatchRequest.UpdatedAt = now;
            db.AiUsage.Add(new AiUsageEntity
            {
                Id = UlidId.New(now),
                AiRequestId = request.Id,
                RequestedProvider = AiProviders.GeminiDirect,
                RequestedModel = AiInitialGradingJobWorker.ModelId,
                ActualProvider = response.Provider,
                ActualModel = response.ActualModel,
                InputTokens = response.Usage.PromptTokens,
                CachedTokens = response.Usage.CachedTokens,
                OutputTokens = response.Usage.OutputTokens,
                ThinkingTokens = response.Usage.ThinkingTokens,
                TotalTokens = response.Usage.TotalTokens,
                EstimatedUsdMicros = 7,
                EstimatedJpyMicros = 1_050,
                ProviderRequestId = response.ProviderResponseId,
                MeasuredAt = now,
            });
            var reservation = await db.AiBudgetReservations.SingleAsync(
                item => item.AiRequestId == request.Id);
            reservation.State = "settled";
            reservation.ActualUsdMicros = 7;
            reservation.SettledAt = now;
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = UlidId.New(now),
                Type = AiInitialGradingJobWorker.ApplyJobType,
                SchemaVersion = 1,
                DeduplicationKey =
                    $"ai-request:{request.Id}:apply:{responseHash}",
                Priority = 0,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    submissionId = seeded.SubmissionId,
                    templateVersionId = seeded.TemplateVersionId,
                    manifestHash = new string('c', 64),
                    aiRequestId = request.Id,
                }),
                State = "queued",
                MaxAttempts = 8,
                NextAttemptAt = now.AddMinutes(-1),
                CreatedAt = now,
                UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        public async Task RequeueAsync(string jobId)
        {
            await using var db = await CreateDbContextAsync();
            var job = await db.BackgroundJobs
                .SingleAsync(item => item.Id == jobId);
            job.State = "queued";
            job.ProgressBasisPoints = 0;
            job.CompletedAt = null;
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            job.ErrorCode = null;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            _secretStore.Dispose();
        }
    }

    private sealed record SeededAiWorkflow(
        string SubmissionId,
        string JobId,
        string TemplateVersionId,
        string QuestionId,
        string AnswerCropSha256,
        string? PrivateNameCropSha256);

    private sealed class BoundaryProbe
    {
        private readonly AsyncLocal<int> _depth = new();

        public bool IsInside => _depth.Value > 0;

        public IDisposable Enter()
        {
            _depth.Value++;
            return new Scope(this);
        }

        private sealed class Scope(BoundaryProbe owner) : IDisposable
        {
            public void Dispose()
            {
                owner._depth.Value--;
            }
        }
    }

    private sealed class BoundaryWriteCoordinator(
        BoundaryProbe boundary) : IWriteCoordinator, IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(
                async token =>
                {
                    await operation(token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ExecuteAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var scope = boundary.Enter();
                return await operation(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }

    private sealed class FakeContentStore(BoundaryProbe boundary) : IContentStore
    {
        private readonly Dictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);

        public List<string> OpenedHashes { get; } = [];
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public void Add(string sha256, byte[] content)
        {
            _content.Add(sha256, content);
        }

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            OpenedHashes.Add(locator.Sha256);
            var bytes = _content[locator.Sha256];
            return Task.FromResult<Stream>(
                new MemoryStream(bytes, writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_content.ContainsKey(locator.Sha256));
        }

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeAiProvider(
        BoundaryProbe boundary,
        string provider,
        Func<AiProviderRequest, AiProviderResponse> responseFactory)
        : IAiProviderClient
    {
        public string Provider { get; } = provider;
        public List<AiConnectionSettings> Connections { get; } = [];
        public List<AiProviderRequest> Requests { get; } = [];
        public bool ObservedInsideWriteCoordinator { get; private set; }

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedInsideWriteCoordinator |= boundary.IsInside;
            Connections.Add(connection);
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeBatchProvider : IAiBatchProviderClient, IDisposable
    {
        private readonly HttpClient _httpClient = new();
        private readonly GeminiBatchClient _serializer;

        public FakeBatchProvider()
        {
            _serializer = new GeminiBatchClient(_httpClient);
        }

        public string Provider => AiProviders.GeminiDirect;
        public List<AiProviderRequest> Requests { get; } = [];

        public byte[] BuildJsonLines(
            IReadOnlyList<AiProviderRequest> requests)
        {
            Requests.AddRange(requests);
            return _serializer.BuildJsonLines(requests);
        }

        public Task<AiBatchInputFile> UploadJsonLinesAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string displayName,
            ReadOnlyMemory<byte> jsonLines,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiBatchCreateReceipt> CreateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchCreateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiBatchStatus> GetAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteBatchAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerBatchName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiBatchListPage> ListAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string? pageToken = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AiBatchItemResult>> ReadResultsAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiBatchStatus completedBatch,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteFileAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            string providerFileName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
