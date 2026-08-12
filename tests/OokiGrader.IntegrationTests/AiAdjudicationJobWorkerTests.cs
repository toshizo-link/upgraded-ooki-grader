using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Grading;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Grading;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Security;

namespace OokiGrader.IntegrationTests;

public sealed class AiAdjudicationJobWorkerTests
{
    private static readonly string[] KanjiScript = ["kanji"];

    [Fact]
    public async Task AppendsTeacherGatedProposalAndAccountsUsage()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(gradingRuleFlags: true);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var providerRequest = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(AiTaskTypes.Adjudication, providerRequest.TaskType);
        Assert.Equal("answer-recheck-v1.3.0", providerRequest.PromptVersion);
        Assert.Equal("answer_transcribe_grade_v1", providerRequest.SchemaVersion);
        Assert.Single(providerRequest.Media);
        Assert.Equal(seeded.CropSha256, providerRequest.Media[0].Sha256);
        Assert.DoesNotContain(
            "東景",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"requires_complete_answer\":true",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"answer_order_insensitive\":true",
            providerRequest.UserInstruction,
            StringComparison.Ordinal);

        await using var db = await fixture.CreateDbContextAsync();
        var result = await db.QuestionResults
            .AsNoTracking()
            .Include(item => item.Revisions)
            .SingleAsync(item => item.Id == seeded.ResultId);
        var run = await db.GradingRuns
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.RunId);
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.ResultId);
        var usage = await db.AiUsage
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);

        Assert.Equal("succeeded", request.State);
        Assert.Equal(AiTaskTypes.Adjudication, request.Purpose);
        Assert.Equal("questionResult", request.EntityType);
        Assert.Equal(AiAdjudicationJobWorker.ModelId + "-001", request.ActualModel);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(2, result.Revisions.Count);
        var proposal = result.Revisions.Single(
            item => item.Id == result.CurrentRevisionId);
        Assert.Equal(2, proposal.RevisionNumber);
        Assert.Equal("regrade_adoption", proposal.Source);
        Assert.Equal("ai_adjudication_disagreement", proposal.ReasonCode);
        Assert.Equal(1_000, proposal.AwardedPointsMilli);
        Assert.Equal("東京", proposal.AnswerTextCorrection);
        Assert.Equal("pending", result.ReviewStatus);
        Assert.True(result.ReviewRequired);
        Assert.Equal("ai_adjudication", result.Method);
        Assert.Equal("needs_grade_review", run.State);
        Assert.Equal(1_000, run.EarnedPointsMilli);
        Assert.Equal(2, run.ResultSourceRevision);
        Assert.Equal("needs_grade_review", submission.State);
        Assert.Null(submission.FinalizedAt);
        Assert.Equal(220, usage.InputTokens);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(usage.EstimatedUsdMicros, reservation.ActualUsdMicros);
    }

    [Fact]
    public async Task CapabilityPassedOneStepProfileRunsWithoutAccuracyEvaluation()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            profileApprovalState: "capability_passed",
            accuracyEvaluationId: null);

        Assert.True(await fixture.Worker.ProcessNextAsync());
        Assert.Single(fixture.Provider.Requests);

        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.ResultId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        Assert.Equal("succeeded", request.State);
        Assert.Equal("succeeded", job.State);
    }

    [Fact]
    public async Task PreservesLocalReconciliationReasonOnProposal()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync(
            request => CreateResponse(
                request,
                proposedOutcome: "incorrect",
                proposedPointsMilli: 0));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var result = await db.QuestionResults
            .AsNoTracking()
            .Include(item => item.Revisions)
            .SingleAsync(item => item.Id == seeded.ResultId);
        var proposal = result.Revisions.Single(
            item => item.Id == result.CurrentRevisionId);

        Assert.Equal("ai_deterministic_recomputed", proposal.ReasonCode);
        Assert.Equal("ai_deterministic_recomputed", result.ReasonCode);
        Assert.Equal(1_000, proposal.AwardedPointsMilli);
        Assert.Equal("correct", proposal.Outcome);
        Assert.Equal("pending", result.ReviewStatus);
    }

    [Fact]
    public async Task DoesNotOverwriteTeacherDecisionMadeDuringDispatch()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();
        fixture.Provider.BeforeReturnAsync = async () =>
        {
            await using var db = await fixture.CreateDbContextAsync();
            var result = await db.QuestionResults
                .Include(item => item.Revisions)
                .Include(item => item.GradingRun)
                    .ThenInclude(run => run.Submission)
                .SingleAsync(item => item.Id == seeded.ResultId);
            var source = result.Revisions.Single(
                item => item.Id == result.CurrentRevisionId);
            var now = DateTimeOffset.UtcNow;
            var teacher = new ResultRevisionEntity
            {
                Id = UlidId.New(now),
                QuestionResultId = result.Id,
                RevisionNumber = source.RevisionNumber + 1,
                AwardedPointsMilli = 0,
                Outcome = "incorrect",
                AnswerTextCorrection = "教師確認",
                ReasonCode = "teacher_confirmed",
                Source = "teacher_override",
                ActorStaffUserId = seeded.StaffId,
                CreatedAt = now,
                SupersedesRevisionId = source.Id,
            };
            db.ResultRevisions.Add(teacher);
            result.CurrentRevisionId = teacher.Id;
            result.ReviewStatus = "resolved";
            result.GradingRun.State = "ready_to_finalize";
            result.GradingRun.Submission.State = "ready_to_finalize";
            await db.SaveChangesAsync();
        };

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var verification = await fixture.CreateDbContextAsync();
        var result = await verification.QuestionResults
            .AsNoTracking()
            .Include(item => item.Revisions)
            .SingleAsync(item => item.Id == seeded.ResultId);
        var current = result.Revisions.Single(
            item => item.Id == result.CurrentRevisionId);
        var request = await verification.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.ResultId);
        var audit = await verification.AuditEvents
            .AsNoTracking()
            .SingleAsync(item =>
                item.EventType == "grading.adjudication_stale_skipped");

        Assert.Equal("teacher_override", current.Source);
        Assert.Equal("教師確認", current.AnswerTextCorrection);
        Assert.Equal("resolved", result.ReviewStatus);
        Assert.DoesNotContain(
            result.Revisions,
            item => item.Source == "regrade_adoption");
        Assert.Equal("succeeded", request.State);
        Assert.Equal("source_revision_changed", audit.ReasonCode);
    }

    [Fact]
    public async Task RejectsUnexpectedActualModelWithoutChangingProposal()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync(
            request => CreateResponse(
                request,
                actualModel: AiAdjudicationJobWorker.ModelId + "-preview"));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var result = await db.QuestionResults
            .AsNoTracking()
            .Include(item => item.Revisions)
            .SingleAsync(item => item.Id == seeded.ResultId);
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.ResultId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == request.Id);

        Assert.Single(result.Revisions);
        Assert.Equal(seeded.SourceRevisionId, result.CurrentRevisionId);
        Assert.Equal("pending", result.ReviewStatus);
        Assert.Equal("invalid_output", request.State);
        Assert.Equal(
            AiResponseMetadataValidator.InvalidMetadataErrorCode,
            request.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Equal("settled", reservation.State);
    }

    [Fact]
    public async Task HardBudgetLimitBlocksBeforeProviderDisclosure()
    {
        await using var fixture = await AdjudicationFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            activeBudget: true,
            dailyHardUsdMicros: 0,
            monthlyHardUsdMicros: 0);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        Assert.Empty(fixture.Provider.Requests);
        await using var db = await fixture.CreateDbContextAsync();
        var request = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.ResultId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.JobId);
        var result = await db.QuestionResults
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.ResultId);

        Assert.Equal("budget_blocked", request.State);
        Assert.Equal("ai_adjudication_budget_hard_limit", request.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Equal("pending", result.ReviewStatus);
    }

    [Theory]
    [InlineData(true, "ambiguous", 9_500, 1)]
    [InlineData(false, "clear", 7_999, 1)]
    [InlineData(false, "clear", 9_500, 0)]
    public async Task SchedulerQueuesOnlyAmbiguousSingleCropResults(
        bool providerReviewRecommended,
        string legibility,
        int confidenceBasisPoints,
        int expectedJobs)
    {
        await using var fixture = await AdjudicationFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(includeJob: false);
        await using var db = await fixture.CreateDbContextAsync();
        var result = await db.QuestionResults
            .Include(item => item.GradingRun)
                .ThenInclude(run => run.Submission)
                    .ThenInclude(submission => submission.TestSession)
            .SingleAsync(item => item.Id == seeded.ResultId);
        var quality = legibility == "clear"
            ? AnswerQuality.Clear
            : AnswerQuality.Ambiguous;
        var observation = new ValidatedAiQuestionObservation(
            seeded.QuestionId,
            new AnswerObservation("東景", quality, false, false),
            0,
            "incorrect",
            confidenceBasisPoints,
            providerReviewRecommended,
            null,
            null,
            new string('a', 64));
        var response = new ValidatedAiGradingResponse(
            "request",
            [observation],
            UnexpectedContent: false);

        var added = await fixture.Scheduler.EnqueueAmbiguousAsync(
            db,
            result.GradingRun.Submission,
            result.GradingRun,
            [result],
            response,
            [
                new AiAdjudicationArtifactCandidate(
                    seeded.QuestionId,
                    ProviderDisclosureAllowed: true),
            ],
            correlationId: null,
            causationId: "initial-job",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(expectedJobs, added);
        Assert.Equal(
            expectedJobs,
            await db.BackgroundJobs.CountAsync(item =>
                item.Type == AiAdjudicationJobWorker.JobType));
    }

    private static AiProviderResponse CreateResponse(
        AiProviderRequest request,
        string? actualModel = null,
        string proposedOutcome = "correct",
        int proposedPointsMilli = 1_000)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                schema_version = "answer_transcribe_grade_v1",
                request_key = request.RequestKey,
                results = new[]
                {
                    new
                    {
                        question_id = ReadQuestionId(request.UserInstruction),
                        transcription = "東京",
                        script_observed = KanjiScript,
                        legibility = "clear",
                        blank = false,
                        proposed_outcome = proposedOutcome,
                        proposed_points_milli = proposedPointsMilli,
                        kanji_observation = "required_kanji_present",
                        reason_code = "exact_match",
                        confidence = 0.96,
                        review_recommended = true,
                        bounded_explanation = "採点者による確認が必要です。",
                    },
                },
                missing_question_ids = Array.Empty<string>(),
                unexpected_content = false,
            }));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            AiAdjudicationJobWorker.ModelId,
            actualModel ?? AiAdjudicationJobWorker.ModelId + "-001",
            "adjudication-response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(220, 0, 24, 0, 244),
            TimeSpan.FromMilliseconds(15));
    }

    private static string ReadQuestionId(string instruction)
    {
        using var document = JsonDocument.Parse(
            instruction[instruction.IndexOf('{', StringComparison.Ordinal)..]);
        return document.RootElement
            .GetProperty("questions")[0]
            .GetProperty("question_id")
            .GetString()!;
    }

    private sealed class AdjudicationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _connectionId;
        private readonly string _secretReference;

        private AdjudicationFixture(
            SqliteConnection connection,
            ServiceProvider services,
            string connectionId,
            string secretReference,
            FakeAiProvider provider,
            FakeContentStore contentStore)
        {
            _connection = connection;
            _services = services;
            _connectionId = connectionId;
            _secretReference = secretReference;
            Provider = provider;
            ContentStore = contentStore;
            Worker = services.GetRequiredService<AiAdjudicationJobWorker>();
            Scheduler = services.GetRequiredService<AiAdjudicationJobScheduler>();
        }

        public AiAdjudicationJobWorker Worker { get; }

        public AiAdjudicationJobScheduler Scheduler { get; }

        public FakeAiProvider Provider { get; }

        public FakeContentStore ContentStore { get; }

        public static async Task<AdjudicationFixture> CreateAsync(
            Func<AiProviderRequest, AiProviderResponse>? responseFactory = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var provider = new FakeAiProvider(
                responseFactory ?? (request => CreateResponse(request)));
            var contentStore = new FakeContentStore();
            var secretStore = new InMemoryAiSecretStore();
            var connectionId = UlidId.New(DateTimeOffset.UtcNow);
            var secretReference = (await secretStore.WriteAsync(
                connectionId,
                1,
                "test-only-adjudication-key".AsMemory())).Value;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IWriteCoordinator, SemaphoreWriteCoordinator>();
            services.AddSingleton<IContentStore>(contentStore);
            services.AddSingleton<IAiProviderClient>(provider);
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton<IAiPromptBundleCatalog>(
                new ApprovedPromptBundleCatalog());
            services.AddSingleton(
                Options.Create(new AiAdjudicationJobWorkerOptions()));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<AiAdjudicationJobScheduler>();
            services.AddSingleton<AiAdjudicationJobWorker>();
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
                return new AdjudicationFixture(
                    connection,
                    serviceProvider,
                    connectionId,
                    secretReference,
                    provider,
                    contentStore);
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

        public async Task<SeededAdjudication> SeedAsync(
            bool activeBudget = false,
            long dailyHardUsdMicros = 1_000_000,
            long monthlyHardUsdMicros = 10_000_000,
            bool includeJob = true,
            bool gradingRuleFlags = false,
            string profileApprovalState = "pilot_approved",
            string? accuracyEvaluationId =
                "adjudication-fixture-evaluation")
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = UlidId.New(now);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var questionId = UlidId.New(now);
            var answerId = UlidId.New(now);
            var sessionId = UlidId.New(now);
            var submissionId = UlidId.New(now);
            var pageId = UlidId.New(now);
            var artifactId = UlidId.New(now);
            var objectId = UlidId.New(now);
            var pageReferenceId = UlidId.New(now);
            var thumbnailReferenceId = UlidId.New(now);
            var cropReferenceId = UlidId.New(now);
            var runId = UlidId.New(now);
            var resultId = UlidId.New(now);
            var sourceRevisionId = UlidId.New(now);
            var jobId = UlidId.New(now);
            var cropBytes = "ambiguous-answer-crop"u8.ToArray();
            var cropHash = Convert.ToHexString(SHA256.HashData(cropBytes))
                .ToLowerInvariant();
            ContentStore.Add(cropHash, cropBytes);
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.Adjudication);

            await using var db = await CreateDbContextAsync();
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "teacher",
                UsernameNormalized = "TEACHER",
                DisplayName = "Teacher",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Adjudication fixture",
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
                RequiresCompleteAnswer = gradingRuleFlags,
                AnswerOrderInsensitive = gradingRuleFlags,
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
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "needs_grade_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AssignmentEvidenceJson = """{"disposition":"unidentified"}""",
                AttemptNumber = 1,
                UploadedByStaffUserId = staffId,
                UploadCompletedAt = now,
                PreprocessingPipelineVersion = "local-raster-v2",
                PreprocessingManifestHash = new string('b', 64),
                PreprocessingCompletedAt = now,
                PageCount = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = objectId,
                Sha256 = cropHash,
                Bytes = cropBytes.Length,
                VerifiedMime = "image/png",
                Extension = ".png",
                RelativeObjectPath = $"scan/derived/{cropHash}.png",
                StorageClass =
                    ContentStorageClass.ManagedScanDerived.ToString(),
                RetentionClass = "submitted_scan_derived",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 3,
            });
            db.FileReferences.AddRange(
                new FileReferenceEntity
                {
                    Id = pageReferenceId,
                    FileObjectId = objectId,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "normalized_page",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                },
                new FileReferenceEntity
                {
                    Id = thumbnailReferenceId,
                    FileObjectId = objectId,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "thumbnail",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                },
                new FileReferenceEntity
                {
                    Id = cropReferenceId,
                    FileObjectId = objectId,
                    OwnerType = "submission_artifact",
                    OwnerId = artifactId,
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
                SourceSha256 = cropHash,
                NormalizedSha256 = cropHash,
                DifferenceHash = "0123456789abcdef",
                PerceptualHash = "0123456789abcdef",
                QualityState = "warning",
                BlurBasisPoints = 3_000,
                ContrastBasisPoints = 4_000,
                BrightnessBasisPoints = 5_000,
                InkCoverageBasisPoints = 2_000,
                AlignmentState = "aligned",
                CreatedAt = now,
            });
            db.SubmissionArtifacts.Add(new SubmissionArtifactEntity
            {
                Id = artifactId,
                SubmissionId = submissionId,
                SubmissionPageId = pageId,
                QuestionId = questionId,
                FileReferenceId = cropReferenceId,
                ArtifactType = "answer_crop",
                Ordinal = 0,
                PanelLabel = "Q1",
                InputManifestHash = new string('c', 64),
                WidthPixels = 800,
                HeightPixels = 500,
                ProviderDisclosureAllowed = true,
                CreatedAt = now,
            });
            db.GradingRuns.Add(new GradingRunEntity
            {
                Id = runId,
                SubmissionId = submissionId,
                RunNumber = 1,
                TemplateVersionId = versionId,
                Reason = "gemini_initial_pilot",
                State = "needs_grade_review",
                Provider = AiProviders.GeminiDirect,
                Model = AiAdjudicationJobWorker.ModelId,
                PromptVersion = "answer-transcribe-grade-v1.1.0",
                SchemaVersion = "answer_transcribe_grade_v1",
                PipelineVersion = AiInitialGradingJobWorker.PipelineVersion,
                CanonicalInputManifestHash = new string('d', 64),
                EarnedPointsMilli = 0,
                PossiblePointsMilli = 1_000,
                ResultSourceRevision = 1,
                CreatedAt = now,
                FinishedAt = now,
            });
            db.QuestionResults.Add(new QuestionResultEntity
            {
                Id = resultId,
                GradingRunId = runId,
                QuestionId = questionId,
                TranscribedAnswer = "東景",
                NormalizedAnswer = "東景",
                ProposedPointsMilli = 0,
                MaximumPointsMilli = 1_000,
                Outcome = "incorrect",
                Method = "ai_pilot",
                ConfidenceBasisPoints = 5_500,
                KanjiCheck = "uncertain",
                ReasonCode = "ambiguous_handwriting",
                AnswerCropFileReferenceId = cropReferenceId,
                ReviewRequired = true,
                ReviewStatus = "pending",
                CreatedAt = now,
            });
            db.ResultRevisions.Add(new ResultRevisionEntity
            {
                Id = sourceRevisionId,
                QuestionResultId = resultId,
                RevisionNumber = 1,
                AwardedPointsMilli = 0,
                Outcome = "incorrect",
                AnswerTextCorrection = "東景",
                ReasonCode = "ambiguous_handwriting",
                Source = "initial",
                CreatedAt = now,
            });
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = _connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = "googleGenerativeLanguage",
                ModelId = AiAdjudicationJobWorker.ModelId,
                SecretReference = _secretReference,
                KeyFingerprint = "sha256:test",
                CredentialRevision = 1,
                TimeoutSeconds = 30,
                ConcurrencyLimit = 1,
                State = "active",
                LastCapabilityProbeState = "passed",
                LastCapabilityProbeAt = now,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = UlidId.New(now),
                Name = "Adjudication pilot",
                TaskType = AiTaskTypes.Adjudication,
                AiConnectionId = _connectionId,
                ConnectionRevision = 1,
                ModelId = AiAdjudicationJobWorker.ModelId,
                ProcessingStrategy = "expedite_standard",
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "minimal",
                MediaResolution = "high",
                MaxOutputTokens = 1_024,
                ConcurrencyLimit = 1,
                ApprovalState = profileApprovalState,
                AccuracyEvaluationId = accuracyEvaluationId,
                Active = true,
                ActivatedAt = now,
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.PricingSnapshots.Add(new PricingSnapshotEntity
            {
                Id = UlidId.New(now),
                Provider = AiProviders.GeminiDirect,
                ModelId = AiAdjudicationJobWorker.ModelId,
                InputUsdMicrosPerMillionTokens = 250_000,
                OutputUsdMicrosPerMillionTokens = 1_500_000,
                ThinkingUsdMicrosPerMillionTokens = 1_500_000,
                SourceUrl = "https://ai.google.dev/gemini-api/docs/pricing",
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
            if (includeJob)
            {
                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = jobId,
                    Type = AiAdjudicationJobWorker.JobType,
                    SchemaVersion = AiAdjudicationJobWorker.JobSchemaVersion,
                    DeduplicationKey =
                        $"question-result:{resultId}:adjudication:{sourceRevisionId}",
                    Priority = 0,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        submissionId,
                        gradingRunId = runId,
                        questionResultId = resultId,
                        sourceRevisionId,
                    }),
                    State = "queued",
                    MaxAttempts = 8,
                    NextAttemptAt = now.AddMinutes(-1),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await db.SaveChangesAsync();
            var storedSubmission = await db.Submissions
                .SingleAsync(item => item.Id == submissionId);
            var storedResult = await db.QuestionResults
                .SingleAsync(item => item.Id == resultId);
            storedSubmission.CurrentGradingRunId = runId;
            storedResult.CurrentRevisionId = sourceRevisionId;
            await db.SaveChangesAsync();
            return new SeededAdjudication(
                staffId,
                submissionId,
                runId,
                resultId,
                questionId,
                sourceRevisionId,
                jobId,
                cropHash);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed record SeededAdjudication(
        string StaffId,
        string SubmissionId,
        string RunId,
        string ResultId,
        string QuestionId,
        string SourceRevisionId,
        string JobId,
        string CropSha256);

    private sealed class FakeAiProvider(
        Func<AiProviderRequest, AiProviderResponse> responseFactory)
        : IAiProviderClient
    {
        public string Provider => AiProviders.GeminiDirect;

        public List<AiProviderRequest> Requests { get; } = [];

        public Func<Task>? BeforeReturnAsync { get; set; }

        public async Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (BeforeReturnAsync is not null)
            {
                await BeforeReturnAsync();
            }

            return responseFactory(request);
        }

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeContentStore : IContentStore
    {
        private readonly Dictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);

        public void Add(string sha256, byte[] content)
        {
            _content.Add(sha256, content);
        }

        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            if (!_content.TryGetValue(locator.Sha256, out var bytes))
            {
                throw new FileNotFoundException();
            }

            return Task.FromResult<Stream>(
                new MemoryStream(bytes, writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_content.ContainsKey(locator.Sha256));

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
