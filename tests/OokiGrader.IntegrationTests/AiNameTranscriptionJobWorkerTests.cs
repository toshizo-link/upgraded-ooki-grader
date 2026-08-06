using System.Security.Cryptography;
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

public sealed class AiNameTranscriptionJobWorkerTests
{
    [Fact]
    public async Task SendsCompletePagesAndPersistsLocalReviewCandidates()
    {
        await using var fixture = await NameWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        var request = Assert.Single(fixture.Provider.Requests);
        Assert.Equal(AiTaskTypes.NameTranscription, request.TaskType);
        var media = Assert.Single(request.Media);
        Assert.Equal(seeded.NameCropSha256, media.Sha256);
        Assert.DoesNotContain(
            request.Media,
            item => item.Sha256 == seeded.AnswerCropSha256);
        Assert.DoesNotContain(
            seeded.AnswerCropSha256,
            fixture.ContentStore.OpenedHashes);
        Assert.DoesNotContain(
            seeded.NumberCropSha256,
            fixture.ContentStore.OpenedHashes);
        Assert.DoesNotContain(
            "大木 花子",
            request.UserInstruction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "S-1042",
            request.UserInstruction,
            StringComparison.Ordinal);
        Assert.False(fixture.Provider.ObservedInsideWriteCoordinator);
        Assert.False(fixture.ContentStore.ObservedInsideWriteCoordinator);

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var aiRequest = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var usage = await db.AiUsage
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == aiRequest.Id);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == aiRequest.Id);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Type
                == AiNameTranscriptionJobWorker.JobType);

        Assert.Equal("needs_name_review", submission.State);
        Assert.Null(submission.AssignedStudentId);
        Assert.Equal("none", submission.AssignmentMethod);
        Assert.Null(submission.AssignmentConfidenceBasisPoints);
        Assert.Equal(
            "local-roster-review-v1",
            submission.AssignmentPolicyVersion);
        Assert.Equal("succeeded", aiRequest.State);
        Assert.Equal(AiTaskTypes.NameTranscription, aiRequest.Purpose);
        Assert.False(aiRequest.PossibleDuplicate);
        Assert.Equal("name-response-1", aiRequest.ProviderResponseId);
        Assert.Equal(180, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(usage.EstimatedUsdMicros, reservation.ActualUsdMicros);
        Assert.Equal("succeeded", job.State);
        Assert.Equal(10_000, job.ProgressBasisPoints);

        Assert.NotNull(submission.AssignmentEvidenceJson);
        Assert.InRange(
            submission.AssignmentEvidenceJson!.Length,
            1,
            16_000);
        using var evidence = JsonDocument.Parse(
            submission.AssignmentEvidenceJson);
        var root = evidence.RootElement;
        Assert.Equal(
            "name_assignment_evidence_v1",
            root.GetProperty("schemaVersion").GetString());
        Assert.Equal(
            AiNameTranscriptionJobWorker.PipelineVersion,
            root.GetProperty("pipelineVersion").GetString());
        Assert.Equal(
            aiRequest.Id,
            root.GetProperty("aiRequestId").GetString());
        Assert.False(
            root.GetProperty("automaticAssignmentEnabled").GetBoolean());
        var transcription = root.GetProperty("transcription");
        Assert.Equal(
            "大木 花子",
            transcription.GetProperty("visibleName").GetString());
        Assert.Equal(
            "S-1042",
            transcription
                .GetProperty("visibleStudentNumber")
                .GetString());
        Assert.Equal(
            9_800,
            transcription
                .GetProperty("providerConfidenceBasisPoints")
                .GetInt32());
        var candidates = root.GetProperty("candidates");
        Assert.InRange(candidates.GetArrayLength(), 1, 5);
        Assert.Equal(
            seeded.StudentId,
            candidates[0].GetProperty("studentId").GetString());
        Assert.Contains(
            candidates[0].GetProperty("evidence")
                .EnumerateArray()
                .Select(item => item.GetString()),
            item => item == "exact_student_number");
    }

    [Theory]
    [InlineData(InvalidResponseKind.Schema, "ai_name_response_schema_invalid")]
    [InlineData(InvalidResponseKind.RequestKey, "ai_name_response_identity_invalid")]
    [InlineData(
        InvalidResponseKind.Legibility,
        "ai_name_legibility_contradiction")]
    [InlineData(InvalidResponseKind.Confidence, "ai_name_confidence_invalid")]
    public async Task RejectsInvalidStructuredOutput(
        InvalidResponseKind kind,
        string expectedError)
    {
        await using var fixture = await NameWorkerFixture.CreateAsync(
            request => CreateResponse(request, kind));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var aiRequest = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Type
                == AiNameTranscriptionJobWorker.JobType);

        Assert.Equal("needs_name_review", submission.State);
        Assert.Null(submission.AssignedStudentId);
        Assert.Null(submission.AssignmentEvidenceJson);
        Assert.Equal("invalid_output", aiRequest.State);
        Assert.Equal(expectedError, aiRequest.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Single(
            await db.AiUsage
                .AsNoTracking()
                .Where(item => item.AiRequestId == aiRequest.Id)
                .ToArrayAsync());
    }

    [Fact]
    public async Task TimeoutIsAmbiguousAndRedeliveryNeverCallsProviderAgain()
    {
        await using var fixture = await NameWorkerFixture.CreateAsync(
            _ => throw new AiProviderException(
                AiFailureKind.Timeout,
                "gemini_timeout",
                isTransient: true));
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());
        var jobId = await fixture.FindJobIdAsync();
        await fixture.RequeueAsync(jobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var aiRequest = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Id == jobId);
        var reservation = await db.AiBudgetReservations
            .AsNoTracking()
            .SingleAsync(item => item.AiRequestId == aiRequest.Id);

        Assert.Single(fixture.Provider.Requests);
        Assert.Equal("needs_name_review", submission.State);
        Assert.Null(submission.AssignedStudentId);
        Assert.True(aiRequest.PossibleDuplicate);
        Assert.Equal("failed", aiRequest.State);
        Assert.Equal("ai_dispatch_outcome_unknown", aiRequest.ErrorCode);
        Assert.Equal(1, aiRequest.DispatchAttempt);
        Assert.Equal("blocked", job.State);
        Assert.Equal(2, job.AttemptCount);
        Assert.Equal("settled", reservation.State);
        Assert.Equal(
            reservation.ReservedUsdMicros,
            reservation.ActualUsdMicros);
    }

    [Fact]
    public async Task SuccessfulRedeliveryIsIdempotent()
    {
        await using var fixture = await NameWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync();

        Assert.True(await fixture.Worker.ProcessNextAsync());
        var jobId = await fixture.FindJobIdAsync();
        Counts before;
        string evidence;
        await using (var db = await fixture.CreateDbContextAsync())
        {
            var submission = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SubmissionId);
            evidence = submission.AssignmentEvidenceJson!;
            before = new Counts(
                await db.AiRequests.CountAsync(),
                await db.AiUsage.CountAsync(),
                await db.AiBudgetReservations.CountAsync(),
                await db.AuditEvents.CountAsync(),
                await db.OutboxEvents.CountAsync());
        }

        await fixture.RequeueAsync(jobId);
        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using (var db = await fixture.CreateDbContextAsync())
        {
            var submission = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == seeded.SubmissionId);
            var after = new Counts(
                await db.AiRequests.CountAsync(),
                await db.AiUsage.CountAsync(),
                await db.AiBudgetReservations.CountAsync(),
                await db.AuditEvents.CountAsync(),
                await db.OutboxEvents.CountAsync());
            var job = await db.BackgroundJobs
                .AsNoTracking()
                .SingleAsync(item => item.Id == jobId);
            Assert.Equal(before, after);
            Assert.Equal(evidence, submission.AssignmentEvidenceJson);
            Assert.Equal("needs_name_review", submission.State);
            Assert.Equal("succeeded", job.State);
            Assert.Equal(2, job.AttemptCount);
        }

        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task HardBudgetStopsBeforeCropOrProviderIo()
    {
        await using var fixture = await NameWorkerFixture.CreateAsync();
        var seeded = await fixture.SeedAsync(
            activeBudget: true,
            dailyHardUsdMicros: 1,
            monthlyHardUsdMicros: 1);

        Assert.True(await fixture.Worker.ProcessNextAsync());

        await using var db = await fixture.CreateDbContextAsync();
        var submission = await db.Submissions
            .AsNoTracking()
            .SingleAsync(item => item.Id == seeded.SubmissionId);
        var aiRequest = await db.AiRequests
            .AsNoTracking()
            .SingleAsync(item => item.EntityId == seeded.SubmissionId);
        var job = await db.BackgroundJobs
            .AsNoTracking()
            .SingleAsync(item => item.Type
                == AiNameTranscriptionJobWorker.JobType);

        Assert.Equal("needs_name_review", submission.State);
        Assert.Equal("budget_blocked", aiRequest.State);
        Assert.Equal("ai_budget_hard_limit", aiRequest.ErrorCode);
        Assert.Equal("blocked", job.State);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Empty(fixture.ContentStore.OpenedHashes);
        Assert.Empty(await db.AiBudgetReservations.ToArrayAsync());
    }

    private static AiProviderResponse CreateResponse(
        AiProviderRequest request,
        InvalidResponseKind invalid = InvalidResponseKind.None)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema_version"] = "name_transcribe_v1",
            ["request_key"] = invalid == InvalidResponseKind.RequestKey
                ? "wrong_request"
                : request.RequestKey,
            ["transcribed_name"] = "大木 花子",
            ["transcribed_student_number"] = "S-1042",
            ["legibility"] = invalid == InvalidResponseKind.Legibility
                ? "blank"
                : "clear",
            ["confidence"] = invalid == InvalidResponseKind.Confidence
                ? 1.5
                : 0.98,
            ["unexpected_content"] = false,
        };
        if (invalid == InvalidResponseKind.Schema)
        {
            values["unknown"] = true;
        }

        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(values));
        return new AiProviderResponse(
            AiProviders.GeminiDirect,
            AiNameTranscriptionJobWorker.ModelId,
            AiNameTranscriptionJobWorker.ModelId,
            "name-response-1",
            "STOP",
            document.RootElement.Clone(),
            new AiUsage(
                PromptTokens: 180,
                CachedTokens: 0,
                OutputTokens: 20,
                ThinkingTokens: 0,
                TotalTokens: 200),
            TimeSpan.FromMilliseconds(20));
    }

    public enum InvalidResponseKind
    {
        None,
        Schema,
        RequestKey,
        Legibility,
        Confidence,
    }

    private sealed class NameWorkerFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly string _connectionId;
        private readonly string _secretReference;

        private NameWorkerFixture(
            SqliteConnection connection,
            ServiceProvider services,
            string connectionId,
            string secretReference,
            FakeContentStore contentStore,
            FakeAiProvider provider)
        {
            _connection = connection;
            _services = services;
            _connectionId = connectionId;
            _secretReference = secretReference;
            ContentStore = contentStore;
            Provider = provider;
            Worker = services.GetRequiredService<AiNameTranscriptionJobWorker>();
        }

        public AiNameTranscriptionJobWorker Worker { get; }
        public FakeContentStore ContentStore { get; }
        public FakeAiProvider Provider { get; }

        public static async Task<NameWorkerFixture> CreateAsync(
            Func<AiProviderRequest, AiProviderResponse>? responseFactory = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var boundary = new BoundaryProbe();
            var writeCoordinator = new BoundaryWriteCoordinator(boundary);
            var contentStore = new FakeContentStore(boundary);
            var provider = new FakeAiProvider(
                boundary,
                responseFactory ?? (request => CreateResponse(request)));
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
            services.AddSingleton<IAiSecretStore>(secretStore);
            services.AddSingleton<IAiPromptBundleCatalog>(
                new ApprovedPromptBundleCatalog());
            services.AddSingleton(
                Options.Create(new AiNameTranscriptionJobWorkerOptions()));
            services.AddDbContextFactory<OokiGraderDbContext>(
                options => options.UseSqlite(connection));
            services.AddSingleton<AiNameTranscriptionJobWorker>();
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
                return new NameWorkerFixture(
                    connection,
                    serviceProvider,
                    connectionId,
                    secretReference,
                    contentStore,
                    provider);
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

        public async Task<SeededNameWorkflow> SeedAsync(
            bool activeBudget = false,
            long dailyHardUsdMicros = 1_000_000,
            long monthlyHardUsdMicros = 10_000_000)
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = UlidId.New(now);
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var questionId = UlidId.New(now);
            var sessionId = UlidId.New(now);
            var studentId = UlidId.New(now);
            var submissionId = UlidId.New(now);
            var pageId = UlidId.New(now);
            var bundle = _services
                .GetRequiredService<IAiPromptBundleCatalog>()
                .GetRequired(AiTaskTypes.NameTranscription);
            var nameBytes = "identity-name-crop"u8.ToArray();
            var numberBytes = "identity-number-crop"u8.ToArray();
            var answerBytes = "private-answer-crop"u8.ToArray();
            var nameHash = Hash(nameBytes);
            var numberHash = Hash(numberBytes);
            var answerHash = Hash(answerBytes);
            ContentStore.Add(nameHash, nameBytes);
            ContentStore.Add(numberHash, numberBytes);
            ContentStore.Add(answerHash, answerBytes);

            await using var db = await CreateDbContextAsync();
            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Name worker fixture",
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
                QuestionText = "回答",
                QuestionType = "exact_short_text",
                GradingMode = "transcribe_then_rules",
                MaxPointsMilli = 1_000,
                PointIncrementMilli = 1_000,
                AllowNonKanji = true,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.Students.Add(new StudentEntity
            {
                Id = studentId,
                StudentNumber = "S-1042",
                StudentNumberNormalized = "S-1042",
                FamilyName = "大木",
                GivenName = "花子",
                FamilyNameNormalized = "大木",
                GivenNameNormalized = "花子",
                FamilyNameKana = "オオキ",
                GivenNameKana = "ハナコ",
                FamilyNameKanaNormalized = "オオキ",
                GivenNameKanaNormalized = "ハナコ",
                DisplayName = "大木 花子",
                SchoolClass = "2-A",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.StudentAliases.Add(new StudentAliasEntity
            {
                Id = UlidId.New(now),
                StudentId = studentId,
                AliasType = "spacing",
                DisplayValue = "大木花子",
                NormalizedValue = "大木花子",
                RecognitionEnabled = true,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = versionId,
                TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                Priority = "economy",
                State = "open",
                ExpectedRosterEnabled = true,
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.SessionRosterMembers.Add(new SessionRosterMemberEntity
            {
                TestSessionId = sessionId,
                StudentId = studentId,
                Expected = true,
                SeatLabel = "1",
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "needs_name_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
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

            var nameObject = AddFileObject(
                db,
                now,
                nameHash,
                nameBytes.Length,
                referenceCount: 3);
            var numberObject = AddFileObject(
                db,
                now,
                numberHash,
                numberBytes.Length,
                referenceCount: 1);
            var answerObject = AddFileObject(
                db,
                now,
                answerHash,
                answerBytes.Length,
                referenceCount: 1);
            var normalizedReferenceId = UlidId.New(now);
            var thumbnailReferenceId = UlidId.New(now);
            db.FileReferences.AddRange(
                new FileReferenceEntity
                {
                    Id = normalizedReferenceId,
                    FileObjectId = nameObject.Id,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "normalized_page",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                },
                new FileReferenceEntity
                {
                    Id = thumbnailReferenceId,
                    FileObjectId = nameObject.Id,
                    OwnerType = "submission_page",
                    OwnerId = pageId,
                    Purpose = "page_thumbnail",
                    RetentionAnchorAt = now,
                    CreatedAt = now,
                });
            db.SubmissionPages.Add(new SubmissionPageEntity
            {
                Id = pageId,
                SubmissionId = submissionId,
                PageNumber = 1,
                NormalizedFileReferenceId = normalizedReferenceId,
                ThumbnailFileReferenceId = thumbnailReferenceId,
                WidthPixels = 1_000,
                HeightPixels = 1_400,
                RotationDegrees = 0,
                SourceSha256 = nameHash,
                NormalizedSha256 = nameHash,
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
            AddArtifact(
                db,
                now,
                submissionId,
                pageId,
                nameObject,
                "name_crop",
                questionId: null,
                ordinal: 0,
                providerDisclosureAllowed: true);
            AddArtifact(
                db,
                now,
                submissionId,
                pageId,
                numberObject,
                "student_number_crop",
                questionId: null,
                ordinal: 1,
                providerDisclosureAllowed: true);
            AddArtifact(
                db,
                now,
                submissionId,
                pageId,
                answerObject,
                "answer_crop",
                questionId,
                ordinal: 2,
                providerDisclosureAllowed: true);

            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = _connectionId,
                Provider = AiProviders.GeminiDirect,
                EndpointProfile = "googleGenerativeLanguage",
                ModelId = AiNameTranscriptionJobWorker.ModelId,
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
                Revision = 1,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = UlidId.New(now),
                Name = "Name transcription pilot",
                TaskType = AiTaskTypes.NameTranscription,
                AiConnectionId = _connectionId,
                ConnectionRevision = 1,
                ModelId = AiNameTranscriptionJobWorker.ModelId,
                ProcessingStrategy = "expedite_standard",
                PromptVersion = bundle.PromptVersion,
                SchemaVersion = bundle.SchemaVersion,
                PromptContentHash = bundle.ContentHash,
                ThinkingLevel = "minimal",
                MediaResolution = "high",
                MaxOutputTokens = 512,
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
                Provider = AiProviders.GeminiDirect,
                ModelId = AiNameTranscriptionJobWorker.ModelId,
                InputUsdMicrosPerMillionTokens = 250_000,
                OutputUsdMicrosPerMillionTokens = 1_500_000,
                ThinkingUsdMicrosPerMillionTokens = 1_500_000,
                SourceUrl =
                    "https://ai.google.dev/gemini-api/docs/pricing",
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
            await db.SaveChangesAsync();
            return new SeededNameWorkflow(
                submissionId,
                studentId,
                nameHash,
                numberHash,
                answerHash);
        }

        public async Task<string> FindJobIdAsync()
        {
            await using var db = await CreateDbContextAsync();
            return await db.BackgroundJobs
                .Where(item => item.Type
                    == AiNameTranscriptionJobWorker.JobType)
                .Select(item => item.Id)
                .SingleAsync();
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
            job.SafeErrorDetail = null;
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static FileObjectEntity AddFileObject(
            OokiGraderDbContext db,
            DateTimeOffset now,
            string hash,
            int bytes,
            int referenceCount)
        {
            var entity = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = hash,
                Bytes = bytes,
                VerifiedMime = "image/png",
                Extension = ".png",
                RelativeObjectPath = $"scan/derived/{hash}.png",
                StorageClass =
                    ContentStorageClass.ManagedScanDerived.ToString(),
                RetentionClass = "submitted_scan_derived",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = referenceCount,
            };
            db.FileObjects.Add(entity);
            return entity;
        }

        private static void AddArtifact(
            OokiGraderDbContext db,
            DateTimeOffset now,
            string submissionId,
            string pageId,
            FileObjectEntity fileObject,
            string artifactType,
            string? questionId,
            int ordinal,
            bool providerDisclosureAllowed)
        {
            var artifactId = UlidId.New(now);
            var referenceId = UlidId.New(now);
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = referenceId,
                FileObjectId = fileObject.Id,
                OwnerType = "submission_artifact",
                OwnerId = artifactId,
                Purpose = artifactType,
                RetentionAnchorAt = now,
                CreatedAt = now,
            });
            db.SubmissionArtifacts.Add(new SubmissionArtifactEntity
            {
                Id = artifactId,
                SubmissionId = submissionId,
                SubmissionPageId = pageId,
                QuestionId = questionId,
                FileReferenceId = referenceId,
                ArtifactType = artifactType,
                Ordinal = ordinal,
                PanelLabel = artifactType,
                InputManifestHash = new string('c', 64),
                WidthPixels = 400,
                HeightPixels = 100,
                ProviderDisclosureAllowed = providerDisclosureAllowed,
                CreatedAt = now,
            });
        }

        private static string Hash(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
        }
    }

    private sealed record SeededNameWorkflow(
        string SubmissionId,
        string StudentId,
        string NameCropSha256,
        string NumberCropSha256,
        string AnswerCropSha256);

    private sealed record Counts(
        int AiRequests,
        int AiUsage,
        int Reservations,
        int AuditEvents,
        int OutboxEvents);

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

    private sealed class FakeContentStore(BoundaryProbe boundary)
        : IContentStore
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
            return Task.FromResult<Stream>(
                new MemoryStream(
                    _content[locator.Sha256],
                    writable: false));
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
        Func<AiProviderRequest, AiProviderResponse> responseFactory)
        : IAiProviderClient
    {
        public string Provider => AiProviders.GeminiDirect;
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
}
