using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Jobs;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class AiObservabilityEndpointsTests
{
    [Fact]
    public async Task AdministratorReceivesBoundedAggregateOnlyMetrics()
    {
        await using var application =
            await AiMetricsTestApplication.CreateAsync();

        var response = await application.GetAsync(
            "/api/v1/admin/ai-metrics?days=30",
            "administrator");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        var jsonText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(
            AiMetricsTestApplication.SecretStudentValue,
            jsonText,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(jsonText);
        var root = document.RootElement;
        Assert.Equal(
            30,
            root.GetProperty("window").GetProperty("days").GetInt32());
        Assert.Equal(
            90,
            root.GetProperty("window").GetProperty("maximumDays").GetInt32());

        var totals = root.GetProperty("totals");
        Assert.Equal(5, totals.GetProperty("requestCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("successCount").GetInt32());
        Assert.Equal(3, totals.GetProperty("failureCount").GetInt32());
        Assert.Equal(1, totals.GetProperty("ambiguousCount").GetInt32());
        Assert.Equal(
            7,
            totals.GetProperty("dispatchAttemptCount").GetInt64());
        Assert.Equal(
            2,
            totals.GetProperty("retriedRequestCount").GetInt32());
        Assert.Equal(
            2,
            totals.GetProperty("retryAttemptCount").GetInt64());

        var errors = totals.GetProperty("errors");
        Assert.Equal(1, errors.GetProperty("rateLimited429").GetInt32());
        Assert.Equal(1, errors.GetProperty("provider5Xx").GetInt32());
        Assert.Equal(
            1,
            errors.GetProperty("schemaOrOutputValidation").GetInt32());
        Assert.Equal(
            192,
            totals.GetProperty("tokens").GetProperty("total").GetInt32());
        Assert.Equal(
            1_200,
            totals
                .GetProperty("cost")
                .GetProperty("estimatedUsdMicros")
                .GetInt64());

        var queueWait = totals.GetProperty("queueWait");
        Assert.Equal(5, queueWait.GetProperty("sampleCount").GetInt32());
        Assert.Equal(
            27_000,
            queueWait.GetProperty("averageMilliseconds").GetInt64());
        Assert.Equal(
            60_000,
            queueWait.GetProperty("p95Milliseconds").GetInt64());
        var providerLatency = totals.GetProperty("providerLatency");
        Assert.Equal(
            4,
            providerLatency.GetProperty("sampleCount").GetInt32());
        Assert.Equal(
            120_000,
            providerLatency.GetProperty("p95Milliseconds").GetInt64());

        var correction = totals.GetProperty("teacherCorrection");
        Assert.True(correction.GetProperty("available").GetBoolean());
        Assert.Equal(
            1,
            correction.GetProperty("reviewedQuestionCount").GetInt32());
        Assert.Equal(
            1,
            correction.GetProperty("correctedQuestionCount").GetInt32());
        Assert.Equal(
            10_000,
            correction.GetProperty("rateBasisPoints").GetInt32());
        Assert.Single(root.GetProperty("byProfile").EnumerateArray());
        Assert.True(
            root.GetProperty("privacy")
                .GetProperty("aggregateOnly")
                .GetBoolean());
        Assert.False(
            root.GetProperty("privacy")
                .GetProperty("includesStudentData")
                .GetBoolean());
    }

    [Fact]
    public async Task MetricsRejectOversizedWindowAndRequireAdministrator()
    {
        await using var application =
            await AiMetricsTestApplication.CreateAsync();

        var oversized = await application.GetAsync(
            "/api/v1/admin/ai-metrics?days=91",
            "administrator");
        var teacher = await application.GetAsync(
            "/api/v1/admin/ai-metrics",
            "teacher");

        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            oversized.StatusCode);
        using var problem = JsonDocument.Parse(
            await oversized.Content.ReadAsStringAsync());
        Assert.Equal(
            "AI_METRICS_WINDOW_INVALID",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, teacher.StatusCode);
    }

    [Fact]
    public async Task GeminiBatchHealthUsesLiveBatchAndJobLedger()
    {
        await using var application =
            await AiMetricsTestApplication.CreateAsync();

        var healthy = await application.ReadGeminiBatchHealthAsync();

        Assert.Equal("healthy", healthy.State);
        Assert.Equal(0, healthy.ActiveBatches);

        await application.SeedBatchAttentionAsync();
        var unavailable = await application.ReadGeminiBatchHealthAsync();

        Assert.Equal("unavailable", unavailable.State);
        Assert.Equal(1, unavailable.ActiveBatches);
        Assert.Equal(1, unavailable.ManualReviewBatches);
        Assert.Equal(1, unavailable.PossibleDuplicateBatches);
        Assert.Equal(1, unavailable.BlockedJobs);
        Assert.Equal(
            "gemini_batch_manual_attention_required",
            unavailable.ErrorCode);
    }

    private sealed class AiMetricsTestApplication : IAsyncDisposable
    {
        public const string SecretStudentValue =
            "PRIVATE-STUDENT-CONTENT-MUST-NOT-LEAK";

        private static readonly DateTimeOffset UtcNow = new(
            2026,
            7,
            27,
            6,
            0,
            0,
            TimeSpan.Zero);
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private AiMetricsTestApplication(
            IHost host,
            SqliteConnection connection)
        {
            _host = host;
            _connection = connection;
            Client = host.GetTestClient();
            Client.Timeout = TimeSpan.FromSeconds(5);
        }

        private HttpClient Client { get; }

        public static async Task<AiMetricsTestApplication> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(connection);
                        services.AddSingleton<TimeProvider>(
                            new FixedTimeProvider(UtcNow));
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddSingleton<IAiSecretStore>(
                            new UnusedSecretStore());
                        services.AddSingleton<IAiProviderClient>(
                            new UnusedProviderClient());
                        services.AddSingleton<IAiPromptBundleCatalog>(
                            new UnusedPromptCatalog());
                        services
                            .AddAuthentication(
                                TestAuthenticationHandler.SchemeName)
                            .AddScheme<
                                AuthenticationSchemeOptions,
                                TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName,
                                _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(
                                new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                    .RequireAuthenticatedUser()
                                    .Build())
                            .AddPolicy(
                                "administrator",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("administrator"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(
                            endpoints => endpoints.MapAiAdminEndpoints());
                    });
                });

            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
                await SeedAsync(db);
            }

            await host.StartAsync();
            return new AiMetricsTestApplication(host, connection);
        }

        public async Task<HttpResponseMessage> GetAsync(
            string path,
            string role)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);
            return await Client.SendAsync(request);
        }

        public async Task<AdminEndpoints.GeminiBatchHealth>
            ReadGeminiBatchHealthAsync()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            return await AdminEndpoints.ReadGeminiBatchHealthAsync(
                db,
                UtcNow,
                CancellationToken.None);
        }

        public async Task SeedBatchAttentionAsync()
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            var profile = await db.AiTaskProfiles
                .AsNoTracking()
                .SingleAsync();
            var connection = await db.AiConnections
                .AsNoTracking()
                .SingleAsync();
            db.AiBatches.Add(new AiBatchEntity
            {
                Id = Id(200),
                Provider = AiProviders.GeminiDirect,
                ModelId = profile.ModelId,
                AiConnectionId = connection.Id,
                ConnectionRevision = connection.Revision,
                AiTaskProfileId = profile.Id,
                TaskProfileRevision = profile.Revision,
                CompatibilityKey = "metrics-test",
                ManifestJson = "{}",
                ManifestHash = new string('a', 64),
                DisplayName = "Aggregate-only test batch",
                State = "manual_review",
                RequestCount = 1,
                PendingRequestCount = 1,
                PossibleDuplicate = true,
                CreatedAt = UtcNow.AddDays(-3),
                UpdatedAt = UtcNow.AddDays(-3),
            });
            db.BackgroundJobs.Add(new BackgroundJobEntity
            {
                Id = Id(201),
                Type = AiBatchJobWorker.PrepareJobType,
                SchemaVersion = 1,
                DeduplicationKey = "batch-health-test",
                PayloadJson = "{}",
                State = "blocked",
                NextAttemptAt = UtcNow.AddDays(-3),
                CreatedAt = UtcNow.AddDays(-3),
                UpdatedAt = UtcNow.AddDays(-3),
            });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        private static async Task SeedAsync(OokiGraderDbContext db)
        {
            var staffId = Id(1);
            var connectionId = Id(2);
            var profileId = Id(3);
            var templateId = Id(4);
            var templateVersionId = Id(5);
            var questionId = Id(6);
            var sessionId = Id(7);
            var submissionId = Id(8);
            var runId = Id(9);
            var resultId = Id(10);

            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "metrics.admin",
                UsernameNormalized = "METRICS.ADMIN",
                DisplayName = "Metrics Admin",
                PasswordHash = "unused",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = UtcNow,
                CreatedAt = UtcNow,
                UpdatedAt = UtcNow,
            });
            db.AiConnections.Add(new AiConnectionEntity
            {
                Id = connectionId,
                Provider = AiProviders.GeminiDirect,
                ModelId = "gemini-3.5-flash-lite",
                SecretReference = "opaque-test-reference",
                KeyFingerprint = "sha256:test",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow.AddDays(-60),
                UpdatedAt = UtcNow,
            });
            db.AiTaskProfiles.Add(new AiTaskProfileEntity
            {
                Id = profileId,
                Name = "Active grading profile",
                TaskType = AiTaskTypes.InitialGrading,
                AiConnectionId = connectionId,
                ConnectionRevision = 1,
                ModelId = "gemini-3.5-flash-lite",
                ProcessingStrategy = "queued_standard",
                PromptVersion = "grade-v1",
                SchemaVersion = "grade-schema-v1",
                PromptContentHash = new string('a', 64),
                ThinkingLevel = "minimal",
                MediaResolution = "high",
                MaxOutputTokens = 8_192,
                ConcurrencyLimit = 2,
                ApprovalState = "pilot_approved",
                Active = true,
                ActivatedAt = UtcNow.AddDays(-30),
                ActivatedByStaffUserId = staffId,
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow.AddDays(-60),
                UpdatedAt = UtcNow,
            });

            db.TestTemplates.Add(new TestTemplateEntity
            {
                Id = templateId,
                Title = "Metrics fixture",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow.AddDays(-10),
                UpdatedAt = UtcNow,
            });
            db.TemplateVersions.Add(new TemplateVersionEntity
            {
                Id = templateVersionId,
                TestTemplateId = templateId,
                VersionNumber = 1,
                State = "published",
                PipelineVersion = "metrics-test-v1",
                PublishedByStaffUserId = staffId,
                PublishedAt = UtcNow.AddDays(-10),
                ContentHash = new string('b', 64),
                CreatedAt = UtcNow.AddDays(-10),
                UpdatedAt = UtcNow,
            });
            db.Questions.Add(new QuestionEntity
            {
                Id = questionId,
                TemplateVersionId = templateVersionId,
                LogicalQuestionId = Id(106),
                OrderIndex = 1,
                DisplayLabel = "1",
                QuestionText = "Question",
                QuestionType = "exact_short_text",
                GradingMode = "deterministic",
                MaxPointsMilli = 1_000,
                PointIncrementMilli = 1,
                TeacherVerified = true,
                CreatedAt = UtcNow.AddDays(-10),
                UpdatedAt = UtcNow,
            });
            db.TestSessions.Add(new TestSessionEntity
            {
                Id = sessionId,
                TemplateVersionId = templateVersionId,
                TestDate = DateOnly.FromDateTime(UtcNow.UtcDateTime),
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = UtcNow.AddDays(-2),
                UpdatedAt = UtcNow,
            });
            db.Submissions.Add(new SubmissionEntity
            {
                Id = submissionId,
                TestSessionId = sessionId,
                State = "needs_grade_review",
                UploadedByStaffUserId = staffId,
                UploadCompletedAt = UtcNow.AddHours(-3),
                CreatedAt = UtcNow.AddHours(-3),
                UpdatedAt = UtcNow,
            });

            var successful = Request(
                Id(20),
                profileId,
                submissionId,
                "succeeded",
                UtcNow.AddHours(-2),
                UtcNow.AddHours(-2).AddMinutes(1),
                UtcNow.AddHours(-2).AddMinutes(3),
                dispatchAttempt: 2);
            successful.ValidatedResponseJson =
                $"{{\"private\":\"{SecretStudentValue}\"}}";
            var rateLimited = Request(
                Id(21),
                profileId,
                Id(101),
                "retry_waiting",
                UtcNow.AddMinutes(-100),
                UtcNow.AddMinutes(-100).AddSeconds(30),
                completedAt: null,
                dispatchAttempt: 2,
                errorCode: "openrouter_rate_limited");
            var providerFailure = Request(
                Id(22),
                profileId,
                Id(102),
                "failed",
                UtcNow.AddMinutes(-80),
                UtcNow.AddMinutes(-80).AddSeconds(10),
                UtcNow.AddMinutes(-79),
                dispatchAttempt: 1,
                errorCode: "openrouter_provider_unavailable");
            var invalidOutput = Request(
                Id(23),
                profileId,
                Id(103),
                "invalid_output",
                UtcNow.AddMinutes(-60),
                UtcNow.AddMinutes(-60).AddSeconds(20),
                UtcNow.AddMinutes(-59),
                dispatchAttempt: 1,
                errorCode: "ai_response_semantics_invalid");
            var ambiguous = Request(
                Id(24),
                profileId,
                Id(104),
                "failed",
                UtcNow.AddMinutes(-40),
                UtcNow.AddMinutes(-40).AddSeconds(15),
                UtcNow.AddMinutes(-39).AddSeconds(-15),
                dispatchAttempt: 1,
                errorCode: "ai_dispatch_outcome_unknown",
                possibleDuplicate: true,
                safeErrorDetail: SecretStudentValue);
            var old = Request(
                Id(25),
                profileId,
                Id(105),
                "succeeded",
                UtcNow.AddDays(-91),
                UtcNow.AddDays(-91).AddMinutes(1),
                UtcNow.AddDays(-91).AddMinutes(2),
                dispatchAttempt: 1);
            db.AiRequests.AddRange(
                successful,
                rateLimited,
                providerFailure,
                invalidOutput,
                ambiguous,
                old);
            db.AiUsage.AddRange(
                Usage(Id(30), successful.Id, 100, 5, 50, 10, 160, 1_000),
                Usage(
                    Id(31),
                    invalidOutput.Id,
                    20,
                    0,
                    10,
                    2,
                    32,
                    200),
                Usage(
                    Id(32),
                    old.Id,
                    999,
                    0,
                    999,
                    0,
                    1_998,
                    99_999));

            var completedAt = successful.CompletedAt!.Value;
            db.GradingRuns.Add(new GradingRunEntity
            {
                Id = runId,
                SubmissionId = submissionId,
                RunNumber = 1,
                TemplateVersionId = templateVersionId,
                Reason = "initial",
                State = "needs_grade_review",
                Provider = AiProviders.GeminiDirect,
                Model = "gemini-3.5-flash-lite",
                PromptVersion = "grade-v1",
                SchemaVersion = "grade-schema-v1",
                PipelineVersion = "metrics-test-v1",
                CanonicalInputManifestHash = new string('c', 64),
                EarnedPointsMilli = 500,
                PossiblePointsMilli = 1_000,
                CreatedAt = completedAt,
            });
            db.QuestionResults.Add(new QuestionResultEntity
            {
                Id = resultId,
                GradingRunId = runId,
                QuestionId = questionId,
                TranscribedAnswer = "initial answer",
                NormalizedAnswer = "initial answer",
                ProposedPointsMilli = 500,
                MaximumPointsMilli = 1_000,
                Outcome = "partial",
                Method = "ai_pilot",
                ConfidenceBasisPoints = 8_000,
                ReviewRequired = true,
                ReviewStatus = "resolved",
                CreatedAt = completedAt,
            });
            await db.SaveChangesAsync();

            var initialRevisionId = Id(40);
            var teacherRevisionId = Id(41);
            db.ResultRevisions.AddRange(
                new ResultRevisionEntity
                {
                    Id = initialRevisionId,
                    QuestionResultId = resultId,
                    RevisionNumber = 1,
                    AwardedPointsMilli = 500,
                    Outcome = "partial",
                    AnswerTextCorrection = "initial answer",
                    Source = "initial",
                    CreatedAt = completedAt,
                },
                new ResultRevisionEntity
                {
                    Id = teacherRevisionId,
                    QuestionResultId = resultId,
                    RevisionNumber = 2,
                    AwardedPointsMilli = 1_000,
                    Outcome = "correct",
                    AnswerTextCorrection = "corrected answer",
                    ReasonCode = "teacher_correction",
                    Source = "teacher_override",
                    ActorStaffUserId = staffId,
                    CreatedAt = completedAt.AddMinutes(5),
                    SupersedesRevisionId = initialRevisionId,
                });
            await db.SaveChangesAsync();

            var result = await db.QuestionResults.SingleAsync(
                item => item.Id == resultId);
            result.CurrentRevisionId = teacherRevisionId;
            var submission = await db.Submissions.SingleAsync(
                item => item.Id == submissionId);
            submission.CurrentGradingRunId = runId;
            await db.SaveChangesAsync();
        }

        private static AiRequestEntity Request(
            string id,
            string profileId,
            string entityId,
            string state,
            DateTimeOffset createdAt,
            DateTimeOffset? dispatchedAt,
            DateTimeOffset? completedAt,
            int dispatchAttempt,
            string? errorCode = null,
            bool possibleDuplicate = false,
            string? safeErrorDetail = null) =>
            new()
            {
                Id = id,
                RequestKey = $"request-{id}",
                AiTaskProfileId = profileId,
                TaskProfileRevision = 1,
                Purpose = AiTaskTypes.InitialGrading,
                EntityType = "submission",
                EntityId = entityId,
                EntityRevision = 1,
                InputManifestHash = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(id)))
                    .ToLowerInvariant(),
                State = state,
                DispatchAttempt = dispatchAttempt,
                PossibleDuplicate = possibleDuplicate,
                ActualModel = "gemini-3.5-flash-lite",
                ErrorCode = errorCode,
                SafeErrorDetail = safeErrorDetail,
                CreatedAt = createdAt,
                UpdatedAt = completedAt ?? dispatchedAt ?? createdAt,
                DispatchedAt = dispatchedAt,
                CompletedAt = completedAt,
            };

        private static AiUsageEntity Usage(
            string id,
            string requestId,
            int input,
            int cached,
            int output,
            int thinking,
            int total,
            long cost) =>
            new()
            {
                Id = id,
                AiRequestId = requestId,
                RequestedProvider = AiProviders.GeminiDirect,
                RequestedModel = "gemini-3.5-flash-lite",
                ActualProvider = AiProviders.GeminiDirect,
                ActualModel = "gemini-3.5-flash-lite",
                InputTokens = input,
                CachedTokens = cached,
                OutputTokens = output,
                ThinkingTokens = thinking,
                TotalTokens = total,
                EstimatedUsdMicros = cost,
                EstimatedJpyMicros = cost * 150,
                MeasuredAt = UtcNow,
            };

        private static string Id(int offset) =>
            UlidId.New(UtcNow.AddMilliseconds(offset));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "AiMetricsIntegrationTest";
        public const string RoleHeader = "X-Test-Role";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, "metrics-test-user"),
                    new Claim(ClaimTypes.Name, "metrics-test-user"),
                    new Claim(ClaimTypes.Role, role),
                ],
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            return Task.FromResult(
                AuthenticateResult.Success(
                    new AuthenticationTicket(
                        new ClaimsPrincipal(identity),
                        SchemeName)));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class UnusedSecretStore : IAiSecretStore
    {
        public Task<AiSecretReference> WriteAsync(
            string ownerId,
            long credentialRevision,
            ReadOnlyMemory<char> secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiSecretLease> ReadAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            AiSecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedProviderClient : IAiProviderClient
    {
        public string Provider => AiProviders.GeminiDirect;

        public Task<AiProviderResponse> GenerateAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            AiProviderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiCapabilityProbeResult> ProbeAsync(
            AiConnectionSettings connection,
            ReadOnlyMemory<byte> credentialUtf8,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPromptCatalog : IAiPromptBundleCatalog
    {
        public AiPromptBundle GetRequired(string taskType) =>
            throw new NotSupportedException();
    }
}
