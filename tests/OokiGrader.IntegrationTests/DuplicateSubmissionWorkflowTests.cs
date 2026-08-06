using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Host.Api;
using OokiGrader.Host.Uploads;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class DuplicateSubmissionWorkflowTests
{
    [Fact]
    public async Task MatchingPageFingerprintsCreateAndResolveVisualDuplicateEvidence()
    {
        await using var application = await DuplicateTestApplication.CreateAsync();
        var graph = await application.SeedAsync(includeMatchingPageFingerprints: true);

        var conflict = await application.PostAsync(
            $"/api/v1/submissions/{graph.PendingSubmissionId}:assignStudent",
            new
            {
                studentId = graph.StudentId,
                sourceRevision = 1,
                reasonCode = "teacher_confirmed_handwriting",
            });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("possibleVisualDuplicate").GetBoolean());
        Assert.Equal(1, problem.GetProperty("visualHammingDistance").GetInt32());

        await application.WithDatabaseAsync(async db =>
        {
            var evidence = await db.VisualDuplicates
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal("possible", evidence.State);
            Assert.Equal(1, evidence.HammingDistance);
            Assert.Null(evidence.ResolvedAt);
        });

        var resolved = await application.PostAsync(
            $"/api/v1/submissions/{graph.PendingSubmissionId}:assignStudent",
            new
            {
                studentId = graph.StudentId,
                sourceRevision = 1,
                reasonCode = "teacher_resolved_duplicate",
                duplicateResolution = "additionalAttempt",
                attemptNumber = 2,
            });

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        await application.WithDatabaseAsync(async db =>
        {
            var evidence = await db.VisualDuplicates
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal("confirmed", evidence.State);
            Assert.NotNull(evidence.ResolvedAt);
            Assert.Equal(
                TestAuthenticationHandler.StaffId,
                evidence.ResolvedByStaffUserId);
        });
    }

    [Fact]
    public async Task AssignmentRequiresExplicitAttemptResolution()
    {
        await using var application = await DuplicateTestApplication.CreateAsync();
        var graph = await application.SeedAsync();

        var conflict = await application.PostAsync(
            $"/api/v1/submissions/{graph.PendingSubmissionId}:assignStudent",
            new
            {
                studentId = graph.StudentId,
                sourceRevision = 1,
                reasonCode = "teacher_confirmed_handwriting",
                note = "",
            });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "CANONICAL_SUBMISSION_DUPLICATE",
            problem.GetProperty("code").GetString());
        Assert.Equal(
            graph.ExistingSubmissionId,
            problem.GetProperty("existingSubmissionId").GetString());
        Assert.Equal(2, problem.GetProperty("nextAttemptNumber").GetInt32());

        var resolved = await application.PostAsync(
            $"/api/v1/submissions/{graph.PendingSubmissionId}:assignStudent",
            new
            {
                studentId = graph.StudentId,
                sourceRevision = 1,
                reasonCode = "teacher_resolved_duplicate",
                duplicateResolution = "additionalAttempt",
                attemptNumber = 2,
            });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var resolvedBody = await resolved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, resolvedBody.GetProperty("attemptNumber").GetInt32());
        Assert.False(
            resolvedBody.GetProperty("canonicalForSession").GetBoolean());

        await application.WithDatabaseAsync(async db =>
        {
            var existing = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.ExistingSubmissionId);
            var additional = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.PendingSubmissionId);
            Assert.True(existing.CanonicalForSession);
            Assert.Equal(1, existing.AttemptNumber);
            Assert.False(additional.CanonicalForSession);
            Assert.Equal(2, additional.AttemptNumber);
            Assert.Equal(graph.StudentId, additional.AssignedStudentId);
            Assert.Equal("grading", additional.State);
            Assert.Contains(
                await db.AuditEvents.AsNoTracking().ToListAsync(),
                item => item.EventType == "submission.duplicate_resolved"
                    && item.ObjectId == additional.Id);
        });
    }

    [Fact]
    public async Task ReplacementMovesPreviousCanonicalToNumberedAttempt()
    {
        await using var application = await DuplicateTestApplication.CreateAsync();
        var graph = await application.SeedAsync();

        var resolved = await application.PostAsync(
            $"/api/v1/submissions/{graph.PendingSubmissionId}:assignStudent",
            new
            {
                studentId = graph.StudentId,
                sourceRevision = 1,
                reasonCode = "teacher_resolved_duplicate",
                duplicateResolution = "replaceCanonical",
            });
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var existing = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.ExistingSubmissionId);
            var replacement = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.PendingSubmissionId);
            Assert.False(existing.CanonicalForSession);
            Assert.Equal(2, existing.AttemptNumber);
            Assert.True(replacement.CanonicalForSession);
            Assert.Equal(1, replacement.AttemptNumber);
        });
    }

    [Fact]
    public async Task ExactDuplicateCanCreateReferenceOnlyAdditionalAttempt()
    {
        await using var application = await DuplicateTestApplication.CreateAsync();
        var graph = await application.SeedAsync(includeDuplicateUpload: true);

        var resolved = await application.PostAsync(
            $"/api/v1/uploads/{graph.DuplicateUploadId}:resolveDuplicate",
            new { action = "createAttempt" });
        Assert.Equal(HttpStatusCode.Accepted, resolved.StatusCode);
        var body = await resolved.Content.ReadFromJsonAsync<JsonElement>();
        var newSubmissionId = body.GetProperty("submissionId").GetString();
        Assert.NotNull(newSubmissionId);
        Assert.Equal(2, body.GetProperty("attemptNumber").GetInt32());

        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(2, await db.Submissions.CountAsync());
            var additional = await db.Submissions
                .AsNoTracking()
                .SingleAsync(item => item.Id == newSubmissionId);
            Assert.Null(additional.AssignedStudentId);
            Assert.False(additional.CanonicalForSession);
            Assert.Equal("needs_name_review", additional.State);
            Assert.Equal(graph.FileObjectId, additional.OriginalFileObjectId);

            var fileObject = await db.FileObjects
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.FileObjectId);
            Assert.Equal(2, fileObject.ReferenceCountCache);
            Assert.Equal(
                2,
                await db.FileReferences.CountAsync(
                    item => item.FileObjectId == graph.FileObjectId));
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.DuplicateUploadId);
            Assert.Equal("completed", upload.State);
            Assert.Equal("submission", upload.DestinationType);
            Assert.Equal(newSubmissionId, upload.DestinationId);
        });
    }

    [Fact]
    public async Task FinalizeDetectsExactContentBeforeCreatingSubmission()
    {
        await using var application = await DuplicateTestApplication.CreateAsync();
        var graph = await application.SeedFinalizingDuplicateAsync();

        var finalized = await application.PostAsync(
            $"/api/v1/uploads/{graph.DuplicateUploadId}:finalize",
            new { });
        Assert.Equal(HttpStatusCode.Conflict, finalized.StatusCode);
        var problem = await finalized.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EXACT_DUPLICATE", problem.GetProperty("code").GetString());
        Assert.Equal(
            graph.ExistingSubmissionId,
            problem.GetProperty("existingSubmissionId").GetString());

        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(1, await db.Submissions.CountAsync());
            Assert.Equal(1, await db.FileReferences.CountAsync());
            var upload = await db.UploadSessions
                .AsNoTracking()
                .SingleAsync(item => item.Id == graph.DuplicateUploadId);
            Assert.Equal("duplicate_pending", upload.State);
            Assert.Equal("duplicate_submission", upload.DestinationType);
            Assert.Equal(graph.ExistingSubmissionId, upload.DestinationId);
            Assert.Contains(
                await db.AuditEvents.AsNoTracking().ToListAsync(),
                item => item.EventType == "upload.duplicate_detected"
                    && item.ObjectId == upload.Id);
        });
    }

    private sealed class DuplicateTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;
        private readonly string _incomingRoot;

        private DuplicateTestApplication(
            IHost host,
            SqliteConnection connection,
            string incomingRoot)
        {
            _host = host;
            _connection = connection;
            _incomingRoot = incomingRoot;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<DuplicateTestApplication> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var incomingRoot = Path.Combine(
                Path.GetTempPath(),
                $"ooki-duplicate-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(incomingRoot);
            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(
                            new Dictionary<string, string?>
                            {
                                ["Data:Incoming"] = incomingRoot,
                            });
                    });
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDataProtection();
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton(connection);
                        services.AddSingleton<UploadLockProvider>();
                        services.AddSingleton<IContentStore, TestContentStore>();
                        services.AddDbContext<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services
                            .AddAuthentication(TestAuthenticationHandler.SchemeName)
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
                                "teacher",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("teacher", "administrator"))
                            .AddPolicy(
                                "results",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole("teacher", "administrator"))
                            .AddPolicy(
                                "upload",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole(
                                        "teacher",
                                        "administrator",
                                        "scanOperator"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapSubmissionsEndpoints();
                            endpoints.MapUploadsEndpoints();
                        });
                    });
                });
            var host = hostBuilder.Build();
            await using (var scope = host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<OokiGraderDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            await host.StartAsync();
            return new DuplicateTestApplication(
                host,
                connection,
                incomingRoot);
        }

        public async Task<SeededDuplicateGraph> SeedAsync(
            bool includeDuplicateUpload = false,
            bool includeMatchingPageFingerprints = false)
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = TestAuthenticationHandler.StaffId;
            var template = new TestTemplateEntity
            {
                Id = UlidId.New(now),
                Title = "重複確認テスト",
                State = "active",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var version = new TemplateVersionEntity
            {
                Id = UlidId.New(now),
                TestTemplateId = template.Id,
                VersionNumber = 1,
                State = "published",
                PublishedAt = now,
                PublishedByStaffUserId = staffId,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var question = new QuestionEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = version.Id,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = 1,
                DisplayLabel = "問1",
                QuestionText = "答えなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "manual",
                MaxPointsMilli = 1_000,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var session = new TestSessionEntity
            {
                Id = UlidId.New(now),
                TemplateVersionId = version.Id,
                TestDate = DateOnly.FromDateTime(now.UtcDateTime),
                Priority = "economy",
                State = "open",
                CreatedByStaffUserId = staffId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var student = new StudentEntity
            {
                Id = UlidId.New(now),
                StudentNumber = "S-001",
                StudentNumberNormalized = "S-001",
                FamilyName = "大木",
                GivenName = "花子",
                FamilyNameNormalized = "大木",
                GivenNameNormalized = "花子",
                FamilyNameKana = "オオキ",
                GivenNameKana = "ハナコ",
                FamilyNameKanaNormalized = "オオキ",
                GivenNameKanaNormalized = "ハナコ",
                DisplayName = "大木 花子",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var fileObject = new FileObjectEntity
            {
                Id = UlidId.New(now),
                Sha256 = new string('b', 64),
                Bytes = 1_024,
                VerifiedMime = "application/pdf",
                Extension = "pdf",
                RelativeObjectPath = "managed/bb/scan.pdf",
                StorageClass = ContentStorageClass.ManagedScanOriginal.ToString(),
                RetentionClass = "submitted_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            };
            var existing = Submission(
                UlidId.New(now),
                session.Id,
                fileObject.Id,
                staffId,
                now);
            existing.AssignedStudentId = student.Id;
            existing.AssignmentMethod = "teacher";
            existing.CanonicalForSession = true;
            var pending = Submission(
                UlidId.New(now),
                session.Id,
                fileObject.Id,
                staffId,
                now.AddSeconds(1));
            var existingReference = new FileReferenceEntity
            {
                Id = UlidId.New(now),
                FileObjectId = fileObject.Id,
                OwnerType = "submission",
                OwnerId = existing.Id,
                Purpose = "original_scan",
                RetentionAnchorAt = now,
                CreatedAt = now,
            };
            var uploadId = UlidId.New(now);

            await WithDatabaseAsync(async db =>
            {
                db.AddRange(
                    template,
                    version,
                    question,
                    session,
                    student,
                    fileObject,
                    existing);
                if (!includeDuplicateUpload)
                {
                    db.Submissions.Add(pending);
                }

                db.FileReferences.Add(existingReference);
                if (includeMatchingPageFingerprints)
                {
                    var pendingReference = new FileReferenceEntity
                    {
                        Id = UlidId.New(now),
                        FileObjectId = fileObject.Id,
                        OwnerType = "submission",
                        OwnerId = pending.Id,
                        Purpose = "normalized_page",
                        RetentionAnchorAt = now,
                        CreatedAt = now,
                    };
                    db.FileReferences.Add(pendingReference);
                    db.SubmissionPages.AddRange(
                        Page(
                            existing.Id,
                            existingReference.Id,
                            "0000000000000000",
                            now),
                        Page(
                            pending.Id,
                            pendingReference.Id,
                            "0000000000000001",
                            now.AddSeconds(1)));
                }

                if (includeDuplicateUpload)
                {
                    db.UploadSessions.Add(new UploadSessionEntity
                    {
                        Id = uploadId,
                        CreatedByStaffUserId = staffId,
                        Purpose = "completed_test",
                        TestSessionId = session.Id,
                        DestinationType = "duplicate_submission",
                        DestinationId = existing.Id,
                        OriginalFileName = "same.pdf",
                        DeclaredMimeType = "application/pdf",
                        ExpectedBytes = fileObject.Bytes,
                        CurrentBytes = fileObject.Bytes,
                        FinalSha256 = fileObject.Sha256,
                        IncomingRelativePath = string.Empty,
                        State = "duplicate_pending",
                        ExpiresAt = now.AddHours(24),
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }

                await db.SaveChangesAsync();
            });

            return new SeededDuplicateGraph(
                student.Id,
                existing.Id,
                pending.Id,
                uploadId,
                fileObject.Id);
        }

        public async Task<SeededDuplicateGraph> SeedFinalizingDuplicateAsync()
        {
            var graph = await SeedAsync(includeDuplicateUpload: true);
            await WithDatabaseAsync(async db =>
            {
                var upload = await db.UploadSessions.SingleAsync(
                    item => item.Id == graph.DuplicateUploadId);
                upload.State = "uploading";
                upload.DestinationType = null;
                upload.DestinationId = null;
                upload.FinalSha256 = null;
                upload.IncomingRelativePath = $"{upload.Id}.part";
                var bytes = System.Text.Encoding.ASCII.GetBytes(
                    "%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF\n");
                upload.ExpectedBytes = bytes.Length;
                upload.CurrentBytes = bytes.Length;
                await File.WriteAllBytesAsync(
                    Path.Combine(_incomingRoot, upload.IncomingRelativePath),
                    bytes);
                await db.SaveChangesAsync();
            });
            return graph;
        }

        public async Task<HttpResponseMessage> PostAsync(
            string path,
            object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add(
                TestAuthenticationHandler.RoleHeader,
                "teacher");
            request.Content = JsonContent.Create(body);
            return await Client.SendAsync(request);
        }

        public async Task WithDatabaseAsync(
            Func<OokiGraderDbContext, Task> action)
        {
            await using var scope = _host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider
                .GetRequiredService<OokiGraderDbContext>();
            await action(db);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
            if (Directory.Exists(_incomingRoot))
            {
                Directory.Delete(_incomingRoot, recursive: true);
            }
        }

        private static SubmissionEntity Submission(
            string id,
            string sessionId,
            string fileObjectId,
            string staffId,
            DateTimeOffset now) =>
            new()
            {
                Id = id,
                TestSessionId = sessionId,
                State = "needs_name_review",
                ScanPayloadState = "scan_available",
                AssignmentMethod = "none",
                AttemptNumber = 1,
                CanonicalForSession = false,
                UploadedByStaffUserId = staffId,
                OriginalFileName = "same.pdf",
                OriginalFileObjectId = fileObjectId,
                UploadCompletedAt = now,
                QualitySummaryJson =
                    """{"pipeline":"safe-ingest-v1","status":"accepted"}""",
                CreatedAt = now,
                UpdatedAt = now,
            };

        private static SubmissionPageEntity Page(
            string submissionId,
            string fileReferenceId,
            string perceptualHash,
            DateTimeOffset now) =>
            new()
            {
                Id = UlidId.New(now),
                SubmissionId = submissionId,
                PageNumber = 1,
                NormalizedFileReferenceId = fileReferenceId,
                ThumbnailFileReferenceId = fileReferenceId,
                WidthPixels = 1_000,
                HeightPixels = 1_400,
                RotationDegrees = 0,
                SourceSha256 = new string('c', 64),
                NormalizedSha256 = new string('d', 64),
                DifferenceHash = perceptualHash,
                PerceptualHash = perceptualHash,
                QualityState = "accepted",
                BlurBasisPoints = 0,
                ContrastBasisPoints = 10_000,
                BrightnessBasisPoints = 5_000,
                InkCoverageBasisPoints = 500,
                AlignmentState = "not_configured",
                CreatedAt = now,
            };
    }

    private sealed record SeededDuplicateGraph(
        string StudentId,
        string ExistingSubmissionId,
        string PendingSubmissionId,
        string DuplicateUploadId,
        string FileObjectId);

    private sealed class TestContentStore : IContentStore
    {
        public async Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            await source.CopyToAsync(Stream.Null, cancellationToken);
            return new ContentWriteResult(
                new ContentObjectLocator(
                    storageClass,
                    new string('b', 64),
                    source.Length,
                    verifiedExtension),
                "managed/bb/scan.pdf",
                Deduplicated: true);
        }

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

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(
            options,
            logger,
            encoder)
    {
        public const string SchemeName = "DuplicateIntegrationTest";
        public const string RoleHeader = "X-Test-Role";
        public const string StaffId = "01J00000000000000000000000";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers[RoleHeader].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, StaffId),
                new Claim(ClaimTypes.Name, "integration-teacher"),
                new Claim(ClaimTypes.Role, role),
            };
            var identity = new ClaimsIdentity(
                claims,
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
}
