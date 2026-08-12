using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
using OokiGrader.Application.Abstractions;
using OokiGrader.Application.Identifiers;
using OokiGrader.Domain.Templates;
using OokiGrader.Host.Api;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Services;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;

namespace OokiGrader.IntegrationTests;

public sealed class GradingWorkspaceEndpointsTests
{
    private static readonly byte[] CompositePdfBytes = Encoding.ASCII.GetBytes(
        "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n%%EOF\n");

    [Fact]
    public async Task WorkspaceReturnsCanonicalTwoPageStepAndRetainedMedia()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();

        var response = await application.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/grading-workspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.NotNull(response.Headers.ETag);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("理科6年STEPセット1-1", body.GetProperty("test")
            .GetProperty("title").GetString());
        Assert.Equal("理科", body.GetProperty("test")
            .GetProperty("subject").GetString());
        Assert.Equal("6年", body.GetProperty("test")
            .GetProperty("gradeLabel").GetString());
        Assert.Equal("STEP", body.GetProperty("test")
            .GetProperty("category").GetString());
        Assert.Equal("大木 花子", body.GetProperty("student")
            .GetProperty("displayName").GetString());
        Assert.Equal(2, body.GetProperty("submission")
            .GetProperty("pageCount").GetInt32());
        Assert.Equal(300, body.GetProperty("bulkConfirmationLimit").GetInt32());

        var pages = body.GetProperty("pages").EnumerateArray().ToArray();
        Assert.Equal(2, pages.Length);
        Assert.Equal([1, 2], pages.Select(page =>
            page.GetProperty("pageNumber").GetInt32()).ToArray());
        Assert.All(pages, page =>
        {
            Assert.True(page.GetProperty("available").GetBoolean());
            Assert.Contains("/api/v1/review/pages/", page
                .GetProperty("contentUrl").GetString()!, StringComparison.Ordinal);
            Assert.Contains("/thumbnail", page
                .GetProperty("thumbnailUrl").GetString()!, StringComparison.Ordinal);
            Assert.NotEqual(
                page.GetProperty("contentUrl").GetString(),
                page.GetProperty("thumbnailUrl").GetString());
        });

        var results = body.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);
        Assert.Equal("東京", results[0].GetProperty("transcription").GetString());
        Assert.Equal(1_000, results[0]
            .GetProperty("awardedPointsMilli").GetInt64());
        Assert.Equal(2, results[0]
            .GetProperty("sourceResultRevision").GetInt32());
        Assert.Equal("system_correction", results[0]
            .GetProperty("currentRevisionSource").GetString());
        Assert.Equal([1], results[0].GetProperty("pageNumbers")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal([2], results[1].GetProperty("pageNumbers")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        Assert.Equal(2, body.GetProperty("unresolvedSnapshot")
            .GetArrayLength());
        Assert.True(body.GetProperty("canBulkConfirm").GetBoolean());

        var originalPdf = body.GetProperty("originalPdf");
        Assert.True(originalPdf.GetProperty("available").GetBoolean());
        Assert.Equal(
            $"/api/v1/submissions/{graph.SubmissionId}/original-pdf",
            originalPdf.GetProperty("url").GetString());

        using var pdfRequest = WorkspaceTestApplication.Request(
            HttpMethod.Get,
            originalPdf.GetProperty("url").GetString()!);
        pdfRequest.Headers.Range = new RangeHeaderValue(0, 3);
        var pdfResponse = await application.Client.SendAsync(pdfRequest);
        Assert.Equal(HttpStatusCode.PartialContent, pdfResponse.StatusCode);
        Assert.Equal("application/pdf", pdfResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", pdfResponse.Content.Headers.ContentDisposition
            ?.DispositionType);
        Assert.Equal("answer.pdf", pdfResponse.Content.Headers.ContentDisposition
            ?.FileName);
        Assert.True(pdfResponse.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", pdfResponse.Headers.GetValues(
            "X-Content-Type-Options").Single());
        var pdfContentSecurityPolicy = pdfResponse.Headers.GetValues(
            "Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'self'", pdfContentSecurityPolicy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("frame-ancestors 'none'", pdfContentSecurityPolicy,
            StringComparison.Ordinal);
        Assert.Equal("%PDF"u8.ToArray(), await pdfResponse.Content.ReadAsByteArrayAsync());

        var thumbnailResponse = await application.GetAsync(
            pages[0].GetProperty("thumbnailUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, thumbnailResponse.StatusCode);
        Assert.Equal("image/png", thumbnailResponse.Content.Headers.ContentType?.MediaType);
        Assert.True(thumbnailResponse.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task OverridePreservesOmittedCorrectionAndPersistsExplicitBlank()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();
        var workspace = await application.ReadWorkspaceAsync(graph.SubmissionId);

        var preserve = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results/" +
            $"{graph.FirstResultId}:override",
            new
            {
                sourceResultRevision = 2,
                awardedPointsMilli = 1_000,
                outcome = "correct",
                transcriptionCorrection = (string?)null,
                reasonCode = "teacher_judgment",
                note = "点数のみ確認",
            },
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, preserve.StatusCode);

        var clearToBlank = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results/" +
            $"{graph.SecondResultId}:override",
            new
            {
                sourceResultRevision = 1,
                awardedPointsMilli = 0,
                outcome = "blank",
                transcriptionCorrection = string.Empty,
                reasonCode = "transcription_corrected",
                note = "無解答を確認",
            },
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, clearToBlank.StatusCode);

        await application.WithDatabaseAsync(async db =>
        {
            var first = await db.QuestionResults.AsNoTracking()
                .SingleAsync(item => item.Id == graph.FirstResultId);
            var firstRevision = await db.ResultRevisions.AsNoTracking()
                .SingleAsync(item => item.Id == first.CurrentRevisionId);
            Assert.Equal("東京", firstRevision.AnswerTextCorrection);

            var second = await db.QuestionResults.AsNoTracking()
                .SingleAsync(item => item.Id == graph.SecondResultId);
            var secondRevision = await db.ResultRevisions.AsNoTracking()
                .SingleAsync(item => item.Id == second.CurrentRevisionId);
            Assert.Equal(string.Empty, secondRevision.AnswerTextCorrection);
            Assert.Equal("blank", secondRevision.Outcome);
        });

        var refreshed = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var secondResult = refreshed.Body.GetProperty("results")
            .EnumerateArray()
            .Single(item => item.GetProperty("resultId").GetString()
                == graph.SecondResultId);
        Assert.Equal(string.Empty,
            secondResult.GetProperty("transcription").GetString());
        Assert.Equal("blank", secondResult.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task OverrideRejectsOutcomePointAndBlankTextMismatch()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();
        var workspace = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var path = $"/api/v1/submissions/{graph.SubmissionId}/results/" +
            $"{graph.FirstResultId}:override";

        var wrongPoints = await application.PostAsync(
            path,
            new
            {
                sourceResultRevision = 2,
                awardedPointsMilli = 0,
                outcome = "correct",
                transcriptionCorrection = "東京",
                reasonCode = "teacher_judgment",
                note = (string?)null,
            },
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongPoints.StatusCode);
        Assert.Equal("RESULT_OVERRIDE_INVALID",
            (await wrongPoints.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());

        var blankWithText = await application.PostAsync(
            path,
            new
            {
                sourceResultRevision = 2,
                awardedPointsMilli = 0,
                outcome = "blank",
                transcriptionCorrection = "東京",
                reasonCode = "teacher_judgment",
                note = (string?)null,
            },
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            blankWithText.StatusCode);

        await application.WithDatabaseAsync(async db =>
            Assert.Equal(3, await db.ResultRevisions.CountAsync()));
    }

    [Fact]
    public async Task BulkConfirmationIsAppendOnlyAtomicAndReplaySafe()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();
        var workspace = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var requestBody = ConfirmationBody(workspace.Body);
        var idempotencyKey = Guid.NewGuid().ToString();

        var response = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            requestBody,
            workspace.ETag,
            idempotencyKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        using var responseDocument = JsonDocument.Parse(responseText);
        var body = responseDocument.RootElement;
        Assert.Equal(2, body.GetProperty("confirmed").GetArrayLength());
        Assert.Equal(0, body.GetProperty("skipped").GetArrayLength());
        Assert.Equal("ready_to_finalize", body.GetProperty("submission")
            .GetProperty("state").GetString());
        Assert.Equal("ready_to_finalize", body.GetProperty("gradingRun")
            .GetProperty("state").GetString());
        Assert.Equal(3, body.GetProperty("gradingRun")
            .GetProperty("resultSourceRevision").GetInt64());
        Assert.True(body.GetProperty("canFinalize").GetBoolean());

        await application.WithDatabaseAsync(async db =>
        {
            var submission = await db.Submissions.AsNoTracking()
                .SingleAsync(item => item.Id == graph.SubmissionId);
            var run = await db.GradingRuns.AsNoTracking()
                .SingleAsync(item => item.Id == graph.GradingRunId);
            var results = await db.QuestionResults.AsNoTracking()
                .Where(item => item.GradingRunId == graph.GradingRunId)
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            var revisions = await db.ResultRevisions.AsNoTracking()
                .Where(item => results.Select(result => result.Id)
                    .Contains(item.QuestionResultId))
                .OrderBy(item => item.QuestionResultId)
                .ThenBy(item => item.RevisionNumber)
                .ToArrayAsync();

            Assert.Equal("ready_to_finalize", submission.State);
            Assert.Equal("ready_to_finalize", run.State);
            Assert.Equal(1_000, run.EarnedPointsMilli);
            Assert.Equal(2_000, run.PossiblePointsMilli);
            Assert.All(results, item => Assert.Equal("resolved", item.ReviewStatus));
            Assert.Equal(5, revisions.Length);
            var confirmations = revisions
                .Where(item => item.ActorStaffUserId
                    == TestAuthenticationHandler.StaffId
                    && item.Source == "teacher_override")
                .ToArray();
            Assert.Equal(2, confirmations.Length);
            var firstConfirmation = confirmations.Single(item =>
                item.QuestionResultId == graph.FirstResultId);
            Assert.Equal(3, firstConfirmation.RevisionNumber);
            Assert.Equal(1_000, firstConfirmation.AwardedPointsMilli);
            Assert.Equal("correct", firstConfirmation.Outcome);
            Assert.Equal("東京", firstConfirmation.AnswerTextCorrection);
            Assert.Equal("ai_recheck", firstConfirmation.ReasonCode);
            Assert.Equal(graph.FirstEditedRevisionId,
                firstConfirmation.SupersedesRevisionId);
            Assert.Equal(2, await db.AuditEvents.CountAsync(item =>
                item.EventType == "result.confirmed"
                && item.ReasonCode == "bulk_teacher_confirmation"));
        });

        var replay = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            requestBody,
            workspace.ETag,
            idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues(
            "Idempotency-Replayed").Single());
        Assert.Equal(responseText, await replay.Content.ReadAsStringAsync());

        var semanticReplay = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            requestBody,
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.OK, semanticReplay.StatusCode);
        var semanticBody = await semanticReplay.Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, semanticBody.GetProperty("confirmed").GetArrayLength());
        Assert.All(semanticBody.GetProperty("skipped").EnumerateArray(), item =>
            Assert.Equal("RESULT_ALREADY_CONFIRMED",
                item.GetProperty("code").GetString()));

        await application.WithDatabaseAsync(async db =>
            Assert.Equal(5, await db.ResultRevisions.CountAsync()));
    }

    [Fact]
    public async Task BulkConfirmationRejectsStaleOrIncompleteSnapshotsWithoutWrites()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();
        var workspace = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var items = workspace.Body.GetProperty("unresolvedSnapshot")
            .EnumerateArray()
            .Select(item => new
            {
                resultId = item.GetProperty("resultId").GetString(),
                sourceResultRevision = item.GetProperty("sourceResultRevision")
                    .GetInt32(),
            })
            .ToArray();
        var incomplete = new
        {
            sourceSubmissionRevision = workspace.Body.GetProperty("submission")
                .GetProperty("revision").GetInt64(),
            gradingRunId = graph.GradingRunId,
            sourceResultSourceRevision = workspace.Body.GetProperty("gradingRun")
                .GetProperty("resultSourceRevision").GetInt64(),
            items = items.Take(1).ToArray(),
        };

        var incompleteResponse = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            incomplete,
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            incompleteResponse.StatusCode);
        var incompleteProblem = await incompleteResponse.Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BULK_CONFIRMATION_SNAPSHOT_STALE",
            incompleteProblem.GetProperty("code").GetString());
        Assert.Contains(incompleteProblem.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString()
                == "UNRESOLVED_RESULT_SET_CHANGED");

        var stale = new
        {
            incomplete.sourceSubmissionRevision,
            incomplete.gradingRunId,
            incomplete.sourceResultSourceRevision,
            items = items.Select((item, index) => new
            {
                item.resultId,
                sourceResultRevision = item.sourceResultRevision
                    + (index == 0 ? 1 : 0),
            }).ToArray(),
        };
        var staleResponse = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            stale,
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        var staleProblem = await staleResponse.Content
            .ReadFromJsonAsync<JsonElement>();
        Assert.Equal("BULK_CONFIRMATION_SNAPSHOT_STALE",
            staleProblem.GetProperty("code").GetString());
        Assert.Contains(staleProblem.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString()
                == "RESULT_REVISION_STALE");

        await application.WithDatabaseAsync(async db =>
        {
            Assert.Equal(3, await db.ResultRevisions.CountAsync());
            Assert.Equal(0, await db.AuditEvents.CountAsync());
            Assert.All(await db.QuestionResults.AsNoTracking().ToArrayAsync(),
                item => Assert.Equal("pending", item.ReviewStatus));
        });
    }

    [Fact]
    public async Task RetentionRemovesWorkspaceUrlsAndMediaReturnsGone()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();
        var before = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var firstThumbnailUrl = before.Body.GetProperty("pages")[0]
            .GetProperty("thumbnailUrl").GetString()!;

        await application.WithDatabaseAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var submission = await db.Submissions.SingleAsync(item =>
                item.Id == graph.SubmissionId);
            submission.ScanPayloadState = "scan_deleted";
            submission.ScanDeletedAt = now;
            submission.ScanDeletionReason = "age";
            submission.OriginalFileObjectId = null;
            var objects = await db.FileObjects
                .Where(item => item.StorageClass
                    == ContentStorageClass.ManagedScanDerived.ToString())
                .ToArrayAsync();
            foreach (var fileObject in objects)
            {
                fileObject.State = "deleted";
                fileObject.DeletedAt = now;
            }

            await db.SaveChangesAsync();
        });

        var pdfResponse = await application.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/original-pdf");
        Assert.Equal(HttpStatusCode.Gone, pdfResponse.StatusCode);
        Assert.Equal("SUBMISSION_PDF_UNAVAILABLE",
            (await pdfResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());
        var thumbnailResponse = await application.GetAsync(firstThumbnailUrl);
        Assert.Equal(HttpStatusCode.Gone, thumbnailResponse.StatusCode);

        var after = await application.ReadWorkspaceAsync(graph.SubmissionId);
        Assert.Equal(JsonValueKind.Null,
            after.Body.GetProperty("originalPdf").ValueKind);
        Assert.All(after.Body.GetProperty("pages").EnumerateArray(), page =>
        {
            Assert.False(page.GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null,
                page.GetProperty("contentUrl").ValueKind);
            Assert.Equal(JsonValueKind.Null,
                page.GetProperty("thumbnailUrl").ValueKind);
        });
    }

    [Fact]
    public async Task WorkspaceAndMutationEnforceRoleArchiveFinalizeAndVoidGuards()
    {
        await using var application = await WorkspaceTestApplication.CreateAsync();
        var graph = await application.SeedStepAsync();

        var unauthenticated = await application.Client.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/grading-workspace");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var forbidden = await application.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/grading-workspace",
            "scanOperator");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var workspace = await application.ReadWorkspaceAsync(graph.SubmissionId);
        var body = ConfirmationBody(workspace.Body);
        using (var missingKeyRequest = WorkspaceTestApplication.Request(
            HttpMethod.Post,
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved"))
        {
            missingKeyRequest.Headers.IfMatch.Add(workspace.ETag);
            missingKeyRequest.Content = JsonContent.Create(body);
            var missingKey = await application.Client.SendAsync(missingKeyRequest);
            Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED",
                (await missingKey.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());
        }

        await application.WithDatabaseAsync(async db =>
        {
            var session = await db.TestSessions.SingleAsync(item =>
                item.Id == graph.SessionId);
            session.State = "archived";
            await db.SaveChangesAsync();
        });
        var archived = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            body,
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
        Assert.Equal("TEST_SESSION_ARCHIVED_READ_ONLY",
            (await archived.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());

        await application.WithDatabaseAsync(async db =>
        {
            var session = await db.TestSessions.SingleAsync(item =>
                item.Id == graph.SessionId);
            session.State = "open";
            var submission = await db.Submissions.SingleAsync(item =>
                item.Id == graph.SubmissionId);
            submission.State = "finalized";
            submission.FinalizedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        var finalized = await application.PostAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/results:confirm-unresolved",
            body,
            workspace.ETag,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict, finalized.StatusCode);
        Assert.Equal("RESULT_FINALIZED",
            (await finalized.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("code").GetString());

        await application.WithDatabaseAsync(async db =>
        {
            var submission = await db.Submissions.SingleAsync(item =>
                item.Id == graph.SubmissionId);
            submission.State = "voided";
            submission.FinalizedAt = null;
            submission.VoidedAt = DateTimeOffset.UtcNow;
            submission.VoidedByStaffUserId = TestAuthenticationHandler.StaffId;
            submission.VoidReason = "duplicate";
            await db.SaveChangesAsync();
        });
        var voidedWorkspace = await application.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/grading-workspace");
        Assert.Equal(HttpStatusCode.NotFound, voidedWorkspace.StatusCode);
        var voidedPdf = await application.GetAsync(
            $"/api/v1/submissions/{graph.SubmissionId}/original-pdf");
        Assert.Equal(HttpStatusCode.NotFound, voidedPdf.StatusCode);
    }

    private static object ConfirmationBody(JsonElement workspace)
    {
        var items = workspace.GetProperty("unresolvedSnapshot")
            .EnumerateArray()
            .Select(item => new
            {
                resultId = item.GetProperty("resultId").GetString(),
                sourceResultRevision = item.GetProperty("sourceResultRevision")
                    .GetInt32(),
            })
            .ToArray();
        return new
        {
            sourceSubmissionRevision = workspace.GetProperty("submission")
                .GetProperty("revision").GetInt64(),
            gradingRunId = workspace.GetProperty("gradingRun")
                .GetProperty("id").GetString(),
            sourceResultSourceRevision = workspace.GetProperty("gradingRun")
                .GetProperty("resultSourceRevision").GetInt64(),
            items,
        };
    }

    private sealed class WorkspaceTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;

        private WorkspaceTestApplication(
            IHost host,
            SqliteConnection connection,
            TestContentStore contentStore)
        {
            _host = host;
            _connection = connection;
            ContentStore = contentStore;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }
        public TestContentStore ContentStore { get; }

        public static async Task<WorkspaceTestApplication> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var contentStore = new TestContentStore();
            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDataProtection();
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton<IdempotencyLockProvider>();
                        services.AddSingleton(connection);
                        services.AddSingleton(contentStore);
                        services.AddSingleton<IContentStore>(contentStore);
                        services.AddDbContext<OokiGraderDbContext>(options =>
                            options.UseSqlite(connection));
                        services
                            .AddAuthentication(TestAuthenticationHandler.SchemeName)
                            .AddScheme<
                                AuthenticationSchemeOptions,
                                TestAuthenticationHandler>(
                                TestAuthenticationHandler.SchemeName,
                                _ => { });
                        services.AddAuthorizationBuilder()
                            .SetFallbackPolicy(new AuthorizationPolicyBuilder(
                                    TestAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser()
                                .Build())
                            .AddPolicy("teacher", policy => policy
                                .AddAuthenticationSchemes(
                                    TestAuthenticationHandler.SchemeName)
                                .RequireRole("teacher", "administrator"))
                            .AddPolicy("review", policy => policy
                                .AddAuthenticationSchemes(
                                    TestAuthenticationHandler.SchemeName)
                                .RequireRole("teacher", "administrator"))
                            .AddPolicy("results", policy => policy
                                .AddAuthenticationSchemes(
                                    TestAuthenticationHandler.SchemeName)
                                .RequireRole("teacher", "administrator"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseAuthorization();
                        application.UseMiddleware<SecurityHeadersMiddleware>();
                        application.UseMiddleware<IdempotencyMiddleware>();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapReviewEndpoints();
                            endpoints.MapResultsEndpoints();
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
            return new WorkspaceTestApplication(host, connection, contentStore);
        }

        public async Task<SeededWorkspaceGraph> SeedStepAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var staffId = TestAuthenticationHandler.StaffId;
            var templateId = UlidId.New(now);
            var versionId = UlidId.New(now);
            var firstQuestionId = UlidId.New(now);
            var secondQuestionId = UlidId.New(now);
            var sessionId = UlidId.New(now);
            var studentId = UlidId.New(now);
            var submissionId = UlidId.New(now);
            var runId = UlidId.New(now);
            var firstResultId = UlidId.New(now);
            var secondResultId = UlidId.New(now);
            var firstInitialRevisionId = UlidId.New(now);
            var firstEditedRevisionId = UlidId.New(now);
            var secondInitialRevisionId = UlidId.New(now);
            var originalLocator = ContentStore.Add(
                CompositePdfBytes,
                ContentStorageClass.ManagedScanOriginal,
                "pdf");
            var originalObject = FileObject(
                UlidId.New(now),
                originalLocator,
                "application/pdf",
                now);
            var originalReference = FileReference(
                UlidId.New(now),
                originalObject.Id,
                "submission",
                submissionId,
                "original_scan",
                now);
            var pageData = Enumerable.Range(1, 2)
                .Select(pageNumber => CreatePageData(
                    submissionId,
                    pageNumber,
                    now.AddTicks(pageNumber)))
                .ToArray();
            var firstQuestionRegion = Region(
                UlidId.New(now), firstQuestionId, 1, "question", now);
            var firstAnswerRegion = Region(
                UlidId.New(now), firstQuestionId, 1, "answer", now);
            var secondQuestionRegion = Region(
                UlidId.New(now), secondQuestionId, 2, "question", now);
            var secondAnswerRegion = Region(
                UlidId.New(now), secondQuestionId, 2, "answer", now);

            await WithDatabaseAsync(async db =>
            {
                db.TestTemplates.Add(new TestTemplateEntity
                {
                    Id = templateId,
                    Title = "template fallback",
                    Subject = "fallback subject",
                    Category = "fallback category",
                    GradeLabel = "fallback grade",
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
                    TargetTotalPointsMilli = 2_000,
                    PipelineVersion = "template-v1",
                    PublishedByStaffUserId = staffId,
                    PublishedAt = now,
                    ContentHash = new string('a', 64),
                    TestType = TestType.Step,
                    StepSetIndex = 1,
                    StepVariationIndex = 1,
                    PrintedTestName = "理科6年STEPセット1-1",
                    ExpectedSubmissionPageCount = 2,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.Regions.AddRange(
                    firstQuestionRegion,
                    firstAnswerRegion,
                    secondQuestionRegion,
                    secondAnswerRegion);
                db.Questions.AddRange(
                    Question(
                        firstQuestionId,
                        versionId,
                        1,
                        "問1",
                        firstQuestionRegion.Id,
                        firstAnswerRegion.Id,
                        now),
                    Question(
                        secondQuestionId,
                        versionId,
                        2,
                        "問2",
                        secondQuestionRegion.Id,
                        secondAnswerRegion.Id,
                        now));
                db.AcceptedAnswers.AddRange(
                    Answer(UlidId.New(now), firstQuestionId, "東京", now),
                    Answer(UlidId.New(now), secondQuestionId, "大阪", now));
                db.TestSessions.Add(new TestSessionEntity
                {
                    Id = sessionId,
                    TemplateVersionId = versionId,
                    TitleOverride = "旧実施名は答案画面に表示しない",
                    TemplateTitleSnapshot = "理科6年STEPセット1-1",
                    TemplateSubjectSnapshot = "理科",
                    TemplateGradeLabelSnapshot = "6年",
                    TemplateCategorySnapshot = "STEP",
                    TemplateCourseSnapshot = "標準",
                    TestDate = new DateOnly(2026, 8, 11),
                    ClassLabel = "6年A組",
                    Priority = "economy",
                    State = "open",
                    CreatedByStaffUserId = staffId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.Students.Add(new StudentEntity
                {
                    Id = studentId,
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
                    SchoolClass = "6年A組",
                    Course = "標準",
                    GradeLabel = "6年",
                    Status = "active",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.FileObjects.Add(originalObject);
                db.FileObjects.AddRange(pageData.SelectMany(page =>
                    new[] { page.NormalizedObject, page.ThumbnailObject }));
                db.FileReferences.Add(originalReference);
                db.FileReferences.AddRange(pageData.SelectMany(page =>
                    new[] { page.NormalizedReference, page.ThumbnailReference }));
                db.Submissions.Add(new SubmissionEntity
                {
                    Id = submissionId,
                    TestSessionId = sessionId,
                    State = "needs_grade_review",
                    ScanPayloadState = "scan_available",
                    AssignedStudentId = studentId,
                    AssignmentMethod = "teacher",
                    AttemptNumber = 1,
                    CanonicalForSession = true,
                    UploadedByStaffUserId = staffId,
                    OriginalFileName = "student-name.pdf",
                    OriginalFileObjectId = originalObject.Id,
                    UploadCompletedAt = now,
                    PreprocessingPipelineVersion = "preprocess-v1",
                    PreprocessingManifestHash = new string('b', 64),
                    PreprocessingCompletedAt = now,
                    PageCount = 2,
                    QualitySummaryJson = "{}",
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.SubmissionPages.AddRange(pageData.Select(page => page.Page));
                db.GradingRuns.Add(new GradingRunEntity
                {
                    Id = runId,
                    SubmissionId = submissionId,
                    RunNumber = 1,
                    TemplateVersionId = versionId,
                    Reason = "initial",
                    State = "needs_grade_review",
                    PipelineVersion = "grading-v1",
                    CanonicalInputManifestHash = new string('c', 64),
                    EarnedPointsMilli = 1_000,
                    PossiblePointsMilli = 2_000,
                    ResultSourceRevision = 2,
                    CreatedAt = now,
                    FinishedAt = now,
                    ActivatedAt = now,
                });
                db.QuestionResults.AddRange(
                    new QuestionResultEntity
                    {
                        Id = firstResultId,
                        GradingRunId = runId,
                        QuestionId = firstQuestionId,
                        TranscribedAnswer = "東京?",
                        NormalizedAnswer = "東京",
                        ProposedPointsMilli = 500,
                        MaximumPointsMilli = 1_000,
                        Outcome = "partial",
                        Method = "ai",
                        ConfidenceBasisPoints = 7_000,
                        ReasonCode = "low_confidence",
                        Explanation = "AIの再確認済み",
                        ReviewRequired = true,
                        ReviewStatus = "pending",
                        CreatedAt = now,
                    },
                    new QuestionResultEntity
                    {
                        Id = secondResultId,
                        GradingRunId = runId,
                        QuestionId = secondQuestionId,
                        TranscribedAnswer = "京都",
                        NormalizedAnswer = "京都",
                        ProposedPointsMilli = 0,
                        MaximumPointsMilli = 1_000,
                        Outcome = "incorrect",
                        Method = "ai",
                        ConfidenceBasisPoints = 7_500,
                        ReasonCode = "answer_mismatch",
                        Explanation = "模範解答と不一致",
                        ReviewRequired = true,
                        ReviewStatus = "pending",
                        CreatedAt = now,
                    });
                await db.SaveChangesAsync();

                db.ResultRevisions.AddRange(
                    new ResultRevisionEntity
                    {
                        Id = firstInitialRevisionId,
                        QuestionResultId = firstResultId,
                        RevisionNumber = 1,
                        AwardedPointsMilli = 500,
                        Outcome = "partial",
                        AnswerTextCorrection = "東京?",
                        ReasonCode = "low_confidence",
                        Source = "initial",
                        CreatedAt = now,
                    },
                    new ResultRevisionEntity
                    {
                        Id = secondInitialRevisionId,
                        QuestionResultId = secondResultId,
                        RevisionNumber = 1,
                        AwardedPointsMilli = 0,
                        Outcome = "incorrect",
                        AnswerTextCorrection = "京都",
                        ReasonCode = "answer_mismatch",
                        Source = "initial",
                        CreatedAt = now,
                    });
                await db.SaveChangesAsync();
                db.ResultRevisions.Add(new ResultRevisionEntity
                {
                    Id = firstEditedRevisionId,
                    QuestionResultId = firstResultId,
                    RevisionNumber = 2,
                    AwardedPointsMilli = 1_000,
                    Outcome = "correct",
                    AnswerTextCorrection = "東京",
                    ReasonCode = "ai_recheck",
                    TeacherNote = "AI再確認結果",
                    Source = "system_correction",
                    CreatedAt = now.AddSeconds(1),
                    SupersedesRevisionId = firstInitialRevisionId,
                });
                await db.SaveChangesAsync();

                var submission = await db.Submissions.SingleAsync(item =>
                    item.Id == submissionId);
                submission.CurrentGradingRunId = runId;
                var firstResult = await db.QuestionResults.SingleAsync(item =>
                    item.Id == firstResultId);
                firstResult.CurrentRevisionId = firstEditedRevisionId;
                var secondResult = await db.QuestionResults.SingleAsync(item =>
                    item.Id == secondResultId);
                secondResult.CurrentRevisionId = secondInitialRevisionId;
                await db.SaveChangesAsync();
            });

            return new SeededWorkspaceGraph(
                submissionId,
                sessionId,
                runId,
                firstResultId,
                secondResultId,
                firstEditedRevisionId);
        }

        public static HttpRequestMessage Request(
            HttpMethod method,
            string path,
            string role = "teacher")
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);
            return request;
        }

        public async Task<HttpResponseMessage> GetAsync(
            string path,
            string role = "teacher")
        {
            using var request = Request(HttpMethod.Get, path, role);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostAsync(
            string path,
            object body,
            EntityTagHeaderValue etag,
            string idempotencyKey,
            string role = "teacher")
        {
            using var request = Request(HttpMethod.Post, path, role);
            request.Headers.IfMatch.Add(etag);
            request.Headers.Add("Idempotency-Key", idempotencyKey);
            request.Content = JsonContent.Create(body);
            return await Client.SendAsync(request);
        }

        public async Task<(JsonElement Body, EntityTagHeaderValue ETag)>
            ReadWorkspaceAsync(string submissionId)
        {
            var response = await GetAsync(
                $"/api/v1/submissions/{submissionId}/grading-workspace");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (
                await response.Content.ReadFromJsonAsync<JsonElement>(),
                response.Headers.ETag!);
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
        }

        private PageData CreatePageData(
            string submissionId,
            int pageNumber,
            DateTimeOffset now)
        {
            var normalizedBytes = Encoding.ASCII.GetBytes(
                $"normalized-page-{pageNumber}-{Guid.NewGuid():N}");
            var thumbnailBytes = Encoding.ASCII.GetBytes(
                $"thumbnail-page-{pageNumber}-{Guid.NewGuid():N}");
            var normalizedLocator = ContentStore.Add(
                normalizedBytes,
                ContentStorageClass.ManagedScanDerived,
                "png");
            var thumbnailLocator = ContentStore.Add(
                thumbnailBytes,
                ContentStorageClass.ManagedScanDerived,
                "png");
            var normalizedObject = FileObject(
                UlidId.New(now), normalizedLocator, "image/png", now);
            var thumbnailObject = FileObject(
                UlidId.New(now), thumbnailLocator, "image/png", now);
            var pageId = UlidId.New(now);
            var normalizedReference = FileReference(
                UlidId.New(now),
                normalizedObject.Id,
                "submission_page",
                pageId,
                "normalized_page",
                now);
            var thumbnailReference = FileReference(
                UlidId.New(now),
                thumbnailObject.Id,
                "submission_page",
                pageId,
                "page_thumbnail",
                now);
            return new PageData(
                new SubmissionPageEntity
                {
                    Id = pageId,
                    SubmissionId = submissionId,
                    PageNumber = pageNumber,
                    NormalizedFileReferenceId = normalizedReference.Id,
                    ThumbnailFileReferenceId = thumbnailReference.Id,
                    WidthPixels = 1_200,
                    HeightPixels = 1_800,
                    RotationDegrees = 0,
                    SourceSha256 = new string('d', 64),
                    NormalizedSha256 = normalizedLocator.Sha256,
                    DifferenceHash = pageNumber.ToString(
                        "x16", CultureInfo.InvariantCulture),
                    PerceptualHash = pageNumber.ToString(
                        "x16", CultureInfo.InvariantCulture),
                    QualityState = "accepted",
                    BlurBasisPoints = 100,
                    ContrastBasisPoints = 8_000,
                    BrightnessBasisPoints = 5_000,
                    InkCoverageBasisPoints = 1_000,
                    AlignmentState = "not_configured",
                    CreatedAt = now,
                },
                normalizedObject,
                normalizedReference,
                thumbnailObject,
                thumbnailReference);
        }

        private static FileObjectEntity FileObject(
            string id,
            ContentObjectLocator locator,
            string mimeType,
            DateTimeOffset now) => new()
            {
                Id = id,
                Sha256 = locator.Sha256,
                Bytes = locator.Bytes,
                VerifiedMime = mimeType,
                Extension = locator.Extension,
                RelativeObjectPath = $"managed/{locator.Sha256[..2]}/object",
                StorageClass = locator.StorageClass.ToString(),
                RetentionClass = "submitted_scan",
                ManagedScanBytes = true,
                State = "available",
                CreatedAt = now,
                VerifiedAt = now,
                ReferenceCountCache = 1,
            };

        private static FileReferenceEntity FileReference(
            string id,
            string fileObjectId,
            string ownerType,
            string ownerId,
            string purpose,
            DateTimeOffset now) => new()
            {
                Id = id,
                FileObjectId = fileObjectId,
                OwnerType = ownerType,
                OwnerId = ownerId,
                Purpose = purpose,
                RetentionAnchorAt = now,
                CreatedAt = now,
            };

        private static RegionEntity Region(
            string id,
            string questionId,
            int pageNumber,
            string regionType,
            DateTimeOffset now) => new()
            {
                Id = id,
                OwnerType = "question",
                OwnerId = questionId,
                PageNumber = pageNumber,
                RegionType = regionType,
                XMillionths = 100_000,
                YMillionths = 100_000,
                WidthMillionths = 300_000,
                HeightMillionths = 100_000,
                RotationDegrees = 0,
                CreatedSource = "teacher",
                CreatedAt = now,
                UpdatedAt = now,
            };

        private static QuestionEntity Question(
            string id,
            string versionId,
            int orderIndex,
            string displayLabel,
            string questionRegionId,
            string answerRegionId,
            DateTimeOffset now) => new()
            {
                Id = id,
                TemplateVersionId = versionId,
                LogicalQuestionId = UlidId.New(now),
                OrderIndex = orderIndex,
                DisplayLabel = displayLabel,
                QuestionText = "都市名を書きなさい。",
                QuestionType = "semantic_short_text",
                GradingMode = "ai_rubric",
                MaxPointsMilli = 1_000,
                PointIncrementMilli = 1_000,
                RequiresCompleteAnswer = true,
                QuestionRegionId = questionRegionId,
                AnswerRegionId = answerRegionId,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            };

        private static AcceptedAnswerEntity Answer(
            string id,
            string questionId,
            string answer,
            DateTimeOffset now) => new()
            {
                Id = id,
                QuestionId = questionId,
                AnswerText = answer,
                NormalizedText = answer,
                VariantType = "canonical",
                TeacherVerified = true,
                AnswerProvenance = "teacher_entered",
                CreatedAt = now,
                UpdatedAt = now,
            };
    }

    private sealed record SeededWorkspaceGraph(
        string SubmissionId,
        string SessionId,
        string GradingRunId,
        string FirstResultId,
        string SecondResultId,
        string FirstEditedRevisionId);

    private sealed record PageData(
        SubmissionPageEntity Page,
        FileObjectEntity NormalizedObject,
        FileReferenceEntity NormalizedReference,
        FileObjectEntity ThumbnailObject,
        FileReferenceEntity ThumbnailReference);

    private sealed class TestContentStore : IContentStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _content =
            new(StringComparer.Ordinal);

        public ContentObjectLocator Add(
            byte[] bytes,
            ContentStorageClass storageClass,
            string extension)
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            _content[Key(storageClass, hash)] = bytes.ToArray();
            return new ContentObjectLocator(
                storageClass,
                hash,
                bytes.LongLength,
                extension);
        }

        public async Task<ContentWriteResult> PutAsync(
            Stream source,
            ContentStorageClass storageClass,
            string verifiedExtension,
            CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var locator = Add(
                buffer.ToArray(), storageClass, verifiedExtension);
            return new ContentWriteResult(
                locator,
                $"managed/{locator.Sha256[..2]}/object",
                Deduplicated: false);
        }

        public Task<Stream> OpenReadAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_content.TryGetValue(
                    Key(locator.StorageClass, locator.Sha256),
                    out var bytes)
                || bytes.LongLength != locator.Bytes)
            {
                throw new FileNotFoundException();
            }

            return Task.FromResult<Stream>(new MemoryStream(
                bytes,
                writable: false));
        }

        public Task<bool> ExistsAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_content.ContainsKey(
                Key(locator.StorageClass, locator.Sha256)));

        public Task DeleteAsync(
            ContentObjectLocator locator,
            CancellationToken cancellationToken = default)
        {
            _content.TryRemove(
                Key(locator.StorageClass, locator.Sha256), out _);
            return Task.CompletedTask;
        }

        private static string Key(
            ContentStorageClass storageClass,
            string sha256) => $"{storageClass}:{sha256}";
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
        public const string SchemeName = "GradingWorkspaceIntegrationTest";
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
                new Claim(ClaimTypes.Name, "grading-workspace-teacher"),
                new Claim(ClaimTypes.Role, role),
            };
            var identity = new ClaimsIdentity(
                claims,
                SchemeName,
                ClaimTypes.Name,
                ClaimTypes.Role);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    SchemeName)));
        }
    }
}
