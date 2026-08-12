using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
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
using OokiGrader.Host.Jobs;
using OokiGrader.Host.Middleware;
using OokiGrader.Host.Reports;
using OokiGrader.Host.Security;
using OokiGrader.Infrastructure.Persistence;
using OokiGrader.Infrastructure.Persistence.Entities;
using OokiGrader.Infrastructure.Storage;
using OokiGrader.Reports.Pdf;

namespace OokiGrader.IntegrationTests;

public sealed class BulkTranscriptExportTests
{
    [Fact]
    public async Task CancelledArchiveCompletionReleasesTemporaryFileHandle()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"ooki-cancelled-archive-{Guid.NewGuid():N}.part");
        try
        {
            await using (var writer = new BulkTranscriptArchiveWriter(
                path,
                1024 * 1024))
            {
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    writer.CompleteAsync(cancellation.Token));
            }

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task AuthorizationSeparatesPreviewCreateAndDownloadRoles()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-001",
            "大木 花子",
            "国語テスト",
            new DateOnly(2026, 8, 1));

        var anonymous = await app.PreviewAsync(null, [seeded.SubmissionId]);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var reviewerPreview = await app.PreviewAsync(
            "readOnlyReviewer",
            [seeded.SubmissionId]);
        Assert.Equal(HttpStatusCode.OK, reviewerPreview.StatusCode);
        var preview = await reviewerPreview.Content.ReadFromJsonAsync<JsonElement>();
        var fingerprint = preview.GetProperty("sourceFingerprint").GetString()!;

        var reviewerCreate = await app.CreateExportAsync(
            "readOnlyReviewer",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Forbidden, reviewerCreate.StatusCode);

        var teacherCreate = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, teacherCreate.StatusCode);
        var body = await teacherCreate.Content.ReadFromJsonAsync<JsonElement>();
        var exportId = body.GetProperty("id").GetString()!;

        var reviewerStatus = await app.SendAsync(
            HttpMethod.Get,
            $"/api/v1/transcript-exports/{exportId}",
            "readOnlyReviewer");
        Assert.Equal(HttpStatusCode.OK, reviewerStatus.StatusCode);
    }

    [Fact]
    public async Task EmptyOrPartlyInvalidExplicitSelectionIsRejectedAtomically()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var valid = await app.SeedFinalizedResultAsync(
            "S-010",
            "佐藤 一郎",
            "理科",
            new DateOnly(2026, 8, 2));

        var empty = await app.PreviewAsync("teacher", []);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal(
            "BULK_EXPORT_SELECTION_EMPTY",
            (await empty.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());

        var invalid = await app.PreviewAsync(
            "teacher",
            [valid.SubmissionId, "01JZZZZZZZZZZZZZZZZZZZZZZZ"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var invalidProblem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "BULK_EXPORT_SELECTION_INVALID",
            invalidProblem.GetProperty("code").GetString());
        Assert.Contains(
            invalidProblem.GetProperty("errors").EnumerateArray(),
            item => item.GetProperty("submissionId").GetString()
                == "01JZZZZZZZZZZZZZZZZZZZZZZZ");
        Assert.Equal(0, await app.CountBulkExportsAsync());
    }

    [Fact]
    public async Task FilterSnapshotMatchesFinalizedReportMembershipAndExcludesUnsafeRows()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var first = await app.SeedFinalizedResultAsync(
            "S-101",
            "大木 花子",
            "国語 基礎",
            new DateOnly(2026, 7, 1),
            subject: "国語",
            category: "確認",
            course: "本科",
            classLabel: "A");
        var second = await app.SeedFinalizedResultAsync(
            "S-102",
            "大木 次郎",
            "国語 応用",
            new DateOnly(2026, 7, 2),
            subject: "国語",
            category: "確認",
            course: "本科",
            classLabel: "A");
        _ = await app.SeedFinalizedResultAsync(
            "S-103",
            "大木 三郎",
            "国語 未確定",
            new DateOnly(2026, 7, 3),
            finalized: false,
            subject: "国語",
            category: "確認",
            course: "本科",
            classLabel: "A");
        _ = await app.SeedFinalizedResultAsync(
            "S-104",
            "大木 四郎",
            "国語 無効",
            new DateOnly(2026, 7, 4),
            voided: true,
            subject: "国語",
            category: "確認",
            course: "本科",
            classLabel: "A");

        var filter = new BulkTranscriptExportFilter(
            "国語 大木",
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            StudentId: null,
            TemplateId: null,
            Subject: "国語",
            Category: "確認",
            Course: "本科",
            Class: "A",
            Sort: "studentName");
        var preview = await app.PreviewFilterAsync("teacher", filter);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, previewBody.GetProperty("resultCount").GetInt32());
        var normalizedSelector = previewBody.GetProperty("normalizedSelector");
        Assert.True(normalizedSelector.TryGetProperty("filter", out var normalizedFilter));
        Assert.False(normalizedSelector.TryGetProperty("mode", out _));
        Assert.False(normalizedSelector.TryGetProperty("state", out _));
        Assert.Equal("国語 大木", normalizedFilter.GetProperty("search").GetString());

        var list = await app.SendAsync(
            HttpMethod.Get,
            "/api/v1/submissions?state=finalized&search=%E5%9B%BD%E8%AA%9E%20%E5%A4%A7%E6%9C%A8&from=2026-07-01&to=2026-07-31&subject=%E5%9B%BD%E8%AA%9E&category=%E7%A2%BA%E8%AA%8D&course=%E6%9C%AC%E7%A7%91&class=A&sort=studentName&pageSize=200",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listedIds = (await list.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!)
            .ToArray();

        var resolvedIds = await app.ResolveFilterIdsAsync(filter);
        Assert.Equal(
            listedIds.Order(StringComparer.Ordinal),
            resolvedIds.Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { first.SubmissionId, second.SubmissionId }
                .Order(StringComparer.Ordinal),
            resolvedIds.Order(StringComparer.Ordinal));

        var created = await app.CreateFilterExportAsync(
            "teacher",
            previewBody.GetProperty("sourceFingerprint").GetString()!,
            normalizedSelector,
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, createdBody.GetProperty("resultCount").GetInt32());
    }

    [Fact]
    public async Task CreateReplayAndWorkerRedeliveryAreIdempotent()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-201",
            "鈴木 花",
            "算数",
            new DateOnly(2026, 8, 3));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        var key = Guid.NewGuid().ToString();

        var first = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            key);
        await app.DeleteHttpIdempotencyRecordAsync(key);
        var replay = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            key);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        var exportId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        Assert.Equal(
            exportId,
            (await replay.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString());
        Assert.Equal(1, await app.CountBulkExportsAsync());
        var persistedRequest = await app.LoadBulkExportAsync(exportId);
        Assert.Equal(key, persistedRequest.RequestIdempotencyKey);
        Assert.Matches("^[0-9a-f]{64}$", persistedRequest.RequestFingerprint);

        Assert.True(await app.Worker.ProcessNextAsync());
        await app.RequeueJobAsync(exportId);
        Assert.True(await app.Worker.ProcessNextAsync());
        Assert.False(await app.Worker.ProcessNextAsync());
        Assert.Equal(1, await app.CountBulkArtifactsAsync(exportId));

        await app.IncrementResultRevisionAsync(seeded.GradingRunId);
        var supersededDownload = await app.SendAsync(
            HttpMethod.Get,
            $"/api/v1/transcript-exports/{exportId}/file",
            "readOnlyReviewer");
        Assert.Equal(HttpStatusCode.Conflict, supersededDownload.StatusCode);
        Assert.Equal(
            "BULK_EXPORT_SUPERSEDED",
            (await supersededDownload.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());
        var superseded = await app.LoadBulkExportAsync(exportId);
        Assert.Equal("superseded", superseded.State);
        Assert.Equal(1, await app.CountSupersededAuditsAsync(exportId));
        Assert.Equal(1, await app.CountSupersededOutboxAsync(exportId));
    }

    [Fact]
    public async Task VerifiedSourceDriftMakesStatusDurablySupersededBeforeFileUrl()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-211",
            "高橋 花",
            "国語",
            new DateOnly(2026, 8, 4));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        var created = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        var exportId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        Assert.True(await app.Worker.ProcessNextAsync());
        await app.IncrementResultRevisionAsync(seeded.GradingRunId);

        var status = await app.SendAsync(
            HttpMethod.Get,
            $"/api/v1/transcript-exports/{exportId}",
            "readOnlyReviewer");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("superseded", body.GetProperty("state").GetString());
        Assert.True(body.GetProperty("superseded").GetBoolean());
        Assert.True(
            !body.TryGetProperty("fileUrl", out var fileUrl)
            || fileUrl.ValueKind == JsonValueKind.Null);

        var download = await app.SendAsync(
            HttpMethod.Get,
            $"/api/v1/transcript-exports/{exportId}/file",
            "readOnlyReviewer");
        Assert.Equal(HttpStatusCode.Conflict, download.StatusCode);
        Assert.Equal(
            "BULK_EXPORT_SUPERSEDED",
            (await download.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("code").GetString());
        Assert.Equal(1, await app.CountSupersededAuditsAsync(exportId));
        Assert.Equal(1, await app.CountSupersededOutboxAsync(exportId));

        _ = await app.SendAsync(
            HttpMethod.Get,
            $"/api/v1/transcript-exports/{exportId}",
            "teacher");
        Assert.Equal(1, await app.CountSupersededAuditsAsync(exportId));
        Assert.Equal(1, await app.CountSupersededOutboxAsync(exportId));
    }

    [Fact]
    public async Task ActiveExportCapsReturnTypedRetryableResponse()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-220",
            "伊藤 花",
            "算数",
            new DateOnly(2026, 8, 4));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;

        for (var index = 0; index < 2; index++)
        {
            var accepted = await app.CreateExportAsync(
                "teacher",
                fingerprint,
                [seeded.SubmissionId],
                Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        var limited = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("60", limited.Headers.GetValues("Retry-After").Single());
        var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "BULK_EXPORT_ACTIVE_LIMIT_REACHED",
            problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("retryable").GetBoolean());
        Assert.Equal(2, problem.GetProperty("actorLimit").GetInt32());
        Assert.Equal(4, problem.GetProperty("siteLimit").GetInt32());
        Assert.Equal(2, await app.CountBulkExportsAsync());
    }

    [Fact]
    public async Task SiteActiveExportCapAppliesAcrossActors()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-221",
            "渡辺 花",
            "理科",
            new DateOnly(2026, 8, 4));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        await app.SeedOtherActorActiveExportsAsync(4);

        var limited = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var problem = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, problem.GetProperty("activeActorCount").GetInt32());
        Assert.Equal(4, problem.GetProperty("activeSiteCount").GetInt32());
        Assert.Equal(4, await app.CountBulkExportsAsync());
    }

    [Fact]
    public async Task CreateRouteHasDedicatedSiteRateLimit()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        for (var index = 0; index < 3; index++)
        {
            var invalid = await app.CreateExportAsync(
                "teacher",
                "invalid",
                ["01JZZZZZZZZZZZZZZZZZZZZZZZ"],
                Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        }

        var limited = await app.CreateExportAsync(
            "teacher",
            "invalid",
            ["01JZZZZZZZZZZZZZZZZZZZZZZZ"],
            Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task FilterPreviewRejectsMatchingNonExportableFinalizedRows()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-230",
            "未割当 花",
            "安全確認テスト",
            new DateOnly(2026, 8, 4));
        await app.UnassignStudentAsync(seeded.SubmissionId);

        var list = await app.SendAsync(
            HttpMethod.Get,
            "/api/v1/submissions?state=finalized&search=%E5%AE%89%E5%85%A8%E7%A2%BA%E8%AA%8D&pageSize=200",
            "teacher");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            listBody.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetString()
                == seeded.SubmissionId);

        var preview = await app.PreviewFilterAsync(
            "teacher",
            new BulkTranscriptExportFilter(
                "安全確認",
                From: null,
                To: null,
                StudentId: null,
                TemplateId: null,
                Subject: null,
                Category: null,
                Course: null,
                Class: null,
                Sort: "-testDate"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        var problem = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "BULK_EXPORT_FILTER_HAS_NON_EXPORTABLE_RESULTS",
            problem.GetProperty("code").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(1, error.GetProperty("count").GetInt32());
        Assert.Equal(0, await app.CountBulkExportsAsync());
    }

    [Fact]
    public async Task ArchiveHasStableSafeOrderingAndFormulaSafeUtf8Manifest()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var later = await app.SeedFinalizedResultAsync(
            "S-900",
            "../\u0001危険\\ 生徒",
            "../社会:確認",
            new DateOnly(2026, 8, 8));
        var earlier = await app.SeedFinalizedResultAsync(
            "=2+3",
            "安全 生徒",
            "国語/基礎",
            new DateOnly(2026, 8, 7));
        var preview = await app.PreviewAsync(
            "teacher",
            [later.SubmissionId, earlier.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        var created = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [later.SubmissionId, earlier.SubmissionId],
            Guid.NewGuid().ToString());
        var exportId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        Assert.True(await app.Worker.ProcessNextAsync());
        var bytes = await app.ReadArchiveAsync(exportId);
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = archive.Entries.Select(item => item.FullName).ToArray();
        Assert.Equal(3, names.Length);
        Assert.EndsWith("manifest.csv", names[^1], StringComparison.Ordinal);
        Assert.All(names, name =>
        {
            Assert.DoesNotContain("..", name, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', name);
            Assert.False(name.StartsWith('/'));
            Assert.DoesNotContain(
                name.Split('/'),
                segment => segment is "" or "." or "..");
        });
        Assert.StartsWith("0001_", names[0], StringComparison.Ordinal);
        Assert.StartsWith("0002_", names[1], StringComparison.Ordinal);

        var manifestEntry = archive.GetEntry("manifest.csv")!;
        await using var manifestStream = manifestEntry.Open();
        using var copy = new MemoryStream();
        await manifestStream.CopyToAsync(copy);
        var manifestBytes = copy.ToArray();
        Assert.True(manifestBytes.AsSpan(0, 3).SequenceEqual(
            new byte[] { 0xEF, 0xBB, 0xBF }));
        var manifest = Encoding.UTF8.GetString(manifestBytes);
        Assert.Contains("\"'=2+3\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"finalized\"", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceRevisionChangeSupersedesWithoutPublishingPartialZip()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-301",
            "田中 花",
            "英語",
            new DateOnly(2026, 8, 5));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        var created = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        var exportId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        await app.IncrementResultRevisionAsync(seeded.GradingRunId);

        Assert.True(await app.Worker.ProcessNextAsync());
        var record = await app.LoadBulkExportAsync(exportId);
        Assert.Equal("superseded", record.State);
        Assert.Equal("bulk_export_source_changed", record.ErrorCode);
        Assert.Null(record.FileReferenceId);
        Assert.Equal(0, await app.CountBulkArtifactsAsync(exportId));
    }

    [Fact]
    public async Task ExpiredFinalLeaseIsTerminalizedWithoutRendering()
    {
        await using var app = await BulkExportTestApplication.CreateAsync();
        var seeded = await app.SeedFinalizedResultAsync(
            "S-401",
            "山田 花",
            "確認",
            new DateOnly(2026, 8, 6));
        var preview = await app.PreviewAsync("teacher", [seeded.SubmissionId]);
        var fingerprint = (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sourceFingerprint").GetString()!;
        var created = await app.CreateExportAsync(
            "teacher",
            fingerprint,
            [seeded.SubmissionId],
            Guid.NewGuid().ToString());
        var exportId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        await app.ExpireAtAttemptLimitAsync(exportId);

        Assert.True(await app.Worker.ProcessNextAsync());
        Assert.False(await app.Worker.ProcessNextAsync());
        var record = await app.LoadBulkExportAsync(exportId);
        Assert.Equal("failed", record.State);
        Assert.Equal("bulk_export_retry_exhausted", record.ErrorCode);
        Assert.Null(record.FileReferenceId);
    }

    private sealed class BulkExportTestApplication : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly SqliteConnection _connection;
        private readonly string _root;

        private BulkExportTestApplication(
            IHost host,
            SqliteConnection connection,
            string root)
        {
            _host = host;
            _connection = connection;
            _root = root;
            Client = host.GetTestClient();
            Worker = host.Services.GetRequiredService<
                BulkTranscriptExportJobWorker>();
        }

        public HttpClient Client { get; }
        public BulkTranscriptExportJobWorker Worker { get; }

        public static async Task<BulkExportTestApplication> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ooki-bulk-export-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var hostBuilder = new HostBuilder()
                .UseEnvironment(Environments.Development)
                .ConfigureAppConfiguration(configuration =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Features:Reports.Pdf"] = "true",
                    }))
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddDataProtection();
                        services.AddRateLimiter(options =>
                        {
                            options.RejectionStatusCode =
                                StatusCodes.Status429TooManyRequests;
                            options.AddPolicy(
                                "search",
                                _ => RateLimitPartition.GetNoLimiter(
                                    "bulk-export-test-search"));
                            options.AddPolicy(
                                BulkTranscriptExportEndpoints
                                    .CreateRateLimitPolicy,
                                _ => RateLimitPartition.GetTokenBucketLimiter(
                                    "bulk-export-test-site",
                                    _ => new TokenBucketRateLimiterOptions
                                    {
                                        TokenLimit = 3,
                                        TokensPerPeriod = 1,
                                        ReplenishmentPeriod =
                                            TimeSpan.FromHours(1),
                                        AutoReplenishment = true,
                                        QueueLimit = 0,
                                    }));
                        });
                        services.AddSingleton<ProtectedCursorCodec>();
                        services.AddSingleton<IdempotencyLockProvider>();
                        services.AddSingleton(TimeProvider.System);
                        services.AddSingleton<IWriteCoordinator,
                            SemaphoreWriteCoordinator>();
                        services.AddSingleton(connection);
                        services.AddDbContextFactory<OokiGraderDbContext>(
                            options => options.UseSqlite(connection));
                        services.AddSingleton<IResultPdfRenderer,
                            ResultPdfRenderer>();
                        services.AddSingleton<IContentStore>(
                            new NtfsContentStore(new ContentStoreOptions
                            {
                                RootPath = Path.Combine(root, "objects"),
                            }));
                        services.AddSingleton<BulkTranscriptExportJobWorker>();
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
                                    .RequireRole("administrator", "teacher"))
                            .AddPolicy(
                                "results",
                                policy => policy
                                    .AddAuthenticationSchemes(
                                        TestAuthenticationHandler.SchemeName)
                                    .RequireRole(
                                        "administrator",
                                        "teacher",
                                        "readOnlyReviewer"));
                    });
                    webBuilder.Configure(application =>
                    {
                        application.UseRouting();
                        application.UseAuthentication();
                        application.UseRateLimiter();
                        application.UseAuthorization();
                        application.UseMiddleware<IdempotencyMiddleware>();
                        application.UseEndpoints(endpoints =>
                        {
                            endpoints.MapSubmissionsEndpoints();
                            endpoints.MapBulkTranscriptExportEndpoints();
                        });
                    });
                });
            var host = hostBuilder.Build();
            try
            {
                await using (var scope = host.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider
                        .GetRequiredService<OokiGraderDbContext>();
                    await db.Database.EnsureCreatedAsync();
                    var now = DateTimeOffset.UtcNow;
                    db.SiteSettings.Add(new SiteSettingsEntity
                    {
                        Id = "site",
                        SchoolName = "大木学習塾",
                        TimeZone = "Asia/Tokyo",
                        Locale = "ja-JP",
                        DataRoot = root,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    db.StaffUsers.Add(new StaffUserEntity
                    {
                        Id = TestAuthenticationHandler.StaffId,
                        Username = "bulk.teacher",
                        UsernameNormalized = "bulk.teacher",
                        DisplayName = "帳票担当",
                        PasswordHash = "test",
                        PasswordAlgorithm = "test",
                        PasswordAlgorithmVersion = 1,
                        Status = "active",
                        CredentialChangedAt = now,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    await db.SaveChangesAsync();
                }

                await host.StartAsync();
                return new BulkExportTestApplication(host, connection, root);
            }
            catch
            {
                host.Dispose();
                await connection.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public Task<HttpResponseMessage> PreviewAsync(
            string? role,
            IReadOnlyList<string> submissionIds) =>
            SendAsync(
                HttpMethod.Post,
                "/api/v1/transcript-exports:preview",
                role,
                new
                {
                    selector = new { submissionIds },
                });

        public Task<HttpResponseMessage> PreviewFilterAsync(
            string role,
            BulkTranscriptExportFilter filter) =>
            SendAsync(
                HttpMethod.Post,
                "/api/v1/transcript-exports:preview",
                role,
                new
                {
                    selector = new { filter },
                });

        public Task<HttpResponseMessage> CreateExportAsync(
            string role,
            string sourceFingerprint,
            IReadOnlyList<string> submissionIds,
            string idempotencyKey) =>
            SendAsync(
                HttpMethod.Post,
                "/api/v1/transcript-exports",
                role,
                new
                {
                    sourceFingerprint,
                    selector = new { submissionIds },
                },
                idempotencyKey);

        public Task<HttpResponseMessage> CreateFilterExportAsync(
            string role,
            string sourceFingerprint,
            JsonElement normalizedSelector,
            string idempotencyKey) =>
            SendAsync(
                HttpMethod.Post,
                "/api/v1/transcript-exports",
                role,
                new
                {
                    sourceFingerprint,
                    selector = normalizedSelector,
                },
                idempotencyKey);

        public async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            string? role,
            object? body = null,
            string? idempotencyKey = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (role is not null)
            {
                request.Headers.Add(TestAuthenticationHandler.RoleHeader, role);
            }

            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await Client.SendAsync(request);
        }

        public async Task<SeededResult> SeedFinalizedResultAsync(
            string studentNumber,
            string studentName,
            string testTitle,
            DateOnly testDate,
            bool finalized = true,
            bool voided = false,
            string? subject = null,
            string? category = null,
            string? course = null,
            string? classLabel = null)
        {
            var now = DateTimeOffset.UtcNow;
            await using var db = await CreateDbAsync();
            var student = new StudentEntity
            {
                Id = UlidId.New(now),
                StudentNumber = studentNumber,
                StudentNumberNormalized = studentNumber.ToLowerInvariant(),
                FamilyName = studentName,
                GivenName = "名",
                FamilyNameNormalized = studentName.ToLowerInvariant(),
                GivenNameNormalized = "名",
                DisplayName = studentName,
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var template = new TestTemplateEntity
            {
                Id = UlidId.New(now.AddMilliseconds(1)),
                Title = testTitle,
                Subject = subject,
                Category = category,
                Course = course,
                State = "active",
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var version = new TemplateVersionEntity
            {
                Id = UlidId.New(now.AddMilliseconds(2)),
                TestTemplateId = template.Id,
                VersionNumber = 1,
                State = "published",
                PipelineVersion = "manual-template-v1",
                PublishedByStaffUserId = TestAuthenticationHandler.StaffId,
                PublishedAt = now,
                ContentHash = new string('a', 64),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var question = new QuestionEntity
            {
                Id = UlidId.New(now.AddMilliseconds(3)),
                TemplateVersionId = version.Id,
                LogicalQuestionId = UlidId.New(now.AddMilliseconds(4)),
                OrderIndex = 0,
                DisplayLabel = "1",
                QuestionText = "答えを書きなさい。",
                QuestionType = "exact_short_text",
                GradingMode = "deterministic",
                MaxPointsMilli = 1_000,
                TeacherVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var session = new TestSessionEntity
            {
                Id = UlidId.New(now.AddMilliseconds(5)),
                TemplateVersionId = version.Id,
                TestDate = testDate,
                Course = course,
                ClassLabel = classLabel,
                Priority = "economy",
                State = "closed",
                CreatedByStaffUserId = TestAuthenticationHandler.StaffId,
                CreatedAt = now,
                UpdatedAt = now,
                ClosedAt = now,
            };
            var submission = new SubmissionEntity
            {
                Id = UlidId.New(now.AddMilliseconds(6)),
                TestSessionId = session.Id,
                State = finalized ? "finalized" : "needs_grade_review",
                ScanPayloadState = "scan_deleted",
                ScanDeletedAt = now,
                ScanDeletionReason = "retention",
                AssignedStudentId = student.Id,
                AssignmentMethod = "teacher",
                AttemptNumber = 1,
                CanonicalForSession = true,
                UploadedByStaffUserId = TestAuthenticationHandler.StaffId,
                FinalizedByStaffUserId = finalized
                    ? TestAuthenticationHandler.StaffId
                    : null,
                FinalizedAt = finalized ? now : null,
                VoidedByStaffUserId = voided
                    ? TestAuthenticationHandler.StaffId
                    : null,
                VoidedAt = voided ? now : null,
                VoidReason = voided ? "duplicate" : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.AddRange(student, template, version, question, session, submission);
            await db.SaveChangesAsync();

            var run = new GradingRunEntity
            {
                Id = UlidId.New(now.AddMilliseconds(7)),
                SubmissionId = submission.Id,
                RunNumber = 1,
                TemplateVersionId = version.Id,
                Reason = "initial",
                State = finalized ? "finalized" : "needs_grade_review",
                PipelineVersion = "test-v1",
                CanonicalInputManifestHash = new string('b', 64),
                EarnedPointsMilli = 1_000,
                PossiblePointsMilli = 1_000,
                ResultSourceRevision = 1,
                CreatedAt = now,
                FinishedAt = now,
                FinalizedAt = finalized ? now : null,
                FinalizedByStaffUserId = finalized
                    ? TestAuthenticationHandler.StaffId
                    : null,
            };
            db.GradingRuns.Add(run);
            await db.SaveChangesAsync();
            var result = new QuestionResultEntity
            {
                Id = UlidId.New(now.AddMilliseconds(8)),
                GradingRunId = run.Id,
                QuestionId = question.Id,
                TranscribedAnswer = "答え",
                NormalizedAnswer = "答え",
                ProposedPointsMilli = 1_000,
                MaximumPointsMilli = 1_000,
                Outcome = "correct",
                Method = "deterministic",
                ConfidenceBasisPoints = 10_000,
                ReviewRequired = false,
                ReviewStatus = "not_required",
                CreatedAt = now,
            };
            db.QuestionResults.Add(result);
            await db.SaveChangesAsync();
            var revision = new ResultRevisionEntity
            {
                Id = UlidId.New(now.AddMilliseconds(9)),
                QuestionResultId = result.Id,
                RevisionNumber = 1,
                AwardedPointsMilli = 1_000,
                Outcome = "correct",
                Source = "initial",
                CreatedAt = now,
            };
            db.ResultRevisions.Add(revision);
            await db.SaveChangesAsync();
            result.CurrentRevisionId = revision.Id;
            submission.CurrentGradingRunId = run.Id;
            await db.SaveChangesAsync();
            return new SeededResult(submission.Id, run.Id);
        }

        public async Task<IReadOnlyList<string>> ResolveFilterIdsAsync(
            BulkTranscriptExportFilter filter)
        {
            await using var db = await CreateDbAsync();
            var selection = await BulkTranscriptSelectionResolver.ResolveAsync(
                db,
                new DefaultHttpContext(),
                new BulkTranscriptExportSelector(null, filter),
                CancellationToken.None);
            return selection.Candidates.Select(item => item.SubmissionId).ToArray();
        }

        public async Task<int> CountBulkExportsAsync()
        {
            await using var db = await CreateDbAsync();
            return await db.BulkTranscriptExports.CountAsync();
        }

        public async Task SeedOtherActorActiveExportsAsync(int count)
        {
            var now = DateTimeOffset.UtcNow;
            var otherActorId = UlidId.New(now);
            await using var db = await CreateDbAsync();
            db.StaffUsers.Add(new StaffUserEntity
            {
                Id = otherActorId,
                Username = $"other-{otherActorId}",
                UsernameNormalized = $"other-{otherActorId}".ToLowerInvariant(),
                DisplayName = "別担当者",
                PasswordHash = "test",
                PasswordAlgorithm = "test",
                PasswordAlgorithmVersion = 1,
                Status = "active",
                CredentialChangedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            for (var index = 0; index < count; index++)
            {
                var jobId = UlidId.New(now.AddMilliseconds(index + 1));
                var exportId = UlidId.New(now.AddMilliseconds(index + 101));
                db.BackgroundJobs.Add(new BackgroundJobEntity
                {
                    Id = jobId,
                    Type = BulkTranscriptExportJobWorker.JobType,
                    SchemaVersion = 1,
                    DeduplicationKey = $"seeded-active-{exportId}",
                    PayloadJson = JsonSerializer.Serialize(new { exportId }),
                    State = "queued",
                    MaxAttempts = 1,
                    NextAttemptAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.BulkTranscriptExports.Add(new BulkTranscriptExportEntity
                {
                    Id = exportId,
                    BackgroundJobId = jobId,
                    RequestIdempotencyKey = Guid.NewGuid().ToString(),
                    RequestFingerprint = new string('a', 64),
                    SelectorJson = "{\"submissionIds\":[\"seed\"]}",
                    SelectorHash = new string('b', 64),
                    SourceSnapshotJson = "[]",
                    SourceFingerprint = new string('c', 64),
                    RendererVersion = ResultPdfRenderer.CurrentRendererVersion,
                    PackageFormatVersion =
                        BulkTranscriptExportJobWorker.PackageFormatVersion,
                    State = "queued",
                    StudentCount = 1,
                    ResultCount = 1,
                    CreatedByStaffUserId = otherActorId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task<int> CountBulkArtifactsAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            return await db.FileReferences.CountAsync(item =>
                item.OwnerType == "bulk_transcript_export"
                && item.OwnerId == exportId
                && item.Purpose == "bulk_result_zip");
        }

        public async Task<int> CountSupersededAuditsAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            return await db.AuditEvents.CountAsync(item =>
                item.EventType == "bulk_transcript_export.superseded"
                && item.ObjectId == exportId);
        }

        public async Task<int> CountSupersededOutboxAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            return await db.OutboxEvents.CountAsync(item =>
                item.AggregateType == "bulk_transcript_export"
                && item.AggregateId == exportId
                && item.PayloadJson.Contains("\"state\":\"superseded\""));
        }

        public async Task DeleteHttpIdempotencyRecordAsync(
            string idempotencyKey)
        {
            await using var db = await CreateDbAsync();
            await db.IdempotencyRecords
                .Where(item =>
                    item.ActorKey == TestAuthenticationHandler.StaffId
                    && item.IdempotencyKey == idempotencyKey)
                .ExecuteDeleteAsync();
        }

        public async Task<BulkTranscriptExportEntity> LoadBulkExportAsync(
            string exportId)
        {
            await using var db = await CreateDbAsync();
            return await db.BulkTranscriptExports
                .AsNoTracking()
                .SingleAsync(item => item.Id == exportId);
        }

        public async Task<byte[]> ReadArchiveAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            var record = await db.BulkTranscriptExports
                .AsNoTracking()
                .Include(item => item.FileReference)
                    .ThenInclude(item => item!.FileObject)
                .SingleAsync(item => item.Id == exportId);
            var file = record.FileReference!.FileObject;
            var store = _host.Services.GetRequiredService<IContentStore>();
            await using var source = await store.OpenReadAsync(
                new ContentObjectLocator(
                    ContentStorageClass.ResultReport,
                    file.Sha256,
                    file.Bytes,
                    file.Extension));
            using var output = new MemoryStream();
            await source.CopyToAsync(output);
            return output.ToArray();
        }

        public async Task IncrementResultRevisionAsync(string gradingRunId)
        {
            await using var db = await CreateDbAsync();
            var run = await db.GradingRuns.SingleAsync(item =>
                item.Id == gradingRunId);
            run.ResultSourceRevision = checked(run.ResultSourceRevision + 1);
            await db.SaveChangesAsync();
        }

        public async Task UnassignStudentAsync(string submissionId)
        {
            await using var db = await CreateDbAsync();
            var submission = await db.Submissions.SingleAsync(item =>
                item.Id == submissionId);
            submission.AssignedStudentId = null;
            submission.AssignmentMethod = "none";
            await db.SaveChangesAsync();
        }

        public async Task RequeueJobAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            var record = await db.BulkTranscriptExports.SingleAsync(item =>
                item.Id == exportId);
            var job = await db.BackgroundJobs.SingleAsync(item =>
                item.Id == record.BackgroundJobId);
            job.State = "queued";
            job.ProgressBasisPoints = 0;
            job.CompletedAt = null;
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        public async Task ExpireAtAttemptLimitAsync(string exportId)
        {
            await using var db = await CreateDbAsync();
            var record = await db.BulkTranscriptExports.SingleAsync(item =>
                item.Id == exportId);
            record.State = "rendering";
            var job = await db.BackgroundJobs.SingleAsync(item =>
                item.Id == record.BackgroundJobId);
            job.State = "leased";
            job.AttemptCount = job.MaxAttempts;
            job.LeaseOwner = "crashed-worker";
            job.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        private Task<OokiGraderDbContext> CreateDbAsync() =>
            _host.Services
                .GetRequiredService<IDbContextFactory<OokiGraderDbContext>>()
                .CreateDbContextAsync();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
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
        public const string SchemeName = "BulkExportIntegrationTest";
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
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(
                    new ClaimsPrincipal(identity),
                    SchemeName)));
        }
    }

    private sealed record SeededResult(string SubmissionId, string GradingRunId);
}
