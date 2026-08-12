using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OokiGrader.Ai.Abstractions;
using OokiGrader.Ai.Gemini;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Api;
using OokiGrader.Host.Common;
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class TemplateGenerationBatchServiceTests
{
    private static readonly (int FirstPage, int LastPage)[] ExpectedStepRanges =
        [(1, 2), (3, 4), (5, 6)];
    private static readonly string[] ExpectedStepSuffixes = ["-1", "-2", "-3"];

    [Fact]
    public async Task StepCreateAndGeneratePersistDeterministicUnitsAndOneJobPerUnit()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 6);
        var create = await fixture.Service.CreateAsync(
            new CreateTemplateGenerationBatchCommand(
                fixture.UploadId,
                TestType.Step,
                "算数",
                AnswerStyle: null,
                ExpectedSourceRowVersion: 1,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: Guid.NewGuid().ToString(),
                CorrelationId: "test-create"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Draft, create.Status);
        Assert.Equal(3, create.ExpectedUnitCount);
        Assert.Equal(
            ExpectedStepRanges,
            create.Units.Select(unit => (unit.FirstPage, unit.LastPage)));
        Assert.Equal(
            ExpectedStepSuffixes,
            create.Units.Select(unit => unit.Suffix));
        Assert.All(create.Units, unit =>
        {
            Assert.Equal(GradeLevel.Grade4, unit.FilenameGrade);
            Assert.Equal(GradeEvidence.FileName, unit.GradeEvidence);
            Assert.Null(unit.ExtractionJobId);
        });
        Assert.Empty(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());

        fixture.Db.ChangeTracker.Clear();
        var generating = await fixture.Service.GenerateAsync(
            new GenerateTemplateGenerationBatchCommand(
                create.Id,
                create.RowVersion,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: Guid.NewGuid().ToString(),
                CorrelationId: "test-generate"),
            CancellationToken.None);

        Assert.Equal(TemplateGenerationBatchStatus.Generating, generating.Status);
        Assert.All(
            generating.Units,
            unit =>
            {
                Assert.Equal(TemplateGenerationUnitStatus.Queued, unit.Status);
                Assert.NotNull(unit.ExtractionJobId);
            });
        var jobs = await fixture.Db.BackgroundJobs
            .AsNoTracking()
            .OrderBy(job => job.Id)
            .ToArrayAsync();
        Assert.Equal(3, jobs.Length);
        Assert.All(jobs, job =>
        {
            Assert.Equal(TemplateGenerationBatchService.UnitJobType, job.Type);
            Assert.Equal(1, job.SchemaVersion);
            using var payload = JsonDocument.Parse(job.PayloadJson);
            Assert.Equal(create.Id, payload.RootElement.GetProperty("batchId").GetString());
            Assert.Equal(
                64,
                payload.RootElement
                    .GetProperty("generationProfileHash")
                    .GetString()!
                    .Length);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    payload.RootElement.GetProperty("unitId").GetString()));
        });
    }

    [Fact]
    public async Task InvalidStepPageCountFailsBeforeBatchOrJobPersistence()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 8);

        var exception = await Assert.ThrowsAsync<
            TemplateGenerationBatchServiceException>(() =>
            fixture.Service.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    fixture.UploadId,
                    TestType.Step,
                    "算数",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: 1,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "test-invalid-step"),
                CancellationToken.None));

        Assert.Equal("STEP_PAGE_COUNT_NOT_DIVISIBLE_BY_SIX", exception.Code);
        Assert.Empty(
            await fixture.Db.TemplateGenerationBatches
                .AsNoTracking()
                .ToArrayAsync());
        Assert.Empty(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task OtherRequiresAnswerStyleBeforeSourceProcessing()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 1);

        var exception = await Assert.ThrowsAsync<
            TemplateGenerationBatchServiceException>(() =>
            fixture.Service.CreateAsync(
                new CreateTemplateGenerationBatchCommand(
                    fixture.UploadId,
                    TestType.Other,
                    "国語",
                    AnswerStyle: null,
                    ExpectedSourceRowVersion: 1,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "test-answer-style"),
                CancellationToken.None));

        Assert.Equal("ANSWER_STYLE_REQUIRED", exception.Code);
        Assert.Equal(0, fixture.PageCounter.InvocationCount);
    }

    [Fact]
    public async Task GenerateWithoutUsableExtractionProfileReturnsConflictWithoutQueuingJobs()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 6);
        var createOperationId = Guid.NewGuid().ToString();
        var create = await fixture.Service.CreateAsync(
            new CreateTemplateGenerationBatchCommand(
                fixture.UploadId,
                TestType.Step,
                "算数",
                AnswerStyle: null,
                ExpectedSourceRowVersion: 1,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: createOperationId,
                CorrelationId: "test-profile-preflight-create"),
            CancellationToken.None);

        var profile = await fixture.Db.AiTaskProfiles.SingleAsync();
        profile.Active = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<
            TemplateGenerationBatchServiceException>(() =>
            fixture.Service.GenerateAsync(
                new GenerateTemplateGenerationBatchCommand(
                    create.Id,
                    create.RowVersion,
                    fixture.StaffId,
                    IsAdministrator: false,
                    OperationId: Guid.NewGuid().ToString(),
                    CorrelationId: "test-profile-preflight-generate"),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
        Assert.Equal(
            "TEMPLATE_EXTRACTION_PROFILE_UNAVAILABLE",
            exception.Code);
        Assert.Equal(
            "テストひな形を生成するAI設定が利用できません",
            exception.Title);
        Assert.Contains("管理のAI設定", exception.Detail, StringComparison.Ordinal);
        Assert.Equal(create.RowVersion, exception.CurrentRowVersion);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TemplateGenerationBatches
            .AsNoTracking()
            .Include(item => item.Units)
            .SingleAsync(item => item.Id == create.Id);
        Assert.Equal(TemplateGenerationBatchStatus.Draft, stored.Status);
        Assert.Equal(create.RowVersion, stored.Revision);
        Assert.Equal(createOperationId, stored.CurrentOperationId);
        Assert.All(stored.Units, unit =>
        {
            Assert.Equal(TemplateGenerationUnitStatus.Pending, unit.Status);
            Assert.Null(unit.ExtractionJobId);
        });
        Assert.Empty(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());
        Assert.DoesNotContain(
            await fixture.Db.AuditEvents.AsNoTracking().ToArrayAsync(),
            item => item.EventType == "TemplateGenerationStarted");
    }

    [Fact]
    public async Task GenerateEndpointReturnsActionableConflictWhenProfileIsUnavailable()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 1);
        var create = await fixture.Service.CreateAsync(
            new CreateTemplateGenerationBatchCommand(
                fixture.UploadId,
                TestType.Hop,
                "算数",
                AnswerStyle: null,
                ExpectedSourceRowVersion: 1,
                fixture.StaffId,
                IsAdministrator: false,
                OperationId: Guid.NewGuid().ToString(),
                CorrelationId: "test-profile-endpoint-create"),
            CancellationToken.None);
        var profile = await fixture.Db.AiTaskProfiles.SingleAsync();
        profile.Active = false;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        using var host = await StartEndpointHostAsync(fixture);
        using var client = host.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            $"/api/v1/template-generation-batches/{create.Id}/generate",
            new { expectedRowVersion = create.RowVersion });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"\"rev-{create.RowVersion}\"", response.Headers.ETag?.Tag);
        using var problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "https://ooki-grader.local/problems/"
                + "template-extraction-profile-unavailable",
            problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            "TEMPLATE_EXTRACTION_PROFILE_UNAVAILABLE",
            problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "テストひな形を生成するAI設定が利用できません",
            problem.RootElement.GetProperty("title").GetString());
        Assert.Contains(
            "APIキーの接続確認",
            problem.RootElement.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        fixture.Db.ChangeTracker.Clear();
        var stored = await fixture.Db.TemplateGenerationBatches
            .AsNoTracking()
            .Include(item => item.Units)
            .SingleAsync(item => item.Id == create.Id);
        Assert.Equal(TemplateGenerationBatchStatus.Draft, stored.Status);
        Assert.All(stored.Units, unit =>
        {
            Assert.Equal(TemplateGenerationUnitStatus.Pending, unit.Status);
            Assert.Null(unit.ExtractionJobId);
        });
        Assert.Empty(await fixture.Db.BackgroundJobs.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ResumableEndpointReturnsOnlyOwnedActiveBatchesInStableOrderWithoutPii()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 1);
        var otherStaffId = await fixture.AddStaffAsync("other.teacher");
        var oldestId = await fixture.AddBatchAsync(
            TemplateGenerationBatchStatus.Draft,
            fixture.StaffId,
            fixture.Now.AddMinutes(-3));
        var sameTimeFirstId = await fixture.AddBatchAsync(
            TemplateGenerationBatchStatus.Generating,
            fixture.StaffId,
            fixture.Now.AddMinutes(-2));
        var sameTimeSecondId = await fixture.AddBatchAsync(
            TemplateGenerationBatchStatus.Failed,
            fixture.StaffId,
            fixture.Now.AddMinutes(-2));
        await fixture.AddBatchAsync(
            TemplateGenerationBatchStatus.Completed,
            fixture.StaffId,
            fixture.Now.AddMinutes(-1));
        await fixture.AddBatchAsync(
            TemplateGenerationBatchStatus.Generating,
            otherStaffId,
            fixture.Now);

        using var host = await StartEndpointHostAsync(fixture);
        using var client = host.GetTestClient();
        using var response = await client.GetAsync(
            "/api/v1/template-generation-batches/resumable?limit=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(20, payload.RootElement.GetProperty("limit").GetInt32());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        var expectedSameTimeOrder = new[]
            {
                sameTimeFirstId,
                sameTimeSecondId,
            }
            .OrderByDescending(id => id, StringComparer.Ordinal)
            .Append(oldestId)
            .ToArray();
        Assert.Equal(
            expectedSameTimeOrder,
            items.Select(item => item.GetProperty("id").GetString()));

        Assert.All(items, item =>
        {
            var id = item.GetProperty("id").GetString();
            Assert.Equal(
                $"/api/v1/template-generation-batches/{id}",
                item.GetProperty("detailUrl").GetString());
            Assert.True(item.TryGetProperty("rowVersion", out _));
            Assert.True(item.TryGetProperty("completedUnitCount", out _));
            Assert.False(item.TryGetProperty("createdByUserId", out _));
            Assert.False(item.TryGetProperty("sourceId", out _));
            Assert.False(item.TryGetProperty("sourceDisplayName", out _));
            Assert.False(item.TryGetProperty("units", out _));
        });
    }

    [Fact]
    public async Task ResumableEndpointLetsAdministratorSeeAllOwnersAndCapsLimit()
    {
        await using var fixture = await ServiceFixture.CreateAsync(pageCount: 1);
        var otherStaffId = await fixture.AddStaffAsync("admin.visible.teacher");
        var expectedNewestId = string.Empty;
        for (var index = 0; index < 55; index++)
        {
            expectedNewestId = await fixture.AddBatchAsync(
                TemplateGenerationBatchStatus.Generating,
                otherStaffId,
                fixture.Now.AddSeconds(index));
        }

        using var host = await StartEndpointHostAsync(
            fixture,
            isAdministrator: true);
        using var client = host.GetTestClient();
        using var response = await client.GetAsync(
            "/api/v1/template-generation-batches/resumable?limit=500");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(50, payload.RootElement.GetProperty("limit").GetInt32());
        var items = payload.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(50, items.Length);
        Assert.Equal(
            expectedNewestId,
            items[0].GetProperty("id").GetString());
    }

    private static async Task<IHost> StartEndpointHostAsync(
        ServiceFixture fixture,
        bool isAdministrator = false)
    {
        var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Features:Ai.TemplateGeneration"] = "true",
                    }))
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorizationBuilder()
                        .AddPolicy(
                            "teacher",
                            policy => policy.RequireAssertion(_ => true));
                    services.AddSingleton(fixture.Service);
                    services.AddSingleton(
                        (TemplateGenerationFinalizationService)
                            RuntimeHelpers.GetUninitializedObject(
                                typeof(TemplateGenerationFinalizationService)));
                });
                webBuilder.Configure(application =>
                {
                    application.Use(async (context, next) =>
                    {
                        var claims = new List<Claim>
                        {
                            new(
                                ClaimTypes.NameIdentifier,
                                fixture.StaffId),
                        };
                        if (isAdministrator)
                        {
                            claims.Add(new Claim(
                                ClaimTypes.Role,
                                "administrator"));
                        }

                        context.User = new ClaimsPrincipal(
                            new ClaimsIdentity(claims, "test"));
                        await next(context);
                    });
                    application.UseRouting();
                    application.UseAuthorization();
                    application.UseEndpoints(endpoints =>
                        endpoints.MapTemplateGenerationBatchEndpoints());
                });
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    private sealed class ServiceFixture : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly SqliteConnection _connection;
        private readonly ApprovedPromptBundleCatalog _promptCatalog;

        private ServiceFixture(
            string rootPath,
            SqliteConnection connection,
            OokiGraderDbContext db,
            TemplateGenerationBatchService service,
            CountingPageCountReader pageCounter,
            ApprovedPromptBundleCatalog promptCatalog,
            string staffId,
            string uploadId,
            DateTimeOffset now)
        {
            _rootPath = rootPath;
            _connection = connection;
            Db = db;
            Service = service;
            PageCounter = pageCounter;
            _promptCatalog = promptCatalog;
            StaffId = staffId;
            UploadId = uploadId;
            Now = now;
        }

        public OokiGraderDbContext Db { get; }
        public TemplateGenerationBatchService Service { get; }
        public CountingPageCountReader PageCounter { get; }
        public string StaffId { get; }
        public string UploadId { get; }
        public DateTimeOffset Now { get; }

        public async Task<string> AddStaffAsync(string username)
        {
            var id = UlidId.New(Now.AddHours(1));
            Db.StaffUsers.Add(new StaffUserEntity
            {
                Id = id,
                Username = username,
                UsernameNormalized = username.ToUpperInvariant(),
                DisplayName = "別の担当者",
                PasswordHash = "argon2id:test",
                PasswordAlgorithm = "argon2id",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = Now,
                CreatedAt = Now,
                UpdatedAt = Now,
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return id;
        }

        public async Task<string> AddBatchAsync(
            TemplateGenerationBatchStatus status,
            string createdByStaffUserId,
            DateTimeOffset createdAt)
        {
            var id = UlidId.New(createdAt);
            var isFinished = status is TemplateGenerationBatchStatus.Completed
                or TemplateGenerationBatchStatus.Cancelled;
            Db.TemplateGenerationBatches.Add(new TemplateGenerationBatchEntity
            {
                Id = id,
                Status = status,
                TestType = TestType.Hop,
                Subject = "理科",
                PromptSystem = TemplatePromptSystem.Standard,
                SourceId = UploadId,
                SourcePageCount = 1,
                ExpectedUnitCount = 1,
                CompletedUnitCount = status is
                    TemplateGenerationBatchStatus.NeedsFinalCheck
                    or TemplateGenerationBatchStatus.Confirming
                    or TemplateGenerationBatchStatus.Completed
                        ? 1
                        : 0,
                FailedUnitCount = status == TemplateGenerationBatchStatus.Failed
                    ? 1
                    : 0,
                CurrentOperationId = $"test-list-{id}",
                PlanHash = new string('c', 64),
                CreatedByUserId = createdByStaffUserId,
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                CompletedAt = isFinished ? createdAt : null,
                LastErrorCode = status == TemplateGenerationBatchStatus.Failed
                    ? "TEST_FAILURE"
                    : null,
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return id;
        }

        public static async Task<ServiceFixture> CreateAsync(int pageCount)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "ooki-template-generation-service-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            var connection = new SqliteConnection(
                $"Data Source={Path.Combine(rootPath, "test.db")};Foreign Keys=True;Pooling=False");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<OokiGraderDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new OokiGraderDbContext(options);
            await db.Database.MigrateAsync();

            var now = new DateTimeOffset(
                2026,
                8,
                9,
                0,
                0,
                0,
                TimeSpan.Zero);
            var staffId = UlidId.New(now);
            var uploadId = UlidId.New(now.AddMilliseconds(1));
            var fileObjectId = UlidId.New(now.AddMilliseconds(2));
            var connectionId = UlidId.New(now.AddMilliseconds(4));
            var profileId = UlidId.New(now.AddMilliseconds(5));
            var promptCatalog = new ApprovedPromptBundleCatalog();
            var bundle = promptCatalog.GetRequired(AiTaskTypes.TemplateExtraction);
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = staffId,
                Username = "template.teacher",
                UsernameNormalized = "template.teacher",
                DisplayName = "テンプレート担当",
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
                OriginalFileName = "STEP算数_小学4年.pdf",
                DeclaredMimeType = "application/pdf",
                ExpectedBytes = 1,
                CurrentBytes = 1,
                FinalSha256 = new string('a', 64),
                IncomingRelativePath = "incoming/source.part",
                State = "completed",
                ExpiresAt = now.AddHours(24),
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.FileObjects.Add(new FileObjectEntity
            {
                Id = fileObjectId,
                Sha256 = new string('a', 64),
                Bytes = 1,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = "template/source/aa/source.pdf",
                StorageClass = ContentStorageClass.TemplateSource.ToString(),
                RetentionClass = "template_source",
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            });
            db.FileReferences.Add(new FileReferenceEntity
            {
                Id = UlidId.New(now.AddMilliseconds(3)),
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
                SecretReference = "test-secret-reference",
                KeyFingerprint = new string('b', 64),
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
                Id = profileId,
                Name = "Template generation service fixture",
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

            var pageCounter = new CountingPageCountReader(pageCount);
            var timeProvider = new FixedTimeProvider(now.AddMinutes(1));
            var service = new TemplateGenerationBatchService(
                db,
                new MemoryContentStore(),
                pageCounter,
                new TemplateUnitPlanner(),
                new TestUlidGenerator(now.AddMinutes(1)),
                timeProvider,
                Options.Create(new TemplateGenerationBatchOptions()),
                promptCatalog,
                AiProviderFeaturePolicy.AllowAll);
            return new ServiceFixture(
                rootPath,
                connection,
                db,
                service,
                pageCounter,
                promptCatalog,
                staffId,
                uploadId,
                timeProvider.GetUtcNow());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            _promptCatalog.Dispose();
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestUlidGenerator(DateTimeOffset now) : IUlidGenerator
    {
        private long _sequence;

        public string NewId() => UlidId.New(now.AddTicks(_sequence++));
    }

    private sealed class MemoryContentStore : IContentStore
    {
        public Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream([0x25]));

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CountingPageCountReader(int pageCount)
        : IPdfPageCountReader
    {
        public int InvocationCount { get; private set; }

        public Task<int> GetPageCountAsync(
            Stream source,
            int maximumPages,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(pageCount);
        }
    }
}
